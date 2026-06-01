param(
    [string]$TaskName = "EndpointSignalAgent-Logon"
)

$ErrorActionPreference = "Stop"

$unregisterScript = Join-Path $PSScriptRoot "unregister-logon-startup.ps1"
if (Test-Path $unregisterScript) {
    & $unregisterScript -TaskName $TaskName
    exit $LASTEXITCODE
}

if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Removed startup task '$TaskName'."
}
else {
    Write-Host "Startup task '$TaskName' was not found."
}
