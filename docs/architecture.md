# FileInvite-Lite: Architecture

How the code is laid out, which direction dependencies run, and the rules an agent must follow to
add a feature without touching anyone else's files.

---

## 1. Shape of the system

```
                 Vercel                         Azure App Service (Linux, F1)
        ┌─────────────────────┐            ┌──────────────────────────────────┐
        │  web/  React + Vite │  HTTPS     │  api/  ASP.NET Core 9 minimal    │
        │  staff app + portal │ ─────────▶ │        APIs, vertical slices     │
        └─────────────────────┘  JWT /     │                                  │
                                 portal    │  BackgroundService: reminders,   │
                                 session   │  webhook retries                 │
                                           │                                  │
                                           │  /home (persistent volume)       │
                                           │    ├── app.db   (SQLite)         │
                                           │    └── files/   (uploads)        │
                                           └──────────────────────────────────┘
                                                        │  HMAC-signed POST
                                                        ▼
                                                 configured webhook URL
```

Two origins, no cookies between them. The JWT travels as a bearer header; CORS allows exactly one
production origin. See `docs/adr/0007-hosting.md`.

---

## 2. Repository layout

```
/
├── PLAN.md                        original plan, frozen after Phase 0
├── CLAUDE.md                      root conventions for agents
├── README.md
├── docs/
│   ├── spec.md                    acceptance criteria (S1.1 …)
│   ├── architecture.md            this file
│   ├── api-contract.md            endpoints, shapes, errors
│   ├── review-checklist.md        human review checklist
│   ├── ai-log.md                  incidents and guardrails
│   ├── discovery.md               user-research notes template
│   ├── runbook.md                 added in Phase 3
│   ├── adr/                       0001… architecture decision records
│   └── tasks/                     NNN-short-name.md task files
├── .claude/
│   ├── settings.json              hooks
│   └── agents/                    test-writer.md, security-reviewer.md
├── .github/workflows/             ci.yml, deploy-api.yml, deploy-web.yml
├── api/
│   ├── FileInviteLite.sln
│   ├── src/FileInviteLite.Api/
│   └── tests/FileInviteLite.Api.Tests/
└── web/
    ├── package.json
    └── src/
```

---

## 3. Backend layout (`api/src/FileInviteLite.Api/`)

```
FileInviteLite.Api/
├── Program.cs                     host wiring only — features never edit this
├── appsettings.json
├── appsettings.Development.json
├── Common/
│   ├── IEndpoint.cs               the auto-registration contract
│   ├── EndpointRegistration.cs    reflection scan + MapEndpoints()
│   ├── Results/ProblemResults.cs  helpers producing the RFC 7807 shape
│   ├── Validation/                shared validators (email, due date, page size)
│   └── Security/
│       ├── AuthPolicies.cs        "Staff" and "Portal" policy names
│       ├── CurrentStaff.cs        reads the staff id from the JWT principal
│       └── CurrentPortal.cs       reads the request id from the portal principal
├── Domain/
│   ├── DocumentRequest.cs         entities, enums, and invariants only
│   ├── ChecklistItem.cs
│   ├── UploadedFile.cs
│   ├── ReviewDecision.cs
│   ├── MagicLink.cs
│   ├── Reminder.cs
│   ├── WebhookDelivery.cs
│   ├── AuditEvent.cs
│   └── StaffUser.cs
├── Data/
│   ├── AppDbContext.cs            DbSets only; ApplyConfigurationsFromAssembly
│   ├── Configurations/            one IEntityTypeConfiguration<T> per entity
│   ├── Migrations/                one migration per phase schema task
│   └── Seed/DemoDataSeeder.cs     the single demo staff user
├── Services/
│   ├── Audit/IAuditWriter.cs      + AuditWriter.cs
│   ├── Storage/IFileStorage.cs    + LocalDiskFileStorage.cs
│   ├── Email/IEmailSender.cs      + DevSinkEmailSender.cs, NoOpEmailSender.cs
│   ├── Tokens/IMagicLinkTokens.cs + MagicLinkTokens.cs (generate, hash, verify)
│   ├── Files/IContentSniffer.cs   + MagicByteContentSniffer.cs
│   └── Webhooks/IWebhookSigner.cs + HmacWebhookSigner.cs
├── Features/
│   ├── Auth/            Login.cs
│   ├── Requests/        CreateRequest.cs, ListRequests.cs, GetRequest.cs, SendRequest.cs
│   ├── Portal/          ExchangeToken.cs, GetPortalRequest.cs, UploadFile.cs
│   ├── Review/          DownloadFile.cs, ApproveItem.cs, RejectItem.cs
│   ├── Audit/           GetRequestAuditTrail.cs
│   └── Health/          GetHealth.cs
└── Jobs/
    ├── ReminderService.cs
    └── WebhookDeliveryService.cs
```

### One endpoint per file

Each file under `Features/` holds one endpoint: its route, its request and response records, its
validation and its handler. The file is named after the action, not the HTTP verb or the resource
(`CreateRequest.cs`, not `RequestsPost.cs`).

```csharp
// Features/Requests/CreateRequest.cs
public sealed class CreateRequest : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/requests", Handle)
           .RequireAuthorization(AuthPolicies.Staff)
           .WithName("CreateRequest");

    public sealed record Body(string ClientName, string ClientEmail, DateOnly DueDate, IReadOnlyList<ItemBody> Items);
    public sealed record ItemBody(string Title, string? Description, bool Required);
    public sealed record Response(Guid Id);

    private static async Task<IResult> Handle(Body body, AppDbContext db, IAuditWriter audit,
                                              CurrentStaff staff, CancellationToken ct) { /* … */ }
}
```

Why: a feature is added by creating files in one folder. No shared registry, no merge conflicts
between parallel agents.

### Endpoint auto-registration

`IEndpoint` has one method, `Map(IEndpointRouteBuilder)`. At startup `MapEndpoints()` scans the
assembly for non-abstract `IEndpoint` implementations, instantiates each and calls `Map`.
**Adding an endpoint never edits `Program.cs`.**

### EF configuration

`AppDbContext` holds `DbSet` properties and `ApplyConfigurationsFromAssembly(...)` only. Every
entity gets its own `IEntityTypeConfiguration<T>` under `Data/Configurations/`. **Adding an entity
configuration never edits `AppDbContext`** beyond the one-line `DbSet`, which happens in the
phase's sequential schema task.

### Layering rules

```
Features/  →  Services/  →  Data/  →  Domain/
Jobs/      →  Services/  →  Data/  →  Domain/
Common/    ←  used by anything
```

- `Domain/` references nothing in the project. No EF attributes, no ASP.NET types.
- `Data/` references `Domain/` only.
- `Services/` reference `Data/`, `Domain/` and `Common/`.
- `Features/` and `Jobs/` are the only places that reference ASP.NET Core request types.
- **A feature folder never references another feature folder.** Shared behaviour moves into
  `Services/` or `Common/` in a sequential task, never by reaching sideways.

### No repository layer, no MediatR

Handlers use `AppDbContext` directly. The project is small enough that an extra indirection buys
nothing, and vertical slices already isolate change. Recorded in `docs/adr/0002-minimal-apis-vertical-slices.md`.

### Audit writing

Handlers do not construct `AuditEvent` rows by hand. They call `IAuditWriter`:

```csharp
audit.Add(AuditAction.RequestCreated, ActorType.Staff, staff.Id,
          nameof(DocumentRequest), request.Id, new { request.ClientEmail });
await db.SaveChangesAsync(ct);   // one transaction: state change + audit event
```

`AuditWriter` adds the entity to the same `DbContext` change tracker, so a single `SaveChangesAsync`
commits the state change and its audit event together. Multi-statement handlers wrap their work in
an explicit transaction. This is how S6.1 is satisfied; a handler that saves state and audit in two
separate `SaveChangesAsync` calls is a review failure.

### Error shape

Handlers return `Results.Problem(...)` through the helpers in `Common/Results/ProblemResults.cs`,
so every error body matches `docs/api-contract.md` §Errors. Unhandled exceptions are converted by
the exception handler registered in `Program.cs`; raw exception text never reaches a client.

---

## 4. Authentication and authorisation

Two schemes, two policies, no overlap.

| | Staff | Portal |
|---|---|---|
| Credential | JWT bearer, HS256, 60 min | Portal session token, scoped to one request |
| Issued by | `POST /api/auth/login` | `POST /api/portal/exchange` |
| Policy | `AuthPolicies.Staff` | `AuthPolicies.Portal` |
| Claims | `sub` = staff id, `email` | `sub` = portal session id, `request_id` |

Each policy requires its own authentication scheme, so a token of one kind cannot authorise an
endpoint of the other kind (S2.7, S2.8). Portal handlers read the request id from
`CurrentPortal.RequestId` and **filter every query by it** — never from the route, never from the
body. A route parameter is only ever used to narrow within that request.

```csharp
var item = await db.ChecklistItems
    .Where(i => i.Id == itemId && i.RequestId == portal.RequestId)   // scope first
    .SingleOrDefaultAsync(ct);
if (item is null) return ProblemResults.NotFound();   // 404, never 403
```

Returning `404` rather than `403` keeps a client from learning that another request's item exists.

---

## 5. Data model

```
StaffUser 1 ──< DocumentRequest 1 ──< ChecklistItem 1 ──< UploadedFile
                      │                      │
                      │                      └──< ReviewDecision
                      ├──< MagicLink
                      ├──< Reminder
                      └──< WebhookDelivery

AuditEvent  (no foreign keys; EntityType + EntityId are a loose reference)
```

| Entity | Fields |
|---|---|
| `StaffUser` | Id, Email, Name, PasswordHash |
| `DocumentRequest` | Id, ClientName, ClientEmail, DueDate, Status, CreatedBy, CreatedAt |
| `ChecklistItem` | Id, RequestId, Title, Description, Required, Status |
| `UploadedFile` | Id, ChecklistItemId, OriginalName, ContentType, SizeBytes, Sha256, StorageKey, UploadedAt |
| `ReviewDecision` | Id, ChecklistItemId, Decision, Comment, ReviewedBy, ReviewedAt |
| `MagicLink` | Id, RequestId, TokenHash, ExpiresAt, RevokedAt |
| `Reminder` | Id, RequestId, SentAt, Reason |
| `WebhookDelivery` | Id, RequestId, Url, PayloadHash, Attempts, LastStatus, DeliveredAt |
| `AuditEvent` | Id, OccurredAt, ActorType, ActorId, Action, EntityType, EntityId, Metadata |

Conventions:

- **Ids are GUIDs**, generated in application code, stored as `TEXT` in SQLite.
- **Timestamps are UTC `DateTime`**, not `DateTimeOffset`: the SQLite provider cannot translate
  `DateTimeOffset` comparisons into SQL, and the overdue query needs exactly that
  (`docs/adr/0001-sqlite.md`). `DueDate` is a `DateOnly`.
- **Enums are stored as strings**, so a migration that reorders an enum cannot silently reinterpret
  existing rows.
- **No cascade delete anywhere.** Nothing in the MVP deletes a domain row; GDPR erasure
  pseudonymises instead (`docs/adr/0006-gdpr-erasure.md`).
- `AuditEvent` has no navigation properties, so a cascade can never reach it.
- Indexes: `MagicLink.TokenHash` (unique), `DocumentRequest.DueDate`, `DocumentRequest.Status`,
  `AuditEvent.EntityId`, `ChecklistItem.RequestId`, `UploadedFile.ChecklistItemId`.

### Migrations

One migration per phase, written in that phase's sequential schema task. Parallel tasks never add
a migration — that is the single hottest merge conflict in the repo and the reason schema work is
sequential (`PLAN.md` §7).

---

## 6. File storage

`IFileStorage` has `SaveAsync(stream, ct) → storageKey`, `OpenReadAsync(key, ct)` and
`ExistsAsync(key, ct)`. The local implementation writes under a configured root
(`Storage:RootPath`; `/home/files` in production, a gitignored folder locally).

- The storage key is `yyyy/MM/{guid}` with no extension and no part of the original filename
  (S3.8). The original name lives in the database and is reapplied on download.
- The root folder is never mapped as static files. Downloads go through `DownloadFile.cs`, which
  requires the staff policy and writes an audit event (S4.1–S4.3).
- Uploads are streamed to disk with a server-side byte counter that aborts past the 20 MB limit;
  the request's `Content-Length` is advisory only (S3.7).
- The first bytes are sniffed against a magic-byte table before the file is accepted (S3.6).
  DOCX and XLSX are ZIP containers, so sniffing checks the ZIP header plus the expected entry.

---

## 7. Background jobs

Both jobs are `BackgroundService`s registered in `Program.cs`. Each one:

- creates its own `IServiceScope` per pass (never captures a scoped `DbContext` in a field),
- wraps a pass in try/catch so one failure cannot stop the host (S5.6),
- reads its interval from configuration,
- is disabled by a configuration flag in integration tests, so tests drive the pass directly.

`ReminderService` finds overdue requests, respects the cool-down, writes a `Reminder` row plus a
`ReminderSent` audit event per request.

`WebhookDeliveryService` picks up pending `WebhookDelivery` rows, signs the body with HMAC-SHA256
and posts it, then records the attempt. Backoff is exponential with a cap on attempts (S7.5, S7.6).

**App Service F1 sleeps when idle, so a pass can be skipped for hours.** Jobs are therefore written
to catch up from state in the database, never to assume they ran on schedule.

---

## 8. Frontend layout (`web/`)

```
web/
├── index.html
├── vite.config.ts
├── src/
│   ├── main.tsx                  router assembly: collects routes from features
│   ├── app/
│   │   ├── AuthContext.tsx       AuthProvider: JWT in memory, no persistence
│   │   ├── authState.ts          the context object, its type and the useAuth hook
│   │   ├── RequireStaffAuth.tsx  redirects an unauthenticated visitor to /login
│   │   ├── PortalSessionContext.tsx
│   │   ├── apiClient.ts          fetch wrapper: base URL, bearer header, problem parsing
│   │   └── routes.tsx            route table built from feature exports
│   ├── components/               shared presentational components only
│   └── features/
│       ├── auth/        LoginPage.tsx, routes.ts
│       ├── requests/    RequestListPage.tsx, CreateRequestPage.tsx, RequestDetailPage.tsx, routes.ts
│       ├── review/      ReviewPanel.tsx, routes.ts
│       ├── audit/       AuditTrailPage.tsx, routes.ts
│       └── portal/      PortalPage.tsx, UploadCard.tsx, routes.ts
└── tests/                        Vitest + React Testing Library
```

Rules that mirror the backend:

- **Each feature exports its own routes** from `features/<name>/routes.tsx`; `app/routes.tsx`
  imports them. Adding a page touches one feature folder plus one import line.
- A file that exports a component exports **only** components. Hooks, contexts and constants go
  in a sibling `.ts` file — otherwise React Fast Refresh stops working, and the lint rule
  `react-refresh/only-export-components` fails the build.
- A feature never imports from another feature. Shared UI goes to `components/`, shared state to
  `app/`.
- All network calls go through `app/apiClient.ts`. No component calls `fetch` directly — that is
  the only place the bearer header and the problem-details parsing live.
- `VITE_API_URL` sets the API base. There is no other environment coupling.

### Token handling in the browser

The staff JWT lives in a React context in memory only (S9.2). A reload loses it and the user logs
in again — a deliberate prototype trade-off recorded in `docs/adr/0003-jwt-staff-auth.md`.

The portal reads the magic-link token from `window.location.hash`, calls exchange once, then
replaces the URL with `history.replaceState` to strip the fragment (S9.3). The magic-link token is
never stored; only the exchanged portal session token is held in memory (S9.4).

---

## 9. Testing layout

```
api/tests/FileInviteLite.Api.Tests/
├── Infrastructure/
│   ├── ApiFactory.cs             WebApplicationFactory: SQLite file per test class, jobs off
│   ├── AuthHelpers.cs            issue a staff JWT / exchange a portal session
│   └── LogCapture.cs             in-memory log provider for S2.13
└── Features/
    ├── Auth/ Requests/ Portal/ Review/ Audit/ Webhooks/ Reminders/
```

- Integration tests over a real HTTP pipeline (`WebApplicationFactory`), against a real SQLite file
  in a temp folder, not an in-memory provider: the SQLite provider's query translation is part of
  what is being tested.
- Each test class gets its own database file and storage folder, so classes can run in parallel.
- Tests name the criterion they cover: `Upload_RejectsExeRenamedToPdf_S3_6`.
- Unit tests only where logic is worth isolating (status roll-up, content sniffing, HMAC signing,
  overdue calculation).
- Frontend: Vitest + React Testing Library, network mocked at `apiClient`.

---

## 10. Configuration

| Key | Dev source | Production source |
|---|---|---|
| `ConnectionStrings:Default` | appsettings.Development.json | App Service setting |
| `Database:Provider` | `Sqlite` | `Sqlite` |
| `Database:JournalMode` | `WAL` | `DELETE` |
| `Jwt:SigningKey` | user secrets | App Service setting / Key Vault |
| `Jwt:Issuer`, `Jwt:Audience` | appsettings | App Service setting |
| `Storage:RootPath` | gitignored local folder | `/home/files` |
| `Email:Sink` | gitignored local folder | unused (no-op sender) |
| `Portal:BaseUrl` | `http://localhost:5173` | Vercel URL |
| `Cors:AllowedOrigin` | `http://localhost:5173` | Vercel URL |
| `Webhook:Url` | optional, local listener | App Service setting, HTTPS only |
| `Webhook:Secret` | user secrets | App Service setting / Key Vault |

Startup validates the options it cannot run without — signing key present and ≥ 32 bytes, storage
path writable, webhook URL HTTPS when set — and fails fast with a clear message (S7.7, S8.7).
**No secret is ever committed.** `appsettings.Development.json` holds paths and URLs only.

---

## 11. Rules for agents

1. Stay inside the file scope your task file lists. If the work seems to need a file outside it,
   stop and say so in the task's progress notes.
2. Never edit `Program.cs`, `AppDbContext.cs`, `Data/Migrations/**`, `.csproj`, `package.json` or
   `docs/api-contract.md` from a parallel task. Those are sequential-task territory.
3. Never weaken, skip or delete an acceptance test to make a build pass.
4. Add a feature by adding files to one feature folder.
5. Every state change goes through `IAuditWriter` and commits in one transaction with its audit
   event.
6. Every portal query filters by `CurrentPortal.RequestId` before anything else.
7. Run the backend tests, the frontend tests, the typecheck and the linter before you call a task
   done.
