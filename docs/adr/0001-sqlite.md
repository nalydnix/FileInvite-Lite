# ADR 0001: SQLite as the database, in development and in production

- **Status:** Accepted
- **Date:** 2026-10-23
- **Deciders:** project owner (human), drafted with Claude Opus

## Context

FileInvite-Lite is a prototype whose purpose is to demonstrate AI-assisted engineering, not to
carry production load. It still needs a real relational store: the data model has eight related
entities, an append-only audit table, and queries that filter on dates and statuses.

Constraints that shaped the decision:

- Hosting is Azure App Service on the **F1 free tier** (ADR 0007). F1 gives one instance, 1 GB of
  storage on a network-backed `/home` volume, and the app sleeps when idle.
- A managed database would add either cost or another provisioning step to a repo meant to be
  cloned and run with one command.
- Agents write most of the code. A database that needs no container, no connection string
  juggling, and no extra setup removes a whole class of "it works in my worktree" failures.
- Parallel agent sessions run integration tests at the same time, so tests must not contend over
  one shared server.

## Decision

Use **EF Core with SQLite** for all environments, including production.

- The provider is chosen by configuration (`Database:Provider`), so another provider can be added
  later without touching feature code.
- The database file and the uploaded files both live on App Service's persistent `/home` volume.
- Journal mode is **WAL locally** and **DELETE in production**. `/home` is a network share; WAL's
  shared-memory index does not behave reliably on one.
- Deployment is pinned to a **single instance**. Two instances over one network-backed SQLite file
  would corrupt it.
- Timestamps are stored as **UTC `DateTime`**, not `DateTimeOffset`.
- Enums are stored as **strings**, not ordinals.
- Integration tests use a **real SQLite file per test class** in a temp folder, not the EF
  in-memory provider.

## Consequences

### What this buys

- `git clone` then `dotnet run` works, with no database to provision.
- Test classes run in parallel without contention, each on its own file.
- The query translation under test is the one that runs in production, because the provider is the
  same everywhere.
- Zero database cost.

### What it costs

- **No scale-out.** One instance, permanently. Scaling the app means migrating off SQLite first.
- **1 GB storage cap** on F1, shared between the database and uploaded files.
- **The app sleeps when idle**, so background passes (reminders, webhook retries) stop while it
  sleeps. Both jobs catch up from database state rather than assuming they ran on schedule.
- **Backups are manual**: a dated copy of the database file, documented in the runbook.
- **Writers serialise.** Fine at prototype volume; a cliff under real concurrency.
- **No `DateTimeOffset` in queries.** The SQLite provider cannot translate comparisons or ordering
  on `DateTimeOffset`, and the overdue check needs exactly that. Hence UTC `DateTime` everywhere,
  with conversion at the API boundary.

### Upgrade path (not built now)

The configuration-driven provider plus `IFileStorage` make the move mechanical when it is needed:
Azure SQL (free offer) for the database, Blob Storage behind a second `IFileStorage`
implementation. Neither is in MVP scope.

## Alternatives considered

| Option | Why not |
|---|---|
| **PostgreSQL in a container** | Best production answer, but it adds a container to every clone and a managed instance (or a VM) to host. Cost and setup friction for a prototype. |
| **Azure SQL free offer** | Viable, and the documented upgrade path. Rejected for now: another resource to provision and keep inside a free tier's limits, for a prototype with no load. |
| **EF in-memory provider for tests** | Rejected outright. It does not translate queries the way a relational provider does, so tests would pass on code that fails in production — exactly the failure mode agent-written code is prone to. |
| **LiteDB / document store** | The data is relational and the audit requirements want transactions across tables. |
