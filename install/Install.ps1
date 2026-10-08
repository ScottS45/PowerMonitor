# Author: Scott Sheen (with ChatGPT)
# Created: 2026-10-08
# Updated: 2026-10-08
# Purpose: Copy a published package into a dedicated per-user folder.
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$PackagePath,
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\PowerMonitor')
)
$ErrorActionPreference = 'Stop'
if (-not $PackagePath) { $PackagePath = Join-Path $PSScriptRoot 'app' }
. (Join-Path $PSScriptRoot 'Check-Requirements.ps1') -PackagePath $PackagePath
$result = Test-PowerMonitorRequirements -Path $PackagePath
if (-not $result.Passed) { throw ($result.Issues -join [Environment]::NewLine) }
if (Get-Process -Name PowerMonitor -ErrorAction SilentlyContinue) { throw 'Exit Power Monitor using its tray menu before installing or updating.' }

$source = (Resolve-Path -LiteralPath $PackagePath).Path.TrimEnd('\')
$target = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
if ($target -eq $source -or $target.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase) -or $source.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Package and destination must be separate folders, neither containing the other.'
}
# A dedicated folder prevents accidental overwrites of unrelated documents/programs.
if ((Test-Path -LiteralPath $target) -and @(Get-ChildItem -LiteralPath $target -Force).Count -gt 0 -and -not (Test-Path -LiteralPath (Join-Path $target 'PowerMonitor.exe'))) {
    throw 'Choose an empty dedicated folder or an existing Power Monitor installation.'
}
if ($PSCmdlet.ShouldProcess($target, 'Copy the complete Power Monitor package')) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $target -Recurse -Force
    Write-Host "Installed to: $target"
    Write-Host "Launch: $(Join-Path $target 'PowerMonitor.exe')"
    Write-Host 'Settings/history remain in %LOCALAPPDATA%\LaptopPowerMonitor. No startup entry or service was created.'
}
