# Windows VM acceptance checklist — not yet executed

Use a disposable Windows 11 x64 VM with .NET Framework 4.8, a checkpoint, and sufficient disk space. Perform these tests only in the VM. The host inspection session could not enumerate Hyper-V VMs due to permissions.

1. Build Release/x64 and run the test executable. Copy the release folder into the VM. Install through the dashboard; verify the service identity is LocalSystem, starts automatically, and records with no logged-in user. Confirm installation did not change automatic page-file management.
2. As the installing standard user, read status and mark sessions. Confirm policy/apply/restore requests require elevation. Verify other ordinary users and remote clients cannot write through the pipe or modify ProgramData/Program Files artifacts.
3. Collect the seven-day study or use an isolated development build with a test-only injected clock/evidence backend. Never weaken production readiness gates or distribute seeded data as actual measurements.
4. Review a fixed recommendation, apply it, confirm JSON/text backups and pending journal, restart, and verify WMI configuration versus actual page-file allocation. Compare committed/limit/peak measurements against Windows counters.
5. Enable growth after the verified first change. Test growth over 2 GiB, ceiling and reserve rejection, 24-hour cooldown, pending-change rejection, and mandatory review of any shrink/move.
6. Test relocation with a second eligible SSD presented to the VM. Confirm the old allocation remains through stage one, then verify the destination after boot before stage two. Test destination missing/unavailable at boot and insufficient temporary free space.
7. Exercise restore-original, restore-previous, and system-managed with the service running and offline with it stopped. Test original automatic and original fixed configurations, missing/replaced drives, insufficient space, and unavailable WMI.
8. Terminate the service during a write in the isolated test build. Verify the journal survives, automatic changes stop, and recovery remains usable. Simulate rejected WMI writes and rollback failures. Alter Windows settings externally and verify automation pauses.
9. Fill a separate small test volume hosting telemetry; verify bounded storage, visible logging errors, preserved backups, and recovery after space is freed. Test sleep/resume, clock adjustments, protected processes and non-English counter names. With a change pending restart, correct the clock by a few seconds and by several hours without restarting and confirm the pending change is neither verified nor failed; confirm a workload marker clears after resume from sleep.
10. Record service CPU and working set over representative workloads and an overnight run. Target under 1% average CPU and 100 MiB working set. Document any divergence from the short host measurement.

Store screenshots, configuration snapshots before/after boot, journal output and test results. Only mark this checklist complete after every applicable behavior is observed on Windows; fake-backend tests do not replace this acceptance run.
