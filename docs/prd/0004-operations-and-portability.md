# PRD 0004: Local operations and portability

## Requirements

| ID | Requirement | Acceptance |
| --- | --- | --- |
| OPS-01 | Run natively on macOS and in Docker on Linux | Same product behavior; host-specific paths configured externally |
| OPS-02 | Restrict intended deployment to localhost/LAN without login | Default binds loopback; LAN exposure is explicitly configured |
| OPS-03 | Keep one active installation | Second process using the same data directory cannot claim the same queue |
| OPS-04 | Persist catalog, settings, progress, and job state outside the container | Container replacement retains all application state |
| OPS-05 | Keep permanent conversion copies beside sources | Writable media mount is required for preparation; read-only degradation is explicit |
| OPS-06 | Bound evictable quality files to 20 GB by default | Storage UI separates permanent, ready cache, temporary, and reserved bytes |
| OPS-07 | Transfer progress with preview and conflict reporting | Root remapping does not depend on the original absolute Mac path |
| OPS-08 | Recover after interrupted jobs and unavailable storage | Restart reconciles files; it does not blindly re-encode completed work |
| OPS-09 | Provide operational status and actionable errors | Missing FFmpeg disables preparation while direct playback can remain available |
| OPS-10 | Preserve source data in all management actions | Refresh, import, clear cache, and retry cannot edit original files |

## Configuration and settings

Host configuration owns library root, data directory, listen addresses, allowed hosts/origins, FFmpeg/ffprobe executable locations, and worker limits. Browser settings own playback/subtitle preferences, queue pause, quality cache size, and course preparation choices. Do not expose a browser endpoint that can browse arbitrary host directories or replace the executable path.

Treat 20 GB as 20,000,000,000 bytes and label it consistently. Show permanent conversions separately because their total can exceed that limit. Proposed free-space reserve: 2 GB on each affected volume, in addition to estimated output requirements. Reserve and cache accounting are implementation defaults to validate on both hosts.

## Transfer scope

Export a versioned document containing library identity, course/lesson relative identities and source fingerprints, progress, completion overrides, last-course lesson references, playback preferences, and subtitle associations. It contains no videos, executable paths, absolute root path, live jobs, or cached files. A full application backup is a separate operational procedure.

Import parses and validates into a preview before writing changes. Match exact compatible identities first; use conservative fingerprint matching when relocating. Ambiguous or conflicting entries remain unmodified unless resolved. Proposed default merge: import unambiguous new progress; where both sides already have different progress, keep local state and surface an explicit Use imported option. Never infer “later timestamp” to mean “more correct,” because machine clocks can differ.

Applying a preview is transactional, idempotent, and checks that the catalog/progress revisions have not changed since preview. If they changed, require a fresh preview instead of overwriting recent watching. Unknown future export versions fail with a readable message. Transfer does not enable live synchronization or migrate jobs between running machines.

## Degraded operation

- Root offline: show known catalog with unavailable labels; suspend preparation and destructive reconciliation.
- Read-only root: allow compatible playback and progress saving when the data directory is writable; report that new copies cannot be prepared.
- FFmpeg missing: direct playback remains available, preparation shows configuration guidance.
- Data directory unwritable or corrupt: do not pretend progress is saved; fail readiness or show a clearly limited diagnostic state.
- Cache full or no evictable files: block the new quality job, retain active playback, and explain the limit.
- Failed migration: do not start the normal application against an incompatible schema.

## Release boundary

The application has no authentication boundary between LAN clients. Operational networking restrictions and host validation are therefore part of the deployment. They do not turn it into a safe public service. No ports are opened, services installed, Docker images built, or media modified during this documentation task.
