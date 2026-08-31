Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$runner = Join-Path $scriptRoot "run-reserved-task.ps1"
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "maddox-missing-runner-test-$([Guid]::NewGuid().ToString('N'))"
$repoRoot = Join-Path $tempRoot "repos"
$toolRoot = Join-Path $tempRoot "tools"
New-Item -ItemType Directory -Path $repoRoot, $toolRoot -Force | Out-Null

try {
    $fakeMaddoxPs1 = Join-Path $toolRoot "fake-maddox.ps1"
    $fakeMaddoxCmd = Join-Path $toolRoot "fake-maddox.cmd"
    $fakeCodexPs1 = Join-Path $toolRoot "fake-codex.ps1"
    $fakeCodexCmd = Join-Path $toolRoot "fake-codex.cmd"
    $fakeGhPs1 = Join-Path $toolRoot "fake-gh.ps1"
    $fakeGhCmd = Join-Path $toolRoot "fake-gh.cmd"
    $argsLog = Join-Path $tempRoot "codex-args.txt"
    $promptLog = Join-Path $tempRoot "codex-prompt.txt"

    Set-Content -LiteralPath $fakeMaddoxPs1 -Value @'
$arguments = @($args)
if ($arguments -contains "claim") {
    [pscustomobject]@{
        sequence = 1
        shortId = "#1"
        guidPrefix = "test"
        issueId = "00000000-0000-0000-0000-000000000001"
        title = "Missing repository test"
        description = "Test"
        status = "Active"
        priority = 1
        parentId = $null
        labels = @()
        repositories = @()
        comments = @()
        createdAt = "2026-01-01T00:00:00Z"
        updatedAt = "2026-01-01T00:00:00Z"
        dueDate = $null
    } | ConvertTo-Json -Compress
    exit 0
}
if ($arguments -contains "reconcile-reviews") { '{"dryRun":false,"outcomes":[]}' ; exit 0 }
throw "Unexpected fake Maddox arguments: $($arguments -join ' ')"
'@
    Set-Content -LiteralPath $fakeMaddoxCmd -Value "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0fake-maddox.ps1`" %*`r`n"

    Set-Content -LiteralPath $fakeCodexPs1 -Value @'
[Console]::In.ReadToEnd() | Set-Content -LiteralPath $env:MADDOX_TEST_PROMPT_LOG
exit 0
'@
    Set-Content -LiteralPath $fakeCodexCmd -Value "@echo off`r`necho %* > `"%MADDOX_TEST_ARGS_LOG%`"`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0fake-codex.ps1`" %*`r`n"
    Set-Content -LiteralPath $fakeGhPs1 -Value "exit 0"
    Set-Content -LiteralPath $fakeGhCmd -Value "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0fake-gh.ps1`" %*`r`n"

    $previousArgsLog = $env:MADDOX_TEST_ARGS_LOG
    $previousPromptLog = $env:MADDOX_TEST_PROMPT_LOG
    try {
        $env:MADDOX_TEST_ARGS_LOG = $argsLog
        $env:MADDOX_TEST_PROMPT_LOG = $promptLog
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner `
            -MaddoxExe $fakeMaddoxCmd -CodexExe $fakeCodexCmd -GhExe $fakeGhCmd `
            -RepoRoot $repoRoot -MaxIterations 1
        if ($LASTEXITCODE -ne 0) { throw "Runner exited with code $LASTEXITCODE." }
    }
    finally {
        $env:MADDOX_TEST_ARGS_LOG = $previousArgsLog
        $env:MADDOX_TEST_PROMPT_LOG = $previousPromptLog
    }

    $codexArgs = Get-Content -LiteralPath $argsLog -Raw
    if ($codexArgs -notlike "*--cd $repoRoot*") { throw "RepoRoot was not passed as --cd. Args: $codexArgs" }
    if ($codexArgs -like "*--add-dir*") { throw "A missing repository claim must not pass --add-dir. Args: $codexArgs" }

    $prompt = Get-Content -LiteralPath $promptLog -Raw
    if ($prompt -notlike "*no repository was specified*" -or $prompt -notlike "*impact scope is unknown*") {
        throw "Prompt did not explain the missing repository scope."
    }
    Write-Output "Missing-repository runner regression passed."
}
finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
}
