# 011: Send a request — magic-link generation and the dev email sink
**Phase:** 1   **Depends on:** 010-phase1-schema   **Parallel-safe with:** 012, 013

## Goal
`POST /api/requests/{id}/send` issues a magic link, moves the request to `Sent`, and returns the
link once — and `GET /api/requests/{id}` reads a request back without ever showing the link again.

## `GetRequest.cs` is part of this task

`GET /api/requests/{id}` is in the contract and in the spec (S1.10), but Phase 0c built only
`CreateRequest` and `ListRequests`, and no Phase 1 task file claimed it. This task's own criteria
(S1.12) are untestable without it — "no later read returns the token" needs a later read to
exist — so it belongs here, in the feature folder this task already owns. Added by the
orchestrator after 010 merged, not by an agent widening its own scope.

## Scope

**May touch**
- `api/src/FileInviteLite.Api/Features/Requests/SendRequest.cs`
- `api/src/FileInviteLite.Api/Features/Requests/GetRequest.cs`
- `api/src/FileInviteLite.Api/Services/Tokens/` — `IMagicLinkTokens` + implementation
- `api/src/FileInviteLite.Api/Services/Email/` — `IEmailSender`, `DevSinkEmailSender`,
  `NoOpEmailSender`
- `api/src/FileInviteLite.Api/Common/Security/PortalOptions.cs` — link lifetime and base URL
- `api/src/FileInviteLite.Api/Program.cs` — **registration lines only** for the services above
- `api/tests/FileInviteLite.Api.Tests/Features/Requests/SendRequestTests.cs`,
  `GetRequestTests.cs`
- `web/src/features/requests/RequestDetailPage.tsx` and that feature's `routes.tsx` — the page
  that shows the link and a copy button

**Must not touch**
`AppDbContext`, `Data/Migrations/**`, any `.csproj`, `docs/api-contract.md`, `Features/Portal/**`
(task 012 owns it), `web/src/features/portal/**` (013 owns it), `web/src/app/**`.

> `Program.cs` is shared with 012, which also registers a service. Keep your edit to one block at
> the point the other registrations already live, so the merge is trivial. If you need more than
> registration lines, stop and say so.

## Decisions (already made — implement, do not revisit)

- **Token:** 32 bytes from `RandomNumberGenerator`, Base64Url-encoded. Stored only as
  `SHA-256` hex (S2.1, S2.2). `IMagicLinkTokens` exposes `Generate()` returning the raw token and
  its hash, and `Hash(string)` for lookup. Nothing else ever hashes a token.
- **Lifetime:** `Portal:MagicLinkLifetimeDays`, default 14. Configuration, not a constant.
- **Link shape:** `{Portal:BaseUrl}/portal#t={token}` — the token goes in the **fragment**, so it
  never reaches a server log (ADR 0004).
- **Re-send revokes:** sending a request that already has a live link sets `RevokedAt` on it and
  issues a new one (S1.14), writing a `MagicLinkRevoked` audit event alongside.
- **Email:** `IEmailSender` with two implementations. `DevSinkEmailSender` writes a `.txt` file
  into `Email:Sink` and is registered **only in Development**; `NoOpEmailSender` everywhere else
  and records nothing. Registration is by environment, not by an `if` inside the sender.

## Acceptance criteria

- [ ] **S1.11** `POST /api/requests/{id}/send` returns `200` with `url` and `expiresAt`, moves the
      request to `Sent`
- [ ] **S1.12** No later read returns the token or the URL — `GET /api/requests/{id}` exposes only
      `issuedAt`, `expiresAt`, `revoked`. A test reads the request back and asserts the token
      string does not appear anywhere in the response
- [ ] **S1.13** `RequestSent` and `MagicLinkIssued` audit events are written in the **same
      transaction** as the link row; neither metadata contains the token
- [ ] **S1.14** Re-sending revokes the previous link; the old token's hash no longer matches a live
      row, and a `MagicLinkRevoked` event is written
- [ ] **S1.16** Sending a request with zero items returns `400`
- [ ] **S1.15** In Development a file lands in the sink containing the link; in Production no file
      is written and no send is attempted — assert both, by running the host in each environment
      the way `UnreadableBodyTests` does
- [ ] `401` without a JWT; `404` for an unknown request id
- [ ] **S2.2** A test asserts the database contains no row whose value equals the issued token, and
      that `TokenHash` equals the SHA-256 of it
- [ ] **No token in logs**: drive a successful send through a capturing log provider and assert the
      token does not appear at any level. Build `Infrastructure/LogCapture.cs` if it does not exist
      — `api/CLAUDE.md` already promises it
- [ ] The staff UI shows the returned link once with a copy control, and says plainly that it will
      not be shown again

`GetRequest`:
- [ ] **S1.10** `GET /api/requests/{id}` returns the request with its items, their statuses and the
      uploaded file metadata per item — **never file contents**, and never a storage key
- [ ] The response matches `docs/api-contract.md` §`GET /api/requests/{id}` field for field,
      including `createdBy`, `isOverdue`, `latestFile` and `latestDecision`. `ReviewDecision` does
      not exist until Phase 2, so `latestDecision` is always `null` for now — keep the field in the
      shape rather than omitting it, so the contract holds and 021 only has to populate it
- [ ] `magicLink` is `null` before the request is sent, and afterwards carries `issuedAt`,
      `expiresAt` and `revoked` and nothing else (S1.12). `issuedAt` is not a column on `MagicLink`
      — derive it from `ExpiresAt` minus the configured lifetime, or state in Progress notes why
      you could not, rather than adding a column (the schema is 010's and is closed)
- [ ] The newest magic link is the one reported, when a request has been sent more than once
- [ ] `latestFile` is the most recent upload per item (S3.13), `null` when there is none
- [ ] **S1.7** `401` without a JWT, `404` for an unknown id, and `401` — not `404` — when
      unauthenticated, so the endpoint never leaks whether the request exists
- [ ] A portal session token on this endpoint returns `401` (S2.7)
- [ ] **S10.3** Timestamps are UTC ISO-8601 with a `Z`; `dueDate` is `yyyy-MM-dd`
- [ ] Reading a request writes **no** audit event — a read is not a state change, and the contract
      lists no action for it

## Follow the pattern in

`Features/Requests/CreateRequest.cs`, `Features/Auth/Login.cs` (audit in one `SaveChangesAsync`),
`web/src/features/requests/CreateRequestPage.tsx`.

## Done when

`npm run check` green, `dotnet format` clean, CI green, security-reviewer findings resolved.

## Progress notes

(append here as you go)

### Task 011 — implementation notes

Done: `SendRequest.cs`, `GetRequest.cs`, `IMagicLinkTokens`/`MagicLinkTokens`, `IEmailSender` +
`DevSinkEmailSender` (Development only) + `NoOpEmailSender` (everywhere else), `PortalOptions`
(validated at startup), `RequestDetailPage.tsx` and its route. 40 new backend tests, 6 new frontend
tests.

Decisions and things the next person needs:

- **Token hash is lowercase hex**, decided in `MagicLinkTokens.Hash` (ADR 0004 only says "hex").
  **Follow-up for task 012's owner:** the dual-case match and the `TODO(011)` in
  `Features/Portal/ExchangeToken.cs` are now redundant. Replace the inline SHA-256 with
  `IMagicLinkTokens.Hash` and match on one value. Not edited here: outside this task's scope.
  (`PortalTestKit.HashToken` already uses lowercase.)
- **`issuedAt` is derived**: `ExpiresAt` minus `Portal:MagicLinkLifetimeDays`. If the lifetime
  setting is changed, links issued before the change report a shifted `issuedAt`; `expiresAt` is
  always exact. No column was added (the schema is closed).
- **"Newest link"**: live (unrevoked) first, then latest `ExpiresAt`. Re-sending revokes every
  earlier link, so there is at most one live link and it is the newest, even if the lifetime was
  shortened between sends.
- **Order of `items`** in `GetRequest` is unspecified in the contract and `ChecklistItem` has no
  position column; it matches `GetPortalRequest` (no explicit `OrderBy`, so storage order).
- **Ids are bound as `string`** on both endpoints and parsed in the handler, so a non-GUID id is a
  404 after authorisation, not a routing 404 that would make an unauthenticated caller see 404
  instead of 401 (S1.7).
- **Email failure after commit** is caught and logged by exception type only; the link is already
  issued and is returned to the caller, so a broken sink does not become a 500.
- `MagicLinkEmail` and `IssuedToken` override `ToString` so the credential cannot be printed by
  accident.
- `Program.cs`: one registration block (options, tokens, email by environment) and the stale
  "arrives with the Phase 1 portal task" comment corrected.
- Existing test touched: `web/tests/CreateRequestPage.test.tsx` asserted the exact list of request
  routes; the expectation gained `'/requests/:id'` (required by wiring the detail page). Nothing
  was weakened.
- Not done (out of scope): `RequestListPage` rows do not link to the new detail page
  (`RequestListPage.tsx` is not in this task's file list). The detail page is reachable at
  `/requests/{id}`. A follow-up should add the link.
- Not verified in a real browser: the page is covered by Vitest only.

### Security review findings, resolved by the orchestrator

An opus security review of this branch found no Critical or High issues. It confirmed, by reading:
32 bytes from `RandomNumberGenerator`; only the lowercase SHA-256 hex persisted; `IMagicLinkTokens`
the single hash site in product code; one `SaveChangesAsync` for the revocations, the new link, the
status change and all three audit events; the dev sink chosen by registration and writing nothing
in Production; `GetRequest` matching the contract field for field with no token, URL, storage key
or file content; and a portal session token answered `401` on both endpoints.

Two Medium findings, both fixed here rather than recorded and deferred:

1. **Concurrent sends could leave two live magic links.** `SendRequest` read the live links,
   mutated them and committed with no concurrency control, so two overlapping sends each revoked
   only what they saw and both inserted a live link — leaving a link staff believe is revoked still
   granting access, against S1.14. The read-modify-write is now inside an explicit transaction, as
   `api/CLAUDE.md` asks of a multi-write handler; SQLite refuses the second writer rather than
   interleaving, so one send succeeds and the other fails loudly. Still only one
   `SaveChangesAsync`. **Not reproduced by a test** — a deterministic test for this race needs two
   concurrent requests against one SQLite file, which is flaky; the fix is reasoned, not pinned.
2. **The one-shot link sat in a form control with no autofill suppression.** The URL was correctly
   in component state only, but an `<input readOnly>` is a value browsers may cache and restore
   across a reload and that extensions and password managers read. The input now sets
   `autoComplete="off"` and `spellCheck={false}` and carries no `name`, pinned by a new test so it
   cannot regress. The warning text also now says the link grants upload access and that copying
   leaves it on the clipboard.

Notes the review raised that need no change here:

- **012's dual-case hash match is redundant, not a weakness.** `hash` is a deterministic SHA-256 of
  the caller's input and `TokenHash` now has one writer, so the `|| upperHash` branch gives an
  attacker no extra preimages. The only residual is theoretical: if both cases of one hash were
  ever stored, `SingleOrDefaultAsync` would throw and produce a 500 distinguishable from S2.6's
  uniform 401. Unreachable today. The cleanup — use `IMagicLinkTokens.Hash` and a single equality,
  drop the `TODO(011)` — belongs in a sequential pass over `Features/Portal/`.
- **`issuedAt` derivation leaks nothing**, because `ExpiresAt` is in the same response and the
  lifetime is fixed. It can only mislead if the configured lifetime changes, which is documented;
  the authoritative record is the `MagicLinkIssued` audit event's real `OccurredAt`.
- **The dev sink keeps live links on disk until they expire**, with no cleanup. Intended by S1.15,
  path gitignored, Development only.
