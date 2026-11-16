# Pagefile Monitor 1.1 — October 17, 2026

This maintenance release keeps the TMOG-inspired interface and .NET Framework 4.8 target.

## Reliability changes

- Replayed samples and backward clock adjustments cannot add duplicate workload-coverage time. A persistent high-water timestamp survives service restarts. Previously collected coverage is retained, not retroactively rewritten.
- The dashboard reports the service version, identifies older services, flags samples older than ten seconds, and catches failures of the offline Windows preview.
- Stale or unhealthy collection cannot be presented as an actionable recommendation.
- Local response writes have timeouts so a client that stops reading cannot hold the pipe indefinitely.
- Invalid empty/null page-file paths return a blocked result instead of throwing during duplicate checks.
- Settings includes **Update installed service**. The elevated updater backs up installed binaries, verifies copied hashes, starts and verifies the new version, and restores the previous files if verification fails. An unconfigured commerce file does not overwrite an existing store configuration.

## Updating and recovery

Close dashboards launched from Program Files. Open the new release folder's dashboard and choose Settings → Update installed service. Approve Windows' administrator prompt. The release folder must include all executable, DLL, configuration and commerce files. The updater refuses a pending page-file transaction and cannot run from the destination directory.

Updates pause collection briefly. Existing measurements, workload history, policy, license and page-file backups stay under ProgramData. The application does not resize a page file or restart Windows as part of an update.

Previous binaries and update-status.txt are retained under `C:\Program Files\PagefileMonitor\updates\<timestamp-id>`. If power loss interrupts a file update, stop the service using an administrator Services console, copy the backed-up files from that one update directory into the installation directory, and start the service. Do not overwrite the ProgramData telemetry or configuration backups. Automatic rollback handles caught failures; power-loss recovery is manual and has not been tested on this host.

## Verified on this machine

- Release build: zero warnings/errors; 38 automated tests passed, including duplicate coverage and update rollback tests.
- Installed service updated successfully from the old build to 1.1.0.0. Installed service executable hash matches the release. LocalSystem identity and automatic start retained.
- Three live IPC tests passed against the updated service: live sample, history retrieval and refusal of non-administrator configuration writes.
- Live Overview and retained History graph visually inspected. The dashboard displays the updated service version and healthy monitoring.
- Existing database integrity check returned `ok`; over 175 valid hours retained. The read-only integrity audit briefly contended with logging and produced a recorded lock/gap. Collection recovered; the audit did not change observations or workload labels.
- No host page-file configuration change or Windows restart performed. Automatic management remains enabled; the active allocation observed before and after update was 28,447 MB.

## Remaining limits

No light, normal or heavy sessions have been marked. A historical Windows resource-exhaustion event also blocks an actionable recommendation. These gates were retained. A seven-day calendar span alone is not sufficient.

Actual resize, relocation, reboot verification and offline settings restoration still need the Windows VM acceptance checklist; Hyper-V enumeration remains denied in this session. Lemon Squeezy store/product IDs have not been supplied in this project, so Pro checkout and live licensing remain unconfigured and unverified. Monitoring, history, recommendations and recovery do not require Pro.

Short collector performance measurements are recorded separately in `validation/collector-1.1.json`; they are not an overnight performance guarantee.

Microsoft confirms .NET Framework 4.8/4.8.1 compatibility with Windows 11: https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements
