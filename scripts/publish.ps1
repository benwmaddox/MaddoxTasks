param(
    [string[]]$Runtime = @("win-x64"),
    [string]$Configuration = "Release",
    [string]$OutputRoot = "F:\\MaddoxTasks",
    [switch]$NoSelfContained,
    [switch]$NoAot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectFile = Join-Path $projectRoot "MaddoxTasks.csproj"
$workerProject = Join-Path $projectRoot "Worker\MaddoxTasks.Worker.csproj"
$validatorScript = Join-Path $PSScriptRoot "validate-published-agent.ps1"

if (-not (Test-Path $projectFile)) {
    throw "Could not locate project file at $projectFile"
}
if (-not (Test-Path $validatorScript)) {
    throw "Could not locate published agent validator at $validatorScript"
}

$selfContained = if ($NoSelfContained) { "false" } else { "true" }
$publishAot = if ($NoAot) { "false" } else { "true" }
$publishTrimmed = if ($NoAot) { "false" } else { "true" }
if (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $projectRoot $OutputRoot
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

# AOT on Windows needs MSVC link.exe and Windows SDK libs.
# Use IlcUseEnvironmentalTools=true (skip findvcvarsall.bat) and set up PATH/LIB manually.
if ($publishAot -eq "true" -and ($env:OS -eq 'Windows_NT')) {
    $msvcRoot = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC"
    $msvcVersion = Get-ChildItem $msvcRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
    if ($msvcVersion) {
        $msvcBin = "$msvcRoot\$msvcVersion\bin\Hostx64\x64"
        $msvcLib = "$msvcRoot\$msvcVersion\lib\x64"
        $sdkLibRoot = "C:\Program Files (x86)\Windows Kits\10\Lib"
        $sdkVersion = Get-ChildItem $sdkLibRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
        $sdkLib = "$sdkLibRoot\$sdkVersion"
        $env:PATH = "$msvcBin;$env:PATH"
        $env:LIB = "$msvcLib;$sdkLib\um\x64;$sdkLib\ucrt\x64"
        Write-Host "[publish] MSVC $msvcVersion / SDK $sdkVersion configured for AOT."
    }
}

foreach ($rid in $Runtime) {
    $outDir = if ($Runtime.Count -eq 1) { $outputRoot } else { Join-Path $outputRoot $rid }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    Write-Host "Publishing runtime '$rid' to '$outDir' (AOT=$publishAot)..."

    $ilcEnvTools = if ($publishAot -eq "true" -and ($env:OS -eq 'Windows_NT')) { "true" } else { "false" }
    dotnet publish $projectFile `
        -c $Configuration `
        -r $rid `
        --self-contained $selfContained `
        /p:PublishSingleFile=true `
        /p:PublishAot=$publishAot `
        /p:PublishTrimmed=$publishTrimmed `
        /p:IlcUseEnvironmentalTools=$ilcEnvTools `
        -o $outDir

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for runtime '$rid'."
    }

    $binaryName = if ($rid -like "win-*") { "MaddoxTasks.exe" } else { "MaddoxTasks" }
    $binaryPath = Join-Path $outDir $binaryName
    if (-not (Test-Path $binaryPath -PathType Leaf)) {
        throw "dotnet publish did not produce the expected runtime binary '$binaryPath'."
    }

    Write-Host "Validating published agent '$binaryPath'..."
    try {
        & $validatorScript -Binary $binaryPath
    }
    catch {
        throw "Published agent validation failed for runtime '$rid': $($_.Exception.Message)"
    }
    if ($rid -like "win-*") {
        dotnet publish $workerProject -c $Configuration -r $rid --self-contained $selfContained /p:PublishSingleFile=true -o $outDir
        if ($LASTEXITCODE -ne 0) { throw "Worker publish failed for runtime '$rid'." }
        Copy-Item (Join-Path $projectRoot "Worker\worker.json") $outDir -Force
        Copy-Item (Join-Path $projectRoot "Worker\worker-prompt.md") $outDir -Force
        Copy-Item (Join-Path $projectRoot "scripts\install-worker-task.ps1") $outDir -Force
    }
}

Write-Host "Publish complete."

