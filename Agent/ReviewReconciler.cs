using System.Text.Json;
using System.Text.RegularExpressions;
using MaddoxTasks.Application;
using MaddoxTasks.Domain;
using MaddoxTasks.Infrastructure;

namespace MaddoxTasks.Agent;

public static class PullRequestUrlExtractor
{
    private static readonly Regex PullRequestUrlPattern = new(
        @"(?<![A-Za-z0-9_])https://github\.com/(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+)/pull/(?<number>[0-9]+)(?![A-Za-z0-9_])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> Extract(Issue issue)
    {
        var content = string.Join("\n", new[] { issue.Description }.Concat(issue.Comments.Select(comment => comment.Comment)));
        return PullRequestUrlPattern.Matches(content)
            .Select(match => $"https://github.com/{match.Groups["owner"].Value.ToLowerInvariant()}/{match.Groups["repo"].Value.ToLowerInvariant()}/pull/{match.Groups["number"].Value}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(url => url, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed class ReviewReconciler
{
    private readonly IssueEngine _engine;
    private readonly IGitHubPullRequestClient _pullRequests;

    public ReviewReconciler(IssueEngine engine, IGitHubPullRequestClient pullRequests)
    {
        _engine = engine;
        _pullRequests = pullRequests;
    }

    public ReviewReconciliationResult Reconcile(bool dryRun = false)
    {
        var outcomes = new List<ReviewReconciliationOutcome>();
        foreach (var view in _engine.QueryIssues(new IssueFilter { StatusEquals = Status.ReadyForReview }, includeDone: false))
        {
            var urls = PullRequestUrlExtractor.Extract(view.Issue);
            if (urls.Count == 0)
            {
                outcomes.Add(new ReviewReconciliationOutcome(view.Issue.Id.ToString(), view.Issue.Title, "noPullRequests", urls, null));
                continue;
            }

            if (dryRun)
            {
                outcomes.Add(new ReviewReconciliationOutcome(view.Issue.Id.ToString(), view.Issue.Title, "dryRun", urls, null));
                continue;
            }

            var pullRequests = new List<GitHubPullRequest>();
            string? lookupError = null;
            foreach (var url in urls)
            {
                try
                {
                    pullRequests.Add(_pullRequests.Get(url));
                }
                catch (Exception exception)
                {
                    lookupError = exception.Message;
                    break;
                }
            }

            if (lookupError is not null)
            {
                outcomes.Add(new ReviewReconciliationOutcome(view.Issue.Id.ToString(), view.Issue.Title, "lookupError", urls, lookupError));
                continue;
            }

            if (pullRequests.Any(pullRequest => !pullRequest.MergedAt.HasValue))
            {
                outcomes.Add(new ReviewReconciliationOutcome(view.Issue.Id.ToString(), view.Issue.Title, "unmerged", urls, null));
                continue;
            }

            var completion = _engine.TryCompleteReadyForReview(view.Issue.Id);
            var outcome = completion switch
            {
                ConditionalStatusChangeResult.Closed => "closed",
                ConditionalStatusChangeResult.AlreadyChanged => "concurrentStateChange",
                ConditionalStatusChangeResult.NotFound => "notFound",
                _ => "unchanged"
            };
            outcomes.Add(new ReviewReconciliationOutcome(view.Issue.Id.ToString(), view.Issue.Title, outcome, urls, null));
        }

        return new ReviewReconciliationResult(dryRun, outcomes);
    }
}

public sealed record ReviewReconciliationResult(bool DryRun, IReadOnlyList<ReviewReconciliationOutcome> Outcomes);

public sealed record ReviewReconciliationOutcome(
    string IssueId,
    string Title,
    string Outcome,
    IReadOnlyList<string> PullRequestUrls,
    string? Error);
