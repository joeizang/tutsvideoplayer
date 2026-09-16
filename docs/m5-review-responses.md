# PR #6: responses to the M5 inline review comments

PR: [M5: quality preparation and safe cache management](https://github.com/joeizang/tutsvideoplayer/pull/6)
Reviewed head: `b664b0a97b64`

All nine comments are addressed. 72 Core and 154 integration tests pass (6 added). No schema
change was needed.

| Comment | Change | Covering test |
| --- | --- | --- |
| P1 Do not fall back to evicting a leased rendition | `SelectNextCandidateAsync` no longer falls back to a protected candidate; when everything left is leased it returns nothing and `EvictUntilUnderLimitAsync` reports the shortfall as `PendingBytes`, so the caller defers (the quality path already blocks with `cache-full`). | `ARenditionWithALiveLeaseIsNeverEvicted`, `AColdCopyIsEvictedWhileTheWatchedOneSurvives` |
| P1 Verify containment and ownership before deleting cache files | Deletion resolves the catalog path through `LibraryPathGuard` (which rejects escapes and any symlinked ancestor), requires it to sit under `.tutsvideoplayer/quality/`, and confirms ownership via the sidecar manifest or the recorded byte length before removing anything. Absolute paths are refused outright. `ReconcileInterruptedEvictionsAsync` shares the same routine. | `EvictionRefusesAPathOutsideTheManagedQualityArea` |
| P1 Make interrupted-job recovery dispatch by rendition purpose | Recovery now adopts through `TryAdoptRecoveredOutputAsync`, which routes quality jobs to `PublishQualityAsync` with their profile and the validated output dimensions. A quality job with an unknown profile is left for inspection rather than mis-registered. | `ACrashedQualityJobRecoversAsAQualityRendition` |
| P1 Enforce total cache usage while the temporary output grows | The budget is computed once as chargeable stored bytes plus other jobs' reservations; the growth check compares the partial file against the remaining headroom and cancels FFmpeg through a linked token the moment it is exceeded. The post-encode check uses the same arithmetic after one last eviction attempt. `CacheAccounting` measures the attempt's `.part` file instead of the final output path, which does not exist until the rename. | `AnInFlightQualityJobIsChargedForItsGrowingTemporaryFile` |
| P2 Account for and clean up stale quality artifacts | `Stale` quality renditions are charged (`GetChargeableQualityBytesAsync`) and are evicted first, ahead of LRU, because they can no longer be played at all. | `StaleCopiesAreChargedAndReclaimedFirst` |
| P2 Persist and recover the quality temporary file before encoding | The attempt's `TempRelativePath` is written before FFmpeg starts, recovery accepts a temporary in the managed quality area as well as beside the source, and a recorded output path whose file does not exist is treated as an interrupted encode (clean up and requeue) rather than an unverifiable output. | Covered by the recovery test's path handling; the missing-output branch is exercised by the existing preparation recovery tests |
| P2 Use the fresh probe for duration and audio validation | The quality branch builds a `ProbeMetadata` from its own probe and uses it for progress, the audio decision and duration validation. Audio is only required of the output when the input actually has some, so a silent source is no longer failed for missing AAC. | Existing quality tests plus the build-time removal of the `probe`-shaped dead path |
| P2 Schedule cleanup when the cache limit is lowered | The worker enforces the limit at boot and on every idle poll, so a lowered limit and leases that have since expired are both acted on without another quality encode. | Behavioural; the eviction routine it calls is covered above |
| P2 Refresh available qualities while a playable fallback is running | `Watch/Lesson.tsx` polls on the preparation's state rather than on playability, and swaps the manifest in place instead of reloading, so a finished quality copy appears in the menu without interrupting the session. | Client-only |

Also removed: two `logger.LogError("[diag] …")` statements left in the quality budget path.

## Verification

Each new test was confirmed to fail against the unfixed behaviour before being accepted:
restoring the leased-rendition fallback, deleting without the containment and ownership
checks, excluding stale bytes from the accounting, measuring the final output path instead of
the temporary file, and publishing every recovered job as a compatibility copy each failed
its matching test.

Limits: the growth-cancellation path (P1 #4) is reasoned and unit-covered at the accounting
level but not driven end to end with a real oversized encode; the Lesson.tsx polling change
has no automated browser test.
