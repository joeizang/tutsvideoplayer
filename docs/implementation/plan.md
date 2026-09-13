# Implementation plan

Status: milestones 0 and 1 executed on 2026-09-13 with explicit user authorization; milestones M2–M7 remain planned. Every M2+ checklist item is intentionally incomplete and requires its own authorization.

## Delivery discipline

Build in vertical increments that produce a working user journey. Keep all application/test/project/deployment code under `src` and a single root `TutsVideoPlayer.slnx`. Do not create unrelated infrastructure or change the user's media to make tests pass. Use a synthetic fixture library for destructive failure injection and conversion experiments.

Each increment includes domain behavior where necessary, EF mappings/query projections, HTTP contracts, React interactions, and relevant verification. Avoid a long phase of disconnected scaffolding followed by UI integration at the end. Fix proven integration constraints early.

## M0: Validate the specified stack and establish the build

Dependencies: later authorization to implement.

Evidence (2026-09-13): all items below completed. See [resolved conventions](../design/architecture.md#resolved-entry-conventions-validated-in-milestone-0) and [resolved versions](../references.md#resolved-implementation-versions-milestone-0-2026-09-13).

- [x] Verify current .NET 10 SDK/runtime and select pinned stable package versions.
- [x] Create the documented `src` projects and root `.slnx`; keep test projects beneath `src/Tests`.
- [x] Configure shared build/package settings under `src`; verify building the root solution from that working directory respects SDK selection.
- [x] Establish ASP.NET Core 10 controller API with ProblemDetails and one JsxCore TSX entry view using actual React.
- [x] Demonstrate browser React state/hooks and a same-origin JSON request.
- [x] Verify local production assets and no runtime CDN dependency.
- [x] Publish and run on macOS; validate Linux container build/runtime with a tiny safe fixture.
- [x] Exercise native video playback and byte-range seeking through the host before introducing a player package.
- [x] Record the exact JsxCore entry conventions and resolved package versions in the architecture/reference documents.

Exit evidence: reproducible restore/build/publish, rendered React page, JSON response, seeking in a fixture MP4, Linux asset delivery. No claim that the full application exists at this stage. If JsxCore cannot meet the stack, document the failing minimal case and resolve it without silent substitution.

Recorded results: `dotnet build ../TutsVideoPlayer.slnx` from `src` resolves SDK 10.0.302 and builds clean with zero warnings; 13 integration tests plus 2 intentionally skipped placeholders pass. `dotnet publish -c Release` minifies 78 assets with esbuild. The published app and the Linux container (`dotnet/sdk:10.0.302` build, `dotnet/aspnet:10.0.10` runtime, non-root `APP_UID` with `--chown`) were verified in a real browser: hydration, same-origin fetch, and seeking to arbitrary positions with 206 partial-content responses on both hosts.

## M1: Persist and browse the library

Dependencies: M0.

Evidence (2026-09-13): all items below completed. Initial `InitialCatalog` migration reviewed (no shadow columns, restrict FKs, unique path indexes, check constraints). Read-only scan of the real library: 19 courses, 1,031 available lessons, 0 missing, 610 subtitle tracks, and the 34-lesson TS/AAC course — matching [decision provenance](../product/decisions.md) exactly.

- [x] Define typed identities, relative-path validation, natural sort keys, and catalog availability rules.
- [x] Create EF configurations and the initial reviewed SQLite migration.
- [x] Implement explicit database initialization and normal startup schema checking.
- [x] Add safe root enumeration, symlink exclusion, extension classification, scan records, and partial-failure handling.
- [x] Probe representative media through a bounded subprocess adapter.
- [x] Discover courses, lesson folders, source components, and subtitle candidates.
- [x] Recognize TS/AAC pairs and exclude companion audio/support/cache files from lesson counts.
- [x] Reconcile unchanged paths without creating duplicate IDs; do not mark unavailable subtrees missing on failed scans.
- [x] Implement library/course/search/tree endpoints with projected no-tracking queries.
- [x] Build the home course list and watch-page lesson rail from real DTOs.
- [x] Add startup/manual scan status and empty/error states.

Exit evidence: stable naturally ordered tree after repeated scans, correct fixture counts, retained missing history, bounded SQL command counts, no source changes. Run a read-only scan of the real library only within the later implementation scope and compare with current filesystem evidence.

Recorded results: 43 Core tests and 62 integration tests pass, including stable lesson IDs across repeated scans, missing-lesson retention, failed-subtree protection, TS/AAC pairing, and SQL command budgets (course list ≤2, course tree ≤3 commands). The home course list and watch-page lesson rail were verified hydrated in a real browser on macOS.

Post-review corrections (PR #2 review, [m1-library-review.md](../m1-library-review.md)): course rows link through a course entry route that resolves a lesson of that course; the probe cache is filtered to each lesson's current source generation; a failed reconciliation is rolled back before the failure is recorded, so failed scans stay retryable; every intermediate lesson folder is created before parents are linked; unresolved probe metadata and unknown fingerprints are retried instead of treated as cache hits; companion audio additions and removals are reconciled as source-set changes; source identity is decided by a complete SHA-256 content digest rather than size and probe hints alone; a running scan is observed when the page loads; the home course list is paginated; mutating requests are rejected unless they are same-origin JSON; the container entrypoint no longer migrates on startup; and the runtime image installs ffprobe. Follow-up validation built the Linux images and verified explicit migration followed by healthy web startup using the shared Compose database volume. A real-browser interruption/retry check verified scan-status recovery. Video and companion fingerprint failures now preserve the verified source set and retry on later scans, with regression coverage for both cases.

## M2: Direct playback and learning continuity

Dependencies: M1.

- [ ] Implement ready source rendition metadata and safe seekable file delivery with GET/HEAD/ranges.
- [ ] Build player controls, aspect fit/fill, speed, skips, previous/next, and optional autoplay.
- [ ] Implement LessonProgress behavior with automatic/manual completion and source generation.
- [ ] Add playback session ownership, monotonic sequence acceptance, revisions, and lease heartbeat.
- [ ] Save position periodically and on relevant player events; show unsaved state on write failure.
- [ ] Implement Continue learning and completed-course replay behavior.
- [ ] Preserve playback state when navigating, buffering, or rejecting autoplay.
- [ ] Add race tests for delayed tab writes and completion overrides.

Exit evidence: watch/pause/restart/resume flow, backward-seek persistence, manual incomplete preserved, multiple-tab stale-write rejection, bounded memory for media delivery, keyboard operation.

## M3: Subtitle discovery and selection

Dependencies: M2.

- [ ] Add SRT/VTT validation and safe bounded WebVTT normalization.
- [ ] Implement matching precedence, language hints, separate Subtitle folders, and ambiguity handling.
- [ ] Persist explicit associations and global enabled/Off preference.
- [ ] Add subtitle candidate/menu controls and track lifecycle behavior.
- [ ] Verify malformed text/encoding and missing-preferred-track messages.
- [ ] Test subtitle persistence through lesson change, quality-source replacement, and restart.

Exit evidence: adjacent SRT, existing VTT, separate-folder track, manual ambiguous selection, explicit Off, safe cue rendering, original subtitle bytes unchanged.

## M4: Permanent compatible playback copies

Dependencies: M1–M3.

- [ ] Implement PreparationJob invariants, dedup keys, attempts, leases, queue pause, and priority.
- [ ] Add single-process installation ownership and scoped background job orchestration.
- [ ] Compute full source-set fingerprints before preparation.
- [ ] Implement WMV-to-MP4 and TS/AAC mapping recipes with source resolution/aspect preservation.
- [ ] Add automatic non-MP4/non-MKV scheduling and explicit MP4/MKV compatibility requests.
- [ ] Implement source-adjacent temporary/final output paths and versioned manifests.
- [ ] Implement output validation and the recoverable publication protocol.
- [ ] Reconcile crash windows before requeueing; skip current valid outputs.
- [ ] Present permanent copies as renditions of the same logical lesson.
- [ ] Add preparation status, priority, pause/resume, blocked, retry, and failure UI.
- [ ] Add disk reserve checks and bounded process diagnostics.

Exit evidence: correct WMV and split-stream playback; original byte preservation; no duplicate lesson/job after repeated scans; successful recovery at every publication boundary; no partial output served; queue pause semantics visible.

## M5: Quality preparation and safe cache management

Dependencies: M4.

- [ ] Implement native/1080/720/480 eligibility and honest nonstandard dimension labels.
- [ ] Add selected-lesson and course quality requests without upscaling or duplicate native renditions.
- [ ] Implement default quality fallback and explicit source selection.
- [ ] Restore position, pause, speed, volume, and subtitles on quality change.
- [ ] Add quality reservation accounting and growth checks under the 20 GB budget.
- [ ] Implement playback lease protection and two-phase eviction of owned quality files.
- [ ] Exclude permanent conversions from eviction and account for them separately.
- [ ] Add storage settings, pending cleanup, and full-disk recovery flows.

Exit evidence: source ≥1080 selects 1080 when ready; 720 source never claims 1080; 4:3 preserved; cache cannot delete permanent/active files; cleanup recovers after restart; playback remains responsive during preparation.

## M6: Transfer, backup, and operational packaging

Dependencies: M2–M5.

- [ ] Implement versioned export with targeted watched-source fingerprint completion.
- [ ] Implement bounded import parser, preview matching, ambiguous/conflict reporting, and transactional apply.
- [ ] Preserve local conflicts by default; detect stale preview revisions.
- [ ] Verify cross-root Mac-to-Linux mapping and duplicate-content ambiguity.
- [ ] Package native startup and Linux Docker deployment under `src`.
- [ ] Implement explicit migration maintenance command and schema readiness checks.
- [ ] Add diagnostics, consistent backup guidance, and recovery messages.
- [ ] Validate non-root mount permissions, local database persistence, listener/host configuration, and single-instance behavior.
- [ ] Update the runbook with tested exact commands and pinned versions.

Exit evidence: export/import round trip without media mutation; successful container replacement preserving state; tested schema upgrade/backup restoration; localhost/LAN exposure verified on target deployment.

## M7: Product verification and design review

Dependencies: all earlier milestones.

- [ ] Execute the [verification matrix](verification.md) and record results, versions, and fixture identities.
- [ ] Measure API query counts and latency while encoding; tune only demonstrated bottlenecks.
- [ ] Review screenshots at desktop/laptop target sizes, long filenames, 4:3 content, and error states.
- [ ] Test keyboard/screen-reader behavior, contrast, focus, and reduced motion.
- [ ] Verify source hashes remain unchanged through all mutation-capable workflows.
- [ ] Confirm the runtime works without external asset requests.
- [ ] Confirm no deferred feature has displaced a required feature.
- [ ] Update PRD acceptance evidence and mark implementation decisions validated or revised.

Exit evidence: a usable personal learning workflow on the Mac plus a verified Linux Docker path, with material limitations documented. Passing unit tests alone is insufficient.

## Technical spikes and unresolved empirical facts

| Unknown | Resolve in | Required evidence |
| --- | --- | --- |
| Exact JsxCore production import/build conventions | M0 (resolved) | Verified: local and Linux build plus browser run; see [resolved conventions](../design/architecture.md#resolved-entry-conventions-validated-in-milestone-0) |
| Linux architecture, CPU, storage, permissions | M0/M6 | Target-host diagnostics and deployment test |
| Whole-library codec variation | M1/M4 | Probe results and categorized unsupported cases |
| TS/AAC timing quality | M4 | Real representative pair plus synthetic timing fixtures |
| Software encoding throughput and text quality | M4 | Timed sample plus visual comparison |
| Browser-specific MKV and codec behavior | M2/M4 | Actual browser playback and fallback tests |
| Appropriate disk reservation estimates | M5 | Overflow/low-space tests and measured output sizes |
| Final typography license/assets and contrast | M0/M7 | Bundled license and rendered checks |

These are validation tasks, not unanswered product questions. They do not authorize work during the documentation-only phase.
