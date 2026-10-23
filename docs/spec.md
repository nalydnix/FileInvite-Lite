# FileInvite-Lite: Specification

Acceptance criteria for the MVP, written as testable statements. Each criterion is phrased so that
a test can assert it without reading the implementation. Criteria are numbered (`S1.3`) so task
files and tests can cite them.

Source of truth for scope decisions: `PLAN.md` §1–§4. Where this document and `PLAN.md` disagree,
this document wins from Phase 1 onwards.

---

## Vocabulary

| Term | Meaning |
|---|---|
| **Staff user** | An authenticated internal user. Holds a JWT. |
| **Client** | An external person with a magic link. No account, no password. |
| **Request** | A `DocumentRequest`: one client, one due date, one checklist. |
| **Item** | A `ChecklistItem` on a request: one document being asked for. |
| **Portal session** | A short-lived token scoped to exactly one request, exchanged from a magic-link token. |
| **Dev sink** | A gitignored local folder that receives "emails" in Development. |

### Status values

`DocumentRequest.Status`: `Draft`, `Sent`, `InProgress`, `Complete`.
`ChecklistItem.Status`: `Outstanding`, `Uploaded`, `Approved`, `Rejected`.

### Status roll-up rule

The request status is derived on write, never set by hand:

- The request has at least one required item, and all of them are `Approved` → `Complete`.
- The request has no required items, and at least one item is `Approved` → `Complete`.
- Otherwise, at least one item not `Outstanding` → `InProgress`.
- Otherwise, a magic link has been issued → `Sent`.
- Otherwise → `Draft`.

"Overdue" is never stored. A request is overdue when `DueDate < today (UTC)` and the status is not
`Complete`.

---

## S1. Request builder

**S1.1** A staff user with a valid JWT can create a request with `ClientName`, `ClientEmail`,
`DueDate` and one or more checklist items; the response is `201 Created` with the request id.

**S1.2** Creating a request with zero checklist items returns `400` with a validation error naming
the `items` field.

**S1.3** Creating a request with a `DueDate` in the past returns `400`.

**S1.4** Creating a request with an invalid `ClientEmail` returns `400`.

**S1.5** A newly created request has status `Draft` and every item has status `Outstanding`.

**S1.6** Creating a request writes a `RequestCreated` audit event whose `ActorId` is the staff
user's id, in the same transaction as the request row. If the insert fails, no audit event exists.

**S1.7** An unauthenticated call to any staff endpoint returns `401`; it never returns `404` or
otherwise leaks whether the resource exists.

**S1.8** A staff user can list requests. Each row returns id, client name, due date, status, item
counts (total / approved) and an `isOverdue` flag computed per the rule above.

**S1.9** The list supports filtering by status and by overdue, and is ordered by due date ascending
by default.

**S1.10** A staff user can read a single request with its items, their statuses, and the uploaded
file metadata per item (never file contents).

**S1.11** Sending a request (`POST /api/requests/{id}/send`) generates a magic link, moves the
request to `Sent`, and returns the full magic-link URL **once** in the response body.

**S1.12** The magic-link URL is never returned again by any later read endpoint; re-reading the
request returns only link metadata (issued at, expires at, revoked).

**S1.13** Sending a request writes `RequestSent` and `MagicLinkIssued` audit events. Neither
event's metadata contains the token.

**S1.14** Sending an already-sent request revokes the previous magic link and issues a new one; the
old token no longer exchanges.

**S1.15** In Development, sending a request writes a file into the dev sink containing the magic
link. Outside Development no file is written and no email is attempted.

**S1.16** A request with zero items cannot be sent (`400`).

---

## S2. Magic links and portal access

**S2.1** A magic-link token carries at least 256 bits of cryptographically random data, URL-safe
encoded.

**S2.2** Only the SHA-256 hash of the token is persisted. The database never contains the token.

**S2.3** `POST /api/portal/exchange` with a valid, unexpired, unrevoked token returns a portal
session token scoped to exactly one request id.

**S2.4** Exchange with an expired token returns `401`.

**S2.5** Exchange with a revoked token returns `401`.

**S2.6** Exchange with an unknown or malformed token returns `401` — the same status and body shape
as expired and revoked, so a caller cannot tell the three apart.

**S2.7** A portal session token presented to a staff endpoint returns `401`.

**S2.8** A staff JWT presented to a portal endpoint returns `401`.

**S2.9** A portal session for request A returns `404` for every item, file and upload belonging to
request B.

**S2.10** Exchange is rate-limited; past the configured attempt count within the window, further
attempts return `429`.

**S2.11** Exchange writes a `PortalAccessed` audit event with `ActorType = Client` and the request
id. The metadata contains no token.

**S2.12** Portal responses carry `Referrer-Policy: no-referrer`.

**S2.13** No log line, at any level, contains a magic-link token, a portal session token, a JWT or
a password. Asserted by a test that drives a full happy path through a capturing log provider.

---

## S3. Client upload portal

**S3.1** A client with a valid portal session can read the client-facing view: client name, due
date, and each item's title, description, required flag and status.

**S3.2** The client-facing view never includes staff identities, audit history, or reviewer
comments other than the rejection comment for the client's own rejected items.

**S3.3** A client can upload one file to an item belonging to their request; the response is `201`
with the stored file's metadata (id, original name, size, uploaded at).

**S3.4** Uploading to an item on another request returns `404`.

**S3.5** A file whose extension is not one of `.pdf`, `.jpg`, `.jpeg`, `.png`, `.docx`, `.xlsx` is
rejected with `415`.

**S3.6** A file whose content does not match its claimed type by magic-byte sniffing is rejected
with `415`, even when the extension is allowed — an executable renamed to `.pdf` is rejected.

**S3.7** A file larger than 20 MB is rejected with `413`. The limit is enforced server-side and
does not rely on any client-sent length header.

**S3.8** A stored file's storage key contains no part of the original filename and is not derivable
from the item or request id.

**S3.9** A successful upload records `OriginalName`, `ContentType`, `SizeBytes`, `Sha256`,
`StorageKey` and `UploadedAt`.

**S3.10** A successful upload moves the item from `Outstanding` or `Rejected` to `Uploaded` and
rolls the request up to `InProgress`.

**S3.11** A successful upload writes an `ItemUploaded` audit event in the same transaction as the
file row and the status change. If any part fails, none of the three is persisted.

**S3.12** Uploading to an item already `Approved` returns `409`.

**S3.13** Re-uploading to a `Rejected` item is allowed and supersedes the previous file. The
previous file row is retained, never deleted; the latest upload is the one offered for review.

**S3.14** A client cannot download any file, including their own uploads: a file path called with
a portal session returns `404`.

---

## S4. Review workflow

**S4.1** A staff user can download an uploaded file; the response is an attachment with the stored
content type and the original filename, from a JWT-protected endpoint.

**S4.2** Downloading a file writes a `FileDownloaded` audit event.

**S4.3** No route serves uploaded files without authentication; the storage folder is not mapped to
any static-file path.

**S4.4** A staff user can approve an item with an optional comment. The item becomes `Approved`.

**S4.5** A staff user can reject an item with a comment. The comment is **required** on reject
(`400` if missing or whitespace); the item becomes `Rejected`.

**S4.6** A rejected item is uploadable again by the client (see S3.13).

**S4.7** Reviewing an item with no uploaded file returns `409`.

**S4.8** Each decision writes a `ReviewDecision` row and an `ItemApproved` or `ItemRejected` audit
event in the same transaction as the item status change.

**S4.9** When the last required item becomes `Approved`, the request becomes `Complete` and a
`RequestCompleted` audit event is written.

**S4.10** Optional items still `Outstanding` do not prevent completion.

**S4.11** Approving an already-`Approved` item returns `409`.

**S4.12** A request whose items are **all optional** becomes `Complete` as soon as staff approve
any one of them. The remaining optional items do not have to be resolved.

This is the "send me whatever you have" case: one accepted document is enough, and without the
rule such a request could never complete, because "all required items approved" is vacuously true
the moment it is created and so cannot be used as the trigger.

Consequence to be aware of: an all-optional request can reach `Complete` — and therefore fire the
completion webhook (S7.1) — while another item is still `Uploaded` and awaiting review. That is
intended. A test pins it so the behaviour is visible rather than incidental.

---

## S5. Reminders

**S5.1** A background service runs on a configured interval and sends a reminder for every request
that is overdue per the rule above.

**S5.2** At most one reminder per request per configured cool-down window; a second pass inside the
window sends nothing.

**S5.3** A reminder writes a `Reminder` row (`RequestId`, `SentAt`, `Reason`) and a `ReminderSent`
audit event with `ActorType = System`.

**S5.4** No reminder is ever sent for a request with status `Complete` or `Draft`.

**S5.5** In Development the reminder goes to the dev sink; outside Development sending is a no-op
and the `Reminder` row records that it was suppressed.

**S5.6** A failure inside the reminder job never crashes the host; it is logged and the next pass
retries.

---

## S6. Audit log

**S6.1** Every state-changing operation in S1–S5 and S7 writes exactly one audit event, in the same
database transaction as the state change.

**S6.2** An audit event records `OccurredAt` (UTC), `ActorType` (`Staff`, `Client`, `System`),
`ActorId`, `Action`, `EntityType`, `EntityId` and JSON `Metadata`.

**S6.3** No application code path updates or deletes an audit event.

**S6.4** A staff user can read a request's audit trail, newest first, paginated.

**S6.5** The audit trail endpoint returns `404` for an unknown request id and `401` without a JWT.

**S6.6** No audit event's metadata contains a token, password, JWT, file content or storage key.

---

## S7. Completion webhook

**S7.1** When a request becomes `Complete`, a `WebhookDelivery` row is created in the same
transaction as the completion.

**S7.2** A background service posts the payload to the configured URL as JSON.

**S7.3** The request carries an `X-Signature` header: the hex HMAC-SHA256 of the exact request body
bytes, keyed with the configured secret.

**S7.4** The payload contains the request id, client name, client email, completion timestamp and
per-item outcomes. It contains no file contents, no tokens and no storage keys.

**S7.5** A non-2xx response or a timeout is retried with exponential backoff up to the configured
attempt limit; `Attempts` and `LastStatus` are updated after each try.

**S7.6** After the final failed attempt the delivery is marked failed and is not retried further.
`DeliveredAt` is set only on success.

**S7.7** The webhook URL comes from configuration only. No request body, query string or database
row can influence it. A configured non-HTTPS URL fails startup validation.

**S7.8** The signing secret is never logged and never appears in a payload.

---

## S8. Staff authentication

**S8.1** `POST /api/auth/login` with the seeded demo user's correct credentials returns a JWT with
a 60-minute expiry.

**S8.2** Wrong password and unknown email return the same `401` body, with no wording difference
that distinguishes them.

**S8.3** Passwords are stored only as ASP.NET Core `PasswordHasher` hashes.

**S8.4** Login is rate-limited; past the threshold the endpoint returns `429`.

**S8.5** An expired JWT returns `401`.

**S8.6** A JWT signed with the wrong key returns `401`.

**S8.7** The signing key comes from user secrets or environment configuration. Startup fails with a
clear error if it is missing or shorter than 32 bytes. No key is committed to the repository.

**S8.8** A successful login writes a `StaffLoggedIn` audit event. A failed login writes
`StaffLoginFailed` with the attempted email and never the password.

---

## S9. Frontend

**S9.1** An unauthenticated visit to a staff route redirects to the login page.

**S9.2** The JWT is held in memory only. A page reload returns the user to the login page, and no
token is present in `localStorage`, `sessionStorage` or a cookie.

**S9.3** The portal reads the token from the URL fragment, exchanges it once, and removes the
fragment from the address bar before any further navigation.

**S9.4** The portal never writes the magic-link token to storage; only the exchanged portal session
token is held, in memory.

**S9.5** Staff can create a request, see the generated link, copy it, and see the checklist with
live statuses.

**S9.6** Staff can download a file, approve an item, and reject an item with a comment; the UI
blocks submitting a reject with an empty comment.

**S9.7** The client portal shows outstanding, uploaded, approved and rejected items distinctly, and
shows the rejection comment on rejected items.

**S9.8** Upload errors (type, size, server rejection) are surfaced as readable messages, not raw
status codes.

---

## S10. Cross-cutting

**S10.1** Every error response uses the shared error shape in `docs/api-contract.md` (RFC 7807
problem details with a `traceId`).

**S10.2** CORS allows only the configured frontend origin, plus `localhost` in Development.

**S10.3** All timestamps crossing the API boundary are UTC ISO-8601 with a `Z` suffix.

**S10.4** All ids exposed by the API are GUIDs; no sequential integer identifies a domain entity.

**S10.5** `GET /health` returns `200` without authentication and reveals no version, connection
string or environment detail beyond a status.

---

## Out of scope (MVP)

Multi-tenancy, real email delivery, e-signatures, billing, SSO, refresh tokens, client-side file
preview, virus scanning, and file download for clients.
