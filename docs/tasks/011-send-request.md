# 011: Send a request — magic-link generation and the dev email sink
**Phase:** 1   **Depends on:** 010-phase1-schema   **Parallel-safe with:** 012, 013

## Goal
`POST /api/requests/{id}/send` issues a magic link, moves the request to `Sent`, and returns the
link once.

## Scope

**May touch**
- `api/src/FileInviteLite.Api/Features/Requests/SendRequest.cs`
- `api/src/FileInviteLite.Api/Services/Tokens/` — `IMagicLinkTokens` + implementation
- `api/src/FileInviteLite.Api/Services/Email/` — `IEmailSender`, `DevSinkEmailSender`,
  `NoOpEmailSender`
- `api/src/FileInviteLite.Api/Common/Security/PortalOptions.cs` — link lifetime and base URL
- `api/src/FileInviteLite.Api/Program.cs` — **registration lines only** for the services above
- `api/tests/FileInviteLite.Api.Tests/Features/Requests/SendRequestTests.cs`
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

## Follow the pattern in

`Features/Requests/CreateRequest.cs`, `Features/Auth/Login.cs` (audit in one `SaveChangesAsync`),
`web/src/features/requests/CreateRequestPage.tsx`.

## Done when

`npm run check` green, `dotnet format` clean, CI green, security-reviewer findings resolved.

## Progress notes

(append here as you go)
