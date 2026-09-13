# PRD 0002: Library discovery and learning continuity

## Functional requirements

| ID | Requirement | Acceptance |
| --- | --- | --- |
| LIB-01 | Configure one library root; initially use the supplied Mac path | Root can change for Linux without rewriting stored lesson paths |
| LIB-02 | Each top-level folder containing discoverable videos represents a course | Existing module folders appear beneath that course; arbitrary nesting is supported |
| LIB-03 | Recognize video, companion audio, subtitle, and generated material separately | The observed TS/AAC pairs form 34 lessons, not 68 |
| LIB-04 | Preserve source names and natural sequence | Lessons 2 and 10 appear in numeric order; display cleanup never renames files |
| LIB-05 | Scan on startup and on Refresh library | A concurrent refresh joins or reports the current scan instead of starting another |
| LIB-06 | Retain missing lessons and progress | Disconnecting the root reports unavailable storage, not an empty replacement library |
| LIB-07 | Present searchable courses and Continue learning | Search title and lesson filename/title, not subtitle contents |
| LIB-08 | Merge app-owned representations into one lesson | A WMV and its managed MP4 appear once after rescans and database recovery |
| LRN-01 | Persist position and last lesson per course | Returning restores within two seconds of the last acknowledged position |
| LRN-02 | Track completion automatically and manually | Manual incomplete is retained until deliberate reset of that override |
| LRN-03 | Navigate previous/next in tree order | Course end stops or offers replay; never silently jumps to another course |
| LRN-04 | Keep viewing state distinct from preparation | A converted lesson still has no viewing progress until watched |
| LRN-05 | Transfer viewing progress between installations | Match preview reports matched, ambiguous, missing, and conflicting entries |

## Discovery rules

Enumerate recursively within the configured root. Use a recognized-video extension allowlist followed by probing; an unfamiliar file is not automatically fed to FFmpeg. Ignore known support files, `.DS_Store`, torrent padding, archives, app metadata, partial outputs, and cache directories. A root-level loose video is reported as unsupported organization in v1, without inventing a course or moving it. Empty top-level folders do not become empty course cards.

Preserve actual filesystem spelling, spaces, punctuation, and case. Use a derived display title only for readability: remove the file extension and optionally separate an ordering prefix from the title. Show the full original filename in lesson details. Natural ordering compares digit runs numerically, then uses a deterministic ordinal tie-breaker. Produce one stable depth-first lesson order across folder boundaries.

Treat scans as reconciliation passes. Do not mark anything missing until its relevant directory was successfully enumerated. Permission failures and partially unreadable subtrees are visible scan issues. A canceled or failed scan must not perform a whole-library missing sweep. When a path is reused for different content, preserve historical progress and require conservative identity handling rather than transferring progress by filename alone.

Root remapping is an operations action. File identity and ambiguity handling are specified in [domain model](../design/domain-model.md) and [persistence](../design/persistence.md).

## Continue learning and completion defaults

The initial implementation default for automatic completion is playback reaching 95% of a known positive duration during actual playback, or a genuine ended event. Merely seeking while paused does not complete a lesson. Unknown duration relies on ended or manual completion. This is position-based completion, not proof that every second was watched.

Store automatic completion separately from manual choice: `Automatic`, `Completed`, or `Incomplete`. Manual Incomplete remains authoritative even if later automatic signals arrive; a Use automatic action clears the override. Manual Complete does not fabricate a last position at the end.

Continue learning selects each course's most recently watched lesson unless it is effectively complete; then it suggests the next available incomplete lesson in order. If all lessons are complete, offer Replay course. If the remembered lesson is missing, show the issue and suggest an available lesson without discarding the missing lesson's history. Browsing a lesson without playing does not reorder Continue learning.

Send progress periodically while playing and on pause, seeking completion, lesson change, and page lifecycle events. Proposed cadence: ten seconds plus event-based flushes; server acknowledgements, not browser exit hooks, establish durability. A hard browser crash can lose the last unacknowledged interval. Completion changes persist immediately.

## Multiple browser sessions

One installation has shared progress. Position writes include a playback session identifier and a monotonically increasing sequence. A newer explicit playback session takes ownership of the lesson's resume position. Delayed writes from an older session cannot rewind it. Seeking backward within the current session is valid; never use the maximum position as the resume position. Completion uses a separate concurrency revision so position updates cannot overwrite a manual choice.

If a stale tab loses ownership, keep playback working and show that progress is being recorded from another session. Offer Take over progress when the learner intentionally resumes there. This is collision handling, not multi-user functionality.

## Empty and failure states

- No root configured: explain the host-side configuration needed.
- Root unavailable: offer Retry scan after the folder or mount is restored.
- No courses: explain expected top-level course folders.
- No search matches: preserve the query and offer Clear search.
- Missing lesson: retain title and progress, disable unavailable playback, and explain Refresh library.
- Database write failure: keep watching possible where safe, but show Progress could not be saved and retry without claiming success.
- Source changed: display the changed-media state and keep historical progress recoverable until identity is resolved.

## Acceptance scenarios

1. Given numbered nested folders, refreshing twice yields the same tree and lesson IDs.
2. Given an unreadable subtree, readable courses remain usable and the subtree's lessons are not declared removed.
3. Given a managed converted copy, deleting and rebuilding the catalog from media provenance does not add a second lesson.
4. Given a lesson manually marked incomplete, a delayed ended event does not override that choice.
5. Given two tabs, an older delayed save does not overwrite the current session's position.
6. Given the same library at a new absolute Linux path, a transfer matches unchanged relative paths and fingerprints.
7. Given two identical files in different courses, matching never silently merges their progress into a single lesson.
