# Third-party dependencies

Power Monitor uses the following NuGet packages. License identifiers below come from the installed package metadata. These packages are not authored by Scott Sheen.

| Package | Version | License |
| --- | --- | --- |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.21 | MIT |
| System.Management | 10.0.2 | MIT |
| Microsoft.Data.Sqlite | 8.0.11 | MIT |
| LibreHardwareMonitorLib | 0.9.6 | MPL-2.0 |
| Microsoft.Toolkit.Uwp.Notifications | 7.1.3 | MIT |

Package references and exact direct versions are declared in PowerMonitor.csproj. Restore also brings in transitive dependencies, including SQLite native components. Their licenses and notices remain applicable.

Before distributing compiled binaries, include the required license and copyright notices for the complete dependency set. This source preparation does not supply a binary release or claim a completed binary-distribution license audit. LibreHardwareMonitor's MPL-2.0 obligations apply to its covered files; this repository does not modify or vendor that library's source.
