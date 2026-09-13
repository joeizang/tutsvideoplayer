# Tuts Video Player documentation

Status: agreed product direction; implementation specification. Updated 2026-09-13.

This is the documentation for a personal tutorial-video library built with ASP.NET Core 10 Web API, JsxCore, and React. Milestone 0 (stack validation) was implemented on 2026-09-13: the solution, host application, JsxCore/React entry view, health and demo endpoints, seekable media delivery, tests, and the Linux container path now exist. No library data, database, migration, or media conversion exists yet, and milestones M1–M7 remain planned.

## Reading order

1. [Confirmed requirements and decision provenance](product/decisions.md)
2. [Product requirements](prd/0001-product.md)
3. [Library and learning requirements](prd/0002-library-and-learning.md)
4. [Playback and preparation requirements](prd/0003-playback-and-preparation.md)
5. [Operations and portability requirements](prd/0004-operations-and-portability.md)
6. [Domain glossary](CONTEXT.md) and [domain model](design/domain-model.md)
7. [Architecture and repository layout](design/architecture.md)
8. [Persistence and query design](design/persistence.md)
9. [Media processing and recovery](design/media-pipeline.md)
10. [API contracts](design/api.md)
11. [Frontend design and interaction specification](design/frontend.md)
12. [Deployment and operations](operations/runbook.md)
13. [Implementation plan](implementation/plan.md) and [verification matrix](implementation/verification.md)
14. [Sources and skill application](references.md)

Documentation-only checks are recorded in the [documentation review](implementation/documentation-review.md).

## Architectural decisions

| ADR | Decision | Status |
| --- | --- | --- |
| [0001](adr/0001-single-host-react-jsxcore.md) | One ASP.NET host with Web API and actual React through JsxCore | Accepted |
| [0002](adr/0002-personal-lan-boundary.md) | One installation and shared personal state within localhost/LAN | Accepted |
| [0003](adr/0003-sqlite-and-focused-domain-model.md) | SQLite/EF Core with focused domain behavior and direct data access | Accepted direction; detailed implementation defaults |
| [0004](adr/0004-permanent-conversions-and-cache.md) | Permanent sibling conversions separate from evictable quality files | Accepted |
| [0005](adr/0005-durable-media-jobs.md) | Durable in-process jobs with file/database reconciliation | Implementation decision |
| [0006](adr/0006-progress-identity-and-transfer.md) | Stable lesson identity separate from machine paths | Implementation decision |
| [0007](adr/0007-file-based-quality-delivery.md) | Complete MP4 renditions with byte-range delivery for v1 | Implementation decision |

## How to use this specification

**Confirmed** requirements come from the planning conversation. **Implementation defaults** make those requirements executable but were not individually selected by the user: thresholds, filenames, schema fields, polling cadence, visual tokens, and API routes. They are the recommended baseline, not historical claims about user approval. Change them through a documented design revision if validation reveals a problem. Product scope changes require a separate conversation.

The PRDs describe observable behavior. Design documents describe mechanisms. ADRs explain durable trade-offs. The glossary contains only domain language. The implementation plan defines deliverables and evidence; its checkboxes intentionally remain incomplete.

All documentation belongs under `docs`, including this glossary. Application code, test code, scripts, project files, and container definitions live under root-level `src`, created in milestone 0. The root-level `TutsVideoPlayer.slnx` sits beside `src` and `docs`.

## Cross-cutting release conditions

- Never delete or overwrite original media or original subtitles.
- Never evict permanent conversion copies through cache management.
- Never expose the library as a static directory or accept arbitrary filesystem paths through the API.
- Never report a conversion as ready before it is validated and recoverably published.
- Never lose progress merely because scanning failed or a mount is temporarily unavailable.
- Never substitute Blazor, Preact, ReactJS.NET, or a separate frontend stack for the specified React/JsxCore integration.
- Verify real SQLite behavior and real browser media playback before declaring the release usable.
