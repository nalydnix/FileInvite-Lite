# FileInvite-Lite: API Contract

Every endpoint in the MVP: route, auth, request shape, response shape and error cases.

**This file is a shared hotspot.** Only a sequential task changes it. A parallel task that finds
the contract wrong stops and records the problem in its task file's progress notes.

Base URL: `https://{api-host}` — `http://localhost:5080` in development.
All request and response bodies are `application/json; charset=utf-8` unless stated.
All timestamps are UTC ISO-8601 with a `Z` suffix (`2026-03-14T09:30:00Z`). Dates without a time
(`dueDate`) are `yyyy-MM-dd`. All ids are GUIDs in canonical hyphenated form.

---

## Authentication

Two credentials, never interchangeable (S2.7, S2.8).

| | Header | Obtained from | Lifetime |
|---|---|---|---|
| Staff | `Authorization: Bearer <jwt>` | `POST /api/auth/login` | 60 minutes |
| Portal | `Authorization: Portal <token>` | `POST /api/portal/exchange` | 30 minutes |

A staff JWT on a `/api/portal/*` endpoint returns `401`. A portal token on any other endpoint
returns `401`.

---

## Error format

Every non-2xx response is RFC 7807 problem details:

```json
{
  "type": "https://fileinvite-lite/errors/validation",
  "title": "One or more validation errors occurred.",
  "status": 400,
  "detail": "Items must contain at least one entry.",
  "traceId": "00-6f1a…-01",
  "errors": {
    "items": ["Items must contain at least one entry."]
  }
}
```

- `errors` is present only on `400` validation failures.
- `traceId` is always present and matches the server log correlation id.
- `detail` never contains a token, a password, a storage key, a file path or exception text.

### Status codes used

| Code | Meaning here |
|---|---|
| `200` | Read succeeded |
| `201` | Created; `Location` header set |
| `204` | Succeeded, no body |
| `400` | Validation failure |
| `401` | Missing, invalid, expired or wrong-kind credential |
| `403` | Authenticated but the policy forbids the endpoint (rare; scoping uses `404`) |
| `404` | Not found **or** not visible to this caller |
| `409` | State conflict (approve an approved item, upload to an approved item) |
| `413` | Upload over the size limit |
| `415` | Upload type not allowed, or content does not match extension |
| `429` | Rate limit exceeded; `Retry-After` set |
| `500` | Unhandled; body carries only `traceId` |

**`404`, not `403`, for cross-request access.** A portal session for request A asking about
request B's item is told the item does not exist (S2.9).

---

## Health

### `GET /health`

Anonymous. Returns `200` with `{"status":"healthy"}`. No version, environment or dependency detail
(S10.5).

---

## Auth

### `POST /api/auth/login`

Anonymous. Rate-limited: 5 attempts per minute per IP, then `429` (S8.4).

Request:
```json
{ "email": "staff@example.com", "password": "…" }
```

`200`:
```json
{ "token": "eyJhbGciOi…", "expiresAt": "2026-03-14T10:30:00Z", "name": "Demo Staff" }
```

Errors: `400` missing fields · `401` wrong password **or** unknown email, identical body (S8.2) ·
`429` rate limited.

Audit: `StaffLoggedIn` on success, `StaffLoginFailed` on `401` (S8.8).

---

## Requests (staff)

All require `Authorization: Bearer <jwt>`.

### `POST /api/requests`

Request:
```json
{
  "clientName": "Jane Client",
  "clientEmail": "jane@example.com",
  "dueDate": "2026-04-01",
  "items": [
    { "title": "Passport", "description": "Photo page", "required": true },
    { "title": "Payslip",  "description": null,         "required": false }
  ]
}
```

Validation: `clientName` 1–200 chars · `clientEmail` valid, ≤ 320 chars · `dueDate` today or later
(S1.3) · `items` 1–50 entries, each `title` 1–200 chars, `description` ≤ 1000 chars.

`201`, `Location: /api/requests/{id}`:
```json
{ "id": "a3f1…" }
```

Errors: `400` validation · `401`.
Audit: `RequestCreated` (S1.6).

### `GET /api/requests`

Query: `status` (`Draft|Sent|InProgress|Complete`) · `overdue` (`true|false`) · `page` (default 1)
· `pageSize` (default 20, max 100) · `sort` (`dueDate|createdAt`, default `dueDate` ascending).

`200`:
```json
{
  "items": [
    {
      "id": "a3f1…",
      "clientName": "Jane Client",
      "dueDate": "2026-04-01",
      "status": "InProgress",
      "itemCount": 4,
      "approvedCount": 2,
      "isOverdue": false,
      "createdAt": "2026-03-14T09:30:00Z"
    }
  ],
  "page": 1,
  "pageSize": 20,
  "total": 37
}
```

Errors: `400` bad query value · `401`.

### `GET /api/requests/{id}`

`200`:
```json
{
  "id": "a3f1…",
  "clientName": "Jane Client",
  "clientEmail": "jane@example.com",
  "dueDate": "2026-04-01",
  "status": "InProgress",
  "isOverdue": false,
  "createdAt": "2026-03-14T09:30:00Z",
  "createdBy": { "id": "7b2c…", "name": "Demo Staff" },
  "magicLink": {
    "issuedAt": "2026-03-14T09:31:00Z",
    "expiresAt": "2026-04-08T09:31:00Z",
    "revoked": false
  },
  "items": [
    {
      "id": "c91d…",
      "title": "Passport",
      "description": "Photo page",
      "required": true,
      "status": "Uploaded",
      "latestFile": {
        "id": "4e88…",
        "originalName": "passport.pdf",
        "contentType": "application/pdf",
        "sizeBytes": 184320,
        "uploadedAt": "2026-03-15T11:02:00Z"
      },
      "latestDecision": null
    }
  ]
}
```

`magicLink` is `null` before the request is sent. **It never contains the token or the URL**
(S1.12). `latestFile` is `null` when nothing is uploaded. `latestDecision` is
`{ "decision": "Reject", "comment": "…", "reviewedAt": "…" }` or `null`.

Errors: `401` · `404`.

### `POST /api/requests/{id}/send`

No body. Issues a magic link, revokes any previous one (S1.14), moves the request to `Sent`.

`200`:
```json
{
  "url": "https://app.example.com/portal#t=Zm9vYmFy…",
  "expiresAt": "2026-04-08T09:31:00Z"
}
```

**This is the only response in the API that contains a magic-link token.** The token sits in the
URL fragment so it is never sent to a server (S2.1, §4 of `PLAN.md`).

Errors: `400` request has no items (S1.16) · `401` · `404`.
Audit: `RequestSent`, `MagicLinkIssued` — neither records the token (S1.13).
Side effect: in Development, writes the link to the dev sink (S1.15).

---

## Portal (client)

`POST /api/portal/exchange` is anonymous. Everything else requires
`Authorization: Portal <token>`. All portal responses carry `Referrer-Policy: no-referrer`
(S2.12).

### `POST /api/portal/exchange`

Rate-limited: 10 attempts per minute per IP, then `429` (S2.10).

Request:
```json
{ "token": "Zm9vYmFy…" }
```

`200`:
```json
{
  "sessionToken": "…",
  "expiresAt": "2026-03-15T11:30:00Z",
  "requestId": "a3f1…"
}
```

Errors: `400` missing token · `401` unknown, malformed, expired or revoked — one identical body for
all four (S2.4–S2.6) · `429`.
Audit: `PortalAccessed` (S2.11).

### `GET /api/portal/request`

No id in the route: the request is the one the session is scoped to.

`200`:
```json
{
  "clientName": "Jane Client",
  "dueDate": "2026-04-01",
  "isOverdue": false,
  "items": [
    {
      "id": "c91d…",
      "title": "Passport",
      "description": "Photo page",
      "required": true,
      "status": "Rejected",
      "rejectionComment": "Photo page is cut off",
      "latestUpload": { "originalName": "passport.pdf", "uploadedAt": "2026-03-15T11:02:00Z" }
    }
  ]
}
```

No staff identities, no audit history, no storage keys, no internal request id beyond what the
client needs (S3.2). `rejectionComment` is present only for `Rejected` items.

Errors: `401`.

### `POST /api/portal/items/{itemId}/files`

`multipart/form-data` with a single `file` part.

Limits: ≤ 20 MB, enforced server-side (S3.7) · extension in `.pdf .jpg .jpeg .png .docx .xlsx`
(S3.5) · content sniffed against the extension (S3.6).

`201`:
```json
{
  "id": "4e88…",
  "originalName": "passport.pdf",
  "sizeBytes": 184320,
  "uploadedAt": "2026-03-15T11:02:00Z"
}
```

Errors: `401` · `404` item not on this session's request (S3.4) · `409` item already `Approved`
(S3.12) · `413` too large · `415` disallowed or mismatched type.
Audit: `ItemUploaded`, in the same transaction as the file row and status change (S3.11).

There is no portal download endpoint. A client requesting a file path gets `404` (S3.14).

---

## Review (staff)

All require `Authorization: Bearer <jwt>`.

### `GET /api/files/{fileId}/content`

`200` with `Content-Type` set to the stored content type and
`Content-Disposition: attachment; filename="passport.pdf"` using the stored original name
(S4.1). The body is the file bytes.

Errors: `401` · `404`.
Audit: `FileDownloaded` (S4.2).

### `POST /api/items/{itemId}/approve`

Request:
```json
{ "comment": "Looks good" }
```
`comment` is optional, ≤ 2000 chars.

`200`:
```json
{
  "itemId": "c91d…",
  "itemStatus": "Approved",
  "requestStatus": "InProgress"
}
```

`requestStatus` reflects the roll-up after the decision, so the UI never has to refetch to learn
that the request completed (S4.9).

Errors: `401` · `404` · `409` no uploaded file (S4.7) or item already `Approved` (S4.11).
Audit: `ItemApproved`, plus `RequestCompleted` when this decision completes the request.

### `POST /api/items/{itemId}/reject`

Request:
```json
{ "comment": "Photo page is cut off" }
```
`comment` is **required**, 1–2000 chars after trimming (S4.5).

`200`: same shape as approve, with `"itemStatus": "Rejected"`.

Errors: `400` missing or whitespace comment · `401` · `404` · `409` no uploaded file.
Audit: `ItemRejected`.

---

## Audit (staff)

### `GET /api/requests/{id}/audit`

Query: `page` (default 1) · `pageSize` (default 50, max 200).

`200`:
```json
{
  "items": [
    {
      "id": "9d10…",
      "occurredAt": "2026-03-15T11:02:00Z",
      "actorType": "Client",
      "actorId": "a3f1…",
      "action": "ItemUploaded",
      "entityType": "ChecklistItem",
      "entityId": "c91d…",
      "metadata": { "originalName": "passport.pdf", "sizeBytes": 184320 }
    }
  ],
  "page": 1,
  "pageSize": 50,
  "total": 12
}
```

Newest first (S6.4). `metadata` never contains tokens, passwords, storage keys or file content
(S6.6). For a client actor, `actorId` is the request id — a client has no user identity.

Errors: `401` · `404` unknown request (S6.5).

---

## Webhook (outbound)

Not an endpoint of this API — the shape FileInvite-Lite **sends** when a request completes.

`POST {Webhook:Url}` (HTTPS only, from configuration — S7.7)

Headers:
```
Content-Type: application/json
X-Signature: sha256=<hex HMAC-SHA256 of the exact body bytes>
X-Delivery-Id: <WebhookDelivery id>
X-Attempt: 1
```

Body:
```json
{
  "event": "request.completed",
  "requestId": "a3f1…",
  "clientName": "Jane Client",
  "clientEmail": "jane@example.com",
  "completedAt": "2026-03-20T14:05:00Z",
  "items": [
    { "id": "c91d…", "title": "Passport", "required": true, "outcome": "Approved" }
  ]
}
```

No file contents, no storage keys, no tokens (S7.4). Receivers verify by computing
HMAC-SHA256 over the raw body with the shared secret and comparing in constant time.

Retries: on a non-2xx or a timeout, retried with exponential backoff (1 min, 5 min, 25 min, …) up
to the configured attempt limit, then marked failed (S7.5, S7.6).

---

## Rate limits

| Endpoint | Limit | Window |
|---|---|---|
| `POST /api/auth/login` | 5 per IP | 1 minute |
| `POST /api/portal/exchange` | 10 per IP | 1 minute |
| Everything else | none in the MVP | — |

`429` responses set `Retry-After` in seconds.

---

## CORS

`Access-Control-Allow-Origin` is the single configured frontend origin (plus `http://localhost:5173`
in Development). Allowed methods `GET, POST, OPTIONS`; allowed headers `Authorization, Content-Type`.
Credentials are **not** allowed — no cookie crosses origins (S10.2).

---

## Conventions for new endpoints

1. Route lives in the handler file, under `/api/`.
2. Staff endpoints use `.RequireAuthorization(AuthPolicies.Staff)`; portal endpoints use
   `AuthPolicies.Portal`. There is no third option and no anonymous endpoint beyond `/health`,
   `/api/auth/login` and `/api/portal/exchange`.
3. Request and response records are nested in the endpoint class, never shared between features.
4. Errors go through `ProblemResults`, so the shape above is automatic.
5. Adding or changing an endpoint means editing this file — in a sequential task.
