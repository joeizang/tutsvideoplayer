# Persistence, identity, and EF Core query design

## Provider and storage

Use EF Core 10 with SQLite in an application data directory independent of the library. Media paths in the database are relative to a configured root. The SQLite file belongs on local storage even if the media root is a network mount. One process owns the data directory and job coordinator.

SQLite does not provide database-generated concurrency tokens and does not support general idempotent migration scripts. Use application-managed revisions and a controlled migration command against a known schema version. Store sortable timestamps in UTC or integer epoch units and durations/positions as integer milliseconds to avoid provider translation surprises. [SQLite provider limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations).

## Logical schema

Names and columns below are implementation defaults, not generated SQL. All tables have explicit primary keys; retained identity-bearing rows use opaque IDs. Use foreign keys and deliberate delete behavior rather than relying on convention.

| Table | Principal columns and relationships | Constraints/indexes |
| --- | --- | --- |
| Libraries | Id, DisplayName, LogicalIdentity, LastSuccessfulScanId, Revision | One configured active library; absolute root stays host configuration |
| Courses | Id, LibraryId, RelativeDirectory, DisplayTitle, SearchTitle, SortKey, Availability | Unique library + exact relative directory; index search/sort |
| LessonFolders | Id, CourseId, ParentFolderId?, RelativeDirectory, Title, SortKey | Unique course + relative directory; prevent cycles in application validation |
| Lessons | Id, CourseId, FolderId?, SourceGeneration, PrimaryRelativePath, Title, SearchTitle, SortKey, DurationMs?, Availability, LastSeenScanId, Revision | Unique active source path per library; course/order index |
| SourceComponents | Id, LessonId, Generation, Role, RelativePath, LengthBytes, ModifiedUtcMs, Sha256?, ProbeMetadata | Unique lesson + generation + role + path; path lookup index |
| Renditions | Id, LessonId, SourceGeneration, Purpose, RecipeVersion, RelativePath, ManifestPath?, Status, Width, Height, VideoCodec, AudioCodec?, ByteLength, OutputHash?, LastAccessUtcMs, Revision | Unique lesson/generation/purpose/profile/recipe; retention/access index |
| SubtitleTracks | Id, CourseId, RelativePath, Format, Language?, LengthBytes, ModifiedUtcMs, Fingerprint?, ParseStatus, NormalizedRelativePath?, NormalizationVersion? | Unique library-relative path |
| SubtitleAssociations | LessonId, SubtitleTrackId, Origin, Priority, SourceGeneration? | Composite key; manual association explicitly identifiable |
| LessonProgress | LessonId, SourceGeneration, PositionMs, MaxObservedPositionMs, AutomaticCompleted, ManualCompletion, ActiveSessionId?, LastSequence, LastWatchedUtcMs, Revision | One current row per lesson generation; last-watched index |
| ProgressHistory | Id, LessonId, SourceGeneration, Snapshot, Reason, CreatedUtcMs | Retain displaced progress for changed-source/import recovery |
| PlaybackSessions | Id, LessonId, SourceGeneration, StartedUtcMs, LastHeartbeatUtcMs, ActiveRenditionId?, ClosedUtcMs? | Lease expiry index; no user table |
| PreparationJobs | Id, LessonId, SourceGeneration, DedupKey, Purpose, Profile, RecipeVersion, State, Priority, EnqueuedUtcMs, Attempt, LeaseOwner?, LeaseExpiresUtcMs?, Progress?, ErrorCode?, Revision | Unique DedupKey; claim index on state/priority/enqueued/ID |
| PreparationAttempts | Id, JobId, StartedUtcMs, FinishedUtcMs?, ExitCode?, SanitizedFailure?, TempRelativePath? | Job/time index; bounded diagnostics retention |
| ScanRuns | Id, StartedUtcMs, FinishedUtcMs?, State, DiscoveredCount, IssueCount, CatalogRevision | One active run enforced through coordinator/claim |
| ScanIssues | Id, ScanRunId, RelativePath?, Code, Message | Bounded and paged; never embed full process logs |
| Preferences | SingletonId, PreferredQuality, PlaybackSpeed, SubtitleEnabled, PreferredLanguage?, Autoplay, FitMode, CacheLimitBytes, QueuePaused, Revision | Validated singleton values |
| TransferExports | Id, State, RequestedUtcMs, FinishedUtcMs?, RelativeDataPath?, ContentDigest?, ErrorCode?, ExpiresUtcMs | Background export state; output lives under application data, never public static root |
| ImportPreviews | Id, ContentDigest, CatalogRevision, ProgressRevisionSnapshot, ExpiresUtcMs, ValidatedPlan | Short-lived; unique applied transfer digest tracked separately |
| AppliedTransfers | ContentDigest, AppliedUtcMs, ResultSummary | Unique digest makes repeated apply idempotent |

Prefer typed columns for fields used by filters/order/concurrency. Probe details and bounded import snapshots may be versioned JSON text; do not hide the entire domain in JSON. SourceComponents can retain past generations while keeping one active generation per lesson. Historical rows are not scanned as new lessons.

For LessonProgress, use `(LessonId, SourceGeneration)` as the key so a changed source cannot overwrite prior progress. A rendition's availability states include Pending, Ready, Stale, Missing, Evicting, and Evicted; its retention class is separately Source, Permanent, or Quality. Enforce that eviction transitions only apply to Quality. A file's extension is neither its retention class nor its availability state.

## Mapping conventions

- One `IEntityTypeConfiguration<T>` per model in Infrastructure.
- Convert strongly typed IDs explicitly to supported scalar storage. Avoid accidental string comparison semantics for numeric fields.
- Use backing fields/private setters for aggregate state. Simple catalog records may have internal mutation mechanisms.
- Use integer revisions as concurrency tokens; increment them on successful writes. SQLite has no SQL Server-style rowversion.
- Enforce nonnegative position/length and valid enum ranges at the domain boundary and add compatible database checks where useful.
- Explicitly configure foreign keys and restrict deletes of lessons with history. Missing is an availability state, not a soft-delete filter that hides history everywhere.
- Use ordinal path identity, separate normalized search fields, and deterministic ordering. SQLite's default case-insensitive behavior does not solve cross-platform Unicode path identity.
- Do not enable lazy-loading proxies or broad AutoInclude navigation rules.

## Context lifetime and transactions

Register a scoped context for requests and use a factory or service scope for background batches. Each operation awaits its calls and passes cancellation tokens. Never use a context concurrently or keep it alive for the duration of a video stream or encoder process.

Use short transactions for job claim, progress mutation, scan batches, import application, and publication metadata. Claim a job with an expected state/revision condition; exactly one successful row update establishes ownership. Use a unique deduplication key to make simultaneous preparation requests converge. Handle unique-key races by returning the existing row.

Enable WAL after validating the selected local filesystem and configure a bounded busy timeout. Keep writes small and retry only transient lock contention with bounded backoff. Do not retry arbitrary mutations unless their command IDs/revisions make them idempotent. No SQLite transaction spans hashing, probing, file encoding, or streaming.

## Query catalogue

These are design budgets to verify using actual EF SQL command logs, not claims of current measurements. Command counts exclude deliberate diagnostics and media file I/O.

| Read path | Query shape | Initial command budget |
| --- | --- | --- |
| Course page | Filter/search/sort, project course summaries with aggregate progress counts, bounded page plus count if UI needs it | 1–2 |
| Continue learning | Project last watched lessons and next candidates in bounded set operations | At most 3, independent of course count |
| Course tree | Flat folder projection plus flat lesson/status/progress projection; assemble hierarchy after bounded materialization | At most 3 |
| Playback manifest | One lesson/rendition projection plus associated subtitle projection and current progress | At most 3 |
| Jobs panel | Filtered ordered projection with pagination | 1–2 |
| Queue claim | Select eligible candidate and conditional update inside short transaction | Bounded constant count |
| Subtitle candidate list | Course-constrained projection of matching metadata | 1 |
| Cache eviction candidates | Ready quality renditions ordered by access, excluding leased/active outputs | 1 per bounded batch |

Course search defaults to 24 items and caps at 100. Job/issue lists default to 50 and cap at 200. Use stable sort keys plus IDs. Offset pagination is adequate for these bounded human lists; keyset pagination is appropriate for chronological job history if it grows. A course tree is intentionally a bounded whole-course projection, not an unbounded entire-library graph. Start with a 5,000-node safety threshold and provide folder-by-folder expansion for larger courses; never silently truncate lessons.

Natural sorting is not delegated to unsupported arbitrary CLR comparers in SQL. Generate deterministic sort keys during scanning, persist them, and test their ordering against the in-memory canonical comparer. Search uses derived normalized title fields and escaped parameterized LIKE semantics where substring behavior is intended. Treat `%` and `_` in user input literally unless a documented wildcard feature is introduced.

## EF optimization discipline

Project only needed columns with `Select`, use no tracking for reads, and filter/order/page before materializing. A flat tree assembled from selected rows is deliberate presentation work; fetching the entire database and filtering in memory is not.

Measure generated SQL and repeated statements. Remove N+1 navigation access and redundant Includes before considering compiled queries. Use split queries only when an actual required entity graph has multiple collections and measured join expansion; DTO projections are the default. Do not apply split queries globally. Compiled queries, raw SQL, Dapper, and additional caches require evidence of a remaining bottleneck. [EF efficient querying](https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying).

Use `ExecuteUpdateAsync`/`ExecuteDeleteAsync` for bounded simple housekeeping, such as expiring previews or marking eligible lease records, only when aggregate invariants and interceptors are not needed. Never bulk-delete originals' rendition metadata simply because a directory enumeration failed. Do not mix bulk commands with tracked stale entities without clearing/reloading deliberately.

Development SQL logging uses the EF database-command category. Sensitive-data logging is opt-in for local diagnosis only and disabled in release. Record query count, selected columns, row count, payload size, and latency against the representative 1,031-lesson fixture and a larger synthetic catalog. No performance optimization is claimed before evidence exists.

## Schema lifecycle

Use migrations from the beginning; do not mix `EnsureCreated` with migrations. Generate and inspect migrations as source under Infrastructure. Test empty creation and previous-version upgrades against real temporary SQLite files. Review table rebuilds, foreign keys, indexes, preservation of typed IDs, and retained progress.

Production startup verifies schema compatibility but does not auto-migrate. An explicit maintenance invocation creates or upgrades the database while normal workers are stopped. Back up first. Use a reviewed migration bundle/command or a script generated from the known installed migration; do not prescribe `--idempotent` for SQLite. On failure restore the backup with its matching application version. Do not clear an abandoned migration lock until confirming no migration process is running.

## Transfer and backup distinction

Progress transfer is a versioned logical document and conservatively matches lessons. A full backup includes the SQLite database and configuration, with WAL handled by stopping the app or using a SQLite-aware backup. Copying only a live main database file is not a reliable backup procedure. Permanent outputs and provenance are library data and should be included in the learner's library backup; cached quality files need not be backed up.

## Index and query review gates

Test uniqueness under case differences, numeric sort ties, duplicate content, simultaneous queue requests, and changed source generations. Capture generated SQL before adjusting LINQ. Use SQLite query plans for index verification separately from the EF query-shape review: the optimizing-ef-core-queries skill does not substitute for schema/index analysis.
