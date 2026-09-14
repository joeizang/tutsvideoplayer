# Sources, skills, and evidence

Checked during documentation on 2026-09-13. External documentation can change; implementation must pin dependencies and validate behavior rather than treating these links as executable proof.

## User-requested skills and their application

| Skill | Applied guidance | Documents |
| --- | --- | --- |
| ASP.NET Core | Modern host, controller APIs, feature cohesion, typed options, ProblemDetails, scoped services, hosted work, integration tests | Architecture, API, runbook, verification |
| Domain modeling | Canonical glossary, precise lesson/source/rendition vocabulary, concrete edge cases, selective ADRs | CONTEXT, domain model, ADRs |
| DDD | Small consistency boundaries, value objects/typed IDs, behavior for progress/jobs, no giant library aggregate | Domain model, ADR 0003 |
| EF Core | Explicit mappings, DbContext as unit of work, projections, reviewed migrations, provider-specific decisions | Persistence, architecture, runbook |
| Optimizing EF Core queries | SQL evidence, no lazy loading/N+1, no premature materialization, deliberate tracking/split/compiled queries | Persistence, verification |
| Frontend design | Subject-specific layout, compact visual tokens, alternatives and critique, purposeful typography, accessible interactions | Frontend design |

The requested ASP.NET skill's `.agents` installation contained its SKILL.md but lacked referenced files. Matching references were found and read in the installed `.codex/skills/aspnet-core/references` directory. No skill was silently skipped because of the missing duplicate references.

The domain-modeling skill normally places CONTEXT.md at the repository root. The user's explicit instruction puts all docs under `docs`, so the glossary is `docs/CONTEXT.md`. It remains a glossary only; implementation detail is in separate design documents.

The frontend skill generally asks for mobile reflow; the user's explicit exclusion of phones limits release verification to desktop/laptop and small-window usability. No mobile-specific scope is introduced.

The DDD/EF repository guidance is reconciled explicitly in ADR 0003. SQLite migration limitations override generic provider-agnostic idempotent-script examples. The query optimization skill is used to specify future evidence and query shape; no existing slow queries or performance improvements are claimed.

## Resolved implementation versions (milestone 0, 2026-09-13)

Pinned in `src/global.json` and `src/Directory.Packages.props`, and in the Web project's generated `package.json`/`package-lock.json`.

| Component | Version |
| --- | --- |
| .NET SDK (`rollForward: latestFeature`) | 10.0.302 |
| ASP.NET Core runtime (macOS and Linux validation) | 10.0.10 |
| JsxCore | 1.0.2 |
| React / ReactDOM | 19.3.0 |
| TypeScript (native compiler, restored by the build) | 7.x |
| esbuild (Release minifier, restored by the build) | 0.28.2 |
| EF Core / SQLite / Design / dotnet-ef | 10.0.12 |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.12 |
| xunit.v3 / xunit.runner.visualstudio | 3.2.2 / 3.1.5 |
| Microsoft.NET.Test.Sdk | 18.10.0 |
| Container images | `dotnet/sdk:10.0.302`, `dotnet/aspnet:10.0.10` |
| Tailwind CSS (CLI, build-time; output committed) | 4.3.3 |

## Primary technical references

| Source | Supports |
| --- | --- |
| [JsxCore runtime selection](https://github.com/davidwhitney/JsxCore/blob/main/docs/runtimes.md) | Actual React selection rather than default Preact |
| [JsxCore views and Web APIs](https://github.com/davidwhitney/JsxCore/blob/main/docs/views-and-web-apis.md) | Same ASP.NET host for frontend views and API requests |
| [JsxCore getting started](https://github.com/davidwhitney/JsxCore/blob/main/docs/getting-started.md) | Framework setup reference; exact package conventions must be pinned and tested |
| [ASP.NET Core Web APIs](https://learn.microsoft.com/en-us/aspnet/core/web-api/?view=aspnetcore-10.0) | Controller routing, validation, and API conventions |
| [ASP.NET hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0) | In-process worker lifetime and scoped-service patterns |
| [EF Core SQLite limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations) | Provider migration/type/concurrency limits |
| [EF Core efficient querying](https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying) | Projection, bounded result sets, and query-cost discipline |
| [FFmpeg processing documentation](https://ffmpeg.org/ffmpeg.html) | Stream selection, stream copy, transcoding, process progress options |
| [FFmpeg formats](https://ffmpeg.org/ffmpeg-formats.html) | MP4 container/muxer behavior |

These sources inform the design. Cache budgets, filenames, API routes, thresholds, state machines, and project boundaries are application-specific recommendations, not claims that a framework mandates them.

## Evidence boundaries

Local library counts and sample codecs came from read-only inspection in the planning conversation. They are recorded in [decision provenance](product/decisions.md). The Linux host and a complete codec inventory were not inspected.

Milestone 0 (2026-09-13) produced a runnable stack: reproducible restore/build/publish of the root solution, a JsxCore React view verified hydrated in a real browser on macOS and in a Linux container, same-origin JSON fetch, and byte-range media delivery (206 partial content) with a synthetic fixture. No library data, database, or conversion behavior exists yet; the real video library was not touched. Later milestones still require their own runtime evidence.

The documentation task creates Markdown only. Its validation checks structure, internal links, requirement coverage, and consistency. Runtime tests, renders, schema migrations, and conversion experiments are future implementation work.
