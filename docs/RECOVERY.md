# Recovery guide

## Backups

`C:\ProgramData\PagefileMonitor\backups\original.json` and `original.txt` preserve the first complete configuration seen by the installed service. Every attempted change also receives a timestamped JSON/text pair. JSON is used by recovery; text is readable without the app. They include automatic management, configured entries, active allocations, raw paging/dump values and drive identities. They are configuration backups, not memory dumps or disk-space reservations.

The app restores page-file settings only. It records crash-dump settings for context but does not write them. If you changed dump settings separately, review their backing requirements before restoring an older allocation.

## Dashboard recovery

Use **Changes & Recovery**:

- **Restore original:** restore the baseline page-file configuration.
- **Restore previous:** restore the state immediately before the most recent attempted change. This is not necessarily the original configuration.
- **Return to system-managed:** let Windows manage sizing again.
- **Acknowledge outside change:** accept the configuration you changed outside the app; automation remains off.

Restoration checks drive identities and available space. Fixed restorations leave at least 1 GiB free; automatic restoration requires at least 1 GiB available on a suitable drive but cannot guarantee Windows' future chosen allocation. Backups with per-drive automatic `0/0` entries are preserved as automatic entries. Verify the result after reboot.

Every change, including a restore, creates a new backup and journal. The app never deletes `pagefile.sys` directly and never restarts Windows.

## If the service or dashboard is unavailable

Open **PowerShell as administrator**. These commands change page-file configuration when executed:

```powershell
Stop-Service PagefileMonitor -ErrorAction SilentlyContinue
& 'C:\Program Files\PagefileMonitor\PagefileMonitor.Recovery.exe' --restore-original --offline
```

Or use `--restore-previous --offline`, or `--system-managed --offline`. Offline mode refuses to run if the service is still active. Restart manually after successful configuration, then restart/reinstall the monitor service and verify the result. These commands can be used in Safe Mode if the required WMI services are available.

If WMI is unavailable, read `original.txt`, open **System Properties → Advanced → Performance Settings → Advanced → Virtual memory**, and restore automatic management or the recorded entries manually. Do not disable all page files as an intermediate step.

An interrupted `writing` journal pauses automation. A detected write failure attempts to restore the previous configuration and records `rolled-back` or `rollback-failed`. Failure after reboot records `verification-failed` and requires review. The app cannot recover a computer that cannot boot; use Windows recovery tools and the readable backup in that case.

## Remove the service

Use the dashboard button or, in an administrator terminal:

```powershell
& 'C:\Program Files\PagefileMonitor\PagefileMonitor.Recovery.exe' --uninstall
```

This leaves the installed recovery tools and ProgramData files available. Restore/verify settings first if you want to undo them. Copy backups elsewhere before manually removing the retained directories.
