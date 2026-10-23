# 010: Phase 1 schema — MagicLink and UploadedFile
**Phase:** 1   **Depends on:** 001 (merged)   **Parallel-safe with:** nothing — sequential, and must merge before 011, 012 and 013 start

## Goal
Add the two entities Phase 1 needs, their configuration and one migration, so that the three
parallel tasks after it never touch the schema.

## Why this is alone and first

A migration is the hottest merge conflict in the repository: two branches each adding one produces
a model snapshot conflict that is miserable to resolve and easy to resolve wrongly. `PLAN.md` §7
therefore puts all schema work for a phase in one sequential task. **011, 012 and 013 do not start
until this is merged.**

Nothing in this task adds an endpoint, a service or a page. Entities, configuration, migration,
and the tests that prove the shape is right.

## Scope

**May touch**
- `api/src/FileInviteLite.Api/Domain/MagicLink.cs`, `UploadedFile.cs`
- `api/src/FileInviteLite.Api/Domain/AuditEvent.cs` — new `AuditAction` constants only
- `api/src/FileInviteLite.Api/Data/AppDbContext.cs` — the two new `DbSet` lines only
- `api/src/FileInviteLite.Api/Data/Configurations/` — one configuration per new entity
- `api/src/FileInviteLite.Api/Data/Migrations/` — **one** new migration
- `api/tests/FileInviteLite.Api.Tests/Features/Schema/`

**Must not touch**
Everything else. No endpoint, no service, no frontend file, no project file, no document.

## The entities

Fields are fixed by `docs/architecture.md` §5. Do not add, rename or drop any.

```
MagicLink      Id, RequestId, TokenHash, ExpiresAt, RevokedAt
UploadedFile   Id, ChecklistItemId, OriginalName, ContentType, SizeBytes, Sha256,
               StorageKey, UploadedAt
```

Decisions, already made:

- `MagicLink.TokenHash` is the **SHA-256 of the token, hex-encoded, 64 chars**, with a **unique
  index**. The token itself is never a column and never reaches the database (S2.2).
- `MagicLink.RevokedAt` and `UploadedFile` timestamps are UTC `DateTime`, never `DateTimeOffset`
  (ADR 0001). `ExpiresAt` is compared in SQL, so this matters.
- **No cascade delete** on either relationship, as everywhere else.
- `UploadedFile.StorageKey` carries a unique index: the same key must never be reused.
- `UploadedFile.Sha256` is indexed but **not** unique — two clients may legitimately upload the
  same file and must stay independently erasable (ADR 0005).
- `OriginalName` max 260, `ContentType` max 128, `StorageKey` max 128.
- Add to `AuditAction`: `RequestSent`, `MagicLinkIssued`, `MagicLinkRevoked`, `PortalAccessed`,
  `ItemUploaded`.
- Navigation: `DocumentRequest` gains `MagicLinks`, `ChecklistItem` gains `Files`. Both are plain
  `List<T>` initialised to `[]`, matching `DocumentRequest.Items`.

## Acceptance criteria

- [ ] Both entities exist with exactly the fields above, and `dotnet build` is clean
- [ ] One migration, additive only; the two existing migrations are untouched
- [ ] The migration applies on top of a database built from the existing migrations, not only to
      an empty one — extend the pattern in `MigrationUpgradeTests.cs`
- [ ] `MagicLink.TokenHash` has a unique index, proven by a test that a duplicate insert throws
- [ ] `UploadedFile.StorageKey` has a unique index, proven the same way
- [ ] `UploadedFile.Sha256` permits duplicates, proven by a test that two rows with the same hash
      both insert
- [ ] Deleting nothing cascades: a test asserts the delete behaviour on both relationships is
      `Restrict` or `NoAction`
- [ ] A test asserts `ExpiresAt` can be compared in a SQL `WHERE` and the query translates — the
      same `ToQueryString()` technique as `DocumentRequestRulesTests`

## Follow the pattern in

- `api/src/FileInviteLite.Api/Data/Configurations/DocumentRequestConfiguration.cs`
- `api/tests/FileInviteLite.Api.Tests/Features/Requests/MigrationUpgradeTests.cs`

## Done when

`npm run check` green from the repo root, `dotnet format --verify-no-changes` clean, CI green on
the PR, security-reviewer run and findings resolved.

## Progress notes

(append here as you go)
