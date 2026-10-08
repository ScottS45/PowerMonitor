# Power Monitor

Windows 10 / .NET 8 / WinForms tray app. It monitors apps on AC power and on battery, keeps history, and separates physical battery measurements from activity attribution.

## Run the updated app

Exit the original running app using its tray menu, then open:

`bin/Release/net8.0-windows10.0.17763.0/PowerMonitor.exe`

Keep the entire folder together. This framework-dependent package uses the .NET 8 Desktop Runtime, already installed on this laptop.
Use this original application folder for future updates; do not create versioned package copies.
Closing the main window hides it in the tray unless Close window to tray is disabled.

## AC diagnostics and modern UI

- Dark sidebar navigation, colored summary cards, resizable layouts and readable app tables.
- Live defaults to CPU/GPU activity on AC and measured battery discharge on battery. Choose a graph mode and time range manually to compare.
- Processes ranks likely contributors by CPU activity, busiest GPU engine and I/O; use the search box and Impact/CPU/GPU/I/O sort.
- Browser and other subprocesses are grouped by executable path when accessible; otherwise by process name. Different installations can appear separately.
- Select an app for the reason behind its rating, full path, PIDs, working sets and GPU engine details. Protected process metadata may be unavailable.
- Native process CPU deltas are normalized to total machine CPU capacity. Protected processes use Windows performance counter CPU/I/O/working-set data when available.
- Windows GPU engine counters are sampled using PDH. Adapter inventory comes from DXGI once per reader startup, with no D3D device creation.
- GPU attribution matches counter LUIDs to actual adapters; physical node numbers are not global GPU numbers. Unresolved mappings remain explicitly unresolved.
- GPU percentages use the busiest engine, summing participating PIDs on that same adapter/physical-node/engine and capping at 100%. Percentages across engines/adapters are not added.
- I/O includes read/write/other file, network and device traffic. It is not disk-only throughput.
- Working sets can double-count shared pages when summed.
- High activity may be an intentional workload. Ratings indicate activity, not a fault or exact app wattage.
- Historical samples record CPU/GPU activity and a compact top-six contributor summary on AC and battery.
- Start/Pause monitoring is available on Live and in the tray. Resume starts fresh rate baselines.

## Physical power measurements

BatteryStatus in root/wmi provides authoritative battery discharge, converted from mW to W.
Battery percentage/source/Battery Saver use GetSystemPowerStatus.
Remaining Wh, voltage, charge rate, design/full-charge capacity and firmware cycle count are shown when valid.
AC charging watts are power entering the battery, not total laptop/wall consumption. Total AC watts and exact app watts are not invented.
NVIDIA hardware watts, clocks and sleeping state remain unknown. No NVML initialization, nvidia-smi or GPU hardware sensor polling is performed.
CPU hardware sensors remain opt-in and may require driver access. Unverified zero package readings stay unavailable.
Display topology identifies internal refresh and external outputs; metadata and power plan refresh every minute.
XMG JSON is read only: 0 Off, 1 On, 2 Auto. MUX routing is separate. No XMG file or device setting is written.

## Settings and data

Settings now has normal controls for intervals, hardware probing, notifications, tray behavior, thresholds and exclusions.
Advanced settings remain editable in:
`%LOCALAPPDATA%/LaptopPowerMonitor/settings.json`.

Defaults:
- Live: 5 seconds.
- App activity: 10 seconds, enabled on AC and battery.
- GPU Windows activity counters: enabled; turn off if not needed.
- History: 20 seconds, batches of three or on exit.
- Retention: 90 days.
- Alerts: battery draw >20 W for 30 s, >30 W for 30 s, >45 W for 15 s.
- Clear thresholds: 17/25/40 W for 30 s.
- Unplug grace: 30 s. Repeat: 600 s.
- No automatic corrective actions. AC activity alone does not trigger battery-watt alerts.

App activity interval must be at least 5 seconds and the live interval.
CPU sensor changes require restart. Retention cleanup occurs on database open.
Excluding an app suppresses battery-drain notifications while it is running; it still appears in measurements and history.
Power Options opens Windows settings only after a user click; it applies no settings.
Test notification lets you verify Windows notification delivery manually. Native toast supports Details/Power Options/Dismiss, with tray notification fallback.

`history.sqlite` holds samples, context and alerts. Existing Phase 1 history stays readable; older samples naturally lack app/GPU activity.
`monitor.log` logs capability/errors with 1 MB rotation.
The live graph holds up to 10,800 samples (15 hours at the default interval). Older data remains in SQLite.
History only sees committed batches, so the latest sample can lag up to a minute.
GPU traces leave gaps when cached activity is more than 30 seconds old. With longer activity intervals, the table shows its explicit update timestamp; graph gaps do not imply zero usage.

## Attribution rules

Very high: CPU >=15%, GPU >=40%, or I/O >=25 MiB/s.
High: CPU >=5%, GPU >=15%, or I/O >=10 MiB/s.
Medium: CPU >=1%, GPU >=5%, or I/O >=2 MiB/s.
Low: CPU >=0.2%, GPU >=1%, or I/O >=0.2 MiB/s.
Below those levels: Very low when enough data is available, otherwise Unknown.
Sorting Impact uses a transparent activity score: max(CPU*3, GPU, min(100,I/O*4)). This is not an energy estimate.
Inspect the details and individual CPU/GPU/I/O sort orders before treating an app as a power problem.

## Build and verify

```powershell
dotnet build PowerMonitor.csproj -c Release
dotnet publish PowerMonitor.csproj -c Release -r win-x64 --self-contained false -o bin/Release/net8.0-windows10.0.17763.0
Start-Process ./bin/Release/net8.0-windows10.0.17763.0/PowerMonitor.exe -ArgumentList '--self-test --output <repository>\self-test-v2.txt' -Wait
Start-Process ./bin/Release/net8.0-windows10.0.17763.0/PowerMonitor.exe -ArgumentList '--diagnose --activity --seconds 60 --output <repository>\activity-v2-diagnostics.json' -Wait
```

Diagnostics only reads system data. Add --no-gpu to compare overhead without GPU counters. Add --cpu-sensors for optional CPU-only sensor probing.
--ui-smoke --output <directory> uses isolated history/configuration, disables test alerts, captures all pages and compact layouts, then exits.

Self-tests cover alerts, hysteresis, unknown values, SQLite batches, PID reuse, machine-normalized CPU, browser grouping, LUID mapping, unresolved physical nodes, busiest-engine aggregation and AC contributor persistence.
UI captures are in ui-v2-smoke. Read-only AC diagnostics are in activity-v2-diagnostics.json.

## Validation

Detected this XMG NEO A25, AMD 610M and RTX 5070 Ti. Real AC activity mapped Opera to AMD; Desktop Window Manager used both GPUs with three external displays connected. Protected-process CPU fallback also exposed Defender activity without requiring the UI to run as administrator.
The one-minute activity diagnostic reported approximately 0.038% mean process CPU after initialization. App activity collection averaged approximately 385 ms, mostly waiting on the bulk Windows performance counter fallback; short UI checks reported roughly 99 MB working set. Self CPU excludes work done by Windows counter-provider processes. These are short overhead checks, not a measurement of added battery watts.
No power plan, GPU device, XMG setting or display mode was changed during development.
Unplugged power readings and actual toast delivery still need live validation. GPU power-gating effects of any Windows/driver telemetry path need observation on battery; no sleeping state is inferred.

## Architecture

Models.cs: measured samples, contributor summaries, preferences, future action contracts.
Telemetry.cs / Native.cs: battery, OS, CPU hardware, displays and XMG detection.
ProcessActivity.cs: native process sampling, PDH GPU counters, DXGI LUID inventory and grouping.
ProcessCounterFallback.cs: bulk Windows counters for protected processes.
MonitorService.cs: one serialized background sampling loop, slow app interval, history batches and clean shutdown.
MainForm.cs / ProcessesView.cs / Theme.cs / PowerGraph.cs: lightweight native desktop UI.
HistoryStore.cs: SQLite with nullable readings and JSON context.
Program.cs / SelfTests.cs: offline checks and diagnostic/render modes.

Future work: confirmed user-selected power actions with before/after measurements, SRUM/E3 historical attribution, richer session analysis, installer and startup registration. No service is installed.

## Primary API references

- [Process CPU times](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes)
- [Process I/O counters](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprocessiocounters)
- [PDH wildcard samples](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhgetformattedcounterarrayw)
- [DXGI adapter inventory](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgifactory1-enumadapters1)
- [Windows GPU utilization](https://devblogs.microsoft.com/directx/gpus-in-the-task-manager/)
- [Battery WMI status](https://learn.microsoft.com/en-us/windows/win32/api/batclass/ns-batclass-battery_wmi_status)

## Source readability and icon (2026-10-02)

Author: Scott Sheen (with ChatGpt). C# files include dated purpose headers, expanded formatting, and comments on measurement semantics, thresholds, GPU identity, threading, and resource ownership. AGENTS.md and .editorconfig preserve these conventions. The custom multi-resolution battery/lightning icon is embedded in the executable and used for the window and notification tray; assets/PowerMonitor.png is the large preview.

Suggested next features: app activity timelines and rolling averages; GPU-to-display mapping; before/after measurement comparisons; battery-session energy and runtime trends. Compare measured battery watts on battery and activity counters on AC.



## Battery charge and lower-overhead monitoring (2026-10-02)

Author: Scott Sheen (with ChatGpt).

The Battery page reports charging state, estimated mA, monitored discharge/charge
totals in mAh, covered observation time, and retained design capacity (health).
mAh totals integrate current using measured power and voltage. They reset on app
restart; pauses, sleep gaps, source transitions, and missing readings are skipped.
Remaining mAh is an equivalent at the current voltage, not nominal pack capacity.
Relative battery units are withheld from absolute-unit estimates. Capacity health
does not diagnose charging hardware. On AC / not charging can reflect a charge limit.

Low-power mode is enabled by default, including when opening older settings files:
live sampling is at least 10 seconds, process sampling at least 30 seconds, and
history recording at least 60 seconds. It skips GPU PDH counters, CPU hardware
sensor updates, and formatted WMI protected-process counters. Native app CPU and
I/O monitoring still works on AC and battery. Hidden/minimized windows skip desktop
rendering. Pausing monitoring releases GPU counter resources.

For GPU diagnostics, disable Low-power mode in Settings. Protected-process WMI
counters are separately opt-in because work in WmiPrvSE is not included in the
monitor's own CPU indicator. CPU hardware sensors still require restart.

A local collector comparison measured roughly 410 ms per old app scan versus
14 ms for a new native-only scan. This is elapsed collector time, not watts, and
the comparison is from different sessions. Reduced polling further limits wakeups.
Total power improvement needs a stable battery-only or external-meter comparison;
AC battery charging power is not total laptop power.

Diagnostics: --diagnose --activity --no-gpu skips GPU queries. Add
--restricted-counters only when testing the protected-process WMI fallback.
Microsoft's unit definitions: https://learn.microsoft.com/en-us/windows/win32/power/battery-information-str
## Windows component energy accounting (v5.2.0, 2026-10-03)

Live includes estimated mAh beside each app, including at compact window sizes.
The battery card shows remaining Wh / equivalent mAh and full-charge Wh /
equivalent mAh on AC and battery. Battery also shows separately integrated Wh
and mAh consumed and added during contiguous measured intervals. Capacity mAh
uses the present pack voltage and is an equivalent, not a nominal capacity label.

Click Windows energy (admin) on Live and accept UAC to start a temporary read-only
ETW collector. No driver or service is installed and no power settings change.
The collector stops when this monitor exits; click the button again after restart.
Windows supplies estimates at its own cadence, usually around a minute. The
Processes details identify the source, averaging interval, age, and GPU coverage.
AC values are battery-equivalent charge for the application's modelled energy,
not charge actually drawn from the battery.

The collector consumes QueryStatsEnergyEstimate (event 37) from Microsoft's E3
provider. MergeStats is excluded because it represents the same energy. Component
millijoules convert to Wh by dividing by 3,600,000, then to mAh using pack voltage.
Each event has a capture sequence so refreshing the UI never accumulates it twice.
The first reporting interval is prorated to the capture lifetime. Energy survives
missing voltage as Wh; equivalent mAh stays unknown for that interval.

Executable identity is matched by full Windows/device path; packaged applications
are matched by package identity. Windows E3 reports app totals, so multi-PID app
energy is not falsely assigned to individual PIDs. E3 averages cannot be subtracted
from instantaneous battery watts to claim exact system/unattributed power.

On this laptop the verified E3 records use software models (Measured = 0), with
CPU and other component energy but no GPU/SoC energy observed during verification.
Discrete GPU energy, fans, idle costs, and conversion losses may be unaccounted
for. Accuracy is not calibrated or guaranteed. Existing activity-based estimates
remain explicitly labelled as legacy fallback for battery intervals without E3.
The previous fixed reserve is not applied to Windows E3 estimates.

Source documentation:
https://devblogs.microsoft.com/sustainable-software/measuring-your-application-power-and-carbon-impact-part-1/
https://learn.microsoft.com/en-us/windows-hardware/drivers/powermeter/rail_naming
## Hardware energy and battery calibration (v5.3.0, 2026-10-03)

All three integrations are now present. The existing executable folder is unchanged.

1. CPU energy counters: discover Windows EMI package meters and convert cumulative
   picowatt-hours and 100 ns timestamps into interval watts. Counter resets, missing
   drivers and unsupported interfaces stay unknown. The existing CPU package sensor
   is a fallback. No new hardware driver is installed. On this Windows 10 laptop,
   EMI had no package meter and the normal-user CPU sensor returned zero/unavailable.
2. GPU readings: load installed System32 NVML and ADLX libraries directly. NVIDIA
   board power and AMD driver-reported GPU chip power appear on Live and in history.
   A single vendor adapter is required; per-PID engine identity uses the existing
   DXGI LUID mapping. AMD APU power is diagnostic and is not added to CPU package
   energy. NVIDIA samples above the driver's maximum (+5% tolerance) are rejected.
   This laptop returned implausible transient NVIDIA samples during verification;
   these readings must not be treated as independently validated physical power.
3. Calibration: collect only actual battery discharge with fresh activity counters.
   Fit nonnegative baseline + CPU + GPU coefficients, keeping idle/system baseline
   separate. Require at least 60 samples, 300 observed battery seconds and varying
   CPU activity. The final 20% of samples are held back from fitting. Reject models
   with validation mean absolute error above max(3 W, 20% of mean battery draw).
   GPU effects cannot be separated without GPU variation. Hardware availability
   changes use separate datasets; old/unsupported samples do not become zero input.
   Profiles are tied to this machine and installed driver DLL versions and expire
   after seven days. The profile is saved in the existing app data folder.

Click **Hardware readings / calibration: toggle** on Live (or use Settings) to
start or stop the hardware diagnostic. It is off by default: polling GPU power can
wake or retain a GPU and alter battery drain. Low-power sampling remains enabled;
the explicit hardware diagnostic enables GPU activity mapping for attribution.
Battery calibration also works with ordinary CPU activity while hardware probes
are off. On AC it waits; charging watts never train a discharge model. At the
10-second default sampling cadence, 60 battery samples take about ten minutes.

The component model uses CPU shares and mapped GPU engine shares to allocate
estimated dynamic energy to individual PIDs, including browser subprocesses.
An observed idle hardware baseline remains unattributed; without a baseline the
hardware component is not presented as known app power. Wh and mAh integrate
valid contiguous intervals using measured battery voltage. AC mAh is equivalent
battery charge, not actual discharge. Pauses, missing inputs, retired/reused PIDs,
source transitions and sleep gaps do not create energy. Disk/network/fans/display
and unmapped activity can remain in system/unattributed power. A fitted model is
an estimate; held-out error is whole-laptop prediction error, not proof of each
PID's individual accuracy. A real discharge session is still needed to calibrate
this laptop; synthetic tests validate the algorithm only.

Validation: original self-tests plus hardware units/reset handling, synthetic
independent CPU/GPU recovery, held-out error, minimum data/variation, AC exclusion,
idle-power separation, APU overlap avoidance, PID mAh integration and gap handling;
isolated full/compact UI smoke captures; direct installed-driver probes.

## Complete CPU coverage (v5.3.1, 2026-10-03)

CPU collection now uses one native PDH query for every process, including protected
Windows processes such as Defender, Desktop Window Manager and system services.
There is no WMI formatted-process poll, elevation or new driver. Raw CPU time, PID
and creation-time counters come from the same query. Duplicate executable names
are preserved as separate instances within each collection; baselines are keyed
by PID and creation counter, not by unstable instance suffixes. Protected process
creation timestamps are accepted only when they represent plausible FILETIMEs.
Unavailable executable paths and memory/I/O fields remain unknown even when CPU
is available. Every process is represented; counters not independently available
are not labelled idle.

The CPU card and process CPU percentages share the same live interval (10 seconds
in low-power mode). The old independent process interval setting is retained for
configuration compatibility but no longer controls CPU sampling. Settings explains
that CPU rows follow the live interval and protected CPU counters are always used.
The first interval warms up before CPU percentages become available.

CPU not assigned to an individual process appears as **System / unattributed CPU**.
This aggregate can include interrupt/DPC time, exited processes and missing counters.
Its detail explains that it is not an executable; it has no invented PID or app
energy estimate. Live and Processes show total/listed/system coverage. Counter
timing discrepancies are explicitly reported if the process sum exceeds the total;
individual process readings are never rescaled just to force agreement. The Live
list no longer stops at eight groups, and group details show all PIDs rather than
only the first twenty. All lists remain scrollable.

Verified on this laptop: a matched 10.1-second sample reported 6.831% total CPU,
2.449% assigned to processes and 4.382% system/unattributed CPU. Defender was
individually measured at 1.608%; the counter collection took about 12 ms. Tests
cover raw CPU normalization, process identity/counter resets, residual accounting,
missing totals and repeated coverage updates. Native diagnostics verified browser
subprocesses remain distinct and protected CPU counters are present. Full/compact
UI smoke checks use isolated data.
Aligned CPU snapshots can finish shortly after the battery sample; calibration accepts this bounded collection skew (up to five seconds) while rejecting stale/future data. A regression check verifies fresh CPU sampling continues to train the model. CPU coverage tests and existing regressions: 95 passed.

## Sorting and system CPU expansion (v5.4, 2026-10-03)

Both Live and Processes default to CPU descending. Each list displays an up/down
arrow beside its active column. Click a column header to choose it; click it again
to reverse direction. CPU, GPU, watts, mAh and I/O sorting uses the underlying
numbers, not formatted text. Unknown values remain last in either direction.
The selected column/direction is reapplied on every update; CPU leaders therefore
move to the top as readings change. Processes' sort picker and clickable headers
stay synchronized. Expanded system children remain attached to their parent.

System / unattributed CPU starts collapsed independently in both lists. Click the
parent row to expand/collapse it. Windows' optional per-processor DPC and interrupt
raw timers provide measured components over the same CPU sampling interval.
The remainder stays Other / unresolved system CPU. Child percentages reconcile
to their parent's CPU subtotal, and they are not extra processes or extra CPU.
No PID or per-process energy is fabricated for these rows. If counters are absent,
they remain unknown. If they overlap/exceed the residual, the split stays unresolved
rather than being forced to fit. Driver names and finer kernel attribution require
an independent tracing session and are not inferred from these counters.

Expansion state survives updates. Child CPU rows follow the selected CPU direction;
their detail explains the measurement and limits. Regression checks cover numeric
sorting, changing leaders, direction toggles, unknown-last behavior, a single
visible sort indicator, subtotal reconciliation and expansion persistence. Isolated
UI smoke additionally exercises real header and row click handlers in both lists
and captures ascending, descending and expanded views. Laptop verification measured
3.759% system residual = 0.614% DPC + 0.121% interrupts + 3.023% unresolved CPU.
The version in the window title is 5.4; the assembly/package version is 5.4.0.
