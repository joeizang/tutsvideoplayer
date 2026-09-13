---
status: proposed
date: 2026-09-13
---

# Keep preparation jobs durable while running the worker inside the host

Automatic bulk preparation must survive restarts and skip completed work, but the application has only one installation and one encoder. Store job state in SQLite and run a single in-process hosted worker, with explicit reconciliation between job records, manifests, and output files.

This is the recommended implementation decision for the accepted durable preparation behavior. It is not an implemented or validated subsystem.

## Considered options

- In-memory task queue alone: loses pending work and cannot reliably distinguish completed publication after a crash.
- External broker/worker platform: adds infrastructure and coordination beyond the local product's needs.
- SQLite jobs plus hosted worker and reconciliation: chosen baseline.

## Consequences

An in-memory signal may wake the worker but is never durable truth. Jobs have deduplication keys, leases, retries, and state revisions. DbContext scopes and transactions are short; encoding runs outside them. Exclusive installation ownership prevents duplicate encoders.

Filesystem rename and database commit are not atomic together. Persist publication intent and provenance, validate outputs, then reconcile crash windows before requeueing. Resume means restarting interrupted encoding when necessary. Optional in-memory events cannot be the sole delivery mechanism for required work.

If multi-host execution or externally reliable event delivery becomes necessary, revisit the worker boundary and consider a broker/outbox. See [domain state machine](../design/domain-model.md) and [publication protocol](../design/media-pipeline.md).
