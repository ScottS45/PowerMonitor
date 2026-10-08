# Author: Scott Sheen (with ChatGPT)
# Created: 2026-10-08
# Updated: 2026-10-08
# Purpose: Read-only checks for a published Windows x64 Power Monitor package.
[CmdletBinding()]
param([string]$PackagePath)
$ErrorActionPreference = 'Stop'

function Test-PowerMonitorRequirements {
    param([string]$Path)
    $issues = New-Object 'System.Collections.Generic.List[string]'
    $osVersion = [Environment]::OSVersion.Version
    if ($env:OS -ne 'Windows_NT' -or $osVersion.Major -lt 10 -or $osVersion.Build -lt 17763) {
        $issues.Add('Requires Windows 10 build 17763 (1809) or later, or Windows 11.')
    }
    $architecture = $env:PROCESSOR_ARCHITEW6432
    if (-not $architecture) { $architecture = $env:PROCESSOR_ARCHITECTURE }
    if ($architecture -ne 'AMD64') { $issues.Add('This package targets Windows x64 (AMD64). Other architectures are not validated.') }

    # Only accept a complete published folder, never a lone copied executable.
    foreach ($file in @('PowerMonitor.exe', 'PowerMonitor.dll', 'PowerMonitor.deps.json', 'PowerMonitor.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $file) -PathType Leaf)) { $issues.Add("Missing package file: $file") }
    }
    $configPath = Join-Path $Path 'PowerMonitor.runtimeconfig.json'
    if (Test-Path -LiteralPath $configPath) {
        try {
            $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
            $frameworks = @($config.runtimeOptions.frameworks) + @($config.runtimeOptions.framework) | Where-Object { $_ }
            if ($frameworks.Count -gt 0) {
                # Inspect the x64 installation, independent of the invoking shell's bitness/PATH.
                $programFiles64 = $env:ProgramW6432
                if (-not $programFiles64) { $programFiles64 = $env:ProgramFiles }
                $dotnetRoot = Join-Path $programFiles64 'dotnet'
                $registryPath = 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\dotnet\Setup\InstalledVersions\x64'
                $registeredRoot = (Get-ItemProperty -LiteralPath $registryPath -ErrorAction SilentlyContinue).InstallLocation
                if ($registeredRoot) { $dotnetRoot = $registeredRoot }
                foreach ($framework in $frameworks) {
                    $required = [version]$framework.version
                    $frameworkPath = Join-Path $dotnetRoot "shared\$($framework.name)"
                    $compatible = @(Get-ChildItem -LiteralPath $frameworkPath -Directory -ErrorAction SilentlyContinue | Where-Object {
                        $installed = $null
                        [version]::TryParse($_.Name, [ref]$installed) -and $installed.Major -eq $required.Major -and $installed.Minor -eq $required.Minor -and $installed -ge $required
                    })
                    if ($compatible.Count -eq 0) { $issues.Add("Missing x64 $($framework.name) $required or a newer patch in the same major/minor. Install the .NET 8 Desktop Runtime: https://dotnet.microsoft.com/en-us/download/dotnet/8.0") }
                }
            } else {
                foreach ($file in @('coreclr.dll', 'hostfxr.dll', 'System.Windows.Forms.dll')) {
                    if (-not (Test-Path -LiteralPath (Join-Path $Path $file))) { $issues.Add("Incomplete self-contained package: missing $file") }
                }
            }
        } catch { $issues.Add("Cannot read package runtime configuration: $($_.Exception.Message)") }
    }
    [pscustomobject]@{ Passed = ($issues.Count -eq 0); Issues = @($issues); PackagePath = $Path }
}

# Dot-sourcing exposes the checker to Install.ps1 without running it twice.
if ($MyInvocation.InvocationName -ne '.') {
    if (-not $PackagePath) { $PackagePath = Join-Path $PSScriptRoot 'app' }
    $result = Test-PowerMonitorRequirements -Path $PackagePath
    if (-not $result.Passed) {
        $result.Issues | ForEach-Object { Write-Host "FAIL: $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host 'PASS: OS, architecture, core package files and runtime requirements.' -ForegroundColor Green
    Write-Host 'Optional sensors depend on hardware/drivers and are checked by the app. DLL completeness beyond core files is verified by running the package self-tests.'
    exit 0
}
