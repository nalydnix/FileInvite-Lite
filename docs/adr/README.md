# Architecture Decision Records

One file per decision, numbered in the order it was taken. A record states the context, the
decision, and the consequences — including the ones we do not like. An ADR is never edited to
change its decision; it is superseded by a later ADR that references it.

| # | Decision | Status |
|---|---|---|
| [0001](0001-sqlite.md) | SQLite as the database, in development and in production | Accepted |
| [0002](0002-minimal-apis-vertical-slices.md) | Minimal APIs organised as vertical slices, with auto-registered endpoints | Accepted |
| [0003](0003-jwt-staff-auth.md) | JWT bearer tokens for staff authentication, held in memory in the browser | Accepted |
| [0004](0004-magic-links.md) | Magic links for client access, exchanged for a request-scoped session | Accepted |
| [0005](0005-file-storage.md) | Local disk file storage behind `IFileStorage`, with opaque keys | Accepted |
| [0006](0006-gdpr-erasure.md) | GDPR erasure against an append-only audit log — pseudonymise, don't delete | Accepted |
| [0007](0007-hosting.md) | Azure App Service for the API, Vercel for the web app, OIDC deploys | Accepted |

## Template

```markdown
# ADR NNNN: <decision in one line>

- **Status:** Proposed | Accepted | Superseded by ADR-NNNN
- **Date:** YYYY-MM-DD
- **Deciders:**

## Context
What forces are in play? What constraints are fixed?

## Decision
What we are doing, specifically enough to check code against.

## Consequences
### What this buys
### What it costs
Including the costs we accept rather than solve.

## Alternatives considered
| Option | Why not |
```
