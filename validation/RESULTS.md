# Validation results — October 5, 2026

## Completed

- Release x64 solution build targeting .NET Framework 4.8: **zero warnings, zero errors**.
- **31 automated tests passed.** Coverage includes sizing/headroom, required learning coverage, boot peaks, dump backing, disk reserves, temporary relocation capacity, invalid paths, auto-grow eligibility, privilege checks, backups, interrupted/failed writes, rollback, outside changes, post-boot state transitions, restored automatic entries, replacement-drive rejection, SQLite retention, full-database errors and Unicode data.
- **Three live IPC checks passed:** status versus native memory readings, history CSV retrieval, and rejection of settings writes from the test client.
- Read-only collection successfully retrieved all four SSDs, native memory measurements, page-file usage and PDH counters. No synthetic observations were counted as a completed learning period.
- Desktop executable launched successfully. Overview and History were visually inspected, including live values, readable controls, and a plotted history trace. Initial contrast issues were corrected.
- Recovery executable launches and prints its command reference.

## Measured collector overhead

| Run | Elapsed | Average CPU, normalized across logical CPUs | Peak working set |
|---|---:|---:|---:|
| Extended measurement, including startup | 184.4 seconds | 0.549% | 47.2 MiB |
| Final UI/IPC session, including startup | 93.7 seconds | 0.617% | 50.3 MiB |

Both runs met the **under 1% average CPU / under 100 MiB memory** targets for the collector in these short observations. They do not establish long-term maxima, and the desktop UI is a separate process. The original one-minute measurement was 1.41% CPU; avoiding a duplicate startup inventory and reading start times only for the top processes improved it.

Machine-readable evidence: `live-measurement.json`, `ui-session.json`, `live-ipc-tests.txt`, and `build-and-tests.txt`. Measurements include local application names and disk identities; review these files before sharing them publicly.

## Host settings preserved

The final inspection still reports automatic management enabled and the original active `C:\pagefile.sys` allocation of **18,252 MB**. The production service was not installed, no page-file write was executed, and Windows was not restarted. Temporary collectors ran in explicitly read-only mode and exited after their test durations.

## Not yet validated

Hyper-V's service is present, but VM enumeration returned an authorization error. Consequently, actual LocalSystem installation, WMI page-file writes, restart verification, physical drive relocation and offline recovery have **not** been exercised in a Windows VM. Transaction tests use an in-memory configuration backend. The VM acceptance checklist is supplied in `docs/VM-ACCEPTANCE.md`; those results must not be inferred from the passing unit tests.

Seven-day workload coverage, overnight overhead, real disk-full handling and non-English Windows are also pending extended acceptance. The app deliberately leaves recommendations provisional until real coverage requirements are satisfied.
