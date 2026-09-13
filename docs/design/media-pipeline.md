# Media preparation, delivery, and recovery

## Responsibilities and trust boundary

The media subsystem discovers/probes local media, selects a compatible encoding or remux recipe, writes app-owned outputs, validates them, and exposes only ready renditions. It never modifies source video, companion audio, or original subtitles. Scanning and processing take paths from a constrained catalog, not arbitrary HTTP parameters.

Keep FFmpeg arguments structured through `ProcessStartInfo.ArgumentList`, disable shell execution, and invoke a configured executable. Do not build shell command strings from filenames. Concurrently drain bounded stdout/stderr streams to avoid deadlocks; capture structured progress and a bounded diagnostic tail. Set cancellation, process-tree shutdown, and watchdog rules. Never accept user-supplied FFmpeg switches.

## Discovery and probing

1. Enumerate only the configured root, ignoring reserved cache/provenance directories and incomplete outputs.
2. Recognize MP4, MKV, WMV, TS, MOV, M4V, AVI, WEBM, MPG/MPEG, MTS/M2TS, and FLV as initial video candidates. Add formats through tests, not by probing every unknown file. AAC alone is a companion candidate, not a lesson.
3. Associate `name.ts` with same-directory `name_audio.aac` when exactly one match exists. Missing/ambiguous required audio becomes an actionable issue. Do not silently produce a silent copy for the known split-stream course.
4. Probe candidates for actual video/audio stream presence, duration, codecs, dimensions, pixel aspect, rotation, pixel format, frame rate, and stream timestamps. A file extension alone cannot establish browser compatibility.
5. Cache probe metadata against size/modified-time hints; establish full source-set content hashes before conversion and transfer identity decisions.
6. Queue eligible permanent work only after a committed discovery record exists. A recurring reconciliation pass fills a crash-created scheduling gap.

Use bounded probe timeouts and treat unsupported/corrupt media as per-lesson issues. A source can intentionally have no audio, but known companion patterns must not be silently ignored. Multiple embedded audio tracks use the marked default or first usable track for v1 and preserve source files; multi-audio selection is not a promised feature.

## Source-preserving recipes

| Input condition | Permanent playback recipe |
| --- | --- |
| MP4/MKV with playable browser support | Direct playback; no automatic compatibility job |
| MP4/MKV fails compatible playback | User-requested MP4 compatibility job, reused afterward |
| Non-exempt container with compatible video/audio streams | Remux to MP4 when validation proves compatibility; avoid re-encoding |
| WMV3/WMA2 source | Encode H.264 video and AAC audio into MP4 at native display size |
| TS H.264 plus separate AAC | Map video and companion audio explicitly; try stream copy with timestamp normalization; transcode only if necessary |
| Unsupported/corrupt source | Report failure and retain original; do not create a fake ready output |

Initial software encoding profile: H.264 via a verified encoder such as libx264, yuv420p, native frame rate, quality-oriented CRF 18 with medium preset, AAC at 192 kbps for stereo when audio re-encoding is required. These settings are proposed baselines to validate on tutorial text, not guarantees of lossless output. Preserve stream copies when possible. Handle mono without unnecessary channel duplication. Verify encoder availability on both platforms; do not silently fall back to an untested GPU profile.

Prepare MP4 for file-based streaming with metadata placed for prompt startup. FFmpeg distinguishes stream copying from transcoding and supports explicit input-stream mapping; its MP4 muxer documents fast-start behavior. Recipe details must be verified against the pinned FFmpeg build. [FFmpeg processing documentation](https://ffmpeg.org/ffmpeg.html), [MP4 format options](https://ffmpeg.org/ffmpeg-formats.html).

The recipe must normalize display rotation/aspect deliberately. Use even dimensions where required by pixel format, preserve display aspect within rounding tolerance, and never upscale. If converting HDR/high-bit-depth material would require tone mapping, report it for a tested recipe rather than silently washing out the picture. The currently sampled library does not establish whether such material exists.

## Quality profiles

Profiles are native, 1080-height, 720-height, and 480-height, with actual dimensions recorded. Do not generate a profile when it would upscale or duplicate an existing equivalent rendition. For a 1024×768 source, retain native 768-height and optionally create lower 720/480 versions. For a 1280×720 source, native already satisfies 720p. For a 3840×2160 source, native remains selectable and 1920×1080 is the default requested version.

Quality recipes read original source components, not another lower-quality rendition. Record recipe version, source-set fingerprint, output dimensions, codec parameters, and output digest. A software upgrade alone does not invalidate every successful output; recipe compatibility is explicit and existing valid files remain reusable unless a correctness issue requires replacement.

## Output naming and ownership

Proposed permanent name: `<original-stem>.tvp-<source-set-hash-prefix>.mp4` beside the source video. Its sibling manifest is `<output-name>.tvp.json`. Use a sufficiently long digest prefix and extend/disambiguate safely on collision. Never overwrite an unrelated existing file, even if the filename looks managed.

Proposed quality path: `<library-root>/.tutsvideoplayer/quality/<lesson-id>/<source-generation>/<profile>-<recipe>.mp4`. Temporary quality work stays within the same managed subtree; permanent conversion temporary files stay in the destination source directory for same-filesystem publication. The scanner excludes reserved paths and recognizes committed provenance before deciding that a sibling MP4 is a new lesson.

Every manifest includes a schema version, app ownership marker, logical library and lesson IDs, source relative paths and roles, full source digests, output digest/length, retention class, dimensions/codecs, recipe version, and publication state. It never grants access outside the root. Validate paths, sizes, enums, and schema version before adoption. A filename suffix alone is not ownership evidence.

If an ordinary `lesson.mp4` already exists beside `lesson.wmv` with no provenance, do not infer successful conversion from matching stems. Report ambiguous pairing or retain separate source candidates. Automatically produced files always include sufficient provenance to avoid this ambiguity in future scans.

## Durable publication protocol

Filesystem operations and SQLite commits cannot be one atomic transaction. Use a recoverable protocol:

1. Claim the job in SQLite with its source generation, recipe, lease, and reserved output path.
2. Verify current inputs and space. Record temporary and intended final paths in the attempt before launching FFmpeg.
3. Encode/remux to a unique temporary file; never stream this file to the learner.
4. Recheck source identity and validate output. Calculate output digest and length.
5. Write and flush an app-owned manifest in a `Prepared` state containing the expected final file digest. Record publication intent in the job.
6. Rename the output to its final path without overwrite, within the same filesystem. Flush as supported. Mark the manifest `Committed` through its own atomic replacement.
7. In a short database transaction, register the ready rendition and mark the job succeeded. Release reservations.

Crash recovery checks publication intent, manifests, and hashes before requeueing. A verified final file plus Prepared manifest can be adopted and completed. A manifest without output is incomplete. An output without sufficient provenance is quarantined from automatic deletion/adoption and shown as a recovery issue. Recovery cannot atomically guarantee both files at every instant; it guarantees that each interrupted state has an explicit reconciliation path.

Do not overwrite a stale permanent copy after a source change. A new source digest gives a new output name. Old permanent copies remain retained and are reported as superseded; automatic cache cleanup must not delete them. A future explicit permanent-copy cleanup workflow is outside v1.

## Output validation

- Require a nonempty output and successful ffprobe.
- Require a decodable video stream, intended audio when required, expected codecs/dimensions, and reasonable duration.
- Compare durations to the source timeline with a provisional tolerance of max(2 seconds, 1%); flag unexplained truncation rather than silently passing it.
- Decode representative beginning/middle/end intervals during implementation verification and validate full short fixtures. Production validation uses bounded checks plus successful encode status and probing; it is not a proof that every frame is visually perfect.
- Check audio/video start offsets and synchronization on split-stream fixtures. Do not use an indiscriminate shortest-stream option that hides unexpectedly truncated audio.
- Detect changed source metadata during work and rehash if identity is uncertain before publication.
- Treat failed validation as Failed, keeping a bounded diagnostic record and cleaning only proven owned temporary files.

## Queue, interruption, and retries

Lease timestamps are application-managed and renewed while a job runs. On startup, exclusive installation ownership plus expired leases identify interrupted jobs. Reconcile output first; restart from scratch only if no valid output exists. Never assume a persisted Running state means an encoder is still alive.

Pause stops new claims and lets the current job finish. Shutdown requests graceful child-process termination, then kills the owned process tree after a bounded timeout and records Interrupted. CPU time elapsed alone is not failure; a no-progress watchdog and cancellation are different from a hard encode-duration limit. Long videos may legitimately take a long time.

Classify failures: Blocked for unavailable mount/permission/disk or missing companion; Failed for unsupported/corrupt source and invalid output; Interrupted for process shutdown. Retry transient process/I/O errors at most twice with backoff as an initial default. Stable format errors require explicit retry or changed inputs. Rescanning does not reset the retry count for unchanged failed work.

## Cache accounting and deletion

The 20,000,000,000-byte limit applies to quality preparation: ready quality bytes plus reserved/temporary quality work. Avoid double-counting a reservation and its growing temporary file; charge the larger of reserved and current size per attempt. Permanent copies and permanent-copy temporary bytes are outside this cache limit but still subject to real free-space checks.

Before quality work, estimate an output budget and evict eligible least-recently-watched quality files until it fits. Monitor growth during work; request additional capacity or stop safely when the budget cannot expand. A source whose single quality output cannot fit remains blocked with an actionable message. Never violate the limit silently. A 2 GB physical free-space reserve applies independently to each volume receiving output.

Protect active rendition leases, open streams, and current jobs. Use playback heartbeat leases plus short grace periods so a browser making successive range requests does not lose its selected file between requests. Stale leases expire. Eviction first claims an eligible rendition as Evicting with a revision check so no new playback session can select it, then removes only its validated managed path and manifest, then marks it Evicted. Startup reconciles interrupted evictions.

Cache reduction takes effect through this same process. If active playback prevents reaching the new limit, report pending cleanup and block new quality work; do not interrupt playback. Update LastAccess at session start or a throttled heartbeat, not on every media range read.

## Media delivery

Expose opaque rendition IDs mapped server-side to verified ready files. Use framework file results with range processing, correct content type and length, and validators for immutable outputs. Support HEAD, normal GET, byte ranges, conditional requests, and unsatisfiable-range errors. Stream from disk without buffering the full video in memory. Do not apply JSON response compression or API output caching to video bytes.

Only complete validated files are served. Quality switching loads another complete file and seeks to the saved time; it may briefly buffer. The player must not describe this as seamless adaptive streaming. HLS/DASH and playback while transcoding are deferred by [ADR 0007](../adr/0007-file-based-quality-delivery.md).

Source files can be modified outside the app. Revalidate expected identity/length when opening a source-backed stream and stop offering it if stale. A request must not serve a replaced file based only on a previously trusted path.

## Subtitle conversion

SRT is normalized into UTF-8 WebVTT, keyed by source fingerprint. Normalized subtitle files are small app metadata under the managed subtitle directory, separately reported from the 20 GB video-quality budget and bounded by a modest housekeeping limit. Source SRT/VTT is never modified. Preserve cue timing; permit only supported safe cue text. Invalid syntax or encoding is reported per track.

Keep parser limits on input bytes, cue count, text length, and timestamps. Conversion should not load unlimited input. Existing VTT can be validated and served as text/vtt. Tracks never become arbitrary downloadable host paths.

## Filesystem containment

Resolve catalog paths within the configured root, reject traversal/absolute paths, and reject symlinks for v1 to avoid leaving the library or following loops. Validate output parent directories and recheck immediately before creation/open. Do not use a simple string prefix to decide containment (`/library-other` is not under `/library`). Use platform-aware path segment checks and safe open semantics where available. A hostile local administrator is outside scope, but remote API input and malformed library metadata are not trusted.
