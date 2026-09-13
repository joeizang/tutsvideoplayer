# PRD 0001: Personal tutorial video library

Status: confirmed product scope with implementation defaults identified in linked specifications.

## Problem and outcome

The learner has over a thousand local tutorial videos organized into named folders. Watching currently requires navigating files, remembering position and completion, handling different media formats, and finding subtitle files. The application must make those folders into a coherent learning library while retaining the files and organization the learner already owns.

The primary outcome is a repeatable loop: find a course, resume the right lesson, watch with suitable picture quality and subtitles, and return later without reconstructing progress.

## Audience and environment

One learner using desktop or laptop browsers. The host is either the learner's Mac or one Linux server, and the latter must have access to the source library. Several browsers may connect, but all share the same personal data. Access is restricted operationally to localhost or LAN. No public hosting, account creation, user profiles, cloud storage, or online metadata service is part of this release.

## Product areas

| Area | Required outcome | Detailed PRD |
| --- | --- | --- |
| Library | Discover folder-defined courses and navigate naturally ordered lessons | [0002](0002-library-and-learning.md) |
| Learning | Resume, complete, and continue lessons reliably | [0002](0002-library-and-learning.md) |
| Playback | Seek, choose speed/quality, display subtitles, preserve proportions | [0003](0003-playback-and-preparation.md) |
| Preparation | Make current non-MP4/non-MKV videos playable without repeated conversions | [0003](0003-playback-and-preparation.md) |
| Operations | Run locally, manage disk use, recover jobs, and transfer progress | [0004](0004-operations-and-portability.md) |

## Primary journeys

### First launch

The app validates configured storage and opens a useful shell immediately. A scan discovers courses while reporting status. The learner can browse discovered lessons without waiting for every probe or conversion. Discovered non-MP4/non-MKV videos enter the background queue automatically. The UI distinguishes discovery from preparation and does not imply the entire library is ready immediately.

### Return to a course

The learner selects Continue learning. The app opens the remembered lesson, expands its location in the tree, chooses the preferred available rendition, restores position and playback preferences, and waits for the learner to press Play if the browser requires interaction. Finished courses offer replay rather than a misleading unfinished recommendation.

### Watch a legacy video

The learner selects a WMV lesson. If its playback copy is ready, it plays as the same lesson. Otherwise its job moves ahead of queued background work and the player displays preparation progress. The learner may navigate away. On return, completion of preparation has not marked the lesson watched.

### Move to Linux

The learner transfers the library and exports progress, starts the Linux installation against the mounted root, scans it, previews the import matches, and applies the transfer. The new absolute directory does not invalidate progress. Unmatched entries are reported rather than assigned to guesses.

## Non-goals

- Streaming services, downloads, DRM bypass, remote URL ingestion, or media acquisition.
- Public internet hosting, VPN provisioning, reverse-proxy provisioning, or multiple authenticated users.
- Editing/deleting/renaming original media or original subtitles.
- Automatic synchronization between installations.
- Bookmarks, timestamped notes, custom playlists, transcript search, recommendations, quizzes, or learning analytics.
- Phone-specific UI or native applications.
- Adaptive bitrate streaming, live transcoding playback, multiple worker replicas, or GPU encoding as initial requirements.

## Quality requirements

- Browser UI and lesson progress remain responsive while conversion is active.
- Original bytes must remain unchanged through scanning, preparation, cache eviction, and recovery tests.
- All source formats observed in the inspection have either a verified working playback path or a specific actionable failure.
- A restart cannot erase viewing progress, turn temporary media into ready content, or multiply already completed jobs.
- No external network dependency is required for normal browsing and watching after installation. Dependencies may be fetched during the build.
- Controls have accessible names, visible focus, and keyboard operation. Status is communicated with text as well as color.

## Success evidence

Release acceptance is behavioral, not a claim about pages being generated. Demonstrate the complete journeys above on a representative safe fixture library, then read-only discovery and user-authorized preparation against real media during the later implementation phase. Measure scan duration, time to first direct playback, API latency while encoding, resume accuracy, and conversion failure recovery. Detailed provisional targets and cases are in [verification](../implementation/verification.md).

## Scope control

Technical defaults can be refined through the implementation spikes. Confirmed product scope must remain stable unless the user changes it. An inability to use JsxCore, preserve media, or deliver required formats is a design issue to resolve explicitly, not a reason to quietly substitute a different stack or omit the requirement.
