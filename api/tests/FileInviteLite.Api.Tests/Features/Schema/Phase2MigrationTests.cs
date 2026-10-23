using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace FileInviteLite.Api.Tests.Features.Schema;

/// <summary>
/// Phase 2 gets exactly one migration, it is additive, and it applies on top of a database that
/// already holds Phase 1 rows. Extends the pattern in Phase1MigrationTests.
/// </summary>
public sealed class Phase2MigrationTests : IDisposable
{
    private const string PreviousMigration = "20261023062030_AddMagicLinksAndUploadedFiles";

    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "fileinvite-tests", $"phase2-upgrade-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Migration_AppliesOnTopOfAPhase1Database_AndKeepsEveryExistingRow()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var staffId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var itemId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "StaffUsers" ("Id","Email","Name","PasswordHash") VALUES ({staffId},'old@test.local','Old','x')""");
            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "AuditEvents" ("Id","OccurredAt","ActorType","Action","EntityType") VALUES ({Guid.NewGuid()},'2026-01-01 00:00:00','Staff','StaffLoggedIn','StaffUser')""");
            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "DocumentRequests" ("Id","ClientName","ClientEmail","DueDate","Status","CreatedBy","CreatedAt") VALUES ({requestId},'Old Client','old@example.com','2026-12-01','Sent',{staffId},'2026-01-01 00:00:00')""");
            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "ChecklistItems" ("Id","RequestId","Title","Required","Status") VALUES ({itemId},{requestId},'Passport',1,'Uploaded')""");
            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "MagicLinks" ("Id","RequestId","TokenHash","ExpiresAt") VALUES ({Guid.NewGuid()},{requestId},{new string('a', 64)},'2027-01-01 00:00:00')""");
            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "UploadedFiles" ("Id","ChecklistItemId","OriginalName","ContentType","SizeBytes","Sha256","StorageKey","UploadedAt") VALUES ({Guid.NewGuid()},{itemId},'a.pdf','application/pdf',1,{new string('b', 64)},'2026/10/abc','2026-01-01 00:00:00')""");
        }

        await using (var db = NewContext())
        {
            Assert.Equal(
                ["AddReviewRemindersAndWebhooks"],
                (await db.Database.GetPendingMigrationsAsync()).Select(m => m[(m.IndexOf('_') + 1)..]));

            await db.Database.MigrateAsync();

            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(1, await db.StaffUsers.CountAsync());
            Assert.Equal(1, await db.AuditEvents.CountAsync());
            Assert.Equal(1, await db.DocumentRequests.CountAsync());
            Assert.Equal(1, await db.ChecklistItems.CountAsync());
            Assert.Equal(1, await db.MagicLinks.CountAsync());
            Assert.Equal(1, await db.UploadedFiles.CountAsync());

            // The new tables exist, are empty, and accept rows that point at the old ones.
            Assert.Equal(0, await db.ReviewDecisions.CountAsync());
            Assert.Equal(0, await db.Reminders.CountAsync());
            Assert.Equal(0, await db.WebhookDeliveries.CountAsync());

            db.ReviewDecisions.Add(new ReviewDecision
            {
                ChecklistItemId = itemId,
                Decision = ReviewOutcome.Approve,
                ReviewedBy = staffId,
                ReviewedAt = DateTime.UtcNow,
            });
            db.Reminders.Add(new Reminder { RequestId = requestId, SentAt = DateTime.UtcNow, Reason = "Overdue" });
            db.WebhookDeliveries.Add(new WebhookDelivery
            {
                RequestId = requestId,
                Url = "https://hooks.example.com/x",
                PayloadHash = new string('c', 64),
            });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Phase2AddsExactlyOneMigration_AndItOnlyCreatesTablesAndIndexes()
    {
        await using var db = NewContext();
        var assembly = db.GetService<IMigrationsAssembly>();

        var names = assembly.Migrations.Keys.Order().ToList();
        Assert.Equal(4, names.Count);
        Assert.Equal(
            [
                "20261023045556_InitialStaffAndAudit",
                "20261023053114_AddDocumentRequestsAndChecklistItems",
                PreviousMigration,
            ],
            names.Take(3));
        Assert.EndsWith("_AddReviewRemindersAndWebhooks", names[3]);

        var migration = assembly.CreateMigration(assembly.Migrations[names[3]], db.Database.ProviderName!);

        Assert.All(migration.UpOperations, op => Assert.True(
            op is CreateTableOperation or CreateIndexOperation,
            $"Unexpected operation {op.GetType().Name}."));
        Assert.Equal(
            ["Reminders", "ReviewDecisions", "WebhookDeliveries"],
            migration.UpOperations.OfType<CreateTableOperation>().Select(o => o.Name).Order());
    }

    private AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={_path}").Options);

    public void Dispose()
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
    }
}
