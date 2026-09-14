# PR #3: M2 review comments

PR: [M2: direct playback and learning continuity](https://github.com/joeizang/tutsvideoplayer/pull/3)

Reviewed base: `6c0eded04006fa1c369ccaa5ce9535940f7fa162`  
Reviewed head: `69766bc3e0792a240fe0a797798480625c7af4d5`  
Comparison: `git diff 6c0eded...69766bc`

The existing tests pass, but the following actionable issues remain. Locations refer to the reviewed head. Application code was not changed during this review.

## Standards

### S1 — [P1] Reject symlinks in every media-path ancestor

Location: [src/TutsVideoPlayer.Infrastructure/FileSystem/LibraryPathGuard.cs:29–32](../src/TutsVideoPlayer.Infrastructure/FileSystem/LibraryPathGuard.cs)

The guard checks only the terminal FileInfo.LinkTarget. Replacing a catalogued course directory with a symlink to a directory outside the library leaves the terminal file looking ordinary, so PhysicalFile serves content outside the configured root. In an isolated fixture, this returned HTTP 200 with 266,375 bytes. Check the root and every ancestor, using the existing symlink-safe media locator as a reference, before serving a rendition.

Requirement: docs/design/api.md, browser and local-network protection; safe media delivery in M2.

### S2 — [P2] Require concurrency revisions for completion and settings mutations

Location: [src/TutsVideoPlayer.Web/Features/Learning/LearningController.cs:60–69](../src/TutsVideoPlayer.Web/Features/Learning/LearningController.cs)

Missing or malformed If-Match headers become null, which bypasses revision validation; the React completion/settings calls omit the header entirely. I verified that completion without a revision returns HTTP 200. A stale tab can therefore replace a newer manual choice or setting through the normal UI. Require and validate the precondition, track returned revisions in the client, and handle conflicts without silently overwriting the latest choice. The settings controller has the same issue.

Requirement: docs/design/api.md, Concurrency and idempotency: settings/completion updates require ETag/If-Match.

## Spec

### F1 — [P1] Flush navigation progress using an accepted lifecycle request

Location: [src/TutsVideoPlayer.Web/Views/Shared/VideoPlayer.tsx:129–146](../src/TutsVideoPlayer.Web/Views/Shared/VideoPlayer.tsx)

The only closeSession caller uses sendBeacon, which cannot supply the X-TutsVideoPlayer-Request header required by the global mutation filter. A request with the beacon's JSON body and no custom header returned HTTP 403. Moreover, React effect cleanup is not a reliable full-page navigation hook, while Previous/Next and rail links navigate normally. Add a supported lifecycle/navigation flush using keepalive fetch with the required header and idempotent sequence handling; ordinary lesson changes must not rely solely on the ten-second timer.

Requirement: PRD 0002, LRN-01 and event-based progress flushes.

### F2 — [P2] Restore observation of scans running when the page opens

Location: [src/TutsVideoPlayer.Web/Views/Shared/RefreshLibraryButton.tsx:4–7](../src/TutsVideoPlayer.Web/Views/Shared/RefreshLibraryButton.tsx)

The Tailwind rewrite removes initialScanRunId, the mount effect, and the observation/retry logic added by the M1 fixes. With initialScanState equal to Running, Refresh is disabled, but polling can only start from its click handler. Opening or reloading the page during startup/manual scanning therefore leaves it showing Scanning indefinitely after the server finishes. Restore the active-scan observation while retaining the new styling.

Requirement: PRD 0002, LIB-05; previously resolved M1 F8.

### F3 — [P2] Connect autoplay-next to current preferences and start the next video

Location: [src/TutsVideoPlayer.Web/Views/Shared/VideoPlayer.tsx:232–238](../src/TutsVideoPlayer.Web/Views/Shared/VideoPlayer.tsx)

onEnded reads the original autoplayNext prop, whereas the checkbox changes the separate autoplay state and its parent callback is a no-op. I enabled the checkbox, played the fixture to completion, and remained on the same lesson. Disabling an initially enabled preference has the inverse problem. Even when navigation occurs, the destination video has neither autoPlay nor a mount-time play attempt, so it remains paused. Use the current state and carry an explicit next-play intent, with a visible fallback when the browser rejects playback.

Requirement: M2 optional autoplay and autoplay-rejection handling.

### F4 — [P2] Expose all three manual completion choices

Location: [src/TutsVideoPlayer.Web/Views/Shared/VideoPlayer.tsx:486–501](../src/TutsVideoPlayer.Web/Views/Shared/VideoPlayer.tsx)

The player exposes Mark completed or Use automatic, but never Mark incomplete. Once automatic completion is true, Use automatic cannot make the lesson incomplete. Conversely, if Incomplete was set through the API or imported, effectiveCompletion is false and the only displayed action is Mark completed, so Use automatic is inaccessible. Browser inspection confirmed the completed state lacks Mark incomplete. Render controls from both automatic/effective state and the manual override so every supported choice remains reachable.

Requirement: PRD 0002, LRN-02 and manual completion defaults.

### F5 — [P2] Resolve optimistic concurrency failures through the playback protocol

Location: [src/TutsVideoPlayer.Web/Features/Learning/LearningService.cs:175–184](../src/TutsVideoPlayer.Web/Features/Learning/LearningService.cs)

Revision is an EF concurrency token, but progress saves do not catch DbUpdateConcurrencyException and reload/re-evaluate the session and sequence. Overlapping writes can both pass the in-memory checks, then one fails at SaveChangesAsync with HTTP 500. Concurrent fixture requests reproduced multiple 500 responses and the corresponding exception in the host log. Serialize or retry the relevant unit of work and return the documented duplicate acknowledgement or stale-write conflict; apply consistent handling to completion/settings/session operations that share these revisions.

Requirement: M2 race tests; docs/design/api.md session/sequence protocol.

### F6 — [P2] Exclude historical source generations from Continue learning

Location: [src/TutsVideoPlayer.Web/Features/Learning/LearningService.cs:267–280](../src/TutsVideoPlayer.Web/Features/Learning/LearningService.cs)

The initial progress query includes every source generation and combines those rows with the lesson's current metadata. After replacing a watched source and rescanning, its playback manifest correctly reported generation 2 with position zero, but Continue learning still advertised the generation-1 position and manual choice. Historical completion can likewise steer the recommendation past new, unwatched content. Filter to the current lesson generation and expose historical progress only through an explicit recovery flow.

Requirement: PRD 0002 source-change handling; docs/design/domain-model.md lesson identity.

### F7 — [P2] Recommend available content when the remembered lesson is missing

Location: [src/TutsVideoPlayer.Web/Features/Learning/LearningService.cs:331–335](../src/TutsVideoPlayer.Web/Features/Learning/LearningService.cs)

LessonAvailable is projected but never used. If the last watched lesson is incomplete and then disappears, the resume branch still links to that unavailable lesson even when another lesson in the course is available. I reproduced a Continue entry recommending the missing lesson with one available lesson remaining. Preserve its history, explain the missing source, and choose an available recommendation rather than sending Resume to an unplayable page.

Requirement: PRD 0002, Continue learning and completion defaults.

### F8 — [P2] Preserve pageSize in course pagination links

Location: [src/TutsVideoPlayer.Web/Views/Home/Index.tsx:126–133](../src/TutsVideoPlayer.Web/Views/Home/Index.tsx)

coursesHref still accepts pageSize, but the rewrite removes it from the query string. For example, Next from /?pageSize=2 points to /?page=2, whose controller uses the default size of 24. With enough courses this skips courses 3–24; with fewer it clamps back to page 1 and repeats the first page. Include the effective page size in Previous/Next links. The existing test requests both pages directly, so it does not exercise the generated links.

Requirement: PRD 0002, LIB-07 and the M1 pagination fix.

### F9 — [P2] Show and recover from playback-session creation failure

Location: [src/TutsVideoPlayer.Web/Views/Shared/VideoPlayer.tsx:96–98](../src/TutsVideoPlayer.Web/Views/Shared/VideoPlayer.tsx)

A failed start only sets staleMessage and leaves stale equal to none. The render branches display errors only for unsaved or taken-over, so this message is invisible. sessionRef remains null and all later progress writes return immediately, allowing playback to continue indefinitely without saving or offering recovery. Render an explicit session-start failure and provide a retry that actually creates a session, rather than invoking writeProgress with a null session.

Requirement: PRD 0002, database/write failure states; M2 unsaved-position feedback.

### F10 — [P2] Start a completed-course replay at the beginning

Location: [src/TutsVideoPlayer.Web/Views/Home/Index.tsx:181–185](../src/TutsVideoPlayer.Web/Views/Home/Index.tsx)

Replay course uses the same plain /watch/{lessonId} link as Resume. The player unconditionally restores savedPositionMs, clamped to duration minus one second. For a course completed through ended events, replay therefore opens its first lesson near the end instead of starting it again; repeating this across completed lessons provides almost no replay. Carry explicit replay intent and start at zero without erasing completion history.

Requirement: M2 completed-course replay behavior.

## Validation and limits

- `dotnet test TutsVideoPlayer.slnx --no-restore`: 43 Core and 73 integration tests passed; one browser placeholder skipped.
- Temporary-library HTTP/database checks reproduced parent-symlink media delivery, rejected beacon-shaped close requests, missing-revision acceptance, concurrent progress-write failures, historical-generation Continue learning entries, and missing-lesson recommendations.
- A real browser verified the player controls, the missing manual-incomplete action, and that enabling Autoplay next during playback did not advance at completion.
- Other findings are established by the changed code and its call sites; this review does not claim an automated browser regression test for every UI scenario.
- The PR's current tests exercise API behavior mainly through sequential requests; they do not cover the concurrent failures or the actual navigation/completion/autoplay controls described above.
- Linux deployment and broad codec/browser compatibility were not re-tested in this review. Original media and the real learning library were not modified or scanned. No review comments were posted to GitHub.

Standards: **2 findings**, worst P1 path escape. Spec: **10 findings**, worst P1 navigation progress flush.

## Resolutions

Addressed on top of the reviewed head. 43 Core and 91 integration tests pass (18 added); the
browser placeholder remains the only skip. Findings map to changes as follows.

| Finding | Change | Covering test |
| --- | --- | --- |
| S1 | `LibraryPathGuard` resolves the configured root through its own link once, then rejects a reparse point in any directory between the root and the file, as well as the terminal file. A path that never reaches the root is not contained. | `LibraryPathGuardTests` |
| S2 | Completion and settings updates parse If-Match explicitly: missing or unparseable is 428 with the current revision, not "no precondition". Every read and successful write returns an ETag, a `GET .../completion` endpoint exposes the current revision, and the player tracks revisions from session start, progress writes and settings responses, reloading the newer value on 412/428 instead of overwriting it. | `PlaybackProtocolTests.CompletionUpdatesRequireCurrentRevision`, `SettingsRoundTripValidatesAndFeedsTheManifest` |
| F1 | The navigation flush is a keepalive `fetch` carrying `X-TutsVideoPlayer-Request`, driven by a `pagehide` listener as well as effect cleanup, and guarded so a repeated close is harmless. Previous/Next and rail links therefore flush without waiting for the ten-second timer. | `PlaybackConcurrencyTests.ABeaconShapedCloseIsRejectedSoTheClientMustUseKeepaliveFetch` |
| F2 | `RefreshLibraryButton` takes the active scan ID again and starts observing on mount, keeping the Tailwind styling. | `CourseBrowsingTests.HomeModelExposesTheActiveScanIdForResumedObservation` |
| F3 | `onEnded` reads the live autoplay preference through a ref, and navigates with `?autoplay=1`; the destination attempts playback once metadata is ready and surfaces the overlay play button when the browser refuses. | Browser verification (no automated media-playback test) |
| F4 | Completion controls are rendered from the manual override rather than the effective state, so Mark completed, Mark incomplete and Use automatic are each present whenever they can change anything. | `PlaybackProtocolTests.ManualIncompleteSurvivesAutomaticEndedSignals` (API side), browser verification |
| F5 | Every session, progress, completion and settings unit of work reloads and re-evaluates on `DbUpdateConcurrencyException`; a loser that keeps losing becomes a retryable 409 instead of a 500. | `LearningConcurrencyTests`, `PlaybackConcurrencyTests` |
| F6 | Continue learning reads only progress whose generation matches the lesson's current generation, and flags `sourceChanged` so superseded history is explained rather than advertised. | `ContinueLearningTests.HistoricalGenerationsDoNotSteerTheRecommendation` |
| F7 | A missing remembered lesson keeps its history but recommends the next available lesson; a course with nothing playable is reported as unavailable rather than linked. | `ContinueLearningTests.AMissingRememberedLessonRecommendsAnAvailableOne`, `ACourseWithNothingPlayableIsReportedRatherThanLinked` |
| F8 | `coursesHref` emits the effective page size again, and the test follows the generated link instead of requesting page 2 directly. | `CourseBrowsingTests.PaginationLinksCarryTheEffectivePageSize` |
| F9 | A failed session start clears the session, shows a dedicated alert and offers a retry that actually starts a session; progress writes with no session say so instead of returning silently. | Browser verification (client-only state) |
| F10 | Replay course links carry `?replay=1`; the player then suppresses the saved-position restore and starts at zero without touching completion history. | `CourseBrowsingTests.ReplayLinksCarryExplicitReplayIntent` |

Verification notes: the S1 and F5 tests were confirmed to fail against the unfixed code before
being accepted. F3, F4 and F9 are client-only behaviours with no automated browser regression
test in this round, as the original review also noted for UI scenarios. Linux deployment and
codec/browser compatibility were not re-tested, and neither the real library nor its media was
modified.

## Follow-up review — 2026-09-14

Reviewed the local fixes on top of `69766bc3e0792a240fe0a797798480625c7af4d5`. PR #3 still points to that original commit. The resolution table above records the author's response; the results below supersede its all-pass claim for this verification run.

### R1 — [P2] Handle races when creating the first progress row

Location: [LearningService.cs:34–43](../src/TutsVideoPlayer.Web/Features/Learning/LearningService.cs)

The retry wrapper handles revision conflicts on existing rows, but concurrent first-time completion/session requests can both find no progress row and attempt insertion in GetOrStartProgressAsync (lines 541–554). The loser gets DbUpdateException with SQLite error 19, not DbUpdateConcurrencyException, and returns HTTP 500. The submitted concurrent-completion test failed this way; two simultaneous If-Match "0" requests against a fresh fixture independently returned 500 and 200, with a unique constraint violation on LessonProgress.LessonId/SourceGeneration in the log. Make initial creation atomic or recover from this specific uniqueness race and re-evaluate the command. Add deterministic coverage for creation as well as revision updates.

### R2 — [P2] Restore recovery after a failed scan-status request

Location: [RefreshLibraryButton.tsx:38–44](../src/TutsVideoPlayer.Web/Views/Shared/RefreshLibraryButton.tsx)

Mount-time observation is restored, but its catch block only sets an error. It leaves scanState as Running, stops polling, and leaves the sole Refresh button disabled. A single failed status request therefore requires a manual page reload even after the server recovers. Restore the enabled Retry scan status state and retained scan ID from the accepted M1 fix, or provide bounded automatic recovery. This is established from the current control flow; the identical failure path was browser-reproduced during the M1 follow-up.

### R3 — [P2] Check for an unavailable course before choosing replay

Location: [LearningService.cs:412–418](../src/TutsVideoPlayer.Web/Features/Learning/LearningService.cs)

The new unavailable handling is only in the incomplete-lesson branch. If the remembered lesson is completed and every source disappears, lessons is empty and the completed branch still returns replay pointing to the missing lesson. A real fixture returned recommendation=replay, lessonAvailable=false and totalLessons=0 after all media was removed and rescanned. Check the empty available-lesson set before the completion branch, so the UI renders Unavailable without a replay link. Extend ACourseWithNothingPlayableIsReportedRatherThanLinked to include completed progress.

### R4 — [P2] Keep course-link assertions independent of Continue learning state

Location: [CourseBrowsingTests.cs:97–126](../src/Tests/TutsVideoPlayer.IntegrationTests/CourseBrowsingTests.cs)

ReplayLinksCarryExplicitReplayIntent leaves watched/completed progress in the collection's shared application. CourseRowsLinkThroughTheCourseEntryRoute still rejects every /watch/ link in the entire home document, including valid Continue learning/replay links. In this run it failed on /watch/8?replay=1. Scope the assertion to course-list rows and isolate or restore test state so valid replay content cannot make the suite order-dependent.

### Follow-up validation

- Full suite: **43 Core passed; 89 integration passed, 2 integration failed; 1 browser placeholder skipped.** Failures: CourseRowsLinkThroughTheCourseEntryRoute and ConcurrentCompletionAndSettingsWritesResolveAsConflictsNotFailures.
- The save-interceptor tests are meaningful: they commit a competing revision change through another SQLite connection at save time. They passed here, as did the symlink tests. Their setup deliberately creates the progress row first, so they do not establish safety of the initial-insert path. I inspected the mechanism but did not repeat the author's mutation-testing experiment.
- **F3 browser check passed:** enabling Autoplay next during playback navigated to the next fixture with autoplay intent, and that fixture played to completion.
- **F4 browser check passed:** Mark incomplete was reachable, Use automatic cleared it, and manual Incomplete on the destination remained effective after playback.
- **F9 browser check passed:** an isolated local proxy returned 503 for session creation; the player displayed the dedicated error and Retry saving progress. Removing the failure and pressing Retry created a session and cleared the error.
- These manual checks do not add automated browser coverage. The lack of automated F3/F4/F9 tests remains accurately disclosed.
- Temporary fixtures only; real media/library untouched. No application fixes or GitHub comments were made during this follow-up.

## Final follow-up fixes — 2026-09-14

- R1: The bounded retry now also recognizes SQLite primary-key/unique conflicts when the failed entries are newly added LessonProgress rows. It clears tracking and re-evaluates against the winning row; unrelated database errors are not retried. A deterministic competing-insert test covers both initial completion updates and session creation.
- R2: Scan observation retains the active scan ID, exposes Retry scan status on failure, clears the error on retry, and observes the same scan.
- R3: Continue learning checks for zero available lessons before evaluating completion, so completed and incomplete missing courses both report Unavailable. The regression test covers both states.
- R4: The course-link assertion targets the course-list section, allowing valid Continue learning and replay links elsewhere on the page.

Validation: 43 Core and 94 integration tests passed, with one browser placeholder skipped. A real-browser check forced a status-request HTTP 503, verified the enabled retry, then restored responses and retried the existing scan. No real library/media changes.
