# HTTP API and browser contracts

## Conventions

Base path `/api/v1`. JSON uses camelCase, opaque string IDs, UTC ISO-8601 timestamps, integer milliseconds for durations/positions, and integer byte counts. DTOs are separate from EF entities. Unknown request fields may be ignored for compatible evolution, but invalid enum values, nonfinite numbers, and excessive input sizes are rejected. Never accept a raw filesystem path where an ID suffices.

Use controller-based APIs with attribute routes and automatic model validation. Errors use ProblemDetails with stable `code`, `traceId`, and relevant resource IDs, without full host paths or raw FFmpeg stderr. Use 400 for malformed input, 404 for unknown resource, 409 for state/identity conflicts, 412 for stale revision preconditions, 413 for excessive import, 422 for a well-formed unsupported operation, and 503 for required unavailable storage. A known-but-missing lesson still has a readable catalog resource; its unavailable rendition is not streamed.

Commands that create durable jobs return 202 with job ID and status location. Repeated requests return the same effective work via a server deduplication key. Do not hold the HTTP request open for encoding. For ordinary created session/preview resources use 201 and Location. Reads have explicit pagination caps.

## Endpoint catalogue

| Method and path | Contract and behavior |
| --- | --- |
| GET `/library` | Library identity, display name, availability, last scan summary, counts and catalog revision; no absolute path required in normal UI |
| POST `/library/scans` | Start/join refresh; returns scan ID and state |
| GET `/library/scans/{id}` | Counts, current phase, revision, issue summary |
| GET `/library/scans/{id}/issues` | Paged relative-path issues with stable codes |
| GET `/courses?q=&page=&pageSize=` | Course summaries, total if required, deterministic ordering |
| GET `/courses/{id}` | Course summary and availability |
| GET `/courses/{id}/tree` | Bounded flat nodes, parent IDs, sort keys, lesson progress and preparation summary |
| GET `/courses/{id}/folders/{folderId}/children` | Bounded child navigation for oversized trees |
| GET `/learning/continue` | Most recently watched course entries and their recommended lesson IDs |
| GET `/lessons/{id}` | Lesson details, relative display filename, availability, progress, previous/next IDs |
| GET `/lessons/{id}/playback` | Ready/preparing rendition descriptors, subtitle tracks, preferred/default rendition, persisted preferences |
| POST `/lessons/{id}/playback-sessions` | Explicit start/takeover; returns session ID, progress revision, saved position, and lease timing |
| POST `/playback-sessions/{id}/heartbeat` | Active rendition ID and activity status; renew cache lease with bounded cadence |
| PUT `/playback-sessions/{id}/progress` | Position, sequence, playing/ended signal, source generation; returns accepted position and effective completion |
| POST `/playback-sessions/{id}/close` | Final idempotent progress flush and lease release; stale session cannot overwrite current owner |
| PUT `/lessons/{id}/completion` | Manual choice Automatic/Completed/Incomplete with If-Match revision |
| GET `/lessons/{id}/subtitle-candidates` | Course-constrained candidate tracks, confidence/reasons, parse states |
| PUT `/lessons/{id}/subtitle-selection` | Saved track ID or automatic selection; validates association; Off is global preference |
| POST `/lessons/{id}/preparations` | Purpose compatibility or quality; profile if needed; returns existing/new job |
| POST `/courses/{id}/preparations` | Queue requested quality profiles for eligible lessons; summary of queued/reused/blocked |
| GET `/preparations?state=&cursor=&limit=` | Paged jobs with phase/progress and retry capability |
| POST `/preparations/{id}/retry` | Retry only valid states; idempotent when already queued/running |
| POST `/preparations/{id}/prioritize` | Raise priority without interrupting the active process |
| PUT `/preparations/queue` | Persist paused true/false; response distinguishes current job from queue pause |
| GET `/settings` | Playback preferences, queue state, cache limit and revision; exclude executable/listen configuration |
| PUT `/settings` | Validated preference changes with If-Match revision |
| GET `/storage` | Permanent, ready quality, temporary, reserved, available and protected byte totals |
| POST `/storage/cache-cleanups` | Request owned evictable-quality cleanup; returns operation summary/job; never deletes originals/permanent copies |
| POST `/transfers/exports` | Create a versioned export, resolving missing watched-source fingerprints in background if needed |
| GET `/transfers/exports/{id}` | Export readiness or bounded download link once complete |
| GET `/transfers/exports/{id}/download` | Download a ready versioned export from constrained application storage; no arbitrary filename/path parameter |
| POST `/transfers/import-previews` | Validate uploaded progress document; produce match/conflict preview without mutation |
| POST `/transfers/import-previews/{id}/apply` | Apply selected resolutions with preview revisions and content digest |
| GET `/health/live` | Minimal host liveness |
| GET `/health/ready` | Database/schema readiness; detailed media degradation belongs to UI diagnostics |
| GET `/diagnostics` | Sanitized dependency capabilities, storage states, versions and queue summary |

Media endpoints are separate: GET/HEAD `/media/renditions/{id}` for byte delivery and GET `/media/subtitles/{id}.vtt` for validated timed text. Cache leases are established by playback-session selection/heartbeat; media handlers also protect open files during each request.

## Representative payload shapes

The following is a schema sketch, not application code:

```text
PlaybackManifest
  lessonId, sourceGeneration, durationMs?, revision
  progress: positionMs, effectiveCompletion, manualCompletion, revision
  renditions[]: id, label, width, height, mimeType, codecHint,
                state, retentionClass, mediaUrl?, preparationJobId?
  subtitles[]: id, label, language?, state, trackUrl?
  selection: preferredProfile, readyDefaultRenditionId?, selectedSubtitleId?, subtitlesEnabled
  preferences: speed, autoplay, fitMode

ProgressCommand
  sequence, sourceGeneration, positionMs, isPlaying, ended

PreparationSummary
  id, lessonId, purpose, profile, state, phase, percent?,
  queuePosition?, attempt, errorCode?, userMessage?, canRetry

ImportPreview
  id, expiresAt, documentVersion, matchedCount, unmatchedCount,
  ambiguousCount, conflictCount, resolutions[], catalogRevision
```

Do not expose guessed remaining seconds when duration/progress is unknown. Labels are user-facing; codec/recipe information belongs in details/diagnostics. Media URLs are same-origin and identify existing catalog resources rather than encoding local paths.

## Concurrency and idempotency

Settings/completion updates require an ETag/If-Match revision; return current revision on conflict. A missing or unparseable precondition is refused with 428 and the current revision, never treated as "no precondition". Progress has a separate session/sequence protocol because frequent playback saves should not collide with unrelated settings. Duplicate progress sequences acknowledge without replaying state changes. Do not treat a stale session as a successful position save. A write that loses an optimistic-concurrency race is reloaded and re-evaluated against the committed state; if it keeps losing, it is reported as a retryable 409 rather than a server error.

Preparation uniqueness is server-derived, not trusted from a client idempotency string. Import apply includes the preview ID, content digest, expected catalog/progress revisions, and explicit conflict resolutions. Repeated apply returns its recorded result. A changed catalog invalidates the preview before any rows are changed.

Commands that change priority/pause can use expected revisions where lost updates matter. A request cancellation before transaction commit leaves no accepted work; once durable creation is committed, client disconnect cannot erase it.

## Browser and local-network protection

No login is implemented. Require intended Host values and reject unrecognized hosts. Mutations use JSON plus a custom same-origin request header, check Origin when provided, reject cross-site Fetch Metadata, and reject simple form content types. Validate against configured origins rather than trusting arbitrary Host-derived values. This limits cross-site browser triggers; it is not authentication and any permitted LAN client can reproduce the API.

Do not enable wildcard CORS. UI assets and scripts load locally. Reject path traversal, remote URL schemes in media metadata, and unsafe import paths. Import bodies are bounded (initial 10 MB limit, with a clear error), parsed as data only, and never written using an uploaded filename. The only supported upload is a progress-transfer document, not videos or executables.

Apply modest rate/concurrency bounds to scan, preparation, import, and cleanup commands; avoid starving media range requests behind management throttles. Relative filenames may be shown in library views, but full host paths and process command lines stay out of ordinary error responses.

## Client error recovery

Abort stale list/tree fetches on navigation. Retry idempotent reads with bounded backoff; do not blindly replay failed mutations. Show a specific Retry action for preparation failures. On 412 reload the current choice and preserve unsaved user intent. On disconnected progress saves, show unsaved state and resume using the existing session protocol when connection returns; do not claim offline persistence.

Generate OpenAPI and verify DTO/client types during implementation. Use the official controller conventions for validation and errors, while keeping the routes and payloads above as this application's design. [ASP.NET Web API guidance](https://learn.microsoft.com/en-us/aspnet/core/web-api/?view=aspnetcore-10.0).
