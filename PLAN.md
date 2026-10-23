# FileInvite-Lite: Project Plan

> **Status:** original project plan. After Phase 0, the current scope lives in `docs/spec.md`,
> `docs/architecture.md` and `docs/tasks/`. This file is kept as a historical record and is not
> updated after Phase 0.
>
> **For Claude Code:** read this file fully before doing anything. Section 2 records decisions
> that are already made: document them, don't revisit them. Work phase by phase and stop for
> human review at the end of each phase.

A prototype document collection platform (in the spirit of FileInvite), built to demonstrate
advanced AI-assisted engineering: planning work for agents, verification loops, parallel
development, context management and grounding agents in a codebase.

---

## 1. Product

Staff request documents from a client. The client uploads them through a magic link (no account).
Staff approve or reject each document. The system chases overdue items, records every action in
an audit log, and notifies external systems when a request completes.

### Users
- **Staff user:** creates requests, reviews uploads, sees audit history. Signs in.
- **Client:** receives a magic link, uploads requested documents, sees what's still outstanding.
  No account.

### MVP features
1. **Request builder:** staff create a request for a client (name, email, due date) with a
   checklist of items (title, description, required yes/no). **Sending** a request generates a
   magic link, which staff can copy from the UI; in development the "email" goes to a local dev sink.
2. **Client upload portal:** magic-link access to one request only; upload files per checklist
   item; validated by type (PDF, JPG, PNG, DOCX, XLSX) and size (max 20 MB).
3. **Review workflow:** staff open or download each uploaded file, then approve or reject the item
   with a comment. A rejected item reopens for re-upload. The request status rolls up from item statuses.
4. **Reminders + audit log:** a background job sends reminders for overdue or incomplete requests
   (dev sink only, no real email). Every create, view, upload, download, review and reminder writes
   an append-only audit event. Staff can view a request's audit trail.
5. **Completion webhook:** when a request is complete, POST a signed (HMAC) JSON payload to a
   configured URL, with retries. Demonstrates integration with customers' existing systems.

### Out of scope (MVP)
Multi-tenancy/organisations, real email delivery, e-signatures, billing, SSO, refresh tokens.

---

## 2. Decisions already made

| Area | Decision |
|---|---|
| Backend | ASP.NET Core (.NET 9) **minimal APIs** |
| Code organisation | **Vertical slices**: one folder per feature under `Features/`, one endpoint per file named as an action (`CreateRequest.cs`) |
| Endpoint registration | Auto-discovered: each endpoint implements `IEndpoint`; startup registers all by reflection. Features never edit `Program.cs` |
| EF configuration | One `IEntityTypeConfiguration<T>` per entity, loaded with `ApplyConfigurationsFromAssembly`. Features never edit `AppDbContext` for config |
| Data | EF Core + **SQLite** (WAL locally; see "Deployed data + files"); provider chosen by config so another provider (e.g. Azure SQL) can be added later. Timestamps stored as **UTC `DateTime`**, not `DateTimeOffset`, because the SQLite provider can't compare or sort `DateTimeOffset` in queries (needed for overdue checks). Ids are GUIDs, so they can't be enumerated |
| Files | Local disk behind `IFileStorage`; stored under generated keys |
| Background jobs | `BackgroundService` for reminders and webhook retries |
| **Staff auth** | **JWT bearer** issued by `POST /api/auth/login`; HS256 signing key from user secrets / env (never committed); 60-minute expiry; no refresh tokens; one seeded demo staff user; password hashed with ASP.NET Core `PasswordHasher` |
| JWT on the client | Held in memory in the React app (lost on reload, acceptable for a prototype). Trade-off vs localStorage/httpOnly cookie recorded in an ADR |
| Client (portal) auth | Magic link: random token, only its SHA-256 hash stored, expiry + revocation, scoped to one request. The token travels in the **URL fragment** (`/portal#t=...`) so it never reaches server logs; the React app exchanges it once for a short-lived portal session token scoped to that request, then removes it from the address bar |
| Email | `IEmailSender` with a dev sink that writes to a gitignored local folder in Development only; in production, sending is a no-op and staff copy the link from the UI. Real email is out of scope |
| Frontend | React + TypeScript + Vite; feature folders under `web/src/features/`; each feature exports its own routes |
| Tests | xUnit + `WebApplicationFactory` integration tests; Vitest + React Testing Library |
| CI/CD | GitHub Actions. Agents write the pipeline; they never perform deployments. Deploys run only on merge to `main` after all checks pass |
| **Backend hosting** | **Azure App Service (Linux, .NET 9), F1 free tier**, single instance, deployed by GitHub Actions. Azure login via **OIDC federated credentials** (no long-lived secrets in GitHub). App secrets (JWT key, webhook secret) in App Service settings / Key Vault |
| **Frontend hosting** | **Vercel**, deployed by GitHub Actions (Vercel CLI) after CI passes, so the same gate applies to both halves. API base URL via `VITE_API_URL` |
| Cross-origin | Frontend and API are on different origins: API CORS allows only the Vercel production URL (and localhost in dev). JWT sent as a bearer header, so no cookies cross origins. Magic links point at the Vercel URL |
| Deployed data + files | **SQLite in production** for simplicity (prototype-only), recorded in an ADR with its limits. Database and uploaded files live on App Service's persistent `/home` volume. Because that volume is network-backed: **single instance only**, journal mode **DELETE** in production (WAL locally), connection string and storage path from config. Limits to note in the ADR: no scale-out, F1 storage cap (1 GB), app sleeps when idle (background jobs pause), backups are manual. Upgrade path, not built now: Azure SQL free offer via the config-driven EF provider, and Blob Storage via a second `IFileStorage` |
| Models | Opus for planning, task breakdown and security review; Sonnet for implementation; escalate to Opus after two failed attempts |

---

## 3. Data model

- `StaffUser`: Id, Email, Name, PasswordHash
- `DocumentRequest`: Id, ClientName, ClientEmail, DueDate, Status (Draft, Sent, InProgress, Complete), CreatedBy, CreatedAt. "Overdue" is **derived** from DueDate and status, not stored
- `ChecklistItem`: Id, RequestId, Title, Description, Required, Status (Outstanding, Uploaded, Approved, Rejected)
- `UploadedFile`: Id, ChecklistItemId, OriginalName, ContentType, SizeBytes, Sha256, StorageKey, UploadedAt
- `ReviewDecision`: Id, ChecklistItemId, Decision (Approve, Reject), Comment, ReviewedBy, ReviewedAt
- `MagicLink`: Id, RequestId, TokenHash, ExpiresAt, RevokedAt
- `Reminder`: Id, RequestId, SentAt, Reason
- `WebhookDelivery`: Id, RequestId, Url, PayloadHash, Attempts, LastStatus, DeliveredAt
- `AuditEvent` (append-only): Id, OccurredAt, ActorType (Staff, Client, System), ActorId, Action, EntityType, EntityId, Metadata (JSON)

---

## 4. Security rules (non-negotiable; SOC 2 / GDPR mindset)

- A magic link grants access to **exactly one request**. Every client endpoint checks this.
- Store only hashes of magic-link tokens. Never log tokens, JWTs, passwords or file contents.
- Staff endpoints require a valid JWT; portal endpoints never accept a staff JWT as a substitute
  for the magic-link check, and vice versa.
- Validate uploads by extension **and** content sniffing; enforce the size limit server-side.
- Every state-changing action writes an `AuditEvent` **in the same transaction**.
- Audit events are never updated or deleted by application code.
- Magic-link tokens travel only in the URL fragment; portal pages send `Referrer-Policy: no-referrer`.
- Rate-limit the login and magic-link exchange endpoints (ASP.NET Core rate limiter).
- Staff file downloads require a JWT, are served as attachments with the stored content type,
  and write an audit event. Files are never served from a public path.
- Webhook payloads are HMAC-signed; the secret comes from config, never source. The webhook URL
  comes from config only and must be HTTPS. Users never supply it, which rules out SSRF.
- The dev email sink is disabled outside Development, because it contains magic links.
- **GDPR erasure vs append-only audit:** resolve in an ADR (proposed: delete or pseudonymise
  client personal data and files; audit events keep a pseudonymous actor ID).

---

## 5. Phases

Each phase ends with: all checks green, PRs merged, `docs/ai-log.md` updated, human review.

### Phase 0: Foundations (human-led, agent-assisted)
**0a. Documents** (agent drafts on Opus, human reviews and edits). Produce:
- `docs/spec.md`: acceptance criteria per feature, written as testable statements
- `docs/architecture.md`: folder layout for `api/` and `web/`, layering rules, data model
- `docs/api-contract.md`: all endpoints, request/response shapes, error format
- `docs/adr/`: SQLite; minimal APIs + vertical slices; JWT staff auth; magic links; file storage;
  GDPR erasure vs audit log; hosting (Azure App Service + Vercel, OIDC, CORS)
- `CLAUDE.md` (root, short): build/test/run commands, conventions, security rules,
  "never weaken or delete acceptance tests", "stay inside the task's file scope",
  "run all checks before finishing", rebase-and-retest before PR
- `api/CLAUDE.md`, `web/CLAUDE.md`: stack-specific conventions only
- `.claude/agents/test-writer.md`: writes acceptance tests from spec + task file, without reading
  the implementation
- `.claude/agents/security-reviewer.md`: read-only, Opus, reviews branches against §4
- `.claude/settings.json`: a hook that runs the relevant tests when the agent finishes a turn
- `docs/review-checklist.md`: human checklist for reviewing AI-generated code
- `docs/ai-log.md`: empty template (date, what happened, how caught, guardrail added)
- `docs/discovery.md`: template for notes from talking to someone who chases documents for a living

**0b. Scaffold** (agent): .NET solution and Vite app in the documented layout, `IEndpoint`
auto-registration, JWT auth, seeded demo staff user, one-command local run, CI workflow
(build, test, typecheck, lint, vulnerability scan). CI green on an empty build.

**0c. Reference slice** (written by the human): "create request" + "list requests", end to end:
endpoint, entity + config, migration, audit event, integration tests, React page. Agents copy
this pattern.

**0d. Task breakdown** (agent drafts on Opus, human reviews): task files in `docs/tasks/` for
phases 1–3 using the template in §6.

### Phase 1: Portal + request builder completion
1. **Schema task (sequential, first):** all entities and the single migration for this phase.
2. **Then in parallel** (separate worktree sessions): send request + magic-link generation,
   portal API (token exchange, view, upload), portal UI.

### Phase 2: Review + reminders + audit viewer + webhook
1. **Schema task (sequential, first):** remaining entities and the single migration.
2. **Then in parallel:** review API + UI (including file download), reminder job, audit trail
   viewer, webhook delivery.

### Phase 3: Production readiness
- Structured logging with correlation IDs; no sensitive data in logs.
- `/health` endpoint; error tracking.
- Production SQLite + files on `/home` per §2: DELETE journal mode, single instance, EF migrations
  applied at startup, a simple backup step (copy the DB file to a dated backup) documented in the runbook.
- Deploy pipeline: API → Azure App Service (OIDC login), web → Vercel; both only after CI passes
  on `main`. Production CORS locked to the Vercel URL.
- Smoke test after each deploy (hit `/health` and the login endpoint); fail the workflow if it fails.
- `docs/runbook.md` and one simulated incident write-up (detect, diagnose, fix, prevent).
- README with screenshots and a 3-minute demo video. It opens with: "An independent learning
  prototype inspired by FileInvite; not affiliated with or endorsed by FileInvite."

---

## 6. Task file template (`docs/tasks/NNN-short-name.md`)

```markdown
# NNN: <title>
**Phase:** 1   **Depends on:** 010-phase1-schema   **Parallel-safe with:** 012, 013

## Goal
One sentence.

## Scope
- May touch: api/src/FileInviteLite.Api/Features/Portal/**, api/tests/**/Features/Portal/**
- Must not touch: AppDbContext, Migrations/, Program.cs, package/project files, web/**

## Acceptance criteria
- [ ] A magic link for request A returns 404 for request B's items
- [ ] An expired or revoked link returns 401
- [ ] A successful upload writes an "ItemUploaded" AuditEvent in the same transaction

## Follow the pattern in
api/src/FileInviteLite.Api/Features/Requests/CreateRequest.cs

## Done when
`dotnet test --filter Portal` passes, CI is green, security-reviewer findings resolved.

## Progress notes
(agent appends here so a fresh session can resume)
```

---

## 7. Per-task workflow

```
task file → test-writer → implement → security-reviewer → PR + CI → human review → merge
            (red tests)   (green)     (findings fixed)
```

- The human writes the acceptance tests for the riskiest tasks (auth, magic links, uploads).
- Parallel tasks run in separate `claude --worktree <name>` sessions with non-overlapping scopes.
- Shared hotspots (migrations, `AppDbContext`, `Program.cs`, package/project files, API contract)
  are only changed in sequential tasks.
- Merge one PR at a time; rebase onto latest main and re-run all tests before each merge.
- Agent-made conflict resolutions are treated as untrusted and read by the human.
- New reusable agents are added only when `docs/ai-log.md` shows a recurring need.

---

## 8. Next step for Claude Code

**Phase 0a only.** Draft every document listed under 0a, based on this plan and its decisions.
Then stop and summarise what you wrote, plus any open questions. Do not scaffold or write code.
