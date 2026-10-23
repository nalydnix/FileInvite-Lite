using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FileInviteLite.Api.Tests.Features.Schema;

/// <summary>
/// Task 010: the shape of the two Phase 1 entities. Fields come from docs/architecture.md §5;
/// the decisions (unique token hash, unique storage key, non-unique Sha256, no cascade, UTC
/// DateTime) from the task file and ADRs 0001, 0004 and 0005.
/// </summary>
public sealed class SchemaTests : SchemaTestBase
{
    private static IEntityType EntityOf<T>(DbContext db) =>
        db.Model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped.");

    // ---- fields ---------------------------------------------------------------------------

    [Fact]
    public async Task MagicLink_HasExactlyTheArchitectureFields_AndNoColumnForARawToken_S2_2()
    {
        await using var db = NewContext();

        var columns = EntityOf<MagicLink>(db).GetProperties().Select(p => p.Name).Order().ToArray();

        // Exact set: a Token column, or any other, would fail here.
        Assert.Equal(["ExpiresAt", "Id", "RequestId", "RevokedAt", "TokenHash"], columns);
    }

    [Fact]
    public async Task UploadedFile_HasExactlyTheArchitectureFields_S3_9()
    {
        await using var db = NewContext();

        var columns = EntityOf<UploadedFile>(db).GetProperties().Select(p => p.Name).Order().ToArray();

        Assert.Equal(
            ["ChecklistItemId", "ContentType", "Id", "OriginalName", "Sha256", "SizeBytes", "StorageKey", "UploadedAt"],
            columns);
    }

    [Fact]
    public async Task NewEntities_NeverUseDateTimeOffset_ADR0001()
    {
        await using var db = NewContext();

        var offenders = new[] { EntityOf<MagicLink>(db), EntityOf<UploadedFile>(db) }
            .SelectMany(e => e.GetProperties())
            .Where(p => (Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType) == typeof(DateTimeOffset))
            .Select(p => $"{p.DeclaringType.DisplayName()}.{p.Name}");

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task Columns_HaveTheAgreedLengthsAndNullability()
    {
        await using var db = NewContext();
        var link = EntityOf<MagicLink>(db);
        var file = EntityOf<UploadedFile>(db);

        Assert.Equal(64, link.FindProperty(nameof(MagicLink.TokenHash))!.GetMaxLength());
        Assert.False(link.FindProperty(nameof(MagicLink.TokenHash))!.IsNullable);
        Assert.False(link.FindProperty(nameof(MagicLink.ExpiresAt))!.IsNullable);
        Assert.True(link.FindProperty(nameof(MagicLink.RevokedAt))!.IsNullable);

        Assert.Equal(260, file.FindProperty(nameof(UploadedFile.OriginalName))!.GetMaxLength());
        Assert.Equal(128, file.FindProperty(nameof(UploadedFile.ContentType))!.GetMaxLength());
        Assert.Equal(128, file.FindProperty(nameof(UploadedFile.StorageKey))!.GetMaxLength());
        Assert.Equal(64, file.FindProperty(nameof(UploadedFile.Sha256))!.GetMaxLength());
        Assert.All(file.GetProperties(), p => Assert.False(p.IsNullable, p.Name));
    }

    // ---- indexes --------------------------------------------------------------------------

    [Fact]
    public async Task MagicLink_DuplicateTokenHash_Throws_S2_2()
    {
        var (requestId, _) = await SeedRequestWithItemAsync();
        var hash = new string('a', 64);

        await using var db = NewContext();
        db.MagicLinks.Add(NewLink(requestId, hash));
        await db.SaveChangesAsync();

        await using var second = NewContext();
        second.MagicLinks.Add(NewLink(requestId, hash));
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task UploadedFile_DuplicateStorageKey_Throws_S3_8()
    {
        var (_, itemId) = await SeedRequestWithItemAsync();
        const string key = "2026/10/0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f";

        await using var db = NewContext();
        db.UploadedFiles.Add(NewFile(itemId, key));
        await db.SaveChangesAsync();

        await using var second = NewContext();
        second.UploadedFiles.Add(NewFile(itemId, key));
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task UploadedFile_TwoRowsWithTheSameSha256_BothInsert_ADR0005()
    {
        var (_, itemId) = await SeedRequestWithItemAsync();
        var (_, otherItemId) = await SeedRequestWithItemAsync();
        var sha = new string('b', 64);

        await using (var db = NewContext())
        {
            db.UploadedFiles.Add(NewFile(itemId, sha256: sha));
            db.UploadedFiles.Add(NewFile(otherItemId, sha256: sha));
            await db.SaveChangesAsync();
        }

        await using var read = NewContext();
        Assert.Equal(2, await read.UploadedFiles.CountAsync(f => f.Sha256 == sha));
    }

    [Fact]
    public async Task Indexes_Exist_AsDeclared()
    {
        await using var db = NewContext();
        var link = EntityOf<MagicLink>(db);
        var file = EntityOf<UploadedFile>(db);

        IIndex IndexOn(IEntityType type, string property) =>
            type.GetIndexes().Single(i => i.Properties.Select(p => p.Name).SequenceEqual([property]));

        Assert.True(IndexOn(link, nameof(MagicLink.TokenHash)).IsUnique);
        Assert.True(IndexOn(file, nameof(UploadedFile.StorageKey)).IsUnique);
        Assert.False(IndexOn(file, nameof(UploadedFile.Sha256)).IsUnique);
        Assert.False(IndexOn(file, nameof(UploadedFile.ChecklistItemId)).IsUnique);
    }

    // ---- relationships --------------------------------------------------------------------

    [Fact]
    public async Task NothingCascades_BothRelationshipsRestrictDeletes()
    {
        await using var db = NewContext();

        var foreignKeys = new[] { EntityOf<MagicLink>(db), EntityOf<UploadedFile>(db) }
            .SelectMany(e => e.GetForeignKeys())
            .ToList();

        Assert.Equal(2, foreignKeys.Count);
        Assert.All(foreignKeys, fk =>
            Assert.True(
                fk.DeleteBehavior is DeleteBehavior.Restrict or DeleteBehavior.NoAction,
                $"{fk.DeclaringEntityType.DisplayName()} -> {fk.PrincipalEntityType.DisplayName()} is {fk.DeleteBehavior}."));
    }

    [Fact]
    public async Task Relationships_AreNavigableFromTheParents()
    {
        var (requestId, itemId) = await SeedRequestWithItemAsync();

        await using (var db = NewContext())
        {
            db.MagicLinks.Add(NewLink(requestId));
            db.UploadedFiles.Add(NewFile(itemId));
            await db.SaveChangesAsync();
        }

        await using var read = NewContext();
        var request = await read.DocumentRequests.Include(r => r.MagicLinks).Include(r => r.Items).ThenInclude(i => i.Files)
            .SingleAsync(r => r.Id == requestId);

        Assert.Single(request.MagicLinks);
        Assert.Single(request.Items.Single().Files);
    }

    [Fact]
    public async Task DeletingARequestOrItemThatOwnsChildren_IsRefusedByTheDatabase()
    {
        var (requestId, itemId) = await SeedRequestWithItemAsync();
        await using (var db = NewContext())
        {
            db.MagicLinks.Add(NewLink(requestId));
            db.UploadedFiles.Add(NewFile(itemId));
            await db.SaveChangesAsync();
        }

        await using var attempt = NewContext();
        await Assert.ThrowsAnyAsync<Exception>(() => attempt.ChecklistItems.Where(i => i.Id == itemId).ExecuteDeleteAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => attempt.DocumentRequests.Where(r => r.Id == requestId).ExecuteDeleteAsync());
    }

    // ---- translation ----------------------------------------------------------------------

    [Fact]
    public async Task MagicLink_ExpiresAtComparison_TranslatesToSqlAndRuns_ADR0001()
    {
        var (requestId, _) = await SeedRequestWithItemAsync();
        var now = DateTime.UtcNow;
        var live = NewLink(requestId, expiresAt: now.AddDays(1));
        var expired = NewLink(requestId, expiresAt: now.AddDays(-1));

        await using (var db = NewContext())
        {
            db.MagicLinks.AddRange(live, expired);
            await db.SaveChangesAsync();
        }

        await using var read = NewContext();
        var query = read.MagicLinks.AsNoTracking()
            .Where(l => l.RequestId == requestId && l.ExpiresAt > now && l.RevokedAt == null);

        var found = await query.Select(l => l.Id).ToListAsync();
        var sql = query.ToQueryString();

        Assert.Equal([live.Id], found);
        Assert.Matches(@"""ExpiresAt""\s*>", sql);
        Assert.Matches(@"""RevokedAt""\s+IS NULL", sql);
    }

    // ---- audit actions --------------------------------------------------------------------

    [Fact]
    public void AuditAction_HasTheFivePhase1Constants_AndTheNamesAreUnique()
    {
        Assert.Equal("RequestSent", AuditAction.RequestSent);
        Assert.Equal("MagicLinkIssued", AuditAction.MagicLinkIssued);
        Assert.Equal("MagicLinkRevoked", AuditAction.MagicLinkRevoked);
        Assert.Equal("PortalAccessed", AuditAction.PortalAccessed);
        Assert.Equal("ItemUploaded", AuditAction.ItemUploaded);

        var all = typeof(AuditAction).GetFields().Select(f => (string)f.GetRawConstantValue()!).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.All(all, name => Assert.True(name.Length <= 64, name)); // AuditEvent.Action max length
    }
}
