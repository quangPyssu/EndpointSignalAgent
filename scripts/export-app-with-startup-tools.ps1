param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "publish\export",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputPath = Join-Path $repoRoot $OutputDir
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

$publishArgs = @(
    "publish",
    (Join-Path $repoRoot "EndpointSignalAgent.csproj"),
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $outputPath
)

if ($SelfContained) {
    $publishArgs += "--self-contained"
    $publishArgs += "true"
}
else {
    $publishArgs += "--self-contained"
    $publishArgs += "false"
}

Write-Host "Publishing EndpointSignalAgent to: $outputPath"
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed"
}

$filesToCopy = @(
    "install-logon-startup.ps1",
    "remove-logon-startup.ps1",
    "install-logon-startup.cmd",
    "remove-logon-startup.cmd"
)

foreach ($file in $filesToCopy) {
    Copy-Item -Path (Join-Path $PSScriptRoot $file) -Destination (Join-Path $outputPath $file) -Force
}

Write-Host "Copied startup setup/remove scripts into export folder."
Write-Host "Done. Export folder: $outputPath"
