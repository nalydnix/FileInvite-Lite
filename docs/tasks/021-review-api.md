# 021: Review API — download, approve, reject
**Phase:** 2   **Depends on:** 020-phase2-schema   **Parallel-safe with:** 022, 023, 024, 025

## Goal
The three staff endpoints that close the loop on an uploaded document: download it, approve the
item, or reject it with a comment.

## Scope

**May touch**
- `api/src/FileInviteLite.Api/Features/Review/` — `DownloadFile.cs`, `ApproveItem.cs`,
  `RejectItem.cs`
- `api/tests/FileInviteLite.Api.Tests/Features/Review/`

**Must not touch**
`Program.cs`, `AppDbContext`, `Data/Migrations/**`, any `.csproj`, `docs/api-contract.md`,
`Features/Portal/**`, `Features/Requests/**`, `Services/**`, `Jobs/**`, `web/**`.

> **`Program.cs` is forbidden in this task**, unlike in Phase 1. You need no new service
> registration: `IFileStorage`, `IAuditWriter` and `CurrentStaff` are all registered already. If
> you believe you need a registration line, stop, write why in Progress notes, and say so in your
> report — the orchestrator makes that edit, not you.

## Decisions (already made — implement, do not revisit)

- **Routes** are exactly as `docs/api-contract.md` has them: `GET /api/files/{fileId}/content`,
  `POST /api/items/{itemId}/approve`, `POST /api/items/{itemId}/reject`. All three use
  `.RequireAuthorization(AuthPolicies.Staff)`.
- **Download** streams from `IFileStorage.OpenReadAsync`, sets the stored `ContentType` and
  `Content-Disposition: attachment` with the stored `OriginalName`. Never the storage key, never a
  file-system path, in the response or in a log (S4.1, S4.3). A missing blob behind a present row
  is a `404`, not a `500`, and is logged without the key.
- **Download writes `FileDownloaded` before the bytes go out** (S4.2). The audit event and the
  `SaveChangesAsync` complete before the stream is returned, so a client that disconnects
  mid-download has still been recorded.
- **Status roll-up is `DocumentRequest.RollUpStatus`**, which already exists and already encodes
  S4.9, S4.10 and S4.12. Load the request with its items, call it, assign the result. **Do not
  restate the rule inline and do not change the method.**
- **Both decisions write a `ReviewDecision` row plus the audit event plus the item status change in
  one `SaveChangesAsync`** (S4.8). When the roll-up turns the request `Complete`, the same
  transaction also writes a `RequestCompleted` audit event (S4.9) **and** a `WebhookDelivery` row
  with `Attempts = 0`, `DeliveredAt = null`, `Url` read from `Webhook:Url` configuration, plus a
  `WebhookQueued` audit event (S7.1). If `Webhook:Url` is not configured, no delivery row is
  created and no audit event is written for it — a prototype without a webhook configured must
  still be able to complete a request.
- **`PayloadHash`** on that row is the SHA-256 hex of the exact JSON body the webhook job will
  send, so task 025 owns the payload shape. To avoid depending on 025's code, serialise the payload
  shape documented in `docs/api-contract.md` §Webhook with `System.Text.Json` defaults and hash
  those bytes. Write the serialisation in your own file under `Features/Review/`. Note in Progress
  notes that 025 must produce byte-identical JSON or the hash check is meaningless — flag it, do
  not try to share code sideways into another feature folder.
- **Validation** through FluentValidation as an endpoint filter, same as `CreateRequest`. Reject's
  comment is required, 1–2000 chars after trimming (S4.5). Approve's comment is optional, ≤ 2000.
- **Conflicts**: reviewing an item with no uploaded file is `409` (S4.7); approving an already
  `Approved` item is `409` (S4.11). Rejecting an already `Rejected` item is allowed and records a
  second decision — the client may have re-uploaded and been rejected again.
- `404` for an unknown file id, an unknown item id, or anything the caller may not see.

## Acceptance criteria

- [ ] **S4.1** Download returns the bytes as an attachment with the stored content type and
      original filename, from a JWT-protected route
- [ ] **S4.2** Download writes a `FileDownloaded` audit event
- [ ] **S4.3** A test asserts no route serves the storage root as static files and that the storage
      path is not reachable anonymously
- [ ] **S4.4** Approve with an optional comment moves the item to `Approved`
- [ ] **S4.5** Reject without a comment, or with whitespace only, returns `400` naming the
      `comment` field; with a comment the item becomes `Rejected`
- [ ] **S4.7** Reviewing an item with no uploaded file returns `409`
- [ ] **S4.8** Each decision writes a `ReviewDecision` row and the matching audit event in the
      **same transaction** as the status change — prove it by forcing a failure, as
      `RequestAtomicityTests` does
- [ ] **S4.9** Approving the last required item makes the request `Complete` and writes
      `RequestCompleted`
- [ ] **S4.10** Optional items still `Outstanding` do not prevent completion
- [ ] **S4.11** Approving an already-`Approved` item returns `409`
- [ ] **S4.12** An all-optional request completes on the first approval, with another item still
      `Uploaded` — pin the behaviour, it is intended
- [ ] **S7.1** Completion creates a `WebhookDelivery` row in the same transaction, when
      `Webhook:Url` is configured; and completion still succeeds when it is not
- [ ] **S3.14** A portal session token on any of these three routes returns `401`
- [ ] `401` without a JWT on all three (S1.7); `404` for unknown ids
- [ ] **S6.6** No audit metadata written here contains a storage key, a token or file content
- [ ] The response body of approve and reject is `{ itemId, itemStatus, requestStatus }` per the
      contract

## Follow the pattern in

`Features/Requests/CreateRequest.cs` (validator as a filter, audit in one `SaveChangesAsync`),
`Features/Requests/ListRequests.cs` (query shape), whatever 012 produced in `Features/Portal/`
(for `IFileStorage` usage), `api/tests/.../Features/Requests/RequestAtomicityTests.cs`.

## Done when

`npm run check` green, `dotnet format` clean, CI green, security-reviewer run and every finding
resolved.

## Progress notes

(append here as you go)

### Task 021 — implemented
- Added `Features/Review/{DownloadFile,ApproveItem,RejectItem}.cs` plus `ReviewDecisionWriter.cs`
  (shared decision/roll-up/commit logic and the webhook payload hash, kept inside the feature
  folder so approve and reject cannot drift). Tests in `Tests/Features/Review/`. No `Program.cs`,
  `AppDbContext`, migration or other forbidden file touched; no registration needed.
- `RollUpStatus` is called, never restated. `RequestCompleted` (and the webhook) fire only on the
  transition into `Complete`, not when a later approval finds it already `Complete` (S4.12).
- Download opens the blob first (missing blob => 404, no audit, key never logged), then commits
  `FileDownloaded`, then streams. `DiscardAsync` is not used.
- Decision calls: rejecting an `Approved` item returns 409 (not in the task file; chosen so a
  rejection cannot silently un-complete a request whose webhook may have gone out). Needs owner
  confirmation. Approve requires a JSON body (`{}`), because the shared `ValidationFilter` needs one.
- **FLAG FOR TASK 025:** `PayloadHash` is SHA-256 of JSON this task serialises (`CompletionPayload`).
  025 must produce byte-identical JSON or the hash is meaningless: `JsonSerializerDefaults.Web`
  (camelCase), compact, UTF-8, properties in contract order (`event, requestId, clientName,
  clientEmail, completedAt, items[id,title,required,outcome]`), items ordered by `id` ascending,
  `outcome` = item status name, `completedAt` = the completing `ReviewDecision.ReviewedAt` truncated
  to whole seconds (UTC). Items' state is as of completion; a later change would alter a recomputed hash.
