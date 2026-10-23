# 012: Portal API — token exchange, request view, file upload
**Phase:** 1   **Depends on:** 010-phase1-schema   **Parallel-safe with:** 011, 013

## Goal
The three client-facing endpoints: exchange a magic-link token for a request-scoped session, read
that request, upload a file against one of its items.

**This is the highest-risk task in the project.** Everything in `PLAN.md` §4 about one link
granting access to exactly one request is enforced here. Read `docs/adr/0004-magic-links.md` and
`docs/adr/0005-file-storage.md` before writing anything.

## Scope

**May touch**
- `api/src/FileInviteLite.Api/Features/Portal/` — `ExchangeToken.cs`, `GetPortalRequest.cs`,
  `UploadFile.cs`
- `api/src/FileInviteLite.Api/Common/Security/` — `PortalSessionHandler.cs`, `CurrentPortal.cs`,
  and the `AuthPolicies.Portal` policy
- `api/src/FileInviteLite.Api/Services/Storage/` — `IFileStorage`, `LocalDiskFileStorage`
- `api/src/FileInviteLite.Api/Services/Files/` — `IContentSniffer`, `MagicByteContentSniffer`
- `api/src/FileInviteLite.Api/Program.cs` — **registration lines only** (the portal scheme, the
  policy, the two services, the exchange rate-limit policy)
- `api/tests/FileInviteLite.Api.Tests/Features/Portal/`

**Must not touch**
`AppDbContext`, `Data/Migrations/**`, `.csproj`, `docs/api-contract.md`,
`Features/Requests/**` (011 owns `SendRequest.cs`), `web/**`.

> `Program.cs` is shared with 011. Keep your edits to one contiguous block.

## Decisions (already made — implement, do not revisit)

- **Portal session is a signed token, not a database row.** A JWT signed with the existing
  `Jwt:SigningKey`, issuer and audience `fileinvite-lite-portal`, 30-minute expiry, carrying
  `request_id`. Stateless, so there is no session table and no revocation — acceptable because the
  lifetime is short and the magic link itself is revocable. State this trade-off in the PR.
- **Its own authentication scheme**, `AuthPolicies.Schemes.PortalSession`, reading the
  `Authorization: Portal <token>` header (note the scheme word is `Portal`, per the contract). The
  `Staff` policy keeps pinning `StaffJwt` and the `Portal` policy pins `PortalSession`, so neither
  credential can authorise the other's endpoints (S2.7, S2.8).
- **`CurrentPortal.RequestId` is the only source of the request id.** No portal endpoint takes a
  request id in its route, query or body. Every query filters by it **first**.
- **Cross-request access returns `404`**, never `403` (S2.9).
- **Uniform exchange failures.** Unknown, malformed, expired and revoked tokens all return the
  identical `401` body (S2.6). Compare the token by hashing the input and looking up the hash.
- **Storage key** is `yyyy/MM/{guid}`, no extension, no part of the original filename (S3.8).
- **Size limit** 20 MB, counted while streaming to disk, aborting past it. `Content-Length` is
  never trusted (S3.7).
- **Sniffing**: allow `.pdf .jpg .jpeg .png .docx .xlsx`; the leading bytes must match the
  extension. DOCX and XLSX are ZIP containers — check the ZIP header and the expected internal
  entry, not just `PK`.
- **Failed upload leaves nothing**: no database row, no orphaned file on disk.

## Acceptance criteria

Exchange:
- [ ] **S2.3** A valid token returns a session token and the request id
- [ ] **S2.4/5/6** Expired, revoked, unknown and malformed tokens return a byte-identical `401`
      body (compare the four, excluding `traceId`)
- [ ] **S2.10** Rate-limited at 10 per IP per minute, then `429`
- [ ] **S2.11** Writes a `PortalAccessed` audit event with `ActorType.Client` and the request id as
      `ActorId`; metadata contains no token
- [ ] **S2.12** The response carries `Referrer-Policy: no-referrer`

Scoping — these are the tests that matter most:
- [ ] **S2.7** A portal token on a staff endpoint returns `401`
- [ ] **S2.8** A staff JWT on a portal endpoint returns `401`
- [ ] **S2.9** With a session for request A, every item, file and upload belonging to request B
      returns `404`. Write this for each portal endpoint, not just one

View:
- [ ] **S3.1** Returns client name, due date and each item's title, description, required and status
- [ ] **S3.2** Contains no staff identity, no audit history, no storage key; the rejection comment
      appears only on `Rejected` items

Upload:
- [ ] **S3.3** `201` with the stored file's metadata
- [ ] **S3.5** A disallowed extension returns `415`
- [ ] **S3.6** An executable renamed `.pdf` returns `415` — use real magic bytes in the fixture
- [ ] **S3.7** Over 20 MB returns `413`, and a lying `Content-Length` does not get past it
- [ ] **S3.8** The storage key contains no part of the original filename
- [ ] **S3.10** The item moves to `Uploaded` and the request rolls up to `InProgress` via
      `DocumentRequest.RollUpStatus`, which already exists — do not rewrite the rule
- [ ] **S3.11** `ItemUploaded` audit event in the **same transaction** as the file row and the
      status change; prove it by forcing a failure, as `RequestAtomicityTests` does
- [ ] **S3.12** Uploading to an `Approved` item returns `409`
- [ ] **S3.13** Re-upload after rejection is allowed, supersedes, and the previous row survives
- [ ] **S3.14** No portal route serves file content
- [ ] A failed upload leaves no row and no file on disk

- [ ] **S2.13** A full happy path through a capturing log provider contains no token at any level

## Follow the pattern in

`Features/Requests/ListRequests.cs` (query shape), `Features/Auth/Login.cs` (audit, uniform 401),
`Features/Requests/RequestAtomicityTests.cs` (forced-failure atomicity).

## Done when

`npm run check` green, `dotnet format` clean, CI green, **security-reviewer run and every finding
resolved** — this task does not merge on an unreviewed branch.

## Progress notes

(append here as you go)
