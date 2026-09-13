# Confirmed requirements and provenance

The user confirmed the consolidated plan, then requested detailed documents only. Later answers override earlier suggestions. This record resolves the apparent conflicts rather than preserving obsolete alternatives as active requirements.

## Conversation decisions

| Question | Confirmed outcome |
| --- | --- |
| Initial request | C# / ASP.NET application for `/Users/josephizang/LEARNING-VIDEOS`, course folders and nested video tree, subtitles, aspect and quality controls |
| Q1, Q5, Q11 | Mac or Linux hosting; localhost or LAN only; no login. Phone-specific support excluded. The earlier VPN suggestion is superseded |
| Q2 | Learning workflow: resume, last lesson, completion, continue learning, previous/next |
| Q3 | Reflect existing folder organization; no rename, move, or delete tools for original content |
| Q4, Q7 | Selectable quality; 1080p default where possible; source fidelity and aspect preserved; no artificial upscaling |
| Q6 | One active installation, server-accessible media, progress transfer between installations; no live synchronization |
| Q8 | Automatic and manual subtitle association; explicit Off; remembered preference |
| Q9 | 0.5×–2×, keyboard shortcuts, ten-second skips, optional autoplay initially off, automatic completion near end, manual override |
| Q10 | Continue learning plus course search at home; collapsible naturally ordered course tree beside player |
| Q12, Q17 | Permanent converted files beside originals; 20 GB cache applies only to extra quality versions; original files untouched |
| Q13, Q18 | Automatically queue recognized non-MP4/non-MKV videos on discovery; skip completed conversions; one worker; selected lesson priority; pause/resume; TS/audio pairing; one logical lesson per original/copy |
| Q14 | Startup and manual scans; retain history for missing files |
| Q15 | ASP.NET Core 10 Web API with JsxCore and actual React; Docker for Linux. Blazor suggestion explicitly rejected |
| Q16 | Defer bookmarks, notes, playlists, subtitle-text search |
| Q19 | MP4/MKV excluded from bulk conversion; offer on-demand compatibility preparation if needed |
| Final agreement | SQLite; single host; temporary outputs and validation; recovery; implementation sequence accepted |
| Documentation instruction | No implementation yet. All docs under `docs`; future code/projects under `src`, beside root `.slnx` |

## Important interpretations

1. The original read-only preference means no destructive editing of source material. The later explicit request to put conversions beside originals permits creating app-owned outputs and metadata there. This does not permit overwriting unrelated files.
2. “All non-MP4/non-MKV files” means recognized video media, not subtitles, archives, text, source-code examples, padding, or arbitrary binaries. Companion AAC is attached to its TS lesson, not converted into a separate video lesson.
3. “Highest quality possible” means retain source dimensions and avoid unnecessary recompression. A lower-resolution WMV cannot acquire real 1080p detail through conversion. Lossy re-encoding is not advertised as lossless.
4. No login means all permitted LAN clients share one progress record and can invoke available management actions. There are no separate user profiles.
5. A complete implementation still needs build and playback verification. Documentation support for a framework is not proof that the future application works.

## Environment evidence

Read-only inspection during planning found an empty project directory with no Git repository or applicable ancestor `AGENTS.md`. The Mac is Apple Silicon with .NET SDK 10.0.302 and ASP.NET Core runtime 10.0.10 installed. FFmpeg and ffprobe are available under `/opt/homebrew/bin`. These are observations, not paths or versions to hard-code for Linux.

The library contained 19 top-level course folders, 937 MP4 videos, 60 WMVs, and 34 TS videos: 1,031 source videos. MP4/WMV content occupied approximately 18.43 GB, and TS content approximately 243 MB. These figures are approximate inspection results, not storage sizing guarantees.

There were 560 SRT files and 50 VTT files. 506 subtitle files had exact same-directory/same-stem MP4/WMV matches. Other files require more careful association; not all unmatched files are necessarily usable subtitles for currently discovered lessons.

All 60 WMVs were under `C# Generics`. The course `Using Configuration and Options in .NET Core and ASP.NET Core Apps` contained 34 TS files with matching `_audio.aac` companions. Sample MP4 was H.264/AAC at 1280×720; sample WMV was WMV3/WMA2 at 1024×768; sampled TS contained H.264 video without its companion audio. This was a sample audit, not a codec survey of all videos. The user’s spoken “WMD” refers to the observed WMVs.

## Implementation defaults requiring verification, not another planning interview

- SQLite schema, entity boundaries, API names, durable job protocol, and sidecar format in the design documents.
- 95% completion threshold, progress write cadence, export merge semantics, and preferred quality fallback.
- Desktop browser matrix and performance targets in the verification plan.
- Visual tokens and typography in the frontend design.
- Software encoding baseline, cache reservation rules, and operational disk reserve.

Do not silently relax a confirmed requirement to make a technical spike pass. Record the evidence and revise the affected design before implementation continues.
