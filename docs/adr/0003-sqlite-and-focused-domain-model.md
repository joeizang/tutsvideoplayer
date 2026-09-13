---
status: accepted
date: 2026-09-13
---

# Use SQLite and EF Core with focused domain behavior

The user accepted SQLite for a single personal installation and requested DDD and EF Core guidance. Use SQLite through EF Core 10, model progress and preparation invariants in small domain objects, and let feature handlers query/project through DbContext without generic repositories or a second unit-of-work layer.

The database choice is confirmed; the three-project layout and direct-context pattern are implementation decisions in this documentation.

## Considered options

- JSON-only application state: simple initially but awkward for atomic progress, job claims, uniqueness, and querying.
- PostgreSQL/SQL Server: capable but adds a separate database service for a small local application.
- Full repository/CQRS framework with an aggregate spanning the library: ceremony and broad consistency boundaries without corresponding domain need.
- SQLite/EF with focused domain objects and projected reads: chosen.

## Consequences

DbContext is the unit of work. Rich state transitions remain on aggregate roots; direct persistence is not permission to bypass invariants. The DDD skill's repository-per-aggregate guidance and the EF skill's rejection of redundant wrappers are reconciled by using EF as the aggregate persistence mechanism, without exposing child-entity CRUD endpoints or adding pass-through interfaces. Reconsider a specialized repository only if a real boundary requires it.

Keep contexts short-lived, avoid lazy loading, and verify generated SQL. Single-process ownership and short transactions fit SQLite's write model. Store media outside the database and the database on local storage.

The generic EF reference's production idempotent-script recommendation is unsuitable for this provider. Use an explicit reviewed migration command/bundle or known-version script with backup and stopped workers. [Provider limitations](https://learn.microsoft.com/en-us/ef/core/providers/sqlite/limitations), [persistence design](../design/persistence.md).
