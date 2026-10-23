using FileInviteLite.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace FileInviteLite.Api.Tests.Features.Schema;

/// <summary>
/// Phase 1 gets exactly one migration, it is additive, and it applies on top of a database that
/// already holds rows from the migrations before it — the situation every deployed database is in.
/// Extends the pattern in Features/Requests/MigrationUpgradeTests.cs.
/// </summary>
public sealed class Phase1MigrationTests : IDisposable
{
    private const string PreviousMigration = "20261023053114_AddDocumentRequestsAndChecklistItems";

    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "fileinvite-tests", $"phase1-upgrade-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Migration_AppliesOnTopOfAnExistingDatabase_AndKeepsEveryExistingRow()
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
                $"""INSERT INTO "ChecklistItems" ("Id","RequestId","Title","Required","Status") VALUES ({itemId},{requestId},'Passport',1,'Outstanding')""");
        }

        await using (var db = NewContext())
        {
            Assert.Equal(
                ["AddMagicLinksAndUploadedFiles"],
                (await db.Database.GetPendingMigrationsAsync()).Select(m => m[(m.IndexOf('_') + 1)..]));

            await db.Database.MigrateAsync();

            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(1, await db.StaffUsers.CountAsync());
            Assert.Equal(1, await db.AuditEvents.CountAsync());
            Assert.Equal(1, await db.DocumentRequests.CountAsync());
            Assert.Equal(1, await db.ChecklistItems.CountAsync());

            // The new tables exist, are empty, and accept rows that point at the old ones.
            Assert.Equal(0, await db.MagicLinks.CountAsync());
            Assert.Equal(0, await db.UploadedFiles.CountAsync());

            db.MagicLinks.Add(new Api.Domain.MagicLink
            {
                RequestId = requestId,
                TokenHash = new string('c', 64),
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            db.UploadedFiles.Add(new Api.Domain.UploadedFile
            {
                ChecklistItemId = itemId,
                OriginalName = "a.pdf",
                ContentType = "application/pdf",
                SizeBytes = 1,
                Sha256 = new string('d', 64),
                StorageKey = "2026/10/abc",
                UploadedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Phase1AddsExactlyOneMigration_AndItOnlyCreatesTablesAndIndexes()
    {
        await using var db = NewContext();
        var assembly = db.GetService<IMigrationsAssembly>();

        var names = assembly.Migrations.Keys.Order().ToList();
        Assert.Equal(
            [
                "20261023045556_InitialStaffAndAudit",
                PreviousMigration,
                names[2], // the Phase 1 migration, asserted by name just below
            ],
            names);
        Assert.EndsWith("_AddMagicLinksAndUploadedFiles", names[2]);

        var migration = assembly.CreateMigration(assembly.Migrations[names[2]], db.Database.ProviderName!);

        // Additive only: nothing dropped, altered or renamed on the existing tables.
        Assert.All(migration.UpOperations, op => Assert.True(
            op is CreateTableOperation or CreateIndexOperation,
            $"Unexpected operation {op.GetType().Name}."));
        Assert.Equal(
            ["MagicLinks", "UploadedFiles"],
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
