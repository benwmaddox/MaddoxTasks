param(
    [string]$MaddoxExe = "MaddoxTasks.exe",
    [string]$CodexExe = "codex",
    [string]$GhExe = "gh",
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot,
    [string]$DatabasePath,
    [string]$Model,
    [ValidateSet("minimal", "low", "medium", "high", "xhigh")]
    [string]$ReasoningEffort = "medium",
    [int]$Retries = 1,
    [int]$MaxIterations = 0,
    [switch]$Preview
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-MaddoxClaim {
    param([switch]$DryRun)

    $arguments = @("agent", "claim")
    if ($DatabasePath) { $arguments += @("--db", $DatabasePath) }
    if ($DryRun) { $arguments += "--dry-run" }

    $json = (& $MaddoxExe @arguments | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "MaddoxTasks agent claim failed with exit code $LASTEXITCODE." }
    if ([string]::IsNullOrWhiteSpace($json)) { throw "MaddoxTasks agent claim returned no JSON." }
    return $json | ConvertFrom-Json
}

function Invoke-MaddoxIssues {
    param([string]$Status)

    $arguments = @("agent", "issues", "--status", $Status, "--include-done", "false")
    if ($DatabasePath) { $arguments += @("--db", $DatabasePath) }
    $json = (& $MaddoxExe @arguments | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "MaddoxTasks agent issues failed with exit code $LASTEXITCODE." }
    if ([string]::IsNullOrWhiteSpace($json)) { throw "MaddoxTasks agent issues returned no JSON." }
    return @($json | ConvertFrom-Json)
}

function Get-CanonicalPullRequestUrls {
    param([object]$Issue)

    $content = @($Issue.description) + @($Issue.comments | ForEach-Object { $_.comment }) -join "`n"
    $pattern = 'https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/pull/([0-9]+)'
    $urls = foreach ($match in [regex]::Matches($content, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) {
        "https://github.com/$($match.Groups[1].Value)/$($match.Groups[2].Value)/pull/$($match.Groups[3].Value)"
    }
    return @($urls | Sort-Object -Unique)
}

function Invoke-PullRequestView {
    param([string]$Url)

    $json = (& $GhExe pr view $Url --json state,mergedAt,url | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "gh pr view failed for '$Url' with exit code $LASTEXITCODE." }
    if ([string]::IsNullOrWhiteSpace($json)) { throw "gh pr view returned no JSON for '$Url'." }
    return $json | ConvertFrom-Json
}

function Set-IssueDone {
    param([string]$IssueId)

    $payload = @{ type = "ChangeStatus"; issueId = $IssueId; newStatus = "Done" } | ConvertTo-Json -Compress
    $arguments = @("agent", "command")
    if ($DatabasePath) { $arguments += @("--db", $DatabasePath) }
    $json = ($payload | & $MaddoxExe @arguments | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "MaddoxTasks agent command failed with exit code $LASTEXITCODE." }
    if ([string]::IsNullOrWhiteSpace($json)) { throw "MaddoxTasks agent command returned no JSON." }
    $response = $json | ConvertFrom-Json
    if (-not $response.success) { throw "MaddoxTasks refused closing issue '$IssueId': $($response.message)" }
}

function Invoke-ReviewReconciliation {
    if ($Preview) {
        $previewIssues = Invoke-MaddoxIssues -Status "ReadyForReview"
        foreach ($issue in $previewIssues) {
            $urls = Get-CanonicalPullRequestUrls -Issue $issue
            if ($urls.Count -eq 0) {
                Write-Host "Review preview: issue $($issue.issueId) has no associated PR URLs; leaving unchanged."
            }
            else {
                Write-Host "Review preview: issue $($issue.issueId) would check $($urls -join ', ') and close only if all are merged. No gh calls or task mutations made."
            }
        }
        return
    }

    if (-not (Get-Command $GhExe -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI '$GhExe' is required for ReadyForReview reconciliation. Install gh or pass -GhExe."
    }

    $issues = Invoke-MaddoxIssues -Status "ReadyForReview"
    foreach ($issue in $issues) {
        try {
            $urls = Get-CanonicalPullRequestUrls -Issue $issue
            if ($urls.Count -eq 0) {
                Write-Host "Review issue $($issue.issueId) has no associated PR URLs; leaving unchanged."
                continue
            }

            $views = foreach ($url in $urls) { Invoke-PullRequestView -Url $url }
            if (@($views | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.mergedAt) }).Count -gt 0) {
                Write-Host "Review issue $($issue.issueId) has an open or unmerged PR; leaving unchanged."
                continue
            }

            Set-IssueDone -IssueId $issue.issueId
            Write-Host "Closed review issue $($issue.issueId): all associated PRs are merged."
        }
        catch {
            try {
                $stillReady = Invoke-MaddoxIssues -Status "ReadyForReview" | Where-Object { $_.issueId -eq $issue.issueId }
                if (@($stillReady).Count -eq 0) {
                    Write-Host "Review issue $($issue.issueId) changed state concurrently; treating reconciliation as complete."
                    continue
                }
            }
            catch {
                # Preserve the original actionable warning if the race check also fails.
            }
            Write-Warning "Could not reconcile review issue $($issue.issueId): $($_.Exception.Message). Leaving it unchanged and continuing."
        }
    }
}

function Resolve-ReservedRepository {
    param([string]$Repository)

    $root = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $root $Repository))
    $rootWithSeparator = "$root$([IO.Path]::DirectorySeparatorChar)"
    if (-not $candidate.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Reserved repository '$Repository' resolves outside RepoRoot '$root'."
    }
    if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
        throw "Reserved repository '$Repository' was not found at '$candidate'."
    }
    return $candidate
}

if ($Retries -lt 1) { throw "Retries must be at least 1." }
if ($MaxIterations -lt 0) { throw "MaxIterations must be zero (unlimited) or greater." }
$RepoRoot = [System.IO.Path]::GetFullPath($RepoRoot)

${workFailure} = $null
try {
    for ($iteration = 1; $MaxIterations -eq 0 -or $iteration -le $MaxIterations; $iteration++) {
        $claim = Invoke-MaddoxClaim -DryRun:$Preview
        if ($null -eq $claim) {
            Write-Host "No repository-backed Next task is available; stopping."
            break
        }

    $repositories = @($claim.repositories)
    if ($repositories.Count -eq 0) { throw "Claimed issue $($claim.issueId) has no repositories." }
    # Resolve every reservation before starting Codex. This makes an invalid
    # secondary repository fail before any work begins.
    $repositoryPaths = @($repositories | ForEach-Object { Resolve-ReservedRepository -Repository $_ })
    $repositoryPath = $repositoryPaths[0]
    $selection = ($repositories -join ", ")
    $repositoryMappings = for ($repositoryIndex = 0; $repositoryIndex -lt $repositories.Count; $repositoryIndex++) {
        "  $($repositories[$repositoryIndex]) -> $($repositoryPaths[$repositoryIndex])"
    }
    $repositoryMappingText = $repositoryMappings -join "`n"
    Write-Host "Claimed issue $($claim.sequence) '$($claim.title)' for repositories: $selection"

    if ($Preview) {
        Write-Host "Preview only; MaddoxTasks was not mutated and Codex was not started."
        break
    }

    $prompt = @"
Work only on the already claimed Maddox Tasks issue below. Do not run MaddoxTasks agent claim or select another issue.

Selected issue (exact JSON):
$($claim | ConvertTo-Json -Depth 10 -Compress)

All reserved repositories are writable for this run. Exact repository-to-path mappings:
$repositoryMappingText
The first reserved repository is the working directory. Do not select another task.
When complete, update the Maddox task as appropriate and leave a concise progress comment.
"@

    $codexArguments = @("exec", "--sandbox", "workspace-write", "--cd", $repositoryPath)
    for ($repositoryIndex = 1; $repositoryIndex -lt $repositoryPaths.Count; $repositoryIndex++) {
        $codexArguments += @("--add-dir", $repositoryPaths[$repositoryIndex])
    }
    if ($Model) { $codexArguments += @("--model", $Model) }
    if ($ReasoningEffort) { $codexArguments += @("-c", "model_reasoning_effort=$ReasoningEffort") }

    $completed = $false
    for ($attempt = 1; $attempt -le $Retries; $attempt++) {
        $prompt | & $CodexExe @codexArguments
        if ($LASTEXITCODE -eq 0) { $completed = $true; break }
        Write-Warning "Codex attempt $attempt failed with exit code $LASTEXITCODE."
    }
        if (-not $completed) { throw "Codex failed for claimed issue $($claim.issueId) after $Retries attempt(s)." }
    }
}
catch {
    $workFailure = $_
}
finally {
    try {
        Invoke-ReviewReconciliation
    }
    catch {
        if ($null -ne $workFailure) {
            Write-Warning "ReadyForReview reconciliation failed after the work failure: $($_.Exception.Message)"
        }
        else {
            throw
        }
    }
}

if ($null -ne $workFailure) {
    throw $workFailure
}
