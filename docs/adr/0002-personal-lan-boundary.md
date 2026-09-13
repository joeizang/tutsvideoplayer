---
status: accepted
date: 2026-09-13
---

# Keep one personal installation within localhost or LAN

The learner needs access on a Mac or from computers connected to a Linux-hosted installation, without accounts or public access. Use one shared personal progress store and no login, with loopback by default and explicitly configured LAN exposure.

## Considered options

- Public hosting and authenticated users: unnecessary scope and operational complexity.
- Independent browser-local progress: would lose continuity between laptops using the same installation.
- Synchronized Mac and Linux instances: rejected for v1 in favor of transfer between one active installation at a time.
- One server-side personal store: chosen for simple cross-browser continuity.

## Consequences

Every permitted LAN client can watch and use available settings/preparation controls. Origin/host checks reduce unwanted browser-triggered actions but are not user authentication. Deployment must bind and firewall appropriately; do not assume a Docker port published on all interfaces is LAN-restricted.

Multiple tabs need stale-write handling even with one person. The app does not create per-device user profiles. Progress transfer is an explicit operation, and a second process sharing the same data directory is rejected. Public access, accounts, and multiple active replicas require a new decision.
