---
status: accepted
date: 2026-09-13
---

# Host React through JsxCore alongside ASP.NET Core 10 Web API

The user explicitly selected ASP.NET Core 10 Web API and JsxCore for React after rejecting Blazor. Use one ASP.NET application to serve the React interface, JSON endpoints, and media, keeping deployment and same-origin browser communication together.

## Considered options

- Blazor: rejected by the user; it does not meet the requested UI technology.
- Separately built/deployed React SPA: familiar but replaces the requested JsxCore integration and adds another deployment surface.
- JsxCore with default Preact: does not meet the explicit actual React choice.
- JsxCore with React and controller APIs: chosen; matches the stack and supports one deployable host.

## Consequences

Explicitly select React in JsxCore configuration. Verify the pinned package's production asset build and Linux behavior in the first implementation milestone. Keep interactive player behavior in browser components and HTTP controllers separate from domain logic. JsxCore conventions and package import compatibility are integration constraints; a failed spike calls for a documented resolution rather than silent substitution.

The proposed source layout uses TSX components for typed React, which is compatible with the user's React requirement. All application assets needed at runtime must be served locally. See [architecture](../design/architecture.md) and the [upstream runtime guide](https://github.com/davidwhitney/JsxCore/blob/main/docs/runtimes.md).
