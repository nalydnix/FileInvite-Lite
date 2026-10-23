# ADR 0002: Minimal APIs organised as vertical slices, with auto-registered endpoints

- **Status:** Accepted
- **Date:** 2026-10-23
- **Deciders:** project owner (human), drafted with Claude Opus

## Context

The defining constraint of this project is that **several agent sessions write code in parallel, in
separate worktrees**, and their branches have to merge cleanly. The usual layered layout
(`Controllers/`, `Services/`, `Repositories/`, `Models/`) spreads one feature across four folders
and concentrates edits in files every feature touches. Two agents adding two features collide in
all four places.

A second constraint: an agent's quality depends on how much of the system it must hold in its head.
A handler that contains its own route, request shape, validation and data access can be read,
written and reviewed without opening anything else.

## Decision

**ASP.NET Core minimal APIs, organised in vertical slices, one endpoint per file, auto-registered
by reflection.**

- One folder per feature under `Features/`. A feature owns its endpoints and nothing else owns
  them.
- One endpoint per file, named after the action: `CreateRequest.cs`, `ApproveItem.cs`.
- Each endpoint class implements `IEndpoint` with a single `Map(IEndpointRouteBuilder)` method. At
  startup, `MapEndpoints()` scans the assembly and calls `Map` on every implementation.
  **Adding an endpoint never edits `Program.cs`.**
- Each entity gets one `IEntityTypeConfiguration<T>`, loaded by
  `ApplyConfigurationsFromAssembly`. **Adding configuration never edits `AppDbContext`.**
- Handlers use `AppDbContext` directly. No repository layer, no MediatR.
- **A feature folder never references another feature folder.** Shared behaviour is promoted into
  `Services/` or `Common/`, in a sequential task.

## Consequences

### What this buys

- Two agents adding two features touch two disjoint sets of files. The merge is a no-op.
- The remaining hotspots are explicit and few — `Program.cs`, `AppDbContext`, `Migrations/`,
  project files, `docs/api-contract.md` — and `PLAN.md` §7 routes all of them through sequential
  tasks.
- A task file can say "follow the pattern in `Features/Requests/CreateRequest.cs`" and that one
  file really is the whole pattern.
- Review is local. A reviewer reading `UploadFile.cs` sees the route, the auth policy, the
  validation, the query scoping and the audit write together, which is where the security
  properties in `docs/spec.md` §S2–S3 actually live.

### What it costs

- **Some duplication between slices.** Two handlers may validate an email the same way. Accepted:
  duplication across slices is cheaper than coupling, and genuinely shared logic moves to
  `Services/`.
- **Reflection at startup.** Negligible for this assembly size, and a test asserts that every
  `IEndpoint` was mapped, so a mistyped class cannot silently vanish.
- **Discoverability depends on naming.** Mitigated by the one-action-per-file rule and the layout
  in `docs/architecture.md`.
- **No pipeline for cross-cutting behaviour** of the kind MediatR behaviours give. Cross-cutting
  concerns are handled by ASP.NET middleware (errors, CORS, rate limiting, logging) or explicit
  service calls (`IAuditWriter`).

## Alternatives considered

| Option | Why not |
|---|---|
| **MVC controllers, layered** | Spreads one feature over four folders; every feature edits shared files. Worst case for parallel agents. |
| **Minimal APIs, grouped in one `Endpoints.cs`** | Simple, but a single file every agent edits — the exact merge conflict this decision exists to prevent. |
| **Vertical slices + MediatR** | The handler-per-request shape fits, but it adds a registration surface and a layer of indirection for an app with no cross-cutting pipeline needs. Slices already give isolation. |
| **Explicit registration list in `Program.cs`** | One line per endpoint, in a file every parallel task would then touch. Reflection removes the conflict entirely. |
| **Repository layer over EF** | EF's `DbSet` is already the abstraction; wrapping it adds files without removing coupling, and makes the per-handler query scoping (`WHERE RequestId = portal.RequestId`) harder to see during review. |
