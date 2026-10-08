# Power Monitor

**Version 5.4.6 · Author: Scott Sheen · Company: Sheen Consulting International**

A Windows desktop and tray application for understanding battery drain and application activity, on battery or AC power. Written in C# with .NET 8 and Windows Forms. Developed by Scott Sheen with assistance from ChatGPT.

## Features

- Live battery power, capacity, charging status, health and session totals when supported by the machine.
- Process CPU activity, including protected Windows processes, with grouped subprocesses and searchable details.
- Sortable CPU, GPU, estimated watts, mAh and I/O columns; expandable system CPU breakdown.
- Optional GPU activity and hardware energy diagnostics, plus battery-based calibration.
- Windows energy estimates where available, with measured and estimated values kept distinct.
- SQLite history, configurable battery-drain alerts, dark interface and notification-area controls.
- Low-power mode enabled by default; hardware energy probing disabled by default.

## Requirements

- Windows 10 version 1809 or later, or Windows 11. Development verification has been on Windows 10; hardware capabilities vary.
- .NET 8 Desktop Runtime for a framework-dependent build.
- .NET 8 SDK or a compatible newer SDK to build from source.
- Some optional hardware readings require supported drivers or elevated access. Ordinary monitoring does not require administrator privileges.

## Build and run

From the repository folder in PowerShell:

```powershell
dotnet restore PowerMonitor.csproj
dotnet publish PowerMonitor.csproj -c Release -r win-x64 --self-contained false -o artifacts/PowerMonitor
.\run.ps1
```

Keep the complete published folder together. Exit any previous copy through its tray menu before starting another. By default, closing the window keeps the monitor running in the tray.

To create a Windows x64 package that includes the .NET runtime:

```powershell
dotnet publish PowerMonitor.csproj -c Release -r win-x64 --self-contained true -o artifacts/PowerMonitor-standalone
```

## Measurements and limitations

Battery discharge watts are physical battery readings when supplied by Windows and the device firmware. Battery charging watts are power entering the battery, and do not represent total wall power.

Process CPU, GPU and I/O activity help identify contributors. Activity scores are not measured per-app watts. Windows energy attribution, hardware-based allocation and battery-calibrated app estimates have separate assumptions and may leave system energy unattributed. Missing readings remain unknown rather than becoming zero.

GPU hardware polling can wake or retain a GPU and change its power consumption. Enable it deliberately in Settings for diagnostics. Reported driver readings are not guaranteed to be accurate. Calibration requires sufficient actual battery discharge data and variation; synthetic tests do not establish physical measurement accuracy.

The app does not automatically change power plans, terminate processes or change GPU settings. XMG-specific information is read-only and optional; an XMG laptop is not required.

## Settings and local data

Settings, history, calibration and logs are stored under:

```text
%LOCALAPPDATA%\LaptopPowerMonitor
```

Low-power mode uses at least a 10-second live/CPU interval and a 60-second history interval. GPU activity counters are disabled in ordinary low-power mode. History retention defaults to 90 days.

Diagnostics and history can contain executable paths, process names and hardware details. Review these before attaching them to a public issue. Generated data and screenshots are excluded from this repository.

## Verification

Publish first, then run the built-in regression checks:

```powershell
$exe = Join-Path $PWD 'artifacts\PowerMonitor\PowerMonitor.exe'
$report = Join-Path $PWD 'artifacts\self-test.txt'
Start-Process -FilePath $exe -ArgumentList "--self-test --output `"$report`"" -WindowStyle Hidden -Wait
Get-Content -LiteralPath $report
```

Tests cover alert thresholds, unknown values, persistence, CPU coverage and process identity, sorting, GPU mapping, energy conversion and calibration. To capture the interface using isolated settings/history:

```powershell
$preview = Join-Path $PWD 'artifacts\ui-smoke'
Start-Process -FilePath $exe -ArgumentList "--ui-smoke --output `"$preview`"" -WindowStyle Hidden -Wait
```

UI capture does sample the local machine. Hardware accuracy and battery behavior require separate verification on the target computer.

## Source layout

| Files | Purpose |
| --- | --- |
| `Program.cs`, `MainForm.cs`, `ProcessesView.cs`, `Theme.cs` | Startup and desktop interface |
| `Telemetry.cs`, `Native.cs`, `BatteryMetrics.cs` | Windows and battery measurements |
| `ProcessActivity.cs`, `ProcessCounterFallback.cs` | Process CPU, I/O and GPU activity |
| `WindowsEnergy.cs`, `HardwareEnergy.cs`, `EnergyCalibration.cs`, `PowerAttribution.cs` | Energy readings and estimates |
| `MonitorService.cs`, `HistoryStore.cs`, `Models.cs` | Sampling, persistence and settings |
| `SelfTests.cs`, `HardwareEnergyTests.cs`, `ActivityUiSmoke.cs` | Regression and interface checks |
| `assets/` | Application icon and editable SVG source |

## Version and authorship

The current release version is **5.4.6**. `PowerMonitor.csproj` is the version source used by the executable and window title. See [CHANGELOG.md](CHANGELOG.md) for release notes. Source headers retain author and development dates.

Copyright © 2026 Scott Sheen. No open-source license has been selected for this project. Public availability alone does not grant permission to reuse or redistribute the source. Third-party packages remain governed by their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Contributing

Use the existing formatting and measurement conventions in [AGENTS.md](AGENTS.md). Include the app version, Windows version and reproduction steps in bug reports. Review diagnostic files for personal information before sharing them.

## Installation

See [install/README.md](install/README.md) for prerequisite checks, per-user installation, folder placement, updates and removal. The install folder contains setup scripts; compiled payloads are supplied separately in a binary release.
