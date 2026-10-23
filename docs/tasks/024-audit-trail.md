# 024: Audit trail — the endpoint and the viewer
**Phase:** 2   **Depends on:** 020-phase2-schema   **Parallel-safe with:** 021, 022, 023, 025

## Goal
`GET /api/requests/{id}/audit` returns a request's audit events newest first, paginated, and a
staff page displays them.

## Why this one task owns both halves

The audit viewer is a thin read over one endpoint, with no other feature touching either side. One
agent owning both avoids a contract handshake for the sake of it. It is the only Phase 2 task that
spans `api/` and `web/`, which is why its web scope is narrow and disjoint from 022's.

## Scope

**May touch**
- `api/src/FileInviteLite.Api/Features/Audit/GetRequestAuditTrail.cs`
- `api/tests/FileInviteLite.Api.Tests/Features/Audit/`
- `web/src/features/audit/` — `AuditTrailPage.tsx`, `routes.tsx`
- `web/src/app/routes.tsx` — **the one import line and the one route entry**, nothing else
- `web/tests/AuditTrailPage.test.tsx`
- `web/src/index.css` — additions only, no edits to existing rules

**Must not touch**
`Program.cs`, `AppDbContext`, `Data/Migrations/**`, any `.csproj`, `docs/api-contract.md`,
`Features/Requests/**`, `Features/Review/**`, `Features/Portal/**`, `Jobs/**`, `Services/**`,
`web/src/features/review/**`, `web/src/features/requests/**`, `web/src/features/portal/**`,
`web/src/app/apiClient.ts`, `web/src/app/AuthContext.tsx`, `package.json`.

> `web/src/app/routes.tsx` is yours this phase — task 022 is forbidden from it — but keep the edit
> to the single import and the single route so it reads like the entries already there.
> **`Program.cs` is forbidden**: this endpoint needs no new registration.

## Decisions (already made — implement, do not revisit)

- **Route and shape are exactly `docs/api-contract.md` §Audit.** `page` default 1, `pageSize`
  default 50 max 200, clamped the way `ListRequests` already clamps — reuse that behaviour's
  shape, do not invent a second convention. `.RequireAuthorization(AuthPolicies.Staff)`.
- **Scoped by request.** The endpoint takes a request id in the route — it is a staff endpoint, so
  that is allowed — but it must `404` for an unknown request id before it reads any audit row
  (S6.5), and it returns only events for that request.
- **"For that request" means the request's own events and its children's.** An `ItemUploaded` event
  carries a `ChecklistItem` id, not a request id, so filtering on `EntityId == requestId` alone
  would show an empty trail for the interesting actions. Resolve the request's item ids and file
  ids first, then filter `EntityId` against that set — in SQL, as a single query, not by loading
  the whole audit table. Write a test that an upload on item X appears in request X's trail and
  **not** in another request's trail.
- **`metadata` is returned parsed, as a JSON object**, not as an escaped string, because the
  contract shows an object. It is stored as a JSON string, so deserialise it; a row whose metadata
  is null returns `null`, and a row whose metadata will not parse returns `null` rather than
  throwing — a malformed legacy row must not break the trail.
- **Read-only, `AsNoTracking()`, newest first by `OccurredAt` descending**, with `Id` as a stable
  tie-breaker so paging cannot show or skip a row when two events share a timestamp.
- **No audit event is written by this endpoint.** Reading the trail is not a state change, and the
  contract lists no audit action for it. Do not add one.
- **The viewer** is a plain table, paginated, using the existing `useApi` hook and `apiClient`. No
  new dependency. Reachable from the request detail page's route tree by URL
  (`/requests/:id/audit`); you may not edit `RequestDetailPage.tsx` to add a link, because 022 owns
  that file — note in Progress notes that the link needs adding later.

## Acceptance criteria

- [ ] **S6.4** A staff user reads a request's audit trail, newest first, paginated, in the
      contract's shape
- [ ] **S6.5** An unknown request id returns `404`; no JWT returns `401`
- [ ] A portal session token on this endpoint returns `401` (S2.7)
- [ ] Events for the request's items and files appear in the trail; events belonging to another
      request do not — assert both directions
- [ ] Paging is stable across pages when two events share an `OccurredAt`
- [ ] `pageSize` above the maximum is clamped, not rejected with a `500`; a non-numeric `page`
      returns `400` in the shared problem shape
- [ ] **S6.6** A test drives a full happy path — create, send, exchange, upload, approve — and
      asserts that no returned metadata contains a token, a password, a storage key or file content
- [ ] **S6.3** A test asserts no application code path updates or deletes an audit event: grep the
      source for `AuditEvents.Remove`, `AuditEvents.Update` and `ExecuteDelete` on that set and
      assert there are none. A test that reads the source is unusual; it is the only way to pin a
      negative like this, and S6.3 is a non-negotiable
- [ ] All timestamps cross the boundary as UTC ISO-8601 with a `Z` (S10.3)
- [ ] The viewer renders loading, empty and error states, and paginates
- [ ] `npm run typecheck` and `npm run lint` clean — no `any`, no non-null assertion to silence the
      compiler

## Follow the pattern in

`Features/Requests/ListRequests.cs` (paging, clamping, query shape),
`web/src/features/requests/RequestListPage.tsx`, `web/src/app/useApi.ts`,
`web/tests/RequestListPage.test.tsx`.

## Done when

`npm run check` green, `dotnet format` clean, CI green, security-reviewer findings resolved.

## Progress notes

(append here as you go)

### 2026-10-23 — implemented (api + web)

- `Features/Audit/GetRequestAuditTrail.cs`: `GET /api/requests/{id:guid}/audit`, staff policy. 404 on
  unknown request before any audit row is read. One SQL filter matches `EntityId` against the
  request id plus correlated subqueries over its items, files (via items), review decisions,
  magic links, reminders and webhook deliveries; count and page are two statements over that one
  filter. Order `OccurredAt desc, Id desc`. Metadata parsed to a JSON node; null/unparseable -> null.
- Paging: `page` 1..int.MaxValue/200 else 400; `pageSize` >= 1 else 400, above 200 clamped to 200.
  NOTE: `ListRequests` does not clamp — it returns 400 above its max of 100. The task's "clamped"
  criterion was followed for pageSize; invalid values still 400 in the shared shape.
- Web: `features/audit/AuditTrailPage.tsx` + `routes.tsx`; `app/routes.tsx` gained one import and
  one spread. Route `/requests/:id/audit`.
- **Follow-up for task 022 (owner of `RequestDetailPage.tsx`): add a link to `/requests/:id/audit`.**
- Not done: "send" and "approve" endpoints do not exist yet (021/022), so the S6.6 happy-path test
  covers create, exchange (magic link seeded directly), upload. Extend it once they merge.
- Implementation was written before the tests (order deviation), so red-first was not observed.
