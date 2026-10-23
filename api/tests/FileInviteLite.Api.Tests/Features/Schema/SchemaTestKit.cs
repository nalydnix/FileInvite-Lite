using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FileInviteLite.Api.Tests.Features.Schema;

/// <summary>
/// A real SQLite file per test class, migrated by the real migrations (ADR 0001) — not the EF
/// in-memory provider, which would not enforce the unique indexes or foreign keys under test.
/// </summary>
public abstract class SchemaTestBase : IAsyncLifetime
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "fileinvite-tests", $"schema-{Guid.NewGuid():N}.db");

    protected AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_path}").Options);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
            // A temp file left behind on Windows is not worth failing the run.
        }

        return Task.CompletedTask;
    }

    /// <summary>Inserts a staff user, a request and one checklist item, and returns their ids.</summary>
    protected async Task<(Guid RequestId, Guid ItemId)> SeedRequestWithItemAsync()
    {
        await using var db = NewContext();

        var staff = new StaffUser
        {
            Email = $"{Guid.NewGuid():N}@test.local",
            Name = "Schema Test",
            PasswordHash = "x",
        };
        db.StaffUsers.Add(staff);

        var request = new DocumentRequest
        {
            ClientName = "Schema Client",
            ClientEmail = "client@example.com",
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7),
            CreatedBy = staff.Id,
            CreatedAt = DateTime.UtcNow,
        };
        var item = new ChecklistItem { RequestId = request.Id, Title = "Passport", Required = true };
        request.Items.Add(item);
        db.DocumentRequests.Add(request);

        await db.SaveChangesAsync();
        return (request.Id, item.Id);
    }

    protected static MagicLink NewLink(Guid requestId, string? tokenHash = null, DateTime? expiresAt = null) =>
        new()
        {
            RequestId = requestId,
            TokenHash = tokenHash ?? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(14),
        };

    protected static UploadedFile NewFile(Guid itemId, string? storageKey = null, string? sha256 = null) =>
        new()
        {
            ChecklistItemId = itemId,
            OriginalName = "passport.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1234,
            Sha256 = sha256 ?? Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            StorageKey = storageKey ?? $"2026/10/{Guid.NewGuid():N}",
            UploadedAt = DateTime.UtcNow,
        };

    protected static ReviewDecision NewDecision(
        Guid itemId, ReviewOutcome outcome = ReviewOutcome.Approve, string? comment = null, DateTime? reviewedAt = null) =>
        new()
        {
            ChecklistItemId = itemId,
            Decision = outcome,
            Comment = comment,
            ReviewedBy = Guid.NewGuid(),
            ReviewedAt = reviewedAt ?? DateTime.UtcNow,
        };

    protected static Reminder NewReminder(Guid requestId, DateTime? sentAt = null, string reason = "Overdue") =>
        new() { RequestId = requestId, SentAt = sentAt ?? DateTime.UtcNow, Reason = reason };

    protected static WebhookDelivery NewDelivery(
        Guid requestId, string? payloadHash = null, DateTime? deliveredAt = null) =>
        new()
        {
            RequestId = requestId,
            Url = "https://hooks.example.com/complete",
            PayloadHash = payloadHash ?? new string('e', 64),
            DeliveredAt = deliveredAt,
        };
}
