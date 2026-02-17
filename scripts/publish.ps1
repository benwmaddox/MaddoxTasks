param(
    [string[]]$Runtime = @("win-x64"),
    [string]$Configuration = "Release",
    [string]$OutputRoot = "F:\\MaddoxTasks",
    [switch]$NoSelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot "MaddoxTasks.csproj"

if (-not (Test-Path $projectFile)) {
    throw "Could not locate project file at $projectFile"
}

$selfContained = if ($NoSelfContained) { "false" } else { "true" }
if (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $projectRoot $OutputRoot
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

foreach ($rid in $Runtime) {
    $outDir = if ($Runtime.Count -eq 1) { $outputRoot } else { Join-Path $outputRoot $rid }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    Write-Host "Publishing runtime '$rid' to '$outDir'..."

    dotnet publish $projectFile `
        -c $Configuration `
        -r $rid `
        --self-contained $selfContained `
        /p:PublishSingleFile=true `
        /p:PublishTrimmed=false `
        -o $outDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for runtime '$rid'."
    }
}

Write-Host "Publish complete."

