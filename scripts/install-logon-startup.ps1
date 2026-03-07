param(
    [string]$TaskName = "EndpointSignalAgent-Logon",
    [string]$ExecutablePath
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $localExe = Join-Path $PSScriptRoot "EndpointSignalAgent.exe"
    if (Test-Path $localExe) {
        $ExecutablePath = (Resolve-Path $localExe).Path
    }
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $repoScript = Join-Path $PSScriptRoot "register-logon-startup.ps1"
    if (Test-Path $repoScript) {
        & $repoScript -TaskName $TaskName
        exit $LASTEXITCODE
    }
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    throw "Could not locate EndpointSignalAgent.exe. Place this script next to the exe or pass -ExecutablePath."
}

$registerScript = Join-Path $PSScriptRoot "register-logon-startup.ps1"
if (Test-Path $registerScript) {
    & $registerScript -TaskName $TaskName -ExecutablePath $ExecutablePath
    exit $LASTEXITCODE
}

$exeFullPath = (Resolve-Path $ExecutablePath).Path
Write-Host "Registering startup task '$TaskName' for user $env:USERNAME"
Write-Host "Executable: $exeFullPath"
$workingDirectory = Split-Path -Path $exeFullPath -Parent
Write-Host "Working directory: $workingDirectory"
$action = New-ScheduledTaskAction -Execute $exeFullPath -WorkingDirectory $workingDirectory
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "Startup task registered successfully."
