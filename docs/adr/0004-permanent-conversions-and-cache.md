---
status: accepted
date: 2026-09-13
---

# Separate permanent sibling playback copies from evictable quality files

The user wants converted MP4s to live beside their originals and to survive cache cleanup. Retain compatibility copies permanently in source directories, while additional quality versions live in a managed subfolder with a 20 GB default budget.

## Considered options

- Convert/replace original files: violates original preservation.
- Put every generated file in an evictable cache: loses requested permanent converted MP4s and repeats work.
- Retain every quality version forever: unbounded growth despite the accepted cache limit.
- Two retention classes: chosen.

## Consequences

Preparation needs write access to the library. Earlier read-only language applies to originals, not to the explicitly authorized creation of managed copies and provenance. Cleanup can delete only owned evictable files. Permanent conversions and their temporary work consume additional physical space outside the cache limit.

Each output needs verifiable ownership and source association. Never overwrite a same-stem unrelated MP4. Sidecar provenance and the database describe one logical lesson with multiple representations. Stale permanent copies remain retained after source changes; deleting them is not part of automatic cleanup. See [media pipeline](../design/media-pipeline.md).
