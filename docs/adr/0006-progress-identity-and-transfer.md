---
status: proposed
date: 2026-09-13
---

# Separate lesson identity from absolute paths and playback copies

The same course library may move from a Mac path to a Linux mount, and one lesson may have several media representations. Give lessons stable logical identities, retain relative source paths and content fingerprints, and transfer progress through a versioned logical document.

## Considered options

- Absolute path as identity: breaks when moving installations.
- Filename alone: collides across courses and assigns progress incorrectly after replacement.
- Content hash alone: merges identical content intentionally present in multiple courses and costs a full-library hash at every scan.
- Persistent IDs plus relative paths, generation tracking, and conservative fingerprint evidence: chosen baseline.

## Consequences

Hashing is targeted to identity decisions, preparation, and export of watched lessons rather than blindly repeated on every scan. Identical files at different course positions remain distinct lessons. Source replacement preserves old history and does not silently carry completion to new content.

Import previews expose ambiguous and conflicting matches. Default to preserving local conflicts rather than using unreliable cross-machine timestamps. Sidecars retain provenance for managed copies, but are validated before adoption. Database backup remains distinct from logical progress transfer. See [domain model](../design/domain-model.md) and [operations PRD](../prd/0004-operations-and-portability.md).
