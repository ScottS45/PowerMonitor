# Installation guide

Power Monitor 5.4.6 · Author: Scott Sheen · Updated: 2026-10-08

## What goes where

| Content | Location |
| --- | --- |
| Complete published application, including DLLs and dependency folders | Recommended: `%LOCALAPPDATA%\Programs\PowerMonitor` |
| Settings, SQLite history, logs and calibration | Created by the app in `%LOCALAPPDATA%\LaptopPowerMonitor` |
| C# source and project files | Keep in a separate development/repository folder |
| Optional shortcut | Point to `PowerMonitor.exe` in the installed folder |

Keep every published file and subfolder together. DLLs belong with the published app; do not put them in Windows or System32. Do not copy personal settings/history into a GitHub release. The installer does not add a service, startup registration or shortcuts.

## Prepare a release package (maintainer)

From the repository root, build one of these into `install/app`:

```powershell
# Smaller download; users need the x64 .NET 8 Desktop Runtime.
dotnet publish PowerMonitor.csproj -c Release -r win-x64 --self-contained false -o install/app

# Alternatively, publish into a fresh folder to bundle the runtime.
dotnet publish PowerMonitor.csproj -c Release -r win-x64 --self-contained true -o artifacts/standalone
```

For the standalone option, copy the entire fresh published folder to `install/app` in your release staging folder. Do not mix two publish outputs. Include this guide and both scripts alongside the `app` folder, plus the README, release notes and required third-party licenses/notices. Review dependency distribution requirements in `THIRD-PARTY-NOTICES.md` before shipping binaries.

Run the package's built-in self-tests as described in the root README before creating a binary release. `install/app` is ignored by Git: release binaries belong in a GitHub Release ZIP, while these instructions/scripts belong in the source repository. The source ZIP alone is not a compiled installer.

## Install (user)

1. Extract the entire release ZIP into a temporary folder.
2. Open PowerShell in its `install` folder and run:

```powershell
.\Check-Requirements.ps1
.\Install.ps1
```

Checks cover Windows 10 1809 or later, x64 architecture, core published files, and required .NET framework versions. A self-contained package uses its bundled runtime. The checker reads the runtime configuration and accepts an installed patch at least as new as the requested version within the same major/minor.

If the runtime is missing, install **.NET 8 Desktop Runtime for Windows x64** from [Microsoft's download page](https://dotnet.microsoft.com/en-us/download/dotnet/8.0), then rerun the check. The Desktop Runtime includes the base .NET Runtime. A newer major runtime alone does not satisfy this app's default .NET 8 requirement.

If Windows blocks a downloaded script, inspect it first and use the file's Properties > Unblock option when you trust its origin. Your organization's execution policy may require administrator assistance; the installer does not change policy.

Run `%LOCALAPPDATA%\Programs\PowerMonitor\PowerMonitor.exe`. Use ordinary user privileges first. Hardware energy diagnostics are optional and off by default; unavailable hardware does not prevent basic monitoring.

For an existing published folder elsewhere:

```powershell
.\Check-Requirements.ps1 -PackagePath 'C:\Downloads\PowerMonitor'
.\Install.ps1 -PackagePath 'C:\Downloads\PowerMonitor' -Destination 'C:\Tools\PowerMonitor' -WhatIf
# Remove -WhatIf to perform the copy after reviewing the destination.
```

Choose a dedicated folder you can write to. Installing under Program Files generally needs administrator privileges; the per-user default avoids that requirement. Custom .NET runtime locations are not automatically detected by this checker; it checks the registered/default x64 installation.

## Updates and removal

Exit through the tray menu before updating. Back up `%LOCALAPPDATA%\LaptopPowerMonitor` while the app is stopped if you want to preserve history. Copy a complete new package into the dedicated application folder, or install to a new empty folder for a clean update; the copy script does not remove obsolete files.

To uninstall, exit the app and delete its dedicated application folder and any shortcut you created. Keep the data folder for later use, or delete it separately if you intentionally want to erase settings/history. Never delete a shared parent folder such as `%LOCALAPPDATA%\Programs`.
