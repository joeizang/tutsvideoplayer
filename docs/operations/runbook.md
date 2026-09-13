# Planned deployment and operations runbook

This is a specification for the later implementation. No commands here have been executed to build, deploy, migrate, or process the user's library. Concrete executable names and scripts must be supplied and verified in the implementation milestones.

## Configuration contract

| Setting | Initial default / requirement | Change mechanism |
| --- | --- | --- |
| Library root | `/Users/josephizang/LEARNING-VIDEOS` for this Mac; `/library` inside Docker | Host configuration, restart and validation |
| Application data | Dedicated local writable directory; `/data` in Docker | Host configuration |
| Listen address | Loopback by default; explicit LAN address for server | Host configuration |
| Allowed hosts/origins | Exact localhost names/ports or explicitly configured LAN name/address | Host configuration |
| FFmpeg/ffprobe | Discover or configure executable; report resolved version/capabilities | Host configuration |
| Encoder concurrency | 1 | Fixed v1 default |
| Probe concurrency | 1 initially, optionally 2 after measurement | Host configuration |
| Quality cache | 20,000,000,000 bytes | Browser setting with revision |
| Disk free reserve | 2,000,000,000 bytes per output volume | Host configuration; verify suitability |
| Queue paused | False initially; persistent thereafter | Browser setting |
| Preferred quality | 1080p with specified availability fallback | Browser preference |
| Speed | 1× initially | Browser preference |
| Subtitles | Enabled initially when a usable track exists; explicit Off persists | Browser preference |
| Autoplay | False initially | Browser preference |
| Fit | Contain initially; Fill optional | Browser preference |

Do not put host filesystem roots, binary paths, or listen addresses in browser-editable settings. Environment variables should use a consistent application-specific prefix and typed options; document final names alongside the implementation. Standard ASP.NET environment-variable binding can map nested options. Avoid mixing database preferences with immutable host configuration precedence.

## Native macOS installation sequence

1. Verify the pinned .NET 10 runtime/SDK requirements, FFmpeg/ffprobe, and the selected source/data directory permissions.
2. Restore/build/publish from the future `src` working directory using its pinned SDK and the root solution path.
3. Configure the library root, local data directory, and loopback listener. Do not hard-code Homebrew paths into application logic.
4. Run the explicit database initialization/migration maintenance operation before starting the normal host.
5. Start the application, check liveness/readiness, open the local URL, and inspect scan status.
6. Expect automatic compatibility preparation after discovery unless the persisted queue is paused. Initial installation instructions must disclose this behavior and storage impact.
7. Verify one direct MP4, a prepared WMV, subtitles, progress, and restart recovery using the authorized test scope.

Optional launchd integration is a later operational convenience, not required for the first usable app. Do not install a system service silently.

## Linux Docker deployment design

Build a multi-stage image using pinned .NET 10 SDK/runtime images and a verified FFmpeg distribution. The pinned ASP.NET runtime image contains no ffprobe, so the runtime stage installs it explicitly and fails the build if the executable is absent; scanning cannot populate duration or codec metadata without it. The container entrypoint starts the application only: it verifies schema compatibility and refuses to serve a stale schema, but never migrates, so deploying a new image cannot change the database on its own. Schema creation and upgrades are an explicit maintenance invocation (`docker compose --profile maintenance run --rm migrate`, or `docker run --rm -v <data-volume>:/app/appdata <image> migrate`) run with the normal container stopped and after taking a backup. Publish JsxCore React assets during the build. Run as a non-root user with UID/GID compatible with the library mount. Keep build tools out of the runtime image unless specifically needed by the verified JsxCore production model.

The supplied Compose file mounts the same named `application-data` volume at `/app/appdata` in both services. Keep the same Compose project name for maintenance and normal startup so both resolve to the same volume. Do not use `docker compose down --volumes` for routine upgrades: it deletes that persistent database.

Mount media read/write at `/library` because permanent sibling conversions and sidecars are required. Mount persistent application data at `/data` on local storage. Do not put the SQLite database inside an unreliable network share simply because media is mounted there. Set a writable temporary directory and avoid relying on the ephemeral container layer for durable output or job state.

Inside the container, Kestrel can listen on the container interface; host port publication determines exposure. Publish to loopback for local-only use or explicitly to the chosen LAN address. Do not publish to all host interfaces by default, configure router forwarding, or assume Docker honors every host firewall convention. Verify reachability from an intended LAN laptop and non-reachability from unintended interfaces.

Use one replica. Include a generous graceful-stop period for FFmpeg shutdown and durable Interrupted state recording. Restart policy must not hide a crash loop caused by a bad schema or corrupt database. Health checks should distinguish a running host from an application whose data store is unusable.

## First scan and bulk preparation

The known library should discover approximately the previously observed courses/videos, but current filesystem truth governs counts. Display unsupported/ambiguous files explicitly. Do not assert exact counts without a fresh scan.

Automatic work initially includes recognized non-MP4/non-MKV videos. Preexisting valid managed outputs are reconciled first. One encoder can process the backlog over time while browsing and direct playback remain available. Show separate totals for queued, ready, blocked, and failed work; do not combine conversion completion with lessons watched.

Pause the queue through the UI when necessary. The current job finishes; the next does not start. Stopping the host may interrupt active work, which restarts after reconciliation. No byte-level continuation of partial encodes is promised.

## Upgrade and migration

1. Pause the queue and stop the application cleanly.
2. Back up the data directory using a consistent SQLite backup method and record the current application/schema version.
3. Review the migration and test it against a backup copy.
4. Run the explicit maintenance migration once, with exclusive data-directory ownership.
5. Start the new application and verify schema readiness, catalog counts, progress, queue, and a representative playback session.
6. On failure, stop the new application and restore the backed-up data with the previous application version. Do not guess at a reverse migration that may discard history.

SQLite-specific note: use a known-version script or migration tool/bundle; generic idempotent scripts are not supported. Investigate an abandoned migration lock only after proving no migration operation is active. See [persistence](../design/persistence.md).

## Backup and transfer

Full backup: stop the application or use a SQLite-aware backup that accounts for WAL, then preserve application data and required host configuration. Separately back up source media, permanent copies, and sidecars as library data. Quality cache files can be regenerated and may be excluded.

Progress transfer: create a versioned export, copy the library to the new host, scan, preview the export, resolve conflicts, then apply. Stop using the old installation as the active progress source. Export/import does not transfer binaries, jobs, media, executable paths, or network configuration.

If watched-source hashing is needed for export, show preparation of the export as a background operation and do not block an HTTP request indefinitely. Unavailable source content produces an explicit partial/unmatched export report; do not invent fingerprints.

## Recovery playbook

| Symptom | Check | Recovery behavior |
| --- | --- | --- |
| Root unavailable | Mount and permissions | Preserve catalog/history, block jobs, restore mount, refresh |
| Direct videos work but conversion fails | FFmpeg capabilities, writable destination, space | Fix prerequisite; retry affected job |
| TS video has no sound | Companion association and stream mapping | Mark validation failure; correct pairing and regenerate safely |
| Queue appears Running after crash | Installation lock, attempt lease, final files/manifests | Reconcile output, then adopt or requeue |
| Existing output causes collision | Provenance and digest | Retain file; report ambiguity; do not overwrite |
| Cache remains above new limit | Active leases/reservations | Wait for playback to release; block new cache jobs |
| Progress stops saving | Database writeability/locks and session ownership | Surface unsaved state; restore storage or take over current session |
| Import has unmatched lessons | Relative paths, source fingerprint, duplicates | Keep entries unmodified; resolve mapping conservatively |
| Sidecar without video | Interrupted publication/deleted file | Do not advertise ready; requeue only if source is available |
| Corrupt database | Backup validity | Stop normal writes; restore a verified backup; do not recreate over existing data |

## Diagnostics and retention

Structured logs use scan/job/lesson/rendition IDs and correlation IDs. Keep raw local paths and process details out of general browser errors. Retain bounded attempt stderr tails, not unbounded logs per frame. Proposed retention: last 100 scan reports and 30 days of completed-attempt diagnostics, preserving unresolved failures. Progress/history is not subject to this diagnostic cleanup.

Expose version/capability status, queue depth, active attempt phase, disk/cache breakdown, last successful scan, and schema state. Measure API latency during conversion, command counts, and job completion/failure rates. No remote telemetry is required.

## Operational validation still required

The Linux server's CPU, architecture, free storage, filesystem, and browser clients have not been inspected. Do not estimate encoding throughput from the Mac alone. Validate container UID/GID, media mount semantics, atomic rename behavior, FFmpeg codec availability, and browser playback on the actual target before calling Linux deployment complete.
