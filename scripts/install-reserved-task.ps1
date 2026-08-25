param(
    [string]$TaskName = "MaddoxTasks Reserved Work",
    [string]$RunnerScript = (Join-Path $PSScriptRoot "run-reserved-task.ps1"),
    [Parameter(Mandatory = $true)]
    [string]$MaddoxExe,
    [string]$GhExe = "gh",
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot,
    [string]$DatabasePath,
    [string]$Model,
    [string]$ReasoningEffort = "medium",
    [int]$Retries = 1,
    [int]$MaxIterations = 0,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RunnerScript = [System.IO.Path]::GetFullPath($RunnerScript)
if (-not (Test-Path -LiteralPath $RunnerScript -PathType Leaf)) { throw "Runner script not found: $RunnerScript" }

$runnerArguments = @(
    "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $RunnerScript,
    "-MaddoxExe", $MaddoxExe, "-GhExe", $GhExe, "-RepoRoot", $RepoRoot,
    "-ReasoningEffort", $ReasoningEffort, "-Retries", $Retries, "-MaxIterations", $MaxIterations
)
if ($DatabasePath) { $runnerArguments += @("-DatabasePath", $DatabasePath) }
if ($Model) { $runnerArguments += @("-Model", $Model) }
$quotedArguments = $runnerArguments | ForEach-Object { '"' + ($_.ToString().Replace('"', '\"')) + '"' }

$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument ($quotedArguments -join " ")
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration (New-TimeSpan -Days 3650)
$settings = New-ScheduledTaskSettingsSet -Hidden -StartWhenAvailable -MultipleInstances Parallel -ExecutionTimeLimit (New-TimeSpan -Hours 12)

if ($DryRun) {
    Write-Host "Would register hourly task '$TaskName' for user $env:USERNAME."
    Write-Host "Action: powershell.exe $($quotedArguments -join ' ')"
    return
}

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
Write-Host "Registered hourly task '$TaskName' for user $env:USERNAME."
