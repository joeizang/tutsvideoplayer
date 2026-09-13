# PRD 0003: Playback, subtitles, and media preparation

## Functional requirements

| ID | Requirement | Acceptance |
| --- | --- | --- |
| PLAY-01 | Stream complete playable renditions with seeking | Seeking near the end does not download the entire video first |
| PLAY-02 | Preserve source proportions by default | Both sampled 16:9 and 4:3 sources display correctly |
| PLAY-03 | Provide fit/fill, volume, fullscreen, seek, ten-second skips, speed, and previous/next | Controls work with pointer and keyboard; fill explicitly crops rather than stretching |
| PLAY-04 | Prefer 1080p where available, with original and lower choices | No option falsely labels a lower-resolution source as 1080p |
| PLAY-05 | Preserve viewing state through quality changes | Position, pause/play state, speed, volume, and subtitle choice survive a source switch |
| PLAY-06 | Autoplay is optional and off initially | Browser autoplay rejection shows a Play action, not a failed lesson |
| SUB-01 | Read SRT and VTT sidecars | SRT is presented in a browser-usable form without modifying the source |
| SUB-02 | Support matching beyond adjacent files | Separate Subtitle folders are searched conservatively within the course |
| SUB-03 | Explicit Off and manual association | Off survives lesson changes and restarts; ambiguous subtitles are not silently chosen |
| PREP-01 | Automatically prepare recognized videos except MP4/MKV | Initial known automatic candidates are 60 WMVs and 34 TS lessons, subject to scan results |
| PREP-02 | Never repeat a valid completed conversion | Rescan and restart reuse validated current-source outputs |
| PREP-03 | Preserve originals and retain permanent sibling MP4 copies | Byte checks verify source video/audio/subtitle files are unchanged |
| PREP-04 | Process one job at a time with priority and pause/resume | Selected queued lesson precedes ordinary backlog; queue survives restart |
| PREP-05 | Support existing split TS/AAC sources | Prepared output contains the correct video and audio with acceptable synchronization |
| PREP-06 | Offer MP4/MKV compatibility preparation on demand | Extension exemption is not misrepresented as universal browser support |
| PREP-07 | Show progress, failure reason, and retry | Incomplete output is never offered as ready playback |

## Quality policy

Permanent playback copies preserve native display dimensions and frame rate where compatible; they are not capped to 1080p. For source above 1080p, retain the original/native copy and prepare a 1080p quality version when requested through playback or course preparation. Extra 720p and 480p versions are available only where downscaling is meaningful. Nonstandard native sizes retain honest labels, such as Original (1024×768).

Default selection picks a ready 1080p rendition, otherwise the highest ready rendition at or below 1080p. If only a higher-resolution playable source exists, allow immediate original playback and queue the preferred 1080p version on explicit lesson opening; tell the learner which quality is currently playing. Do not interrupt playback to switch automatically when a new rendition becomes ready. Quality selection is explicit file selection in v1, not adaptive bitrate streaming.

For portrait or unusual ratios, “1080p” is a height target, with actual width and height also shown. Scaling never increases source display height or width, and output preserves display aspect. The implementation spike must verify rotation and non-square-pixel handling before accepting a source as correctly processed.

Quality versions are generated from source media, using stream copy when applicable and avoiding serial generation from already degraded lower-quality versions. If originals are missing but a permanent copy remains, it can still play; do not silently use it as a replacement master for regeneration.

## Subtitle policy

Order matching by explicit saved association, exact adjacent stem, exact course-local Subtitle-directory stem, then unambiguous normalized title/language match. Do not automatically choose among equal candidates. Language suffixes are parsed conservatively and labels show the available language or filename. A manual selection is constrained to discovered subtitles in that course.

Subtitle preference is installation-wide: enabled/Off plus preferred language where available. A lesson-specific selected track overrides language preference for that lesson, but global Off always wins until the learner turns subtitles on. If the preferred track is missing, explain this and offer available alternatives without forgetting the preference.

Support text captions only in v1. Embedded subtitle extraction and image-based subtitles are deferred; report such tracks without pretending they are selectable sidecars. Do not burn subtitles into video. Reject unsafe markup when normalizing text, bound parser memory, retain original timings, and report malformed or undecodable files with a useful explanation. An encoding override may be provided in subtitle association details as an implementation fallback; never silently replace undecodable text with corrupted captions.

## Preparation lifecycle and user control

Discovery queues permanent compatibility work automatically. The learner can pause the global queue, resume it, prepare extra quality versions for a course, prioritize a selected lesson, and retry failed jobs. Pausing allows the active encode to finish and prevents new jobs starting; the UI must explicitly say so. Host shutdown or a crash interrupts active encoding; the job restarts from the beginning after recovery. “Resume” promises durable queue continuation, not byte-level continuation of a partial MP4.

Opening a non-ready lesson boosts queued priority without killing the current encode. Show Queued, Preparing with percentage when duration is known, Checking output, Ready, Paused, Blocked, or Failed. Unknown duration shows indeterminate progress. A failed job does not loop forever on each rescan; retries are bounded and meaningful changes can make it eligible again.

## Storage and failure behavior

Permanent copies are retained outside the 20 GB limit. Additional quality files are evictable; current playback and active work are protected. Insufficient space blocks preparation with a recoverable state. Cache cleanup removes only manifest-owned quality files and temporary files proven to belong to the app. Detailed reservations, provenance, collision handling, and crash windows are in [media pipeline](../design/media-pipeline.md).

Do not overwrite an existing same-stem MP4 on the assumption that it is a previous conversion. Verify provenance or report a collision. A changed source invalidates the old conversion's relationship to the current source; retain the old permanent copy and prepare a versioned replacement without destroying either original.

## Acceptance scenarios

1. A 1024×768 WMV becomes a correctly proportioned compatible MP4; no fake 1920×1080 version is offered.
2. An MP4 that plays directly does not enter automatic compatibility conversion.
3. A non-playable MKV can request one reusable compatibility job.
4. A TS/AAC lesson plays with sound after preparation and remains one tree item.
5. Killing the process before or after publication leaves either a recoverable job or a reusable ready copy, never two lessons or corrupted ready media.
6. Turning subtitles Off survives a quality switch, lesson change, and application restart.
7. A quality switch during paused playback stays paused at the same position.
8. Cache pressure cannot remove the permanent WMV conversion or a rendition in active use.
