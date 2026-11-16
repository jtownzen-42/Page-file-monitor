# Architecture and operating contract

## Processes and privilege boundary

- `PagefileMonitor.exe`: WPF dashboard, ordinary user privileges, optional tray presence. Communicates over the local named pipe. It starts the Recovery helper with UAC for privileged requests.
- `PagefileMonitor.Service.exe`: ServiceBase host running as LocalSystem after explicit installation. Samples native PSAPI and language-neutral PDH counters; WMI inventory runs on a separate worker. Owns the SQLite database and change manager. Administrator-only operations enable WMI privileges for page-file updates.
- `PagefileMonitor.Recovery.exe`: installer, privileged pipe client and independent offline restoration. Fixed installation/data paths; installer protects directories for Administrators/SYSTEM with read access for the installing user. Service removal retains backups/binaries. No automatic restart or scheduled task.
- `PagefileMonitor.Core.dll`: models, native collectors, SQLite wrapper, deterministic recommendation policy, configuration transaction manager and pipe framing.

## Local interfaces

Pipe name: `PagefileMonitor.v1`. Four-byte little-endian length followed by UTF-8 JSON. Requests are capped at 64 KiB, replies at 8 MiB, and reads time out after ten seconds. The pipe ACL denies network clients and allows SYSTEM, Administrators and the installing user's SID. The service checks the client's impersonated token before any privileged command; a claimed boolean in JSON cannot grant administrator rights.

Read commands: `status`, `history`, `events`. Workload annotation: `mark` with `unmarked`, `light`, `normal`, or `heavy`. Elevated commands: `apply` with the current recommendation ID, `finish-move`, `restore-original`, `restore-previous`, `system-managed`, `confirm-heavy`, `acknowledge`, and `policy` with enabled flag and MB ceiling. The service generates allocations itself; clients cannot submit arbitrary file paths or shell commands.

`Snapshot` holds configured entries, active allocations, drive identities and raw relevant registry values. `Sample` holds measured bytes and marker. `Evidence` aggregates coverage and maxima. `Recommendation` records sizing assumptions, target allocations, blocking reasons and configuration fingerprint. `Policy` records reviewed paths, ceiling, cooldown and pause reason. `ChangeJournal` records backup name, desired configuration, pre-change boot, and progress.

## Persistence and recovery states

SQLite tables: `samples`, `minutes`, `detail`, `events`, `meta`. Minute summaries preserve peak committed and boot-peak values, current physical capacity and credited workload duration. UTC timestamps are used for storage; UI event times are local. A monotonic stopwatch detects sampling delays and excludes sleep/clock-change gaps. Raw SQL values are escaped through one quoting function; no SQL is accepted from IPC clients.

`policy.json`, `journal.json`, `owner.sid`, and `backups` live outside the telemetry database. JSON and text backup writes use replacement files and explicit flush. The original backup is never overwritten. Configuration changes are serialized with the service state lock.

Change states: `writing` → `pending-restart` → `verified`; relocation adds `stage-one-verified`, then a second reviewed transaction. Errors use `rolled-back`, `rollback-failed`, or `verification-failed`. Incomplete writes after process termination are detected and left for explicit recovery. No transaction is treated as active solely because its WMI write succeeded.

## Defaults and limitations

Learning: seven elapsed days, 24 valid hours, 30 light minutes, 120 normal minutes, 30 heavy minutes, and user confirmation. Inference uses a rolling retained window of up to 90 days; early boot peaks can precede app installation and are labeled. Resource-exhaustion events in the retained window block applying an estimate; the application does not assume that an observed peak is an upper bound on an allocation that failed.

Snapshot disk topology is refreshed every minute. A candidate is a uniquely identified internal NTFS SSD, not an arbitrary removable/mapped drive. Destination page-file creation after restart provides the final boot-availability test. Latency ranking uses up to 12,000 recent five-second records with at least one transfer/second; at least twelve valid active samples are needed. Absent latency data is labeled unknown.

Database writes stop crediting new coverage on failure; the monitor attempts pruning and retries rather than silently accepting lost data. The bounded in-memory queue retains at most 60 samples. Backups are deliberately outside the telemetry cap. Extended unattended operation and disk-space exhaustion must still be tested in a VM before relying on automatic changes.
