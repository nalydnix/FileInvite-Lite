# api/ — backend conventions

ASP.NET Core 9 minimal APIs, vertical slices, EF Core + SQLite. Read the root `CLAUDE.md` first;
this file adds only what is specific to the backend. Layout and layering live in
`docs/architecture.md` §3.

## Commands

```bash
dotnet build api/FileInviteLite.sln
dotnet test  api/FileInviteLite.sln
dotnet test  api/FileInviteLite.sln --filter FullyQualifiedName~Portal
dotnet run   --project api/src/FileInviteLite.Api            # http://localhost:5080
dotnet format api/FileInviteLite.sln --verify-no-changes
```

Migrations (sequential schema tasks only):

```bash
dotnet ef migrations add <Name> --project api/src/FileInviteLite.Api
dotnet ef database update     --project api/src/FileInviteLite.Api
```

## Adding an endpoint

One file, one endpoint, named after the action. Nothing outside the feature folder changes.

```csharp
namespace FileInviteLite.Api.Features.Requests;

public sealed class CreateRequest : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/requests", Handle)
           .RequireAuthorization(AuthPolicies.Staff)
           .WithName(nameof(CreateRequest));

    public sealed record Body(string ClientName, string ClientEmail, DateOnly DueDate,
                              IReadOnlyList<ItemBody> Items);
    public sealed record ItemBody(string Title, string? Description, bool Required);
    public sealed record Response(Guid Id);

    private static async Task<IResult> Handle(
        Body body, AppDbContext db, IAuditWriter audit, CurrentStaff staff, CancellationToken ct)
    {
        if (body.Items.Count == 0)
            return ProblemResults.Validation("items", "Items must contain at least one entry.");

        var request = new DocumentRequest { /* … */ };
        db.DocumentRequests.Add(request);

        audit.Add(AuditAction.RequestCreated, ActorType.Staff, staff.Id,
                  nameof(DocumentRequest), request.Id, new { request.ClientEmail });

        await db.SaveChangesAsync(ct);          // state + audit, one transaction
        return Results.Created($"/api/requests/{request.Id}", new Response(request.Id));
    }
}
```

`IEndpoint` implementations are discovered by reflection at startup. **Do not edit `Program.cs`.**

## Rules

### Structure

- `sealed` on endpoint classes and records. Request/response records are **nested in the endpoint
  class** and never shared between features.
- One action per file; the filename is the class name.
- A feature folder never references another feature folder. Shared logic goes to `Services/` or
  `Common/`, in a sequential task.
- `Domain/` has no EF attributes and no ASP.NET types.
- File-scoped namespaces, nullable reference types on, `var` where the type is obvious.

### Data access

- Handlers use `AppDbContext` directly. No repository layer, no MediatR (ADR 0002).
- **Every query that serves a portal endpoint filters by `CurrentPortal.RequestId` first:**

  ```csharp
  var item = await db.ChecklistItems
      .Where(i => i.Id == itemId && i.RequestId == portal.RequestId)
      .SingleOrDefaultAsync(ct);
  if (item is null) return ProblemResults.NotFound();   // 404, never 403
  ```

- Reads that do not feed an update use `AsNoTracking()`.
- Always pass the `CancellationToken`.
- **UTC `DateTime`, never `DateTimeOffset`** — the SQLite provider cannot translate
  `DateTimeOffset` comparisons, and the overdue query needs them (ADR 0001). `DueDate` is a
  `DateOnly`.
- GUID ids, generated in application code.
- Enums are stored as strings (configured per entity), so reordering an enum cannot reinterpret
  existing rows.
- No cascade deletes. Nothing in the MVP deletes a domain row.

### Entity configuration

One `IEntityTypeConfiguration<T>` per entity under `Data/Configurations/`, picked up by
`ApplyConfigurationsFromAssembly`. **Do not edit `AppDbContext`** except for the one `DbSet` line,
and only in a sequential schema task.

### Auditing

Never construct an `AuditEvent` by hand. Call `IAuditWriter.Add(...)`, which attaches it to the
same change tracker, then one `SaveChangesAsync`. A handler that does several writes wraps them in
an explicit transaction. Metadata must never contain a token, password, JWT, storage key or file
content.

### Errors

Return `ProblemResults.*` helpers so every body matches `docs/api-contract.md`. Never return a raw
string, never surface exception text. `404` for anything the caller may not see.

### Authentication

- Staff endpoints: `.RequireAuthorization(AuthPolicies.Staff)`.
- Portal endpoints: `.RequireAuthorization(AuthPolicies.Portal)`.
- There is no third option. The only anonymous endpoints are `/health`, `/api/auth/login` and
  `/api/portal/exchange` — do not add a fourth without a sequential task and a contract change.

### Background services

- Create an `IServiceScope` per pass; never hold a scoped `DbContext` in a field.
- Wrap a pass in try/catch — one failure must not stop the host.
- Read intervals from configuration, and honour the test flag that disables the loop so tests can
  drive a pass directly.
- Assume passes get skipped (App Service F1 sleeps). Catch up from database state; never assume
  "the last pass ran an hour ago".

### Configuration and secrets

- New settings get a strongly typed options class, validated at startup with a clear failure
  message.
- Secrets come from user secrets locally and App Service settings in production. **Never a default
  value in code, never a value in `appsettings*.json`.**

## Tests

`api/tests/FileInviteLite.Api.Tests/`, mirroring the feature folders.

- Integration tests over `WebApplicationFactory`, against a **real SQLite file** in a temp folder
  — not the EF in-memory provider (ADR 0001).
- One database file and storage folder per test class, so classes run in parallel.
- Background jobs are off by default in tests; drive a pass explicitly.
- Name tests after the criterion: `Exchange_ReturnsSameBodyForExpiredAndUnknown_S2_6`.
- Use `ApiFactory`, `AuthHelpers` and `LogCapture` in `Infrastructure/` rather than rolling your
  own setup.
- Unit tests only where logic is worth isolating: status roll-up, content sniffing, HMAC signing,
  overdue calculation.

**Never weaken or delete a test to get to green.**
