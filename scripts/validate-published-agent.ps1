[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Binary
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-PublishedAgent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Executable
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true

    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdout = ""
    $stderr = ""
    $exitCode = -1

    try {
        if (-not $process.Start()) {
            throw "Could not start '$Executable'."
        }

        $stdout = $process.StandardOutput.ReadToEnd()
        $stderr = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        $exitCode = $process.ExitCode
    }
    catch {
        throw "Published agent invocation failed for '$Executable agent $($Arguments -join ' ')': $($_.Exception.Message)"
    }
    finally {
        $process.Dispose()
    }

    if ($exitCode -ne 0) {
        throw "Published agent exited with code $exitCode for 'agent $($Arguments -join ' ')'. Stdout: $($stdout.Trim()) Stderr: $($stderr.Trim())"
    }

    [pscustomobject]@{
        StdOut   = $stdout
        StdErr   = $stderr
        ExitCode = $exitCode
    }
}

function Convert-AgentJson {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Json,

        [Parameter(Mandatory = $true)]
        [string]$Step
    )

    if ([string]::IsNullOrWhiteSpace($Json)) {
        throw "$Step returned no JSON."
    }

    try {
        return ConvertFrom-Json -InputObject $Json
    }
    catch {
        throw "$Step returned invalid JSON: $($_.Exception.Message). Output: $($Json.Trim())"
    }
}

function Write-CommandFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [object]$Command
    )

    $json = $Command | ConvertTo-Json -Compress
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($Path, $json, $utf8NoBom)
}

function Assert-CommandSucceeded {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Response,

        [Parameter(Mandatory = $true)]
        [string]$Step
    )

    $result = Convert-AgentJson -Json $Response.StdOut -Step $Step
    if ($result.success -ne $true) {
        throw "$Step was rejected: $($result.message)"
    }

    return $result
}

$binaryPath = [System.IO.Path]::GetFullPath($Binary)
if (-not (Test-Path -LiteralPath $binaryPath -PathType Leaf)) {
    throw "Published agent binary was not found: $binaryPath"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("maddox-published-agent-" + [Guid]::NewGuid().ToString("N"))
$validationError = $null
$cleanupError = $null

try {
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

    # The executable discovers this relative settings file from its isolated CWD.
    # The database is created only by the executable through its agent commands.
    $settingsPath = Join-Path $tempRoot "MaddoxTasks.json"
    $settingsJson = '{"databasePath":"MaddoxTasks.db"}'
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($settingsPath, $settingsJson, $utf8NoBom)

    Write-Host "[smoke] Checking $binaryPath against an isolated temporary database."

    $initialIssuesResponse = Invoke-PublishedAgent -Executable $binaryPath -Arguments @("agent", "issues") -WorkingDirectory $tempRoot
    if ($initialIssuesResponse.StdOut.Trim() -ne "[]") {
        throw "Initial 'agent issues' response was not an empty array: $($initialIssuesResponse.StdOut.Trim())"
    }

    $initialNextResponse = Invoke-PublishedAgent -Executable $binaryPath -Arguments @("agent", "next") -WorkingDirectory $tempRoot
    if ($initialNextResponse.StdOut.Trim() -ne "null") {
        throw "Initial 'agent next' response was not null: $($initialNextResponse.StdOut.Trim())"
    }

    $createCommandPath = Join-Path $tempRoot "create-command.json"
    Write-CommandFile -Path $createCommandPath -Command ([ordered]@{
            type        = "CreateIssue"
            title       = "Published agent smoke test"
            description = "Created by the published agent validator."
            priority    = 3
        })
    $createResponse = Invoke-PublishedAgent -Executable $binaryPath -Arguments @("agent", "command", "--file", $createCommandPath) -WorkingDirectory $tempRoot
    $createResult = Assert-CommandSucceeded -Response $createResponse -Step "CreateIssue"
    if ([string]::IsNullOrWhiteSpace([string]$createResult.issueId)) {
        throw "CreateIssue returned no issueId."
    }
    $issueId = [string]$createResult.issueId

    $labelCommandPath = Join-Path $tempRoot "label-command.json"
    Write-CommandFile -Path $labelCommandPath -Command ([ordered]@{
            type    = "AddLabel"
            issueId = $issueId
            label   = "repo:published-smoke"
        })
    $labelResponse = Invoke-PublishedAgent -Executable $binaryPath -Arguments @("agent", "command", "--file", $labelCommandPath) -WorkingDirectory $tempRoot
    [void](Assert-CommandSucceeded -Response $labelResponse -Step "AddLabel repo:published-smoke")

    $statusCommandPath = Join-Path $tempRoot "status-command.json"
    Write-CommandFile -Path $statusCommandPath -Command ([ordered]@{
            type      = "ChangeStatus"
            issueId   = $issueId
            newStatus = "Next"
        })
    $statusResponse = Invoke-PublishedAgent -Executable $binaryPath -Arguments @("agent", "command", "--file", $statusCommandPath) -WorkingDirectory $tempRoot
    [void](Assert-CommandSucceeded -Response $statusResponse -Step "ChangeStatus Next")

    $issuesResponse = Invoke-PublishedAgent -Executable $binaryPath -Arguments @("agent", "issues") -WorkingDirectory $tempRoot
    $issues = @(Convert-AgentJson -Json $issuesResponse.StdOut -Step "agent issues after create")
    if ($issues.Count -ne 1) {
        throw "Expected one issue after CreateIssue, got $($issues.Count)."
    }

    $issue = $issues[0]
    if ([string]$issue.issueId -ne $issueId) {
        throw "agent issues returned issue id '$($issue.issueId)' instead of '$issueId'."
    }
    if ([string]$issue.status -ne "Next") {
        throw "agent issues returned status '$($issue.status)' instead of 'Next'."
    }
    if (-not @($issue.repositories | Where-Object { [string]$_ -ieq "published-smoke" }).Count) {
        throw "agent issues did not expose repository 'published-smoke'."
    }

    $nextResponse = Invoke-PublishedAgent -Executable $binaryPath -Arguments @("agent", "next") -WorkingDirectory $tempRoot
    $next = Convert-AgentJson -Json $nextResponse.StdOut -Step "agent next after create"
    if ($null -eq $next) {
        throw "agent next returned null after a Next issue was created."
    }
    if ([string]$next.issueId -ne $issueId) {
        throw "agent next selected issue '$($next.issueId)' instead of '$issueId'."
    }
    if ([string]$next.status -ne "Next") {
        throw "agent next returned status '$($next.status)' instead of 'Next'."
    }
    if (-not @($next.repositories | Where-Object { [string]$_ -ieq "published-smoke" }).Count) {
        throw "agent next did not expose repository 'published-smoke'."
    }

    Write-Host "[smoke] PASS: agent issues, agent next, and agent command completed SQLite-backed validation."
}
catch {
    $validationError = $_.Exception
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        try {
            Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction Stop
        }
        catch {
            $cleanupError = $_.Exception
        }
    }
}

if ($null -ne $validationError) {
    if ($null -ne $cleanupError) {
        Write-Warning "Could not remove validator temporary directory '$tempRoot': $($cleanupError.Message)"
    }
    throw $validationError
}

if ($null -ne $cleanupError) {
    throw "Published agent smoke test passed, but cleanup failed for '$tempRoot': $($cleanupError.Message)"
}
