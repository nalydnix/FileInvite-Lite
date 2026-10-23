# FileInvite-Lite

> An independent learning prototype inspired by FileInvite; not affiliated with or endorsed by
> FileInvite.

A document collection platform: staff request documents from a client, the client uploads them
through a magic link without creating an account, and staff approve or reject each one. The system
chases overdue requests, records every action in an append-only audit log, and notifies external
systems when a request completes.

It exists to demonstrate AI-assisted engineering — planning work for agents, verification loops,
parallel development in worktrees, context management, and grounding agents in a codebase — rather
than to be adopted. The engineering process is as much the subject as the product.

## Status

**Phase 0a complete: documentation only. No application code yet.**

| Phase | What | State |
|---|---|---|
| 0a | Spec, architecture, API contract, ADRs, agent guardrails | Done |
| 0b | .NET solution and Vite app scaffold, CI | Not started |
| 0c | Reference slice, written by hand | Not started |
| 0d | Task breakdown for phases 1–3 | Not started |
| 1 | Portal and request builder | Not started |
| 2 | Review, reminders, audit viewer, webhook | Not started |
| 3 | Production readiness, deployment, runbook | Not started |

## Documentation

| | |
|---|---|
| [docs/spec.md](docs/spec.md) | Acceptance criteria, numbered and testable |
| [docs/architecture.md](docs/architecture.md) | Folder layout, layering rules, data model |
| [docs/api-contract.md](docs/api-contract.md) | Endpoints, shapes, errors |
| [docs/adr/](docs/adr/README.md) | Why it is built this way, and what that costs |
| [CLAUDE.md](CLAUDE.md) | Conventions and rules for agent sessions |
| [docs/review-checklist.md](docs/review-checklist.md) | Reviewing AI-generated code |
| [docs/ai-log.md](docs/ai-log.md) | What went wrong, and the guardrail added |
| [PLAN.md](PLAN.md) | The original plan, frozen after Phase 0 |

## Planned stack

ASP.NET Core 9 minimal APIs in vertical slices · EF Core + SQLite · React + TypeScript + Vite ·
xUnit and Vitest · GitHub Actions → Azure App Service and Vercel.

Screenshots, a demo video and run instructions arrive with Phase 3.
