# Pagefile Monitor 1.2.0

A Windows 11 desktop dashboard and background service that measures memory demand, explains fixed page-file recommendations, and provides administrator-controlled changes and recovery.

**Existing installation:** use Settings → Update installed service from the new release folder. See the [1.2.0](docs/UPDATE-1.2.0.md), [1.1.2](docs/UPDATE-1.1.2.md) and [1.1.1](docs/UPDATE-1.1.1.md) update notes and the earlier [1.1 maintenance and recovery notes](docs/UPDATE-1.1.md).

**Start here:** open `release\PagefileMonitor.exe`, then choose **Install monitoring service**. Accept the administrator prompt yourself. Installation starts measurement at Windows startup; it does not change your page-file settings. Keep all files in the release folder together, including commerce.json.

The app is built for Windows x64 and .NET Framework 4.8, already present on this computer. Free monitoring requires no account. Optional Pro licensing uses Gumroad; memory telemetry stays local. There is no third-party driver or TMOG dependency. Windows' own `winsqlite3.dll` supplies SQLite. The executables are not code-signed.

## How to use it

1. Open the dashboard and install its service. Installation copies binaries to `C:\Program Files\PagefileMonitor` and stores data under `C:\ProgramData\PagefileMonitor`.
2. Use your computer normally. Mark **Light**, **Normal**, or **Heavy** while the corresponding workload is running; choose **End marker** afterward. These are your descriptions, not classifications inferred from CPU usage.
3. After seven days, at least 24 hours of valid samples, and the required marked sessions, confirm that the heavy sessions represent your demanding work. The confirmation is refused until at least 30 minutes of heavy workload has been marked. If Windows recorded resource-exhaustion events, find out what caused them, then choose **Record exhaustion review** on the Recommendations tab; any later event blocks recommendations again.
4. Review the proposed allocation, headroom, target drives, disk reserves, and any blocking reasons. **Review and apply** invokes an elevated helper. It refuses stale or incomplete recommendations.
5. Restart manually when convenient. The service checks actual allocation after boot. A relocation keeps the old allocation for the first stage; use **Finish verified relocation** only after the new destination is verified, then restart again.
6. Optionally enable **Auto-grow** after the first reviewed allocation is verified. The default total ceiling is 64 GiB. Shrinking and moving always require review.

Closing the dashboard does not stop an installed service. **Minimize to tray** hides the window and leaves a tray icon. Reopen the installed dashboard from `C:\Program Files\PagefileMonitor\PagefileMonitor.exe`.

## What it measures

| Interval | Measurements |
|---|---|
| 1 second | Commit, commit limit, peak since boot, usable/available RAM, cache, paged/nonpaged kernel pools |
| 5 seconds | CPU, modified-memory backlog, hard-fault pages in, pages out, page-file usage percentage, per-volume disk latency/throughput/queue/transfers |
| 30 seconds | Top 20 processes by private bytes: name, PID, available start time, private bytes, working set |
| 60 seconds | Page-file settings and active allocations/usage, drive capacity and identity, crash-dump configuration, resource-exhaustion events |

Hard faults can include executable and mapped-file reads; they are not presented as page-file reads. Process private bytes are diagnostic context, not summed to estimate system commit. Inaccessible processes are skipped, unavailable counters are null, and sampling gaps do not earn workload-coverage time. Markers reset when the service restarts and after any interruption in collection longer than five minutes, such as sleep or hibernation.

`telemetry.sqlite` retains seven days of detail and 90 days of minute summaries. Maxima survive raw-data pruning. The database is limited to about 430 MiB, leaving room within the 512 MiB telemetry budget for transaction overhead. Samples are read once a second in memory and written in ten-second batches by a separate writer, so a busy disk delays storage instead of interrupting measurement; an abrupt stop can lose the unflushed seconds. If storage stays unavailable, up to ten minutes of samples are held for retry. Logs and measurements are local. Names and memory totals are collected, but not document contents, window titles or command lines.

History CSV export includes the latest 1,440 minute records; the database retains the longer history. The report includes the current settings, coverage, and recommendation. Backups are retained separately and are not automatically pruned.

## How recommendations work

The policy uses the larger of observed peak commit and boot peak, adds the greater of 4 GiB or 20% headroom, subtracts usable RAM, and rounds up to GiB with a 2 GiB minimum. Dump backing can increase that amount. This is a transparent engineering estimate, not a universal Windows formula or guarantee for future workloads.

The app preserves your crash-dump setting. It supports no-dump, small-dump, and complete-dump sizing. Small dumps retain a 1 GiB boot-volume file when paging moves elsewhere. Complete-dump backing uses RAM rounded up to GiB plus 257 MiB. Kernel/automatic/active dump configurations and dedicated dump files block application until independently resolved; this version does not silently change those settings.

Internal NTFS SSDs are evaluated. Unknown storage identities are excluded. Existing suitable placement is preferred, followed by measured latency under activity and free space. Final allocations must leave at least 20 GiB or 10% of each target volume, whichever is greater. A temporary relocation stage may retain an unchanged existing file on a low-space drive; it must have space for the new destination without relying on the old file's future removal. Boot availability is verified by actual page-file allocation after restart.

Automatic growth requires a reviewed, verified fixed configuration, unchanged configuration fingerprint, complete evidence, approved paths, adequate space, at least 2 GiB growth, and 24 hours since the previous change. Pending changes, collection failures, outside edits, and unreviewed exhaustion events block automatic action. No automatic restart occurs. An observed memory crisis produces evidence; a pending page-file change does not resolve an immediate out-of-memory condition.

## Build and test in Visual Studio 2022 Professional

Open `PagefileMonitor.sln`. Select **Release / x64** and set **PagefileMonitor.Desktop** as the startup project. Install the **.NET desktop development** workload and .NET Framework 4.8 targeting pack if building on another computer.

From a PowerShell terminal in this folder, run:

```powershell
.\build.ps1
```

The script uses the .NET SDK/MSBuild, runs the dependency-free test executable, and copies the release files together. No NuGet runtime packages are required. The solution contains Core, Desktop, Service, Recovery and Tests projects.

Read-only collector validation without installation:

```powershell
.\release\PagefileMonitor.Service.exe --measure "$env:TEMP\PagefileMonitor-check" --seconds 180
```

Add `--serve` to expose the local dashboard connection during this temporary run. Settings writes are rejected in measurement mode. Stop other instances first; the named pipe has one application-wide name. This mode stores test data in the supplied directory, never in the installed service data folder unless you explicitly choose it.

## Recovery and removal

See [Recovery guide](docs/RECOVERY.md) for backups, offline restoration and manual settings. See [Architecture](docs/ARCHITECTURE.md) for interfaces and implementation choices, and [Validation results](validation/RESULTS.md) for exactly what was tested.

**Remove monitoring service** stops and removes the service, retaining data and recovery binaries. It does not undo page-file settings. Restore your chosen settings and verify after restart before removing the service if you want to revert them.

## Validation limits

Compilation, automated tests, live read-only collection, local communication, and dashboard inspection were exercised on this machine; the latest run is recorded in [validation/RESULTS-2026-11-15.md](validation/RESULTS-2026-11-15.md). The service has run installed as LocalSystem on this computer since October 9, 2026. Privileged Windows page-file writes and reboot recovery still require the VM acceptance procedure in `docs/VM-ACCEPTANCE.md`. Hyper-V was present but each session, including November 15, 2026, was denied VM access. No page-file setting was changed during development. Do not interpret the simulated transition tests as evidence of an actual Windows resize/reboot test.

## References

- [Microsoft page-file sizing guidance](https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/how-to-determine-the-appropriate-page-file-size-for-64-bit-versions-of-windows)
- [GetPerformanceInfo data and boot peak](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-performance_information)
- [Configured versus active page-file settings](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-pagefilesetting)
- [Language-neutral performance counters](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcountera)

## Theme and editions

The navy/cyan theme credits Dave Plummer and TMOG for inspiration. Free includes monitoring, recommendations, manual changes and recovery. Pro adds optional auto-grow. Sales are not configured yet. See [Gumroad licensing](docs/LICENSING.md) for setup, updating an installed service and validation limits.


