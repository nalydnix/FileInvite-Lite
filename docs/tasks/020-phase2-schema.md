# 020: Phase 2 schema — ReviewDecision, Reminder, WebhookDelivery
**Phase:** 2   **Depends on:** 010, 011, 012, 013 (all merged)   **Parallel-safe with:** nothing — sequential, and must merge before 021–025 start

## Goal
Add the three remaining entities, their configuration and one migration, so that the four parallel
tasks after it never touch the schema.

## Why this is alone and first

Same reason as 010: a migration is the hottest merge conflict in the repository, and a model
snapshot conflict is easy to resolve wrongly. `PLAN.md` §7 puts all schema work for a phase in one
sequential task. **021, 022, 023, 024 and 025 do not start until this is merged.**

This task adds no endpoint, no service, no job and no page.

## Scope

**May touch**
- `api/src/FileInviteLite.Api/Domain/ReviewDecision.cs`, `Reminder.cs`, `WebhookDelivery.cs`
- `api/src/FileInviteLite.Api/Domain/AuditEvent.cs` — new `AuditAction` constants only
- `api/src/FileInviteLite.Api/Domain/ChecklistItem.cs` — the one new navigation property
- `api/src/FileInviteLite.Api/Domain/DocumentRequest.cs` — the two new navigation properties only;
  **do not touch `RollUpStatus` or the overdue expressions**
- `api/src/FileInviteLite.Api/Data/AppDbContext.cs` — the three new `DbSet` lines only
- `api/src/FileInviteLite.Api/Data/Configurations/` — one configuration per new entity
- `api/src/FileInviteLite.Api/Data/Migrations/` — **one** new migration
- `api/tests/FileInviteLite.Api.Tests/Features/Schema/`
- `docs/adr/0005-file-storage.md` — document `IFileStorage.DiscardAsync` as rollback-only (added by
  the project owner; task 012 put the method on the interface but the ADR omitted it)

**Must not touch**
Everything else. No endpoint, no service, no job, no frontend file, no project file, no document
other than appending to this task's Progress notes.

## The entities

Fields are fixed by `docs/architecture.md` §5. Do not add, rename or drop any.

```
ReviewDecision    Id, ChecklistItemId, Decision, Comment, ReviewedBy, ReviewedAt
Reminder          Id, RequestId, SentAt, Reason
WebhookDelivery   Id, RequestId, Url, PayloadHash, Attempts, LastStatus, DeliveredAt
```

Decisions, already made:

- `ReviewDecision.Decision` is a new enum `ReviewOutcome { Approve, Reject }`, stored **as a
  string** like every other enum in the project.
- `ReviewDecision.Comment` is `string?`, max 2000, matching the contract's limit. It is required on
  reject, but that is an endpoint rule (S4.5), not a column constraint — the column stays nullable.
- `ReviewDecision.ReviewedBy` is the staff user's `Guid`. `ReviewedAt` is UTC `DateTime`.
- `Reminder.Reason` is `string`, max 200. `Reminder.SentAt` is UTC `DateTime`.
- `Reminder` is indexed on `(RequestId, SentAt)` — the cool-down query in S5.2 reads the latest
  reminder per request and must do it in SQL, not in memory.
- `WebhookDelivery.Url` max 2048, `PayloadHash` is SHA-256 hex (64 chars), `Attempts` is `int`
  defaulting to 0, `LastStatus` is `int?` (null until the first attempt), `DeliveredAt` is a
  nullable UTC `DateTime` set only on success (S7.6).
- `WebhookDelivery` is indexed on `DeliveredAt` — the delivery job's pending query filters on it.
- **No cascade delete** on any of the three relationships, as everywhere else.
- Add to `AuditAction`: `FileDownloaded`, `ItemApproved`, `ItemRejected`, `RequestCompleted`,
  `ReminderSent`, `WebhookQueued`, `WebhookDelivered`, `WebhookFailed`.
- Navigation: `ChecklistItem` gains `Decisions`, `DocumentRequest` gains `Reminders` and
  `WebhookDeliveries`. All plain `List<T>` initialised to `[]`, matching `DocumentRequest.Items`.

## Acceptance criteria

- [ ] All three entities exist with exactly the fields above, and `dotnet build` is clean
- [ ] One migration, additive only; the three existing migrations are untouched
- [ ] The migration applies on top of a database built from the existing migrations, not only to an
      empty one — extend the pattern in `MigrationUpgradeTests.cs`
- [ ] Enums are stored as strings, proven by a test that reads the raw column value
- [ ] `WebhookDelivery.PayloadHash` accepts duplicates (two completions of different requests may
      hash identically in principle) — no unique index
- [ ] A test asserts the delete behaviour on all three relationships is `Restrict` or `NoAction`
- [ ] A test asserts `Reminder.SentAt`, `WebhookDelivery.DeliveredAt` and
      `ReviewDecision.ReviewedAt` can each be compared in a SQL `WHERE` and the query translates —
      the `ToQueryString()` technique from `DocumentRequestRulesTests`
- [ ] `AuditEvent` still has no navigation property, so no cascade can reach it

## Follow the pattern in

- `api/src/FileInviteLite.Api/Data/Configurations/DocumentRequestConfiguration.cs`
- whatever 010 produced under `Data/Configurations/` for `MagicLink` and `UploadedFile`
- `api/tests/FileInviteLite.Api.Tests/Features/Requests/MigrationUpgradeTests.cs`

## Done when

`npm run check` green from the repo root, `dotnet format --verify-no-changes` clean, CI green on
the PR, security-reviewer run and findings resolved.

## Progress notes

(append here as you go)

### 2026-10-23 — implemented

- Tests first (`Phase2SchemaTests`, `Phase2MigrationTests`, kit helpers): red because the three
  entity types did not exist (CS0246/CS0103), then green after the entities, configurations,
  navigations, `AuditAction` constants and `DbSet` lines.
- One migration, `AddReviewRemindersAndWebhooks`: three CreateTable + four CreateIndex, nothing
  else. Indexes: `Reminders(RequestId, SentAt)`, `ReviewDecisions(ChecklistItemId)`,
  `WebhookDeliveries(DeliveredAt)`, `WebhookDeliveries(RequestId)`. `PayloadHash` has no index.
  Existing three migrations untouched; only the model snapshot changed.
- `Decision` is stored as text, max 16. `Attempts` has a database default of 0.
- `Phase1MigrationTests` asserted that Phase 1's migration was the *last* one (exact pending list
  and exact migration name list). A new migration necessarily changes that, so I loosened those two
  assertions to "first pending is Phase 1's" and "first three names are the known ones". Phase 1's
  additive-only and keeps-rows checks are unchanged; `Phase2MigrationTests` now pins the new
  migration. Flagged for review as a touch to an existing test.
- Also amended ADR 0005 for `IFileStorage.DiscardAsync` (rollback-only), per the owner.
- Verified: `dotnet build` 0 warnings/errors; `dotnet test` 277 passed; `dotnet format
  --verify-no-changes` clean. Frontend untouched, not re-run.
