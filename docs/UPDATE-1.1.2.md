# Pagefile Monitor 1.1.2 — November 16, 2026

One dashboard fix on top of 1.1.1. The service, recovery tool and data formats are unchanged apart from the version number.

## Fix

- **The Light, Normal, Heavy and End marker buttons failed with "The calling thread cannot access this object because a different thread owns it."** The button's tag was read on a background thread. This defect was present in every earlier build, so no workload session could be marked from the dashboard; it is why the installed telemetry shows zero light, normal and heavy minutes after 37 days. The marker is now read before the background request starts.

## Verified on November 16, 2026

- Release x64 build: 0 warnings, 0 errors; 43 automated tests passed (`validation/build-and-tests-2026-11-16.txt`).
- With the 1.1.2 dashboard connected to the installed 1.1.1 service, **End marker** was invoked through UI Automation while the marker was already "unmarked". No error dialog appeared and the service logged `marker | unmarked`. The Light, Normal and Heavy buttons share the same handler; they were not pressed, because marking a workload is the owner's statement about what is running.

## Updating

The markers work as soon as you open `release\PagefileMonitor.exe` from this folder; the installed 1.1.1 service accepts them. To also replace the installed dashboard copy and clear the "Service update available" notice, use **Settings → Update installed service**. See `UPDATE-1.1.1.md` for the 1.1.1 changes and what remains unvalidated; nothing in that list changed.
