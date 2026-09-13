---
status: proposed
date: 2026-09-13
---

# Deliver completed MP4 renditions with byte ranges in the first version

The learner needs selectable quality on localhost/LAN, not adaptive internet delivery. Prepare complete MP4 renditions and serve them through seekable file endpoints; switch quality by changing the player's source while restoring position and preferences.

## Considered options

- HLS/DASH with adaptive bitrate ladders: useful for variable internet bandwidth but adds segments, manifests, packaging, and player integration.
- Live transcoding on every playback: reduces preparation storage but increases CPU/seek complexity and conflicts with permanent prepared copies.
- Complete MP4 files with HTTP range delivery: chosen baseline for predictable local playback and reusable outputs.

## Consequences

Preparation must finish before that new rendition plays. Existing compatible originals can play while other work continues. A quality switch may briefly buffer and is not advertised as seamless adaptation. Only validated ready files are exposed; partial files never become playback sources.

Byte-range correctness, cache leases, browser codec behavior, and state restoration become important verification gates. This decision can be revisited if measured LAN usage requires adaptive streaming, but HLS/DASH is not added speculatively. See [playback PRD](../prd/0003-playback-and-preparation.md).
