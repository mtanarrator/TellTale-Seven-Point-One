param(
    [string]$Target = (Join-Path $env:USERPROFILE 'Documents\Vegas Application Extensions')
)

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot

if (-not (Test-Path $Target)) {
    New-Item -ItemType Directory -Path $Target | Out-Null
}

Copy-Item (Join-Path $source 'TellTale7PointOne.dll') $Target -Force
Copy-Item (Join-Path $source 'telltale_7point1_core.dll') $Target -Force

Write-Host "Installed TellTale7PointOne.dll + telltale_7point1_core.dll"
Write-Host "Target: $Target"
Write-Host "EXPERIMENTAL BETA build. Back up projects and verify exports."
Write-Host "Restart VEGAS Pro to load the new panel."
