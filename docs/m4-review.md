# PR #5: M4 preparation review

Correction status: see [M4 review responses](m4-review-responses.md) for the working-tree fixes and regression evidence. The findings below describe the original reviewed head.

PR: [M4: permanent compatible playback copies](https://github.com/joeizang/tutsvideoplayer/pull/5)

Reviewed base: `4c6462c4f27f`
Reviewed head: `c4e0f87427d9`
Comparison: `git diff 4c6462c...c4e0f87`

61 Core and 115 integration tests pass at this head, and the build succeeds. The shape of
the milestone is right — durable job rows, a real state machine, encode-to-temp then
rename, provenance sidecars, managed outputs excluded from discovery. The findings below
concentrate in one place: the state machine's transition table does not permit the
transitions the recovery and blocking code actually performs, so the crash-safety and
disk-reserve behaviour the PR describes does not execute. Application code was not changed
during this review.

## Standards

### S1 — [P2] Return the documented status for a stale queue-pause precondition

Location: [PreparationControllers.cs:193–197](../src/TutsVideoPlayer.Web/Features/Preparation/PreparationControllers.cs)

A valid-but-stale `If-Match` returns 409 Conflict. Completion and settings both return 412
for the same condition, the design says "return current revision on conflict", and this
PR's own summary says "queue pause with If-Match (428/412 per the documented
preconditions)". A client written against the rest of the API will not recognise this
response. The 428 branch also omits the `ETag` header the other controllers set, so the
caller is told a revision is required without being told which one.

Requirement: docs/design/api.md, Concurrency and idempotency.

### S2 — [P2] Measure free space on the volume that will hold the output

Location: [PreparationExecutor.cs:508–521](../src/TutsVideoPlayer.Web/Features/Preparation/PreparationExecutor.cs)

`Path.GetPathRoot` returns `/` for every absolute path on Linux and macOS, so the drive
matched is always the root filesystem. A library on a separate volume — the normal
deployment, and the one the runbook describes mounting at `/library` — has its reserve
checked against the wrong device. Select the `DriveInfo` whose `Name` is the longest prefix
of the output directory instead.

Requirement: docs/operations/runbook.md, Linux Docker deployment design.

## Spec

### F1 — [P1] Allow recovery to requeue jobs that were in flight when the process died

Location: [PreparationJobState.cs:68–85](../src/TutsVideoPlayer.Core/Preparation/PreparationJobState.cs), [PreparationExecutor.cs:450–461](../src/TutsVideoPlayer.Web/Features/Preparation/PreparationExecutor.cs)

`PreparationTransitions` has no `Running → Queued` and no `Validating → Queued`, but
`RecoverInterruptedAsync` reaches `RequeueAsync` for exactly those states, and
`TransitionAsync` throws on a disallowed move. I seeded one job per state and called
`RecoverInterruptedAsync` directly:

- `Interrupted` → requeued correctly.
- `Running` → `InvalidOperationException: A preparation job cannot move from Running to Queued.`
- `Validating` → `InvalidOperationException: A preparation job cannot move from Validating to Queued.`

`Running` and `Validating` are the states a job is actually in when a host is killed
mid-encode; `Interrupted` is only reached by the orderly shutdown path. Worse, the loop in
`RecoverInterruptedAsync` has no per-job error handling and `PreparationWorker` catches at
the top of `ExecuteAsync`, so the first such job aborts recovery for **every** remaining
job. After an unclean shutdown the affected jobs stay `Running` forever: `IsClaimable`
admits only `Queued`, so no worker ever picks them up again, and the watch page keeps
advertising a preparation that will never finish.

Add the two transitions (and `Running → Blocked`, `Validating → Interrupted`, see F2 and
F3), wrap each job's recovery in its own try/catch so one bad row cannot strand the rest,
and add a test that seeds `Running` and `Validating` rather than only `Interrupted`.

Requirement: docs/design/domain-model.md job state machine; docs/prd/0003, crash-safe preparation.

### F2 — [P2] Let the executor actually block a job

Location: [PreparationExecutor.cs:409–422](../src/TutsVideoPlayer.Web/Features/Preparation/PreparationExecutor.cs)

`BlockAsync` transitions to `Blocked`, but the job is already `Running` by then — the
worker's claim sets `Running` before `ExecuteAsync` is entered — and `Running → Blocked` is
not in the transition table. All three block paths therefore throw:
`source-unavailable`, `companion-unavailable`, and `insufficient-disk`. The throw is caught
by the general handler at line 207, which records `Interrupted`, requeues, and repeats until
the attempt bound turns it into `Failed` with `unexpected-error`. So a full disk produces
three wasted attempts and "The preparation failed unexpectedly. Retry from the queue."
instead of the "Not enough free space to prepare this version…" message the summary
advertises, and a temporarily unavailable source is burned through its retry budget rather
than parked.

### F3 — [P2] Record an interrupted shutdown that happens during validation

Location: [PreparationExecutor.cs:200–206](../src/TutsVideoPlayer.Web/Features/Preparation/PreparationExecutor.cs)

The cancellation handler transitions to `Interrupted` unconditionally, but
`Validating → Interrupted` is not permitted. `ValidateAsync` probes and hashes with the same
token, so a shutdown there is ordinary. The transition throws from inside the catch block,
the outcome is never recorded, and the job is left in `Validating` — which F1 then cannot
recover.

### F4 — [P2] Store the recipe that was actually used

Location: [PreparationScheduler.cs:153–158](../src/TutsVideoPlayer.Infrastructure/Catalog/PreparationScheduler.cs), [PreparationControllers.cs:58](../src/TutsVideoPlayer.Web/Features/Preparation/PreparationControllers.cs), [PreparationExecutor.cs:344](../src/TutsVideoPlayer.Web/Features/Preparation/PreparationExecutor.cs)

`RecipeVersionFor` guesses from the file extension, so a `.ts` lesson with no companion is
scheduled as `tsaac-mux-v1` while `PreparationRecipes.Choose` actually selects `remux-v1`
for it. A manually requested job stores the literal string `"explicit-request"`. The
executor never writes the chosen `recipe.RecipeVersion` back to the job, and `PublishAsync`
copies `job.RecipeVersion` onto the rendition. The persisted provenance therefore does not
identify the recipe that produced the file, which is what decides whether a stored
conversion is still current. The manifest sidecar records the right value; the database does
not.

Requirement: docs/design/domain-model.md, "A conversion is current only for its source generation and compatible recipe"; docs/design/media-pipeline.md, recorded recipe version.

### F5 — [P2] Block unsupported TS codecs instead of failing validation three times

Location: [PreparationRecipes.cs:64–82](../src/TutsVideoPlayer.Infrastructure/Media/PreparationRecipes.cs)

The no-companion TS branch checks the probe and returns `unsupported-source-codecs` when the
streams cannot be copied. The companion branch does not: it always returns a stream-copy
recipe with `ExpectedAudioCodec` "aac", and the executor validates against "h264". An
mpeg2video TS with a companion — the format that motivates the split-stream recipe in the
first place — copies happily, fails validation, and is retried twice more before failing.
The summary says unsupported sources are "Blocked with an honest reason rather than a fake
output"; check the probe in this branch too so that is true.

### F6 — [P2] Keep library-relative paths consistent with the resolved root

Location: [PreparationExecutor.cs:523–524](../src/TutsVideoPlayer.Web/Features/Preparation/PreparationExecutor.cs)

`RelativeToLibraryRoot` calls `Path.GetRelativePath(appOptions.Value.LibraryRoot, fullPath)`,
but `fullPath` came from `LibraryPathGuard`, which since the M2 review resolves the
configured root through its own symlink before composing paths. When `App:LibraryRoot` is a
symlink — a mounted or aliased library, which the runbook treats as normal — the two are
different strings and `GetRelativePath` yields a `../..`-prefixed path. That value is stored
on the job and the rendition, and `MediaRenditionsController` then feeds it back through
`LibraryPathGuard`, which rejects it. The permanent copy exists on disk and cannot be served.
Resolve the root once and use the resolved value on both sides.

### F7 — [P2] Do not mark a committed scan as failed because scheduling threw

Location: [LibraryScanner.cs:74–80](../src/TutsVideoPlayer.Infrastructure/Catalog/LibraryScanner.cs)

`ScheduleCompatibilityJobsAsync` is called after `transaction.CommitAsync` but still inside
the `try`. An exception there runs `RollbackQuietlyAsync` on an already-committed
transaction and then `AbortRunAsync`, so a scan whose catalog work is durably committed is
recorded as `Failed` with a `ScanAborted` issue and returns `Succeeded: false`. That undoes
the M1 F3 behaviour, where scan status had to reflect what actually happened. Move the
scheduling call after the `try`, or wrap it in its own handler that logs without touching
the run's outcome.

### F8 — [P2] Persist progress while the encode is running

Location: [PreparationExecutor.cs:139–149](../src/TutsVideoPlayer.Web/Features/Preparation/PreparationExecutor.cs)

The progress callback assigns `job.Progress`, but the only `SaveChangesAsync` calls are
before `RunAsync` and after it returns. Nothing reaches the database during the encode, so
the queue panel and the watch page — which read `Progress` through the API — show nothing
moving for the whole conversion and then jump to complete. Save on a throttled cadence (a
few seconds, or on each whole percent) so "live progress" is live.

### F9 — [P2] Do not delete the sidecar of an output you have declared invalid

Location: [PreparationExecutor.cs:496–505](../src/TutsVideoPlayer.Web/Features/Preparation/PreparationExecutor.cs)

The comment says "quarantine nothing … We never overwrite it blindly", and the next
statement is `File.Delete(manifestFullPath)`. When an existing output fails validation this
removes the provenance record inside the user's library — the one piece of evidence that
explains where the unrecognised file came from — while leaving the file itself. Leave both
in place and report.

## Smaller points

- **Duplicate, divergent definitions.** `PreparationExecutor.DedupKeyFor` builds
  `lesson:gen:compatibility:<three recipe versions>` while `PreparationScheduler.DedupKeyFor`
  builds `lesson:gen:compatibility`. Only the scheduler's is used; the executor's is dead and
  would silently break dedup if anyone reached for it. `IsManagedOutputName` is likewise
  defined twice (`PreparationExecutor` and `MediaFileClassification`) with slightly different
  logic, and only the latter is used. Delete the unused copies.
- **`SourceSetInfo.Probe` is declared non-nullable and receives null** (PreparationRecipes.cs:17,
  PreparationExecutor.cs:85). The compiler says so — CS8604, a new warning this PR introduces —
  and every use inside `Choose` already null-checks it. Mark it `ProbeMetadata?`.
- **Manifest writes are not durable.** `WriteAtomically` writes and renames without flushing,
  where the media-pipeline design asks for "write and flush". The `.tvp.json.tmp` it uses is
  also left in the library if the process dies mid-write, and recovery only cleans `.part`
  files.
- **`unsupported-source` blocks are never re-evaluated.** The scheduler unblocks only
  `missing-companion-audio`. Since M1, probe metadata is retried when it is missing, so a
  lesson blocked because ffprobe was broken at first scan stays blocked after ffprobe is
  repaired.
- **Watchdog details.** On the kill path the stdout/stderr tasks are abandoned, so the stderr
  tail that would explain the stall is discarded; `lastProgressUtc` is written plainly but
  read with `Volatile.Read`; and 120 s with no `out_time_us` line is tight for the initial
  analysis of a very large TS on slow storage.
- **`SourceFingerprint` hashes role and path as ASCII**, so non-ASCII course names collapse to
  `?`. The content hash dominates, but UTF-8 costs nothing.
- **Lease fields are written and never read.** `LeaseOwner`/`LeaseExpiresUtcMs` are set on
  claim and transition but nothing reclaims an expired lease; recovery adopts unconditionally
  instead. Fine under the install lock — worth a comment saying so.
- **`readyDefault` prefers height over retention** (LearningService.cs:114–117): the permanent
  copy only wins a tie. Unreachable today because incompatible sources are `Pending`, but the
  expressed intent is the reverse of the code.
- **`Task.Delay(2500)` in `QueuePauseStopsClaimsWhileAnnouncingTheSemantics`** trades wall
  clock for determinism; a readiness poll would be steadier under load.

## Already fixed in the working tree

Two defects in this head are already corrected locally but not committed to the branch:

- `AddSingleton<InstallationLockHolder>()` registers a type nothing resolves, so the install
  lock — the premise of the single-process claims — was never acquired. Now
  `AddHostedService`.
- `AddSingleton(preparationOptions)` registers the concrete instance while the worker and
  executor inject `IOptions<PreparationOptions>`, which resolves to a default-constructed
  instance. `Preparation:DiskReserveBytes` was silently ignored. Now `Options.Create(...)`.

Both belong in the branch before merge.

## Validation and limits

- `dotnet build` succeeds; `dotnet test TutsVideoPlayer.slnx`: 61 Core and 115 integration
  tests pass, one browser placeholder skipped. The counts in the PR summary are accurate.
- F1 was reproduced directly, by seeding one job per in-flight state and calling
  `RecoverInterruptedAsync`; F2, F3 and F5 follow from the same transition table and were
  confirmed against it. The probe test was removed after use and is not part of this branch.
- The PR summary states that integration tests cover "worker claim concurrency", "recovery
  adoption" and the disk reserve. No test in the repository references `RecoverInterruptedAsync`,
  `ClaimNextAsync`, `PreparationJobState.Interrupted`, or the disk reserve; the five
  preparation tests cover scheduling exemptions, an end-to-end WMV and TS run with byte
  preservation, queue pause (428 only), dedup convergence, and managed-output exclusion.
  Recovery, blocking and claim concurrency are the parts most in need of tests, and they are
  the parts that are untested.
- The real-library results in the summary (94/94 jobs, codec and duration checks, byte
  preservation, rescan stability) were not re-run here; they were taken as reported.
- Linux container packaging of ffmpeg is deferred to M6 by the author, which is reasonable,
  but note that F2 means a host without ffmpeg does not surface a blocked job — it burns the
  attempt budget and reports an unexpected failure.

Standards: **2 findings**, worst P2 wrong volume for the disk reserve. Spec: **9 findings**,
worst P1 crash recovery cannot requeue the states a crash actually leaves behind.
