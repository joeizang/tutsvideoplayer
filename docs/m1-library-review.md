# PR #2: M1 library review

PR: [M1: persist and browse the library](https://github.com/joeizang/tutsvideoplayer/pull/2)

Reviewed base: `ced02ff958d4ca953af5da733a139588ae792bd0`  
Reviewed head: `363a9aa9a7b20148395310a1eaa267850a41a854`  
Comparison: `git diff ced02ff...363a9aa`

The existing tests pass, but several browsing and reconciliation defects need fixes. Findings below refer to the reviewed commit; line numbers may change in later revisions. Standards and Spec findings are kept separate.

## Standards

### S1 — [P2] Enforce same-origin protection on scan requests

Location: [LibraryController.cs:64–75](../src/TutsVideoPlayer.Web/Features/Library/LibraryController.cs)

The API design requires rejecting cross-site mutations and simple form content types. I sent a POST with an unrelated `Origin`, `Sec-Fetch-Site: cross-site`, and `application/x-www-form-urlencoded`; it returned 202. A cross-site request can therefore trigger filesystem scanning and probing. Enforce the documented origin/request validation before accepting work.

Standard: [HTTP API — Browser and local-network protection](design/api.md#browser-and-local-network-protection).

### S2 — [P2] Keep database migration separate from container startup

Location: [Dockerfile:13](../src/deploy/Dockerfile)

The persistence design explicitly says production startup “does not auto-migrate” and requires a separate maintenance invocation with backup and stopped workers. This entrypoint instead migrates on every container start, so deploying a new image can immediately change the database. Keep normal startup separate from the explicit migration operation.

Standard: [Persistence — Schema lifecycle](design/persistence.md#schema-lifecycle).

## Spec

### F1 — [P1] Link course rows to a lesson belonging to that course

Location: [Index.tsx:85–86](../src/TutsVideoPlayer.Web/Views/Home/Index.tsx)

The home page passes a course ID to `/watch/{lessonId}`, but course and lesson IDs are independent. In my fixture, clicking course B opened the TS course’s lesson. This breaks M1’s end-to-end course browsing. Resolve the selected course’s first lesson, or introduce a course entry route that performs that lookup.

Requirement: [Implementation plan — M1: Persist and browse the library](implementation/plan.md#m1-persist-and-browse-the-library).

### F2 — [P1] Filter probe-cache entries to the current source generation

Location: [LibraryScanner.cs:223–225](../src/TutsVideoPlayer.Infrastructure/Catalog/LibraryScanner.cs)

After a source changes, reconciliation retains both generations with the same `RelativePath`. This dictionary includes both and throws a duplicate-key `ArgumentException` on every subsequent scan. I reproduced a successful generation-2 scan followed by a failed scan. Filter by `Generation == LessonGeneration` before constructing the dictionary.

Requirement: [Domain model — Lesson identity](design/domain-model.md#lesson-identity), together with M1’s repeated-scan reconciliation requirement.

### F3 — [P1] Persist scan failure after rolling back reconciliation

Location: [LibraryScanner.cs:84–88](../src/TutsVideoPlayer.Infrastructure/Catalog/LibraryScanner.cs)

`AbortRunAsync` saves the failure using the transaction that is subsequently disposed without committing, so the failure status is rolled back too. After a reconciliation exception, I observed the persisted run remain `Running` and every refresh join that dead run. Roll back first, then record failure through a fresh transaction/context so failed scans remain retryable.

Requirement: [PRD 0002 — Discovery rules](prd/0002-library-and-learning.md#discovery-rules) and M1’s scan status/error handling.

### F4 — [P1] Create intermediate lesson folders before assigning parents

Location: [LibraryScanner.cs:323–325](../src/TutsVideoPlayer.Infrastructure/Catalog/LibraryScanner.cs)

LIB-02 requires “arbitrary nesting”. Only directories directly containing lessons are created here. `Course/Parent/Child/video.mp4` therefore creates Child without Parent, and the later parent lookup throws `KeyNotFoundException`, rolling back the entire catalog scan. Enumerate and create all required ancestors before linking the hierarchy.

Requirement: [PRD 0002 — LIB-02](prd/0002-library-and-learning.md#functional-requirements).

### F5 — [P2] Retry missing probe metadata on subsequent scans

Location: [LibraryScanner.cs:231–238](../src/TutsVideoPlayer.Infrastructure/Catalog/LibraryScanner.cs)

A failed probe still stores the source’s size and modification time. Later scans treat those matching hints as a valid cache hit, even though no probe metadata exists. I restored ffprobe after an initial failure and rescanned; every duration remained unknown. Cache successful probes, and retry unresolved metadata so fixing the dependency actually recovers discovery.

Requirement: [Implementation plan — M1 media probing](implementation/plan.md#m1-persist-and-browse-the-library).

### F6 — [P2] Reconcile additions and removals of companion audio

Location: [LibraryScanner.cs:429–432](../src/TutsVideoPlayer.Infrastructure/Catalog/LibraryScanner.cs)

`companionChanged` requires both the existing and discovered companions to be non-null. Adding a previously missing companion therefore never creates its `SourceComponent`; removing one leaves the stale component attached. I reproduced adding `lesson_audio.aac`: the warning disappeared, but the database still contained only the video. Compare the complete source-component set, including presence changes, to satisfy TS/AAC pairing.

Requirement: [PRD 0002 — LIB-03](prd/0002-library-and-learning.md#functional-requirements) and [domain source-set identity](design/domain-model.md#ids-and-value-objects).

### F7 — [P2] Verify source identity when metadata changes

Location: [LibraryScanner.cs:427–428](../src/TutsVideoPlayer.Infrastructure/Catalog/LibraryScanner.cs)

The domain design says to “probe and fingerprint the candidate” when source metadata changes. This implementation instead compares file length and a few probe fields; changed bytes with identical length, duration, codecs, and dimensions retain the old generation. I reproduced that behavior. Compare content identity before accepting the replacement as the same source generation.

Requirement: [Domain model — Lesson identity](design/domain-model.md#lesson-identity).

### F8 — [P2] Observe scans already running when the page loads

Location: [RefreshLibraryButton.tsx:4–7](../src/TutsVideoPlayer.Web/Views/Shared/RefreshLibraryButton.tsx)

M1 requires startup/manual scan status. When `initialScanState` is `Running`, this component disables Refresh but never starts polling: polling is reachable only from the disabled button’s click handler. Opening or reloading the page during a scan leaves it showing Scanning indefinitely after completion. Pass the active scan ID and start observing it on mount.

Requirement: [Implementation plan — M1 startup/manual scan status](implementation/plan.md#m1-persist-and-browse-the-library).

### F9 — [P2] Provide navigation beyond the first course page

Location: [HomeController.cs:41–44](../src/TutsVideoPlayer.Web/Features/Home/HomeController.cs)

The home query takes 24 courses, but neither the model nor UI provides pagination or Load more. With 25 fixture courses, the API reported 25 while the home page displayed only 24, hiding the last course from normal browsing. Expose pagination so the required course list remains complete as folders are added.

Requirement: [PRD 0002 — LIB-07](prd/0002-library-and-learning.md#functional-requirements) and [persistence pagination design](design/persistence.md#query-catalogue).

### F10 — [P2] Include ffprobe in the runtime container

Location: [Dockerfile:8–9](../src/deploy/Dockerfile)

M1 now probes discovered media, but the runtime image contains no ffprobe and this Dockerfile only copies the published application and creates `appdata`. I checked the pinned runtime image: the executable is absent. Consequently Linux scans cannot populate duration or codec metadata. Install the required probe dependency in the runtime image and verify a real fixture scan there.

Requirement: [Implementation plan — M1 media probing](implementation/plan.md#m1-persist-and-browse-the-library) and [Linux deployment design](operations/runbook.md#linux-docker-deployment-design).

## Validation and limits

- 43 Core tests and 44 integration tests passed.
- One browser placeholder was skipped.
- Additional temporary-library checks reproduced wrong course links, failure after a source-generation change, nested-folder failure and stuck scan status, failed-probe caching, missing companion reconciliation, unchanged generation after equal-sized byte changes, and the 24-course display limit.
- A request with cross-site/form headers was accepted with HTTP 202.
- The pinned ASP.NET runtime image was checked for ffprobe; the executable was absent. A complete PR #2 Docker deployment/browser run was not repeated during this review.
- The existing tests do not cover the identified scenarios sufficiently; passing them does not establish that M1 is complete.
- The review did not modify application code or the real video library, and no GitHub review comments were posted. This Markdown file was subsequently added at the user’s request.

Standards: **2 findings**, worst P2 cross-site mutations. Spec: **10 findings**, worst P1 persistent scan failure after source changes.

## Resolutions

Addressed on top of the reviewed head. 43 Core tests and 60 integration tests pass; the
browser placeholder is still the only skip. Findings map to changes as follows.

| Finding | Change | Covering test |
| --- | --- | --- |
| S1 | `SameOriginMutationFilter`, registered for every controller: unsafe methods require a JSON content type, a non-cross-site `Sec-Fetch-Site`, a recognized `Origin` when one is sent, and the `X-TutsVideoPlayer-Request` header a foreign origin cannot set without an approved CORS preflight. Recognized origins come from `App:AllowedOrigins`, falling back to the configured `AllowedHosts` rather than this request's Host. | `ScanRequestProtectionTests` |
| S2 | The container entrypoint runs the application only. Migration is the documented `migrate` argument, with a `maintenance` compose profile for it. | Dockerfile/compose review |
| F1 | `/courses/{courseId}` resolves the course's first lesson and redirects to it; the home course rows link there instead of reusing the course ID as a lesson ID. | `CourseBrowsingTests.CourseEntryRouteOpensALessonOfThatCourse` |
| F2 | The pre-scan inspection loads only components whose `Generation` equals their lesson's `SourceGeneration`, and indexes them by lesson path. | `ScannerReconciliationTests.ScansAfterASourceGenerationChangeKeepSucceeding` |
| F3 | The scan transaction is rolled back and the change tracker cleared before the failure is recorded through a clean unit of work. | `ScannerReconciliationTests.AFailedReconciliationIsPersistedAndLaterScansStillRun` |
| F4 | Every intermediate ancestor directory is materialized before parents are linked, and a missing ancestor is logged instead of failing the scan. | `ScannerReconciliationTests.NestedLessonFoldersCreateEveryIntermediateAncestor` |
| F5 | Missing probe metadata and unknown fingerprints force re-inspection even when size and modification time match, so repairing ffprobe recovers discovery. | `ScannerReconciliationTests.RepairingTheProbeRecoversMetadataOnTheNextScan` |
| F6 | Companion comparison covers presence: an added companion starts a new source generation carrying it, a removed one starts a generation without it. | `ScannerReconciliationTests.AddingAndRemovingCompanionAudioIsReconciled` |
| F7 | Inspected sources are fingerprinted with a complete SHA-256 digest (`ContentFingerprint`, persisted in `SourceComponents.Sha256`). Identity is decided by digest when one is established, and by the previous size/probe hints only while it is still unknown. | `ScannerReconciliationTests.ChangedBytesOfEqualLengthStartANewSourceGeneration` |
| F8 | `RefreshLibraryButton` takes the active scan ID and starts observing it on mount, so a scan already running when the page loads is followed to completion. | `CourseBrowsingTests.HomeModelExposesTheActiveScanIdForResumedObservation` plus browser verification |
| F9 | The home course list is paginated with Previous/Next links and a page-size parameter (default 24, capped at 100), and reports the matching course count. | `CourseBrowsingTests.HomePageOffersNavigationBeyondTheFirstPage` |
| F10 | The runtime image installs ffmpeg and fails the build if `ffprobe` is absent. | Dockerfile build check |

Limits: F8's mount-time observation and F10's runtime image were not exercised in a browser
or a real container during this round; both still need the Linux deployment run the original
review deferred. Fingerprinting reads each inspected source in full, so the first scan after
this change re-reads the library once to establish digests.


## Follow-up fixes

The second review identified three remaining problems, now corrected:

- **S2:** Both Compose services share the named `application-data` volume at `/app/appdata`. The explicit maintenance command migrates the database the web container subsequently opens.
- **F7:** A failed digest attempt is distinct from an unchanged source that was not inspected. For an existing lesson, failure to hash either component defers reconciliation of the whole source set and retains the last verified digest, size, timestamp, and probe metadata. This keeps the change detectable on the next scan and avoids mixing source generations. Companion failures also produce a `FingerprintFailed` issue. New sources without a digest remain explicitly unresolved and are retried. Full SHA-256 hashing remains in use.
- **F8:** Failed observation retains the scan ID and enables **Retry scan status**. Retrying observes that same scan, clears the old error, and reloads the catalog on completion; it does not submit another scan request.

Regression coverage: `FailedFingerprintPreservesVerifiedSourceAndRetriesAfterRecovery` exercises both video and companion failures, verifies that old identity metadata survives, and verifies exactly one new generation after recovery. The suite passes 43 Core and 62 integration tests; the browser placeholder remains skipped.

Manual validation: a real browser opened during a running scan, displayed an enabled Retry scan status button after the host stopped, and returned to the refreshed catalog after retrying against the restarted host. An isolated Compose project built the images, ran the documented disposable migration command, then started the web service with the same named volume; `/health/ready` returned HTTP 200 with a healthy database. No real video library was scanned or modified.
