# 001: Reference slice — create request and list requests
**Phase:** 0c   **Depends on:** 0b scaffold (merged)   **Parallel-safe with:** nothing — this is a sequential task

## Goal
Build "create a request" and "list requests" end to end, as the pattern every later feature slice
is copied from.

## Why this one is different

This is the **reference slice**. Phase 1 and 2 task files will say "follow the pattern in
`Features/Requests/CreateRequest.cs`", and agents will copy whatever is there — including the
mistakes. Correctness matters more than speed, and consistency matters more than cleverness.

It is also a **sequential task**, so it may touch the shared files that parallel tasks are
forbidden from touching (`AppDbContext`, migrations, project files, the API contract). Nothing
else runs at the same time.

## Design decisions (already made — implement these, do not revisit)

| Area | Decision |
|---|---|
| Validation | **FluentValidation.** One validator class per endpoint, next to the endpoint, wired as an endpoint filter so a handler never calls the validator itself. Failures must come out as the project's existing problem shape via `ProblemResults.Validation(errors)` — not FluentValidation's default. |
| Derived state | **On the domain entity.** `DocumentRequest.RollUpStatus()` for the status rule, plus a `static Expression<Func<DocumentRequest, bool>>` for the overdue predicate so it translates to SQL. Handlers and queries both use those — the rules are never rewritten inline. |
| Frontend fetching | **A small `useApi` hook** in `web/src/app/`, wrapping `apiClient` with loading, error and refetch. No TanStack Query. |
| List endpoint | **The full documented contract** — pagination, `status` filter, `overdue` filter and `sort`. Do not trim it. |

## Scope

**May touch**
- `api/src/FileInviteLite.Api/Domain/` — `DocumentRequest.cs`, `ChecklistItem.cs`
- `api/src/FileInviteLite.Api/Data/AppDbContext.cs` — the two new `DbSet` lines only
- `api/src/FileInviteLite.Api/Data/Configurations/` — one configuration per new entity
- `api/src/FileInviteLite.Api/Data/Migrations/` — one new migration for both entities
- `api/src/FileInviteLite.Api/Features/Requests/` — `CreateRequest.cs`, `ListRequests.cs`
- `api/src/FileInviteLite.Api/Common/Validation/` — the endpoint filter that turns FluentValidation
  failures into `ProblemResults.Validation`
- `api/src/FileInviteLite.Api/Program.cs` — registering FluentValidation validators only
- `api/src/FileInviteLite.Api/FileInviteLite.Api.csproj` — the FluentValidation package only
- `api/src/FileInviteLite.Api/Domain/AuditEvent.cs` — adding `RequestCreated` to `AuditAction`
- `api/tests/FileInviteLite.Api.Tests/Features/Requests/`
- `web/src/app/useApi.ts`
- `web/src/features/requests/` — `CreateRequestPage.tsx`, `RequestListPage.tsx`, `routes.tsx`
- `web/src/app/routes.tsx` — replacing the Phase 0c placeholder with the real routes
- `web/tests/`

**Must not touch**
- `docs/api-contract.md` — the contract is already written; implement it as specified. If it is
  genuinely wrong, stop and say so in Progress notes rather than changing either side.
- Anything under `Features/Auth/`, `Features/Health/`, `Features/Portal/`, `Services/Audit/`
- `.github/`, `.claude/`, `docs/adr/`, `docs/spec.md`
- The existing migration, `20261023045556_InitialStaffAndAudit`

## Acceptance criteria

Backend — create (`POST /api/requests`):
- [ ] **S1.1** A staff JWT creates a request with items; `201` with the id and a `Location` header
- [ ] **S1.2** Zero items → `400`, with `items` named in the `errors` dictionary
- [ ] **S1.3** A due date in the past → `400`
- [ ] **S1.4** An invalid client email → `400`
- [ ] **S1.5** A new request is `Draft`; every item is `Outstanding`
- [ ] **S1.6** A `RequestCreated` audit event is written **in the same transaction** — one
      `SaveChangesAsync`. A test must prove that a failed insert leaves no audit event behind
- [ ] **S1.7** No JWT → `401`, from both endpoints

Backend — list (`GET /api/requests`):
- [ ] **S1.8** Returns id, client name, due date, status, item count, approved count, `isOverdue`
- [ ] **S1.9** Filters by `status` and by `overdue`; sorts by due date ascending by default;
      `sort=createdAt` is honoured
- [ ] Pagination: `page`, `pageSize` (default 20, max 100), and a `total` that reflects the
      filters, not the page
- [ ] `pageSize=101` → `400`
- [ ] **The overdue filter is evaluated in SQL, not in memory.** A test must prove the predicate
      translates — SQLite cannot translate `DateTimeOffset`, which is why the model uses UTC
      `DateTime` and `DateOnly` (ADR 0001)

Frontend:
- [ ] **S9.5** A signed-in staff user can create a request and see it in the list
- [ ] The create form shows per-field server validation messages from the `errors` dictionary
- [ ] The list has a loading state, an empty state and an error state
- [ ] Overdue rows are distinguishable by something other than colour alone
- [ ] `useApi` is used by both pages; no component calls `fetch` or `apiClient` directly

Cross-cutting:
- [ ] Every error body matches `docs/api-contract.md` — FluentValidation's own response shape must
      not leak through
- [ ] All ids are GUIDs; all timestamps UTC with a `Z`

## Follow the pattern in

- `api/src/FileInviteLite.Api/Features/Auth/Login.cs` — endpoint shape, audit write, one
  `SaveChangesAsync`, `ProblemResults`
- `api/src/FileInviteLite.Api/Data/Configurations/AuditEventConfiguration.cs` — entity config,
  enums stored as strings
- `api/tests/FileInviteLite.Api.Tests/Features/Auth/LoginTests.cs` — test naming (`..._S1_6`) and
  `ApiFactory` usage
- `web/src/features/auth/LoginPage.tsx` and `web/tests/LoginPage.test.tsx`

## Done when

- `npm run check` is green from the repo root (build, 13+ backend tests, frontend tests,
  typecheck, lint)
- `dotnet format api/FileInviteLite.sln --verify-no-changes` is clean
- The new migration applies to a database created from the previous migration, not only to a
  fresh one
- `npm run dev` runs, and creating a request through the UI is visible in the list
- The `security-reviewer` agent has reviewed the branch and its findings are resolved
- CI is green on the PR

## Progress notes

(append here as you go, so a fresh session can resume without re-deriving anything)

### Session 1

- Read task file, spec S1/S9.5, api-contract Requests section, architecture, three CLAUDE.md files.
- **Schema first, then tests.** Tests cannot compile without `db.DocumentRequests`, so the domain
  entities, EF configurations, `DbSet` lines, `AuditAction.RequestCreated`, the FluentValidation
  package and the migration `AddDocumentRequestsAndChecklistItems` went in before the red tests.
  Endpoints were deliberately not written until the tests existed and were seen to fail.
- Package: `FluentValidation.DependencyInjectionExtensions` 12.2.0 (brings `FluentValidation`
  transitively; needed for `AddValidatorsFromAssembly…` so a new validator never edits Program.cs).
- Migration is additive only (two new tables); `InitialStaffAndAudit` is untouched.
- **Decision — `RollUpStatus(bool magicLinkIssued)`**: the spec rule needs "a magic link has been
  issued" but `MagicLink` does not exist until a later task, so the flag is a parameter. A request
  with no *required* items is never `Complete` (the rule is vacuously true there, and S1.5 needs a
  new request to be `Draft`). Spec is silent on this; flagged for the owner.
- **Decision — overdue rule**: `DocumentRequest.IsOverdue(DateOnly today)` returns an
  `Expression<Func<DocumentRequest,bool>>`; the list handler uses it for the SQL filter and compiles
  the same expression once per request to compute `isOverdue` on returned rows.

### Session 1 (continued) — state at hand-off

- Red tests written by the `test-writer` agent (84 fail on the unmapped route, 24 pass against the
  existing domain), then endpoints implemented. `dotnet test`: 122 pass, **2 fail** (below).
  `dotnet format --verify-no-changes` clean; no pending model changes; frontend 23 tests, typecheck
  and lint green.
- Migration upgrade proven by `MigrationUpgradeTests` (applies on top of `InitialStaffAndAudit` with
  rows present).
- Ran the real app (`npm run dev`), signed in, empty state renders. Submitting the create form with
  no date returned **500** — found by hand, not by the criteria.
- security-reviewer: no Critical/High. One Medium (page offset overflow) — fixed: `page` capped at
  `int.MaxValue / 100`, test added.

**BLOCKED — scope question for the owner.** Framework body-binding errors (malformed JSON, an empty
or unparseable `dueDate`) never reach `ValidationFilter`. In Development ASP.NET *throws*
`BadHttpRequestException`, so `UseExceptionHandler` turns it into a **500**; outside Development it
is a 400 in the framework's own shape (wrong `type`, no `errors`). Either way it violates the
cross-cutting criterion "every error body matches the contract", and a user clearing the date field
hits it. The fix is an `IExceptionHandler` (in `Common/`) mapping `BadHttpRequestException` to
`ProblemResults.Validation(...)`, **registered with one line in `Program.cs`** — which the task's
scope limits to FluentValidation registration. Two tests pin this:
`Create_UnparseableDueDate_…_S10_1` and `Create_MalformedJson_…_S10_1`. Not skipped or weakened.

### Open decisions / assumptions (flag in PR)
- `sort=createdAt` is ascending (contract gives no direction).
- `overdue=false` returns only non-overdue requests.
- Nested validation keys are `items[0].title`; bad query values are keyed by parameter name.
- `detail` is absent on validation errors: `ProblemResults.Validation(errors)` does not set it and
  `Common/Results` is outside this task's scope. The contract example shows `detail`.
- Email check is stricter than FluentValidation's `EmailAddress()` (which accepts spaces).
- All-optional-items request never becomes `Complete` (pinned by a test); owner to confirm.
- Styling is inline/minimal because `web/src/index.css` is outside scope.
