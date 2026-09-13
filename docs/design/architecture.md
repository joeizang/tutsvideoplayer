# Architecture and repository layout

## System shape

Use a single deployable ASP.NET Core 10 application. It hosts controller-based Web API endpoints, React UI through JsxCore, seekable media delivery, and a durable background preparation worker. SQLite holds application data. The filesystem holds sources, permanent outputs, and evictable quality outputs. No separate JavaScript development server, cloud dependency, distributed broker, or second backend is required for normal operation.

```mermaid
flowchart LR
    Browser[Desktop browser: React and video element] --> Host[ASP.NET Core 10 Web API and JsxCore]
    Host --> DB[(SQLite application data)]
    Host --> Files[Library sources and ready renditions]
    Host --> Jobs[Background job coordinator]
    Jobs --> DB
    Jobs --> Probe[ffprobe and FFmpeg child processes]
    Probe --> Files
```

The diagrams describe intended design. As of milestone 0, the ASP.NET host, JsxCore entry view, JSON endpoints, health endpoints, seekable media delivery, and the Linux container path exist; SQLite, the background preparation worker, and the library/preparation features arrive with later milestones.

## Future repository layout

```text
tutsvideoplayer/
├── TutsVideoPlayer.slnx
├── docs/
│   ├── README.md
│   ├── CONTEXT.md
│   ├── product/
│   ├── prd/
│   ├── adr/
│   ├── design/
│   ├── implementation/
│   └── operations/
└── src/
    ├── global.json
    ├── Directory.Build.props
    ├── Directory.Packages.props
    ├── .config/dotnet-tools.json
    ├── TutsVideoPlayer.Core/
    │   ├── TutsVideoPlayer.Core.csproj
    │   ├── Catalog/
    │   ├── Learning/
    │   └── Preparation/
    ├── TutsVideoPlayer.Infrastructure/
    │   ├── TutsVideoPlayer.Infrastructure.csproj
    │   ├── Persistence/Configurations/
    │   ├── Persistence/Migrations/
    │   ├── FileSystem/
    │   ├── Media/
    │   └── Transfer/
    ├── TutsVideoPlayer.Web/
    │   ├── TutsVideoPlayer.Web.csproj
    │   ├── Program.cs
    │   ├── Features/
    │   │   ├── Library/
    │   │   ├── Learning/
    │   │   ├── Playback/
    │   │   ├── Preparation/
    │   │   ├── Subtitles/
    │   │   └── Settings/
│   ├── Hosting/
│   │   └── ...                    # health checks and registration helpers
│   ├── Views/                     # JsxCore TSX entry views, shared components, and stylesheets
│   ├── Media/                     # synthetic demo fixtures served through media endpoints
│   └── wwwroot/                   # bundled public assets only
│   ├── Tests/
│   │   ├── TutsVideoPlayer.Core.Tests/
│   │   ├── TutsVideoPlayer.IntegrationTests/
│   │   └── TutsVideoPlayer.BrowserTests/
│   ├── deploy/
│   │   ├── Dockerfile
│   │   └── compose.yaml
│   └── scripts/
│       └── generate-fixtures.sh
```

This layout was created in milestone 0 and matches the documented shape. All project and code files, including tests and deployment scripts, stay within `src`. The root `.slnx` references projects beneath it. Run SDK-sensitive build commands from `src` so its `global.json` participates in SDK selection; use `../TutsVideoPlayer.slnx` as the solution argument. This convention was validated in milestone 0 on macOS (`dotnet --version` from `src` resolves the pinned SDK) and inside the Linux container build.

## Dependencies and responsibilities

| Component | Owns | May depend on |
| --- | --- | --- |
| Core | Lesson/source identity rules, progress behavior, job transitions, quality policy value objects | BCL only |
| Infrastructure | EF mappings/context/migrations, safe path resolution, file provenance, media subprocess adapters, export serialization | Core, EF Core SQLite, platform APIs |
| Web feature handlers | Use cases, DTO projections, transactions, orchestration, API validation | Core and Infrastructure |
| Web controllers | Routing, binding, response status, ProblemDetails | Feature handlers |
| React client | Presentation, navigation state, browser player lifecycle, API interaction | JsxCore React runtime and browser APIs |
| Hosted worker | Durable queue polling/claiming, scoped orchestration, cancellation | Core and Infrastructure through short-lived scopes |

This is a modular single application, not a strict four-layer Clean Architecture template. Application orchestration lives in cohesive Web features and can use the concrete EF context. Domain state transitions stay inside focused domain objects. Do not add a generic repository, another unit-of-work, mediator, message bus, or a project per feature by default. See [ADR 0003](../adr/0003-sqlite-and-focused-domain-model.md).

## ASP.NET composition

Use `WebApplicationBuilder`/`WebApplication`. Register controllers with `[ApiController]` behavior, ProblemDetails and centralized exception handling, typed options validation, EF Core SQLite, JsxCore, a background worker, and health endpoints. Keep `Program.cs` as a readable composition root and extract focused registration extensions only when they clarify ownership.

Use `ControllerBase` for JSON endpoints and the JsxCore-documented view adapter for HTML entry routes. Keep JSON under `/api/v1`; keep media endpoints distinct from document routes. Do not mix Minimal API and controller styles inside product features. Small infrastructure health mappings are acceptable.

Request sequence: allowed-host/connection policy, production-safe error handling, request correlation, routing, same-origin mutation protection, bounded mutation rate limits, and mapped API/UI/media endpoints. Only add forwarded-header handling when a known local proxy is actually configured. Default HTTP on loopback or the configured trusted LAN avoids assuming certificates exist; do not enable unconditional HTTPS redirection without a working HTTPS listener.

Serve UI assets using the pinned JsxCore production asset workflow. Keep the external library outside `wwwroot`. UI route fallback must not turn `/api`, `/media`, missing assets, or unknown JSON routes into HTML success responses. Unknown API routes return appropriate HTTP errors.

No authentication middleware or Identity schema is required. Still restrict unsafe browser actions to intended same-origin requests, explicit allowed hosts, and the configured network boundary. Cross-origin CORS is unnecessary for a same-origin client. CORS alone is not a mutation-protection mechanism. Detailed handling appears in [API](api.md) and [runbook](../operations/runbook.md).

## JsxCore integration contract

Select actual React with the project's documented `JsxCoreFramework` setting. Use TSX for typed React components; the user's React choice does not require untyped `.jsx`. Keep interactive video behavior client-side. Use C# DTOs and the JsxCore-supported type-generation workflow where verified; never generate client contracts directly from EF entities.

The integration spike must establish the exact entry-view conventions, React version, import resolution, production build outputs, local asset bundling, and Linux publish behavior of the pinned JsxCore package. Avoid inventing unverified registration APIs in advance. Confirm direct navigation to a lesson route returns the application shell, then React fetches same-origin data. A small server-rendered shell is acceptable; player state must not depend on server-side browser APIs.

Do not silently replace JsxCore with Vite, Blazor, or ReactJS.NET if a component import fails. Prefer a native video element and small local React controls to minimize package compatibility risk. JsxCore documents API-plus-view hosting and selectable React; this design chooses that pattern. [Official integration guide](https://github.com/davidwhitney/JsxCore/blob/main/docs/views-and-web-apis.md), [runtime selection](https://github.com/davidwhitney/JsxCore/blob/main/docs/runtimes.md).

### Resolved entry conventions (validated in milestone 0)

The spike used JsxCore 1.0.2 with `<JsxCoreFramework>react</JsxCoreFramework>`, which restores React 19.3 from npm, compiles TSX with the native TypeScript 7 compiler, and minifies Release assets with esbuild 0.28.2. No Node process runs at build or request time.

- Registration: `builder.AddJsxCore()` on the host builder and `app.UseJsxCore()` before `UseRouting()`.
- Entry view: `Views/Home/Index.tsx` default-exports a component typed with `ViewProps<T>`; the controller returns `this.Jsx("Home/Index", model, RenderMode.ServerAndClient)` for first paint plus hydration.
- Shared types: records in a namespace containing the `Models` segment are exported automatically; views import them as `dotnet:types/TutsVideoPlayer/Web/Models` — the .NET namespace with its dots as slashes.
- Stylesheets imported beside a view become document links automatically; keep styles under the views directory or the web root.
- `dotnet build` creates `package.json`/`package-lock.json` beside the project; commit both, ignore `node_modules` and generated `obj/` output except `Views/tsconfig.json`, which editors use.
- Release publish emits precompiled minified assets. React's "dead code elimination has not been applied" console error is a Debug-build artifact and does not appear in Release.
- The runtime writes generated modules under the content root's `obj/JsxCore`, so containers must run as a user that owns the published output (`COPY --chown=$APP_UID`); a root-owned copy fails startup with `UnauthorizedAccessException`.
- Integration tests share one `WebApplicationFactory` in a single xunit collection: concurrent hosts in one process race on JsxCore's stylesheet staging directory (`obj/JsxCore/js/_dist/.staging`) and fail intermittently.
- Shared React components and stylesheets live under `Views/Shared`: JsxCore refuses relative imports that resolve outside the views directory, so the planned top-level `Client/` folder is folded into the views tree (recorded in milestone 1).

## Concurrency and lifetime rules

- Request contexts are scoped; no DbContext is shared among parallel tasks.
- The singleton hosted service creates a fresh scope/context for each bounded database operation. It never retains a transaction during FFmpeg execution.
- A database job record is durable truth; an in-memory wake-up signal only reduces latency.
- A single installation lock prevents a second process using the same application data from starting normal work.
- Limit conversion concurrency to one. Probe concurrency starts at one or two and is measured against disk pressure. Playback delivery does not acquire the encoder semaphore.
- Respect cancellation for requests, enumeration, child-process waits, and shutdown. A disconnected request does not cancel an already accepted durable preparation job.

## Backend status to UI

Start with bounded polling: every two seconds while relevant jobs/scans are active and the page is visible, backing off when idle or hidden. Progress acknowledgements have their own ten-second playback cadence. Use request cancellation and revisions to avoid stale responses. SignalR is unnecessary initially; add push only if measured UX or load requires it.

## Build and package policy

Target `net10.0`, nullable enabled, implicit usings, centrally managed stable dependencies, and an SDK version pinned during implementation. Match EF runtime, design package, and dotnet-ef major versions. Commit migrations as source. Do not adopt preview .NET, Native AOT, trimming, or speculative compiled-query optimization.

External fonts/scripts must be bundled locally for runtime offline use. Keep FFmpeg version and codec availability in diagnostics; software encoding is the baseline on both platforms. Reproducible restore/build and actual Linux playback are release requirements, not assumptions based on a Mac build.
