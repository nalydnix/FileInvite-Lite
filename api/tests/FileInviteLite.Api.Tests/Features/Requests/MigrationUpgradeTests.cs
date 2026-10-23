using FileInviteLite.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace FileInviteLite.Api.Tests.Features.Requests;

/// <summary>
/// The request-tables migration must apply to a database that already holds data from the
/// previous one, not only to an empty file — that is the situation every deployed database is in.
/// </summary>
public sealed class MigrationUpgradeTests : IDisposable
{
    private const string PreviousMigration = "20261023045556_InitialStaffAndAudit";

    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "fileinvite-tests", $"upgrade-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Migration_AppliesOnTopOfInitialStaffAndAudit_AndKeepsExistingRows()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var staffId = Guid.NewGuid();

        await using (var db = NewContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);

            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "StaffUsers" ("Id","Email","Name","PasswordHash") VALUES ({staffId},'old@test.local','Old','x')""");
            await db.Database.ExecuteSqlAsync(
                $"""INSERT INTO "AuditEvents" ("Id","OccurredAt","ActorType","Action","EntityType") VALUES ({Guid.NewGuid()},'2026-01-01 00:00:00','Staff','StaffLoggedIn','StaffUser')""");
        }

        await using (var db = NewContext())
        {
            Assert.Contains(
                "AddDocumentRequestsAndChecklistItems",
                string.Join(' ', await db.Database.GetPendingMigrationsAsync()));

            await db.Database.MigrateAsync();

            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            Assert.Equal(1, await db.StaffUsers.CountAsync());
            Assert.Equal(1, await db.AuditEvents.CountAsync());
            Assert.Equal(0, await db.DocumentRequests.CountAsync());
            Assert.Equal(0, await db.ChecklistItems.CountAsync());
        }
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
