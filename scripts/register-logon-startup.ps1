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
# See install-logon-startup.ps1 for why this isn't the exe's own directory:
# the agent's relative "spool\..." paths need a working directory the
# Limited-run-level scheduled task can actually write to.
$workingDirectory = Join-Path $env:LOCALAPPDATA "ContinuousAuth\agent"
New-Item -ItemType Directory -Path $workingDirectory -Force | Out-Null
Write-Host "Working directory: $workingDirectory"
$action = New-ScheduledTaskAction -Execute $exeFullPath -WorkingDirectory $workingDirectory
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "Startup task registered successfully."
