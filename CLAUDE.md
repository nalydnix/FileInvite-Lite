# FileInvite-Lite — conventions for agents

A prototype document-collection platform. Staff request documents, clients upload them through a
magic link, staff approve or reject. Read `docs/spec.md` for what it must do and
`docs/architecture.md` for how the code is laid out.

> An independent learning prototype inspired by FileInvite; not affiliated with or endorsed by
> FileInvite.

## Where to look first

| Question | File |
|---|---|
| What must this feature do? | `docs/spec.md` (criteria are numbered: S3.6) |
| Where does my code go? | `docs/architecture.md` |
| What is the route and shape? | `docs/api-contract.md` |
| Why is it like this? | `docs/adr/` |
| What am I allowed to touch? | your task file in `docs/tasks/` |
| Backend specifics | `api/CLAUDE.md` |
| Frontend specifics | `web/CLAUDE.md` |

## Commands

```bash
# backend
dotnet build  api/FileInviteLite.sln
dotnet test   api/FileInviteLite.sln
dotnet test   api/FileInviteLite.sln --filter FullyQualifiedName~Portal
dotnet run    --project api/src/FileInviteLite.Api        # http://localhost:5080

# frontend  (from web/)
npm ci
npm run dev          # http://localhost:5173
npm run build
npm run test
npm run typecheck
npm run lint
```

Both halves together: `npm run dev:all` from the repo root.

## Rules

### Scope

1. **Stay inside the file scope your task file lists.** If the work seems to need a file outside
   it, stop, write why in the task's Progress notes, and say so in your summary. Do not widen the
   scope yourself.
2. **Never touch these from a parallel task.** They are sequential-task territory and the only
   real merge hotspots in the repo:
   - `api/src/FileInviteLite.Api/Program.cs`
   - `api/src/FileInviteLite.Api/Data/AppDbContext.cs`
   - `api/src/FileInviteLite.Api/Data/Migrations/**`
   - any `.csproj`, `.sln`, `package.json`, `package-lock.json`
   - `docs/api-contract.md`
3. A feature is added by **adding files to one feature folder**. If you are editing a shared file
   to add a feature, the design is wrong — say so rather than working around it.

### Tests

4. **Never weaken, skip, delete or rewrite an acceptance test to make a build pass.** If a test
   looks wrong, say so and stop. A failing acceptance test is information, not an obstacle.
5. Tests name the criterion they cover: `Upload_RejectsExeRenamedToPdf_S3_6`.
6. **Run every check before you call a task done**: backend tests, frontend tests, typecheck,
   lint. Report what you actually ran and what it said. Never report a task as done on an
   unverified build.
7. Rebase onto the latest `main` and re-run everything before opening a PR.

### Security — non-negotiable

These come from `PLAN.md` §4 and are enforced by review, not just by tests.

8. **Every state-changing action writes an `AuditEvent` through `IAuditWriter`, committed in the
   same transaction as the state change.** Two `SaveChangesAsync` calls is a bug.
9. **Audit events are never updated or deleted by application code.** (One declared exception
   exists for GDPR erasure — ADR 0006 — and it is not reachable from feature code.)
10. **A magic link grants access to exactly one request.** Every portal query filters by
    `CurrentPortal.RequestId` before anything else. Never read a request id from a route or body
    on a portal endpoint.
11. **Store only hashes of magic-link tokens.** Never log a token, JWT, password, session token or
    file content — at any log level.
12. **Staff and portal credentials are never interchangeable.** A staff JWT must not authorise a
    portal endpoint, and a portal token must not authorise a staff endpoint.
13. **Validate uploads by extension *and* content sniffing**, and enforce the 20 MB limit
    server-side while streaming. Never trust `Content-Length`.
14. **Cross-request access returns `404`, not `403`.** Do not confirm that someone else's resource
    exists.
15. **Never commit a secret.** Signing keys and webhook secrets come from user secrets or
    environment configuration. If you need a new secret, add the configuration key and document
    it — do not invent a default and inline it.
16. Uploaded files are never served from a static path; downloads go through the authenticated
    endpoint and write an audit event.

### Working style

17. Match the surrounding code: same naming, same structure, same comment density. The reference
    slice in `api/src/FileInviteLite.Api/Features/Requests/CreateRequest.cs` is the pattern to
    copy.
18. Append to your task file's **Progress notes** as you go, so a fresh session can resume without
    re-deriving anything.
19. Report honestly. If a criterion is unmet, a test is skipped, or something is unverified, say
    so plainly in your summary.
20. When something goes wrong in a way a guardrail could have prevented, add an entry to
    `docs/ai-log.md`.

## Workflow per task

```
task file → test-writer (red tests) → implement (green) → security-reviewer → PR + CI → human review → merge
```

Parallel tasks run in separate worktrees with non-overlapping scopes. One PR merges at a time.
