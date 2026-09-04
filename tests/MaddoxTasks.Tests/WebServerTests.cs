using System.Net;
using System.Text;
using System.Text.Json;
using MaddoxTasks.Web;

namespace MaddoxTasks.Tests;

public sealed class WebServerTests
{
    [Fact]
    public void BindingDefaultsAreLanFriendlyAndPortValidationIsUseful()
    {
        Assert.Equal("0.0.0.0", WebServer.DefaultHost);
        Assert.InRange(WebServer.DefaultPort, 1024, 65535);
        Assert.True(WebServer.TryValidateBinding("127.0.0.1", WebServer.DefaultPort, out var validError), validError);
        Assert.False(WebServer.TryValidateBinding("", WebServer.DefaultPort, out var emptyError));
        Assert.Contains("host", emptyError, StringComparison.OrdinalIgnoreCase);
        Assert.False(WebServer.TryValidateBinding("127.0.0.1", 0, out var zeroError));
        Assert.Contains("port", zeroError, StringComparison.OrdinalIgnoreCase);
        Assert.False(WebServer.TryValidateBinding("http://127.0.0.1", WebServer.DefaultPort, out var schemeError));
        Assert.Contains("scheme", schemeError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerServesUiAndEverydayMutationsAgainstIsolatedDatabase()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"MaddoxTasks-web-{Guid.NewGuid():N}.db");
        using var app = WebServer.CreateApplication(databasePath, "127.0.0.1", 0);
        using var client = new HttpClient();

        try
        {
            await app.StartAsync();
            var baseAddress = new Uri(app.Urls.Single().TrimEnd('/') + "/");
            client.BaseAddress = baseAddress;

            using var html = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, html.StatusCode);
            Assert.Contains("MaddoxTasks", await html.Content.ReadAsStringAsync());

            using var favicon = await client.GetAsync("favicon.ico");
            Assert.Equal(HttpStatusCode.NoContent, favicon.StatusCode);

            using var apiRoot = await client.GetAsync("api");
            Assert.Equal(HttpStatusCode.OK, apiRoot.StatusCode);
            using var apiRootJson = JsonDocument.Parse(await apiRoot.Content.ReadAsStringAsync());
            Assert.True(apiRootJson.RootElement.GetProperty("success").GetBoolean());

            using var health = await client.GetAsync("api/health");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var healthJson = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
            Assert.Equal("ok", healthJson.RootElement.GetProperty("status").GetString());

            using var initial = await client.GetAsync("api/issues");
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            using var initialJson = JsonDocument.Parse(await initial.Content.ReadAsStringAsync());
            Assert.Equal(0, initialJson.RootElement.GetProperty("count").GetInt32());

            using var create = await client.PostAsync("api/issues", Json("""
                {"title":"Web integration","description":"From test","priority":2,"status":"Next"}
                """));
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var createJson = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
            var issueId = createJson.RootElement.GetProperty("issueId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(issueId));

            using var priority = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch,
                $"api/issues/{issueId}/priority") { Content = Json("""{"priority":1}""") });
            Assert.Equal(HttpStatusCode.OK, priority.StatusCode);
            using var description = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch,
                $"api/issues/{issueId}/description") { Content = Json("""{"description":"Updated from test"}""") });
            Assert.Equal(HttpStatusCode.OK, description.StatusCode);
            using var label = await client.PostAsync($"api/issues/{issueId}/labels", Json("""{"label":"mobile"}"""));
            Assert.Equal(HttpStatusCode.OK, label.StatusCode);
            using var removedLabel = await client.DeleteAsync($"api/issues/{issueId}/labels/mobile");
            Assert.Equal(HttpStatusCode.OK, removedLabel.StatusCode);
            using var restoredLabel = await client.PostAsync($"api/issues/{issueId}/labels", Json("""{"label":"mobile"}"""));
            Assert.Equal(HttpStatusCode.OK, restoredLabel.StatusCode);
            using var comment = await client.PostAsync($"api/issues/{issueId}/comments", Json("""{"comment":"Hello"}"""));
            Assert.Equal(HttpStatusCode.OK, comment.StatusCode);
            using var status = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch,
                $"api/issues/{issueId}/status") { Content = Json("""{"status":"Done"}""") });
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);

            using var detail = await client.GetAsync($"api/issues/{issueId}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            using var detailJson = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
            var issue = detailJson.RootElement.GetProperty("issue");
            Assert.Equal("Done", issue.GetProperty("status").GetString());
            Assert.Equal(1, issue.GetProperty("priority").GetInt32());
            Assert.Equal("Updated from test", issue.GetProperty("description").GetString());
            Assert.Contains("mobile", issue.GetProperty("labels").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(1, issue.GetProperty("comments").GetArrayLength());
            Assert.True(issue.GetProperty("history").GetArrayLength() >= 4);

            using var missing = await client.GetAsync("api/issues/not-an-issue");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            using var invalid = await client.PostAsync("api/issues", Json("""{"title":"bad","priority":9}"""));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            TryDelete(databasePath);
            TryDelete(databasePath + "-shm");
            TryDelete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task RepositoryLocksReportReservingIssuesInDeterministicOrder()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"MaddoxTasks-web-locks-{Guid.NewGuid():N}.db");
        using var app = WebServer.CreateApplication(databasePath, "127.0.0.1", 0);
        using var client = new HttpClient();

        try
        {
            await app.StartAsync();
            client.BaseAddress = new Uri(app.Urls.Single().TrimEnd('/') + "/");

            using (var emptyResponse = await client.GetAsync("api/repository-locks"))
            {
                Assert.Equal(HttpStatusCode.OK, emptyResponse.StatusCode);
                using var emptyJson = JsonDocument.Parse(await emptyResponse.Content.ReadAsStringAsync());
                Assert.Equal(0, emptyJson.RootElement.GetProperty("count").GetInt32());
                Assert.Empty(emptyJson.RootElement.GetProperty("locks").EnumerateArray());
            }

            var activeId = await CreateIssueAsync(client, "Active lock", priority: 2);
            await AddLabelAsync(client, activeId, "repo:zeta");
            await AddLabelAsync(client, activeId, "repo:alpha");
            await ChangeStatusAsync(client, activeId, "Active");

            var reviewId = await CreateIssueAsync(client, "Review lock", priority: 1);
            await AddLabelAsync(client, reviewId, "repo:beta");
            await ChangeStatusAsync(client, reviewId, "ReadyForReview");

            var missingId = await CreateIssueAsync(client, "Missing scope", priority: 3);
            await ChangeStatusAsync(client, missingId, "Active");

            var ignoredId = await CreateIssueAsync(client, "Next is not locked", priority: 1);
            await AddLabelAsync(client, ignoredId, "repo:ignored");

            var blockedId = await CreateIssueAsync(client, "Blocked is not locked", priority: 1);
            await AddLabelAsync(client, blockedId, "repo:blocked");
            await ChangeStatusAsync(client, blockedId, "Blocked");

            using var response = await client.GetAsync("api/repository-locks");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var locks = json.RootElement.GetProperty("locks").EnumerateArray().ToArray();

            Assert.Equal(4, json.RootElement.GetProperty("count").GetInt32());
            Assert.Equal(["alpha", "beta", "missing", "zeta"],
                locks.Select(item => item.GetProperty("repository").GetString()!).ToArray());
            Assert.Equal([activeId, reviewId, missingId, activeId],
                locks.Select(item => item.GetProperty("issueId").GetString()!).ToArray());
            Assert.Equal(["Active", "ReadyForReview", "Active", "Active"],
                locks.Select(item => item.GetProperty("status").GetString()!).ToArray());
            Assert.Equal(["Active", "Ready for Review", "Active", "Active"],
                locks.Select(item => item.GetProperty("statusLabel").GetString()!).ToArray());
            Assert.Equal(["Active lock", "Review lock", "Missing scope", "Active lock"],
                locks.Select(item => item.GetProperty("title").GetString()!).ToArray());
            Assert.All(locks, item => Assert.False(string.IsNullOrWhiteSpace(
                item.GetProperty("shortId").GetString())));
            Assert.Equal([2, 1, 3, 2],
                locks.Select(item => item.GetProperty("priority").GetInt32()).ToArray());
            Assert.DoesNotContain(locks, item => item.GetProperty("issueId").GetString() == ignoredId);
            Assert.DoesNotContain(locks, item => item.GetProperty("issueId").GetString() == blockedId);
        }
        finally
        {
            await app.StopAsync();
            TryDelete(databasePath);
            TryDelete(databasePath + "-shm");
            TryDelete(databasePath + "-wal");
        }
    }

    [Fact]
    public void EmbeddedWebUiIncludesPriorityLocksRepositoryTagsAndDraftPreservation()
    {
        var html = WebAssets.IndexHtml;

        Assert.Contains("id=\"edit-status\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"done-button\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"locks-button\"", html, StringComparison.Ordinal);
        Assert.Contains("/api/repository-locks", html, StringComparison.Ordinal);
        Assert.Contains("repository-tag", html, StringComparison.Ordinal);
        Assert.Contains("Repository: ${repository}", html, StringComparison.Ordinal);
        Assert.Contains("visibleIssueLabels(issue.labels)", html, StringComparison.Ordinal);
        Assert.Contains("Use repo:&lt;name&gt; to identify and reserve a related repository.", html,
            StringComparison.Ordinal);
        Assert.Contains("left.priority - right.priority || left.sequence - right.sequence", html,
            StringComparison.Ordinal);
        Assert.Contains("captureDetailDraft", html, StringComparison.Ordinal);
        Assert.Contains("restoreDetailDraft", html, StringComparison.Ordinal);
        Assert.Contains("dirtyFields", html, StringComparison.Ordinal);
        Assert.Contains("selectionStart", html, StringComparison.Ordinal);
        Assert.Contains("panelScrollTop", html, StringComparison.Ordinal);
        Assert.Contains("async function refresh(silent = false, preserveDetailDraft = true)", html,
            StringComparison.Ordinal);
        Assert.Contains("setInterval(() => refresh(true), 10000)", html, StringComparison.Ordinal);
        Assert.DoesNotContain("await refresh(true, false)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedWebUiOnlyRendersDoneColumnWhenIncluded()
    {
        var html = WebAssets.IndexHtml;

        Assert.Contains("const boardStatuses = statuses.filter(status => status !== 'Done' || byId('include-done').checked);",
            html, StringComparison.Ordinal);
        Assert.Contains("boardStatuses.forEach(status =>", html, StringComparison.Ordinal);
    }

    private static async Task<string> CreateIssueAsync(HttpClient client, string title, int priority)
    {
        using var response = await client.PostAsync("api/issues",
            Json($$"""{"title":"{{title}}","priority":{{priority}},"status":"Next"}"""));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Assert.IsType<string>(json.RootElement.GetProperty("issueId").GetString());
    }

    private static async Task AddLabelAsync(HttpClient client, string issueId, string label)
    {
        using var response = await client.PostAsync($"api/issues/{issueId}/labels",
            Json($$"""{"label":"{{label}}"}"""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task ChangeStatusAsync(HttpClient client, string issueId, string status)
    {
        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch,
            $"api/issues/{issueId}/status") { Content = Json($$"""{"status":"{{status}}"}""") });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static StringContent Json(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // SQLite may briefly retain a sidecar handle after shutdown on CI.
        }
    }
}
