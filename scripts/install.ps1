param(
    [string]$BinaryDir,
    [string]$SkillSource,
    [switch]$SkipPath,
    [switch]$SkipSkills,
    [switch]$Force,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host "[install] $Message"
}

function Ensure-Directory {
    param([string]$PathValue)
    if (Test-Path $PathValue) {
        return
    }

    if ($DryRun) {
        Write-Step "Would create directory: $PathValue"
        return
    }

    New-Item -ItemType Directory -Path $PathValue -Force | Out-Null
    Write-Step "Created directory: $PathValue"
}

function Resolve-BinaryDir {
    param([string]$Root)

    if ($BinaryDir) {
        $candidate = [System.IO.Path]::GetFullPath($BinaryDir)
        if (-not (Test-Path (Join-Path $candidate "MaddoxTasks.exe"))) {
            throw "MaddoxTasks.exe not found in '$candidate'."
        }
        return $candidate
    }

    $candidates = @(
        $Root,
        (Split-Path -Parent $Root),
        "F:\MaddoxTasks",
        (Join-Path (Split-Path -Parent $Root) "artifacts\release\win-x64")
    )

    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }
        $full = [System.IO.Path]::GetFullPath($candidate)
        if (Test-Path (Join-Path $full "MaddoxTasks.exe")) {
            return $full
        }
    }

    return $null
}

function Resolve-SkillSource {
    param([string]$Root)

    if ($SkillSource) {
        $full = [System.IO.Path]::GetFullPath($SkillSource)
        if (-not (Test-Path (Join-Path $full "SKILL.md"))) {
            throw "SKILL.md not found in '$full'."
        }
        return $full
    }

    $candidates = @(
        (Join-Path (Split-Path -Parent $Root) "skills\maddox-tasks"),
        (Join-Path $Root "skills\maddox-tasks")
    )

    foreach ($candidate in $candidates) {
        $full = [System.IO.Path]::GetFullPath($candidate)
        if (Test-Path (Join-Path $full "SKILL.md")) {
            return $full
        }
    }

    return $null
}

function Remove-PathIfNeeded {
    param([string]$PathValue)

    if (-not (Test-Path $PathValue)) {
        return
    }

    if (-not $Force) {
        throw "Path '$PathValue' already exists. Re-run with -Force to replace it."
    }

    if ($DryRun) {
        Write-Step "Would remove existing path: $PathValue"
        return
    }

    Remove-Item -Path $PathValue -Recurse -Force
    Write-Step "Removed existing path: $PathValue"
}

function Install-SkillLink {
    param(
        [string]$TargetPath,
        [string]$SourcePath,
        [string]$Label
    )

    $parent = Split-Path -Parent $TargetPath
    Ensure-Directory -PathValue $parent

    if (Test-Path $TargetPath) {
        $item = Get-Item -Path $TargetPath -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            $resolved = (Resolve-Path -Path $TargetPath).Path
            if ($resolved -eq $SourcePath) {
                Write-Step "$Label already linked: $TargetPath -> $SourcePath"
                return
            }
        }
        Remove-PathIfNeeded -PathValue $TargetPath
    }

    if ($DryRun) {
        Write-Step "Would create $Label link: $TargetPath -> $SourcePath"
        return
    }

    try {
        New-Item -ItemType Junction -Path $TargetPath -Target $SourcePath | Out-Null
        Write-Step "Linked ${Label}: $TargetPath -> $SourcePath"
    }
    catch {
        Copy-Item -Path $SourcePath -Destination $TargetPath -Recurse -Force
        Write-Step "Junction failed; copied $Label skill files to: $TargetPath"
    }
}

function Add-ToUserPath {
    param([string]$Entry)

    $normalizedEntry = $Entry.TrimEnd('\')
    $current = [Environment]::GetEnvironmentVariable("Path", "User")
    if (-not $current) { $current = "" }

    $parts = @($current.Split(';') | Where-Object { $_ -and $_.Trim().Length -gt 0 })
    $match = $false
    foreach ($part in $parts) {
        if ($part.TrimEnd('\') -ieq $normalizedEntry) {
            $match = $true
            break
        }
    }

    if ($match) {
        Write-Step "User PATH already includes: $Entry"
        return
    }

    $newParts = @($parts + $Entry)
    $newValue = $newParts -join ';'

    if ($DryRun) {
        Write-Step "Would add to user PATH: $Entry"
        return
    }

    [Environment]::SetEnvironmentVariable("Path", $newValue, "User")
    if (-not (($env:Path -split ';') | Where-Object { $_.TrimEnd('\') -ieq $normalizedEntry })) {
        $env:Path = "$Entry;$env:Path"
    }
    Write-Step "Added to user PATH: $Entry"
}

$scriptRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$userHome = [Environment]::GetFolderPath("UserProfile")

Write-Step "Script root: $scriptRoot"

if (-not $SkipSkills) {
    $skillRoot = Resolve-SkillSource -Root $scriptRoot
    if (-not $skillRoot) {
        throw "Could not find maddox-tasks skill source. Provide -SkillSource."
    }

    $skillTargets = @(
        @{ Label = "Codex"; Path = Join-Path $userHome ".agents\skills\maddox-tasks" },
        @{ Label = "Codex (legacy)"; Path = Join-Path $userHome ".codex\skills\maddox-tasks" },
        @{ Label = "Claude Code"; Path = Join-Path $userHome ".claude\skills\maddox-tasks" }
    )

    foreach ($target in $skillTargets) {
        Install-SkillLink -TargetPath $target.Path -SourcePath $skillRoot -Label $target.Label
    }
}
else {
    Write-Step "Skipping skill setup."
}

if (-not $SkipPath) {
    $resolvedBinaryDir = Resolve-BinaryDir -Root $scriptRoot
    if (-not $resolvedBinaryDir) {
        throw "Could not locate MaddoxTasks.exe. Provide -BinaryDir."
    }
    Add-ToUserPath -Entry $resolvedBinaryDir
}
else {
    Write-Step "Skipping PATH setup."
}

if ($DryRun) {
    Write-Step "Dry run complete. No changes were made."
}
else {
    Write-Step "Install complete. Open a new terminal so PATH updates are visible everywhere."
}
