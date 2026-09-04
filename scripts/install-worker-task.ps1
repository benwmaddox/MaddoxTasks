param([string]$BinaryDir = "F:\MaddoxTasks", [string]$TaskName = "Maddox Tasks Worker", [switch]$DryRun)
$ErrorActionPreference = "Stop"
$binaryDirectory = [IO.Path]::GetFullPath($BinaryDir)
$exe = Join-Path $binaryDirectory "MaddoxTasks.Worker.exe"
if (-not (Test-Path $exe)) { throw "Worker executable not found: $exe" }
if ($DryRun) {
    Write-Host "Would register '$TaskName' at logon: $exe"
    Write-Host "Working directory: $binaryDirectory; interactive user: $env:USERDOMAIN\$env:USERNAME; StartWhenAvailable; IgnoreNew"
    return
}
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $binaryDirectory
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Write-Host "Registered visible at-logon task '$TaskName'."
