# Pagefile Monitor 1.1.1 — November 15, 2026

This maintenance release fixes defects found by reading the installed 1.1.0 service's own telemetry after 37 days of collection. The interface, .NET Framework 4.8 target, data formats and safety gates are unchanged. Existing telemetry, policy, backups and license data are read as they are; nothing is migrated or rewritten.

## What the installed 1.1.0 data showed

- **3,823 collection gaps** were logged between October 9 and November 15. On quiet days there were a handful; on November 2 and 3 there were 1,141 and 1,461, and those days lost 11–14% of coverage. The gaps repeated about every 66 seconds for hours at a time, which is the once-a-minute housekeeping pass holding the sampler's lock while it wrote to a busy disk.
- **About 1% of every day was never credited** even without gaps (typically 85,400–85,700 valid seconds out of 86,400). Each one-second tick actually takes about 1.012 seconds, and the extra milliseconds were rounded away.
- **Thirteen "boot boundary" events were logged, but several had no restart behind them.** Windows moves its reported boot time by a few seconds whenever the clock is corrected. The service treated that as a restart and cleared the workload marker.
- **Fifteen resource-exhaustion events** are on record (October 6, and October 29–31), not one. The application blocked recommendations for them, as intended, but offered no way to record that they had been reviewed.
- **"Heavy sessions confirmed" was set in the policy although no heavy session has ever been marked** (heavy coverage is zero seconds). The confirm action was recorded on October 9 and October 20. Other gates still blocked the recommendation, so nothing was applied.
- Three service starts read an empty Windows inventory (boot time 0) and waited a full minute before trying again.

## Fixes

- **Sampling no longer waits on the disk.** The one-second sampler now only reads memory counters and keeps them in memory. A separate writer stores samples, counter detail and events in one transaction every ten seconds. Housekeeping (pruning, snapshot logging, latency averaging, event-log reads) runs outside the sampler's lock; pruning runs every ten minutes instead of every minute and releases the database between batches. If storage is unavailable, up to ten minutes of samples are kept for retry.
- **Coverage is credited from a monotonic clock with the remainder carried forward**, so timer drift is neither lost nor invented. Stalls, suspends and clock steps in either direction are still excluded and logged as gaps.
- **A clock correction is no longer mistaken for a restart.** A pending page-file change is verified only when the boot time moved by more than 30 seconds and system uptime is shorter than the age of the change. The workload marker is no longer cleared by clock corrections; it is cleared after an interruption in collection longer than five minutes (sleep or hibernation).
- **Heavy-session confirmation needs heavy sessions.** The service refuses the confirmation until 30 minutes of heavy workload is marked, and records how much heavy time existed when it was given. A confirmation saved by an earlier version carries no such record and must be given again after real heavy sessions exist. The installed policy file is not edited by the update.
- **Exhaustion events can be reviewed.** The Recommendations tab shows how many events Windows reported in the last 90 days and when the latest occurred. **Record exhaustion review** (administrator prompt) records that events up to that one were reviewed. A newer event blocks recommendations again. The request is refused if events changed since they were displayed.
- A failed first inventory is retried every five seconds instead of after a minute; the original-settings backup is taken on the first healthy inventory.
- History and event-log reads from the dashboard no longer hold the sampler's lock. The history graph parses numbers independent of the Windows regional format.

Pro, Lemon Squeezy and update/rollback behaviour are unchanged. `commerce.json` still contains zero IDs.

## Updating

Close any dashboard opened from Program Files. Open `release\PagefileMonitor.exe` from this folder, go to **Settings → Update installed service**, and approve the administrator prompt. Collection pauses for a few seconds. The previous 1.1.0 binaries are kept under `C:\Program Files\PagefileMonitor\updates\<timestamp-id>` and restored automatically if the new service cannot be started and verified. The update does not change page-file settings and does not restart Windows.

Rolling back to 1.1.0 by hand is safe: 1.1.0 ignores the two new policy fields.

## After updating, expect

- The dashboard header to read `Service connected • 1.1.1.0`.
- The Recommendations tab to list three blocking reasons until you act: marked sessions, heavy confirmation, and the exhaustion review.
- Far fewer `gap` lines in the change log. Compare a few days of the new version against the per-day counts in `validation/RESULTS-2026-11-15.md`; that comparison is the real test of the sampling fix, because it could not be reproduced on demand here.

## Not validated

Actual resize, relocation, restart verification and offline restoration have still not been exercised: Hyper-V enumeration was denied again on November 15. The restart-detection change is covered by automated tests with a simulated backend only. Lemon Squeezy checkout and live licensing remain unconfigured and untested. See `validation/RESULTS-2026-11-15.md`.
