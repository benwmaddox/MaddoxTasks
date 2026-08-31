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

function Invoke-ReviewReconciliation {
    $arguments = @("agent", "reconcile-reviews", "--gh-exe", $GhExe)
    if ($DatabasePath) { $arguments += @("--db", $DatabasePath) }
    if ($Preview) { $arguments += "--dry-run" }
    $json = (& $MaddoxExe @arguments | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "MaddoxTasks agent reconcile-reviews failed with exit code $LASTEXITCODE." }
    if ([string]::IsNullOrWhiteSpace($json)) { throw "MaddoxTasks agent reconcile-reviews returned no JSON." }
    $result = $json | ConvertFrom-Json
    foreach ($outcome in @($result.outcomes)) {
        switch ($outcome.outcome) {
            "closed" { Write-Host "Closed review issue $($outcome.issueId): all associated PRs are merged." }
            "noPullRequests" { Write-Host "Review issue $($outcome.issueId) has no associated PR URLs; leaving unchanged." }
            "unmerged" { Write-Host "Review issue $($outcome.issueId) has an open or unmerged PR; leaving unchanged." }
            "lookupError" { Write-Warning "Could not reconcile review issue $($outcome.issueId): $($outcome.error). Leaving it unchanged and continuing." }
            "concurrentStateChange" { Write-Host "Review issue $($outcome.issueId) changed state concurrently; treating reconciliation as complete." }
            "notFound" { Write-Warning "Review issue $($outcome.issueId) was not found during reconciliation; leaving it unchanged and continuing." }
            "dryRun" { Write-Host "Review preview: issue $($outcome.issueId) would check $($outcome.pullRequestUrls -join ', ') and close only if all are merged. No gh calls or task mutations made." }
            default { Write-Warning "Unexpected ReadyForReview reconciliation outcome '$($outcome.outcome)' for issue $($outcome.issueId); leaving it unchanged and continuing." }
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
            Write-Host "No claimable Next task is available; stopping."
            break
        }

    $repositories = @($claim.repositories)
    # Resolve every reservation before starting Codex. This makes an invalid
    # secondary repository fail before any work begins.
    $repositoryPaths = @($repositories | ForEach-Object { Resolve-ReservedRepository -Repository $_ })
    if ($repositories.Count -eq 0) {
        if (-not (Test-Path -LiteralPath $RepoRoot -PathType Container)) {
            throw "RepoRoot was not found at '$RepoRoot'."
        }
        $repositoryPath = $RepoRoot
        $repositoryMappingText = "  missing (no repository specified; working directory is RepoRoot: $RepoRoot)"
        Write-Host "Claimed issue $($claim.sequence) '$($claim.title)' with missing repository scope; impact scope is unknown."
    }
    else {
        $repositoryPath = $repositoryPaths[0]
        $selection = ($repositories -join ", ")
        $repositoryMappings = for ($repositoryIndex = 0; $repositoryIndex -lt $repositories.Count; $repositoryIndex++) {
            "  $($repositories[$repositoryIndex]) -> $($repositoryPaths[$repositoryIndex])"
        }
        $repositoryMappingText = $repositoryMappings -join "`n"
        Write-Host "Claimed issue $($claim.sequence) '$($claim.title)' for repositories: $selection"
    }

    if ($Preview) {
        Write-Host "Preview only; MaddoxTasks was not mutated and Codex was not started."
        break
    }

    $prompt = @"
Work only on the already claimed Maddox Tasks issue below. Do not run MaddoxTasks agent claim or select another issue.

Selected issue (exact JSON):
$($claim | ConvertTo-Json -Depth 10 -Compress)

Repository scope for this run:
$repositoryMappingText
If no repository was specified, the impact scope is unknown: inspect from RepoRoot and make only changes required by the issue. Otherwise, all reserved repositories are writable and the first reserved repository is the working directory. Do not select another task.
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
