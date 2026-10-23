using FileInviteLite.Api.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FileInviteLite.Api.Tests.Features.Schema;

/// <summary>
/// Task 020: the shape of the three Phase 2 entities. Fields come from docs/architecture.md §5;
/// the decisions (string enum, nullable comment, no unique hash, no cascade, UTC DateTime) from
/// the task file and ADRs 0001 and 0006.
/// </summary>
public sealed class Phase2SchemaTests : SchemaTestBase
{
    private static IEntityType EntityOf<T>(DbContext db) =>
        db.Model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped.");

    // ---- fields ---------------------------------------------------------------------------

    [Fact]
    public async Task ReviewDecision_HasExactlyTheArchitectureFields_S4_8()
    {
        await using var db = NewContext();
        Assert.Equal(
            ["ChecklistItemId", "Comment", "Decision", "Id", "ReviewedAt", "ReviewedBy"],
            EntityOf<ReviewDecision>(db).GetProperties().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task Reminder_HasExactlyTheArchitectureFields_S5_3()
    {
        await using var db = NewContext();
        Assert.Equal(
            ["Id", "Reason", "RequestId", "SentAt"],
            EntityOf<Reminder>(db).GetProperties().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task WebhookDelivery_HasExactlyTheArchitectureFields_S7_1()
    {
        await using var db = NewContext();
        Assert.Equal(
            ["Attempts", "DeliveredAt", "Id", "LastStatus", "PayloadHash", "RequestId", "Url"],
            EntityOf<WebhookDelivery>(db).GetProperties().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public async Task NewEntities_NeverUseDateTimeOffset_ADR0001()
    {
        await using var db = NewContext();
        var offenders = new[] { EntityOf<ReviewDecision>(db), EntityOf<Reminder>(db), EntityOf<WebhookDelivery>(db) }
            .SelectMany(e => e.GetProperties())
            .Where(p => (Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType) == typeof(DateTimeOffset))
            .Select(p => $"{p.DeclaringType.DisplayName()}.{p.Name}");
        Assert.Empty(offenders);
    }

    [Fact]
    public async Task Columns_HaveTheAgreedLengthsAndNullability()
    {
        await using var db = NewContext();
        var decision = EntityOf<ReviewDecision>(db);
        var reminder = EntityOf<Reminder>(db);
        var delivery = EntityOf<WebhookDelivery>(db);

        Assert.Equal(2000, decision.FindProperty(nameof(ReviewDecision.Comment))!.GetMaxLength());
        Assert.True(decision.FindProperty(nameof(ReviewDecision.Comment))!.IsNullable);
        Assert.False(decision.FindProperty(nameof(ReviewDecision.Decision))!.IsNullable);
        Assert.False(decision.FindProperty(nameof(ReviewDecision.ReviewedBy))!.IsNullable);
        Assert.False(decision.FindProperty(nameof(ReviewDecision.ReviewedAt))!.IsNullable);

        Assert.Equal(200, reminder.FindProperty(nameof(Reminder.Reason))!.GetMaxLength());
        Assert.All(reminder.GetProperties(), p => Assert.False(p.IsNullable, p.Name));

        Assert.Equal(2048, delivery.FindProperty(nameof(WebhookDelivery.Url))!.GetMaxLength());
        Assert.Equal(64, delivery.FindProperty(nameof(WebhookDelivery.PayloadHash))!.GetMaxLength());
        Assert.False(delivery.FindProperty(nameof(WebhookDelivery.Url))!.IsNullable);
        Assert.False(delivery.FindProperty(nameof(WebhookDelivery.PayloadHash))!.IsNullable);
        Assert.False(delivery.FindProperty(nameof(WebhookDelivery.Attempts))!.IsNullable);
        Assert.True(delivery.FindProperty(nameof(WebhookDelivery.LastStatus))!.IsNullable);
        Assert.True(delivery.FindProperty(nameof(WebhookDelivery.DeliveredAt))!.IsNullable);
    }

    // ---- defaults and round trips ---------------------------------------------------------

    [Fact]
    public async Task WebhookDelivery_DefaultsToNoAttemptsAndNothingDelivered_S7_6()
    {
        var (requestId, _) = await SeedRequestWithItemAsync();
        var delivery = NewDelivery(requestId);

        await using (var db = NewContext())
        {
            db.WebhookDeliveries.Add(delivery);
            await db.SaveChangesAsync();
        }

        await using var read = NewContext();
        var stored = await read.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.Id == delivery.Id);
        Assert.Equal(0, stored.Attempts);
        Assert.Null(stored.LastStatus);
        Assert.Null(stored.DeliveredAt);
    }

    [Fact]
    public async Task Timestamps_ComeBackAsUtc_ADR0001()
    {
        var (requestId, itemId) = await SeedRequestWithItemAsync();
        var when = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        await using (var db = NewContext())
        {
            db.ReviewDecisions.Add(NewDecision(itemId, reviewedAt: when));
            db.Reminders.Add(NewReminder(requestId, when));
            db.WebhookDeliveries.Add(NewDelivery(requestId, deliveredAt: when));
            await db.SaveChangesAsync();
        }

        await using var read = NewContext();
        var d = await read.ReviewDecisions.AsNoTracking().SingleAsync();
        var r = await read.Reminders.AsNoTracking().SingleAsync();
        var w = await read.WebhookDeliveries.AsNoTracking().SingleAsync();
        Assert.Equal((when, DateTimeKind.Utc), (d.ReviewedAt, d.ReviewedAt.Kind));
        Assert.Equal((when, DateTimeKind.Utc), (r.SentAt, r.SentAt.Kind));
        Assert.Equal((when, DateTimeKind.Utc), (w.DeliveredAt!.Value, w.DeliveredAt.Value.Kind));
    }

    [Fact]
    public async Task ReviewDecision_CommentIsOptionalAtTheColumn_S4_5()
    {
        var (_, itemId) = await SeedRequestWithItemAsync();
        await using var db = NewContext();
        db.ReviewDecisions.Add(NewDecision(itemId, ReviewOutcome.Reject, comment: null));
        await db.SaveChangesAsync();
        Assert.Null((await db.ReviewDecisions.AsNoTracking().SingleAsync()).Comment);
    }

    // ---- enums as strings -----------------------------------------------------------------

    [Theory]
    [InlineData(ReviewOutcome.Approve, "Approve")]
    [InlineData(ReviewOutcome.Reject, "Reject")]
    public async Task ReviewDecision_Decision_IsStoredAsAString_S4_8(ReviewOutcome outcome, string expected)
    {
        var (_, itemId) = await SeedRequestWithItemAsync();
        var decision = NewDecision(itemId, outcome);
        await using (var db = NewContext())
        {
            db.ReviewDecisions.Add(decision);
            await db.SaveChangesAsync();
        }

        await using var raw = NewContext();
        var connection = raw.Database.GetDbConnection();
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """SELECT "Decision", typeof("Decision") FROM "ReviewDecisions" WHERE "Id" = $id""";
        cmd.Parameters.Add(new SqliteParameter("$id", decision.Id.ToString().ToUpperInvariant()));
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expected, reader.GetString(0));
        Assert.Equal("text", reader.GetString(1));
    }

    // ---- indexes --------------------------------------------------------------------------

    [Fact]
    public async Task WebhookDelivery_PayloadHash_AcceptsDuplicates_S7_1()
    {
        var (requestId, _) = await SeedRequestWithItemAsync();
        var (otherRequestId, _) = await SeedRequestWithItemAsync();
        var hash = new string('f', 64);

        await using var db = NewContext();
        db.WebhookDeliveries.Add(NewDelivery(requestId, hash));
        db.WebhookDeliveries.Add(NewDelivery(otherRequestId, hash));
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.WebhookDeliveries.CountAsync(d => d.PayloadHash == hash));
        Assert.DoesNotContain(
            EntityOf<WebhookDelivery>(db).GetIndexes(),
            i => i.IsUnique || i.Properties.Any(p => p.Name == nameof(WebhookDelivery.PayloadHash)));
    }

    [Fact]
    public async Task Reminder_IsIndexedOnRequestIdThenSentAt_ForTheCooldownQuery_S5_2()
    {
        await using var db = NewContext();
        Assert.Contains(
            EntityOf<Reminder>(db).GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(Reminder.RequestId), nameof(Reminder.SentAt)]));
    }

    [Fact]
    public async Task WebhookDelivery_IsIndexedOnDeliveredAt_ForThePendingQuery_S7_6()
    {
        await using var db = NewContext();
        Assert.Contains(
            EntityOf<WebhookDelivery>(db).GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(WebhookDelivery.DeliveredAt)]));
    }

    // ---- relationships --------------------------------------------------------------------

    [Fact]
    public async Task NothingCascades_AllThreeNewRelationshipsRestrictDeletes()
    {
        await using var db = NewContext();
        var fks = new[] { EntityOf<ReviewDecision>(db), EntityOf<Reminder>(db), EntityOf<WebhookDelivery>(db) }
            .SelectMany(e => e.GetForeignKeys())
            .ToList();

        Assert.Equal(3, fks.Count);
        Assert.All(
            fks,
            fk => Assert.True(
                fk.DeleteBehavior is DeleteBehavior.Restrict or DeleteBehavior.NoAction,
                $"{fk.DeclaringEntityType.DisplayName()} -> {fk.PrincipalEntityType.DisplayName()} is {fk.DeleteBehavior}."));
    }

    [Fact]
    public async Task DeletingAParent_WithNewChildren_IsRefusedByTheDatabase()
    {
        var (requestId, itemId) = await SeedRequestWithItemAsync();
        await using (var db = NewContext())
        {
            db.ReviewDecisions.Add(NewDecision(itemId));
            db.Reminders.Add(NewReminder(requestId));
            db.WebhookDeliveries.Add(NewDelivery(requestId));
            await db.SaveChangesAsync();
        }

        await using var del = NewContext();
        await Assert.ThrowsAnyAsync<Exception>(
            () => del.Database.ExecuteSqlAsync($"""DELETE FROM "ChecklistItems" WHERE "Id" = {itemId}"""));
        await Assert.ThrowsAnyAsync<Exception>(
            () => del.Database.ExecuteSqlAsync($"""DELETE FROM "DocumentRequests" WHERE "Id" = {requestId}"""));
    }

    [Fact]
    public async Task ChildrenWithoutAParent_AreRejected()
    {
        await using var db = NewContext();
        db.Reminders.Add(NewReminder(Guid.NewGuid()));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Navigations_ExistOnChecklistItemAndDocumentRequest()
    {
        var (requestId, itemId) = await SeedRequestWithItemAsync();
        await using (var db = NewContext())
        {
            db.ReviewDecisions.Add(NewDecision(itemId));
            db.Reminders.Add(NewReminder(requestId));
            db.WebhookDeliveries.Add(NewDelivery(requestId));
            await db.SaveChangesAsync();
        }

        await using var read = NewContext();
        var request = await read.DocumentRequests
            .Include(r => r.Reminders)
            .Include(r => r.WebhookDeliveries)
            .Include(r => r.Items).ThenInclude(i => i.Decisions)
            .SingleAsync(r => r.Id == requestId);
        Assert.Single(request.Reminders);
        Assert.Single(request.WebhookDeliveries);
        Assert.Single(request.Items.Single().Decisions);
    }

    [Fact]
    public async Task AuditEvent_StillHasNoNavigationOrForeignKey_S6_3()
    {
        await using var db = NewContext();
        var audit = EntityOf<AuditEvent>(db);
        Assert.Empty(audit.GetNavigations());
        Assert.Empty(audit.GetForeignKeys());
        Assert.Empty(audit.GetReferencingForeignKeys());
        Assert.DoesNotContain(
            db.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()),
            fk => fk.PrincipalEntityType == audit);
    }

    // ---- audit actions --------------------------------------------------------------------

    [Fact]
    public void AuditAction_HasThePhase2Constants_SpelledAsTheirNames()
    {
        Assert.Equal("FileDownloaded", AuditAction.FileDownloaded);
        Assert.Equal("ItemApproved", AuditAction.ItemApproved);
        Assert.Equal("ItemRejected", AuditAction.ItemRejected);
        Assert.Equal("RequestCompleted", AuditAction.RequestCompleted);
        Assert.Equal("ReminderSent", AuditAction.ReminderSent);
        Assert.Equal("WebhookQueued", AuditAction.WebhookQueued);
        Assert.Equal("WebhookDelivered", AuditAction.WebhookDelivered);
        Assert.Equal("WebhookFailed", AuditAction.WebhookFailed);
    }

    // ---- SQL translation ------------------------------------------------------------------

    [Fact]
    public async Task TimestampComparisons_TranslateToSql_AndFilterCorrectly()
    {
        var (requestId, itemId) = await SeedRequestWithItemAsync();
        var cutoff = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var newReminder = NewReminder(requestId, cutoff.AddDays(3));
        var newDecision = NewDecision(itemId, reviewedAt: cutoff.AddDays(3));
        var pending = NewDelivery(requestId);
        var done = NewDelivery(requestId, deliveredAt: cutoff);

        await using var db = NewContext();
        db.Reminders.AddRange(NewReminder(requestId, cutoff.AddDays(-3)), newReminder);
        db.ReviewDecisions.AddRange(NewDecision(itemId, reviewedAt: cutoff.AddDays(-3)), newDecision);
        db.WebhookDeliveries.AddRange(pending, done);
        await db.SaveChangesAsync();

        var reminders = db.Reminders.AsNoTracking().Where(r => r.SentAt > cutoff);
        var decisions = db.ReviewDecisions.AsNoTracking().Where(d => d.ReviewedAt > cutoff);
        var undelivered = db.WebhookDeliveries.AsNoTracking().Where(d => d.DeliveredAt == null);
        var deliveredBefore = db.WebhookDeliveries.AsNoTracking().Where(d => d.DeliveredAt < cutoff.AddDays(1));

        Assert.Matches(@"""SentAt""\s*>", reminders.ToQueryString());
        Assert.Matches(@"""ReviewedAt""\s*>", decisions.ToQueryString());
        Assert.Matches(@"""DeliveredAt""\s*IS NULL", undelivered.ToQueryString());
        Assert.Matches(@"""DeliveredAt""\s*<", deliveredBefore.ToQueryString());

        Assert.Equal([newReminder.Id], await reminders.Select(r => r.Id).ToListAsync());
        Assert.Equal([newDecision.Id], await decisions.Select(d => d.Id).ToListAsync());
        Assert.Equal([pending.Id], await undelivered.Select(d => d.Id).ToListAsync());
        Assert.Equal([done.Id], await deliveredBefore.Select(d => d.Id).ToListAsync());
    }

    [Fact]
    public async Task LatestReminderPerRequest_IsComputedInSql_S5_2()
    {
        await using var db = NewContext();
        var sql = db.Reminders.AsNoTracking()
            .GroupBy(r => r.RequestId)
            .Select(g => new { g.Key, Last = g.Max(r => r.SentAt) })
            .ToQueryString();
        Assert.Contains("MAX(", sql, StringComparison.OrdinalIgnoreCase);
    }
}
