# Pagefile Monitor 1.2.0 — November 16, 2026

A dashboard release. The service, recovery tool, data formats and every safety gate are unchanged apart from the version number.

## What is new in the dashboard

- **Workload banner.** A colour-coded badge in the header shows the active marker at all times, on every tab: green for Light, blue for Normal, orange for Heavy, grey when nothing is marked. A pulsing dot and a coloured strip across the top of the window make an active marker hard to miss. The badge also shows how long the dashboard has seen the marker active and how many minutes of that workload have been collected toward the minimum. The window title and tray tooltip name the active workload.
- **Marker buttons.** Each button carries its workload colour and the active one is filled.
- **Live memory graph.** Committed memory for roughly the last ten minutes, with dashed guides for the commit limit and usable RAM. The trace takes the colour of the marker each stretch was recorded under.
- **Pressure bar.** Turns orange at 70% of the commit limit and red at 85%.
- **Progress toward a recommendation.** One bar per requirement: days of learning, valid samples, light, normal and heavy minutes, heavy confirmation, and the exhaustion review, with a count of how many are complete.
- **Top private-memory users and drives** as bar charts. A drive bar turns orange when free space is below the reserve a page file must leave (20 GiB or 10%).
- **History graph.** Taller, filled, with a usable-RAM guide and shading over the minutes that were marked light, normal or heavy.

The "recording for" time counts from when this dashboard window saw the marker change. If the dashboard is opened while a marker is already active, it shows "Recording" without a duration.

## Verified on November 16, 2026

- Release x64 build: 0 warnings, 0 errors; 43 automated tests passed (`validation/build-and-tests-2026-11-16.txt`).
- The 1.2.0 dashboard was run against the installed 1.1.1 service and screenshots of the Overview and History tabs were inspected. The owner had a Light marker active at the time, so the green banner, filled Light button, progress count and history graph were seen with real data. The Normal and Heavy colours and the red pressure state were not seen live.

## Updating

Open `release\PagefileMonitor.exe` from this folder to use the new dashboard immediately. **Settings → Update installed service** replaces the installed copy as well. See `UPDATE-1.1.1.md` for what remains unvalidated; nothing in that list changed.
