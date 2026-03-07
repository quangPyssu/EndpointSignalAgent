param(
    [string]$TaskName = "EndpointSignalAgent-Logon",
    [string]$ExecutablePath
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $candidate = Join-Path $PSScriptRoot "..\bin\Release\net8.0-windows\EndpointSignalAgent.exe"
    if (Test-Path $candidate) {
        $ExecutablePath = (Resolve-Path $candidate).Path
    }
}

if ([string]::IsNullOrWhiteSpace($ExecutablePath) -or -not (Test-Path $ExecutablePath)) {
    throw "Provide -ExecutablePath with a valid EndpointSignalAgent.exe path."
}

$exeFullPath = (Resolve-Path $ExecutablePath).Path
Write-Host "Registering startup task '$TaskName' for user $env:USERNAME"
Write-Host "Executable: $exeFullPath"

$action = New-ScheduledTaskAction -Execute $exeFullPath
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "Startup task registered successfully."
