using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Features.Portal;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FileInviteLite.Api.Tests.Features.Requests.RequestsTestKit;
using static FileInviteLite.Api.Tests.Features.Review.ReviewTestKit;

namespace FileInviteLite.Api.Tests.Features.Review;

/// <summary>Approve and reject: S4.4 to S4.12, S7.1 and the credential rules S1.7 and S3.14.</summary>
public sealed class ReviewDecisionTests
{
    private static readonly ItemSpec RequiredUploaded = new(ItemStatus.Uploaded);
    private static readonly ItemSpec OptionalUploaded = new(ItemStatus.Uploaded, Required: false);

    private static Task<ItemStatus> ItemStatusAsync(ReviewFactory f, Guid id) =>
        f.WithDbAsync(db => db.ChecklistItems.Where(i => i.Id == id).Select(i => i.Status).SingleAsync());

    private static Task<RequestStatus> RequestStatusAsync(ReviewFactory f, Guid id) =>
        f.WithDbAsync(db => db.DocumentRequests.Where(r => r.Id == id).Select(r => r.Status).SingleAsync());

    // ---- approve --------------------------------------------------------------------------

    [Fact]
    public async Task Approve_WithAComment_MovesTheItemToApprovedAndRecordsTheDecision_S4_4()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded, RequiredUploaded]);
        var client = await factory.StaffClientAsync();
        var staffId = await factory.Services.DemoStaffIdAsync();
        var itemId = seeded.Items[0].Id;

        var response = await client.ApproveAsync(itemId, new { comment = "  Looks good  " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ItemStatus.Approved, await ItemStatusAsync(factory, itemId));

        var decision = await factory.WithDbAsync(db => db.ReviewDecisions.SingleAsync());
        Assert.Equal(itemId, decision.ChecklistItemId);
        Assert.Equal(ReviewOutcome.Approve, decision.Decision);
        Assert.Equal("Looks good", decision.Comment);
        Assert.Equal(staffId, decision.ReviewedBy);

        var audit = await factory.WithDbAsync(db => db.AuditEvents.SingleAsync(e => e.Action == AuditAction.ItemApproved));
        Assert.Equal(ActorType.Staff, audit.ActorType);
        Assert.Equal(staffId, audit.ActorId);
        Assert.Equal(itemId, audit.EntityId);
    }

    [Fact]
    public async Task Approve_WithoutAComment_IsAccepted_S4_4()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded, RequiredUploaded]);
        var client = await factory.StaffClientAsync();

        var response = await client.ApproveAsync(seeded.Items[0].Id, new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await factory.WithDbAsync(db => db.ReviewDecisions.SingleAsync())).Comment);
    }

    [Fact]
    public async Task Approve_WithACommentOver2000Characters_Returns400NamingComment_S4_4()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded]);
        var client = await factory.StaffClientAsync();

        var response = await client.ApproveAsync(seeded.Items[0].Id, new { comment = new string('x', 2001) });

        await AssertValidationProblemAsync(response, "comment");
        Assert.Equal(ItemStatus.Uploaded, await ItemStatusAsync(factory, seeded.Items[0].Id));
    }

    [Fact]
    public async Task Approve_ReturnsItemIdItemStatusAndRequestStatus_PerTheContract()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded, RequiredUploaded]);
        var client = await factory.StaffClientAsync();

        var body = await (await client.ApproveAsync(seeded.Items[0].Id)).JsonBodyAsync();

        Assert.Equal(["itemId", "itemStatus", "requestStatus"], body.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(seeded.Items[0].Id, body.GetProperty("itemId").GetGuid());
        Assert.Equal("Approved", body.GetProperty("itemStatus").GetString());
        Assert.Equal("InProgress", body.GetProperty("requestStatus").GetString());
    }

    [Fact]
    public async Task Approve_AnAlreadyApprovedItem_Returns409_S4_11()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded, RequiredUploaded]);
        var client = await factory.StaffClientAsync();
        (await client.ApproveAsync(seeded.Items[0].Id)).EnsureSuccessStatusCode();

        var response = await client.ApproveAsync(seeded.Items[0].Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await factory.WithDbAsync(db => db.ReviewDecisions.CountAsync()));
        Assert.Equal(1, await factory.AuditCountAsync(AuditAction.ItemApproved));
    }

    [Fact]
    public async Task Approve_AnItemWithNoUploadedFile_Returns409_S4_7()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync(
            [new ItemSpec(ItemStatus.Outstanding, WithFile: false), RequiredUploaded]);
        var client = await factory.StaffClientAsync();

        var response = await client.ApproveAsync(seeded.Items[0].Id);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ItemStatus.Outstanding, await ItemStatusAsync(factory, seeded.Items[0].Id));
        Assert.Equal(0, await factory.WithDbAsync(db => db.ReviewDecisions.CountAsync()));
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.ItemApproved));
    }

    // ---- reject ---------------------------------------------------------------------------

    [Fact]
    public async Task Reject_WithAComment_MovesTheItemToRejectedAndRecordsTheDecision_S4_5()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded]);
        var client = await factory.StaffClientAsync();
        var staffId = await factory.Services.DemoStaffIdAsync();
        var itemId = seeded.Items[0].Id;

        var response = await client.RejectAsync(itemId, new { comment = "  Photo page is cut off " });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonBodyAsync();
        Assert.Equal(itemId, body.GetProperty("itemId").GetGuid());
        Assert.Equal("Rejected", body.GetProperty("itemStatus").GetString());
        Assert.Equal("InProgress", body.GetProperty("requestStatus").GetString());

        Assert.Equal(ItemStatus.Rejected, await ItemStatusAsync(factory, itemId));
        var decision = await factory.WithDbAsync(db => db.ReviewDecisions.SingleAsync());
        Assert.Equal(ReviewOutcome.Reject, decision.Decision);
        Assert.Equal("Photo page is cut off", decision.Comment);
        Assert.Equal(staffId, decision.ReviewedBy);

        var audit = await factory.WithDbAsync(db => db.AuditEvents.SingleAsync(e => e.Action == AuditAction.ItemRejected));
        Assert.Equal(itemId, audit.EntityId);
        Assert.Equal(staffId, audit.ActorId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" \t\r\n ")]
    public async Task Reject_WithoutARealComment_Returns400NamingComment_S4_5(string? comment)
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded]);
        var client = await factory.StaffClientAsync();

        var response = await client.RejectAsync(seeded.Items[0].Id, new { comment });

        await AssertValidationProblemAsync(response, "comment");
        Assert.Equal(ItemStatus.Uploaded, await ItemStatusAsync(factory, seeded.Items[0].Id));
        Assert.Equal(0, await factory.WithDbAsync(db => db.ReviewDecisions.CountAsync()));
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.ItemRejected));
    }

    [Fact]
    public async Task Reject_WithNoCommentProperty_Returns400NamingComment_S4_5()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded]);
        var client = await factory.StaffClientAsync();

        var response = await client.RejectAsync(seeded.Items[0].Id, new { });

        await AssertValidationProblemAsync(response, "comment");
    }

    [Fact]
    public async Task Reject_CommentLengthIsCountedAfterTrimming_S4_5()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded, RequiredUploaded]);
        var client = await factory.StaffClientAsync();

        var tooLong = await client.RejectAsync(seeded.Items[0].Id, new { comment = new string('x', 2001) });
        await AssertValidationProblemAsync(tooLong, "comment");

        // 2000 characters plus padding is 2000 after trimming: accepted.
        var padded = await client.RejectAsync(seeded.Items[0].Id, new { comment = "  " + new string('x', 2000) + "  " });
        Assert.Equal(HttpStatusCode.OK, padded.StatusCode);
        Assert.Equal(2000, (await factory.WithDbAsync(db => db.ReviewDecisions.SingleAsync())).Comment!.Length);
    }

    [Fact]
    public async Task Reject_AnItemWithNoUploadedFile_Returns409_S4_7()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync(
            [new ItemSpec(ItemStatus.Outstanding, WithFile: false), RequiredUploaded]);
        var client = await factory.StaffClientAsync();

        var response = await client.RejectAsync(seeded.Items[0].Id, new { comment = "No." });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(0, await factory.WithDbAsync(db => db.ReviewDecisions.CountAsync()));
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.ItemRejected));
    }

    [Fact]
    public async Task Reject_AnAlreadyRejectedItem_IsAllowedAndRecordsASecondDecision_S4_8()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([new ItemSpec(ItemStatus.Rejected)]);
        var client = await factory.StaffClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.RejectAsync(seeded.Items[0].Id, new { comment = "Still blurry" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.RejectAsync(seeded.Items[0].Id, new { comment = "Still blurry again" })).StatusCode);

        Assert.Equal(2, await factory.WithDbAsync(db => db.ReviewDecisions.CountAsync()));
        Assert.Equal(2, await factory.AuditCountAsync(AuditAction.ItemRejected));
    }

    // ---- roll-up and completion -------------------------------------------------------------

    [Fact]
    public async Task Approve_TheLastRequiredItem_CompletesTheRequestAndWritesRequestCompleted_S4_9()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync(
            [new ItemSpec(ItemStatus.Approved), RequiredUploaded]);
        var client = await factory.StaffClientAsync();

        var response = await client.ApproveAsync(seeded.Items[1].Id);

        Assert.Equal("Complete", (await response.JsonBodyAsync()).GetProperty("requestStatus").GetString());
        Assert.Equal(RequestStatus.Complete, await RequestStatusAsync(factory, seeded.RequestId));

        var completed = await factory.WithDbAsync(db => db.AuditEvents.SingleAsync(e => e.Action == AuditAction.RequestCompleted));
        Assert.Equal(seeded.RequestId, completed.EntityId);
        Assert.Equal(nameof(DocumentRequest), completed.EntityType);
    }

    [Fact]
    public async Task Approve_ARequiredItemWhileAnotherIsStillPending_DoesNotComplete_S4_9()
    {
        using var factory = new ReviewFactory(ReviewFactory.ConfiguredWebhookUrl);
        var seeded = await factory.SeedReviewAsync([RequiredUploaded, RequiredUploaded]);
        var client = await factory.StaffClientAsync();

        await client.ApproveAsync(seeded.Items[0].Id);

        Assert.Equal(RequestStatus.InProgress, await RequestStatusAsync(factory, seeded.RequestId));
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.RequestCompleted));
        Assert.Equal(0, await factory.WithDbAsync(db => db.WebhookDeliveries.CountAsync()));
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.WebhookQueued));
    }

    [Fact]
    public async Task Approve_TheLastRequiredItem_CompletesDespiteOutstandingOptionalItems_S4_10()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync(
        [
            RequiredUploaded,
            new ItemSpec(ItemStatus.Outstanding, Required: false, WithFile: false),
        ]);
        var client = await factory.StaffClientAsync();

        var response = await client.ApproveAsync(seeded.Items[0].Id);

        Assert.Equal("Complete", (await response.JsonBodyAsync()).GetProperty("requestStatus").GetString());
        Assert.Equal(RequestStatus.Complete, await RequestStatusAsync(factory, seeded.RequestId));
        Assert.Equal(ItemStatus.Outstanding, await ItemStatusAsync(factory, seeded.Items[1].Id));
    }

    [Fact]
    public async Task Approve_InAnAllOptionalRequest_CompletesOnTheFirstApprovalWithAnotherItemStillUploaded_S4_12()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([OptionalUploaded, OptionalUploaded]);
        var client = await factory.StaffClientAsync();

        var response = await client.ApproveAsync(seeded.Items[0].Id);

        Assert.Equal("Complete", (await response.JsonBodyAsync()).GetProperty("requestStatus").GetString());
        Assert.Equal(RequestStatus.Complete, await RequestStatusAsync(factory, seeded.RequestId));
        Assert.Equal(ItemStatus.Uploaded, await ItemStatusAsync(factory, seeded.Items[1].Id));
        Assert.Equal(1, await factory.AuditCountAsync(AuditAction.RequestCompleted));
    }

    [Fact]
    public async Task Approve_ASecondItemOfAnAlreadyCompleteRequest_DoesNotCompleteItAgain_S4_12()
    {
        using var factory = new ReviewFactory(ReviewFactory.ConfiguredWebhookUrl);
        var seeded = await factory.SeedReviewAsync([OptionalUploaded, OptionalUploaded]);
        var client = await factory.StaffClientAsync();

        await client.ApproveAsync(seeded.Items[0].Id);
        var second = await client.ApproveAsync(seeded.Items[1].Id);

        Assert.Equal("Complete", (await second.JsonBodyAsync()).GetProperty("requestStatus").GetString());
        Assert.Equal(1, await factory.AuditCountAsync(AuditAction.RequestCompleted));
        Assert.Equal(1, await factory.WithDbAsync(db => db.WebhookDeliveries.CountAsync()));
        Assert.Equal(1, await factory.AuditCountAsync(AuditAction.WebhookQueued));
    }

    [Fact]
    public async Task Reject_NeverCompletesARequest_S4_9()
    {
        using var factory = new ReviewFactory(ReviewFactory.ConfiguredWebhookUrl);
        var seeded = await factory.SeedReviewAsync([OptionalUploaded]);
        var client = await factory.StaffClientAsync();

        var body = await (await client.RejectAsync(seeded.Items[0].Id, new { comment = "No" })).JsonBodyAsync();

        Assert.Equal("InProgress", body.GetProperty("requestStatus").GetString());
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.RequestCompleted));
        Assert.Equal(0, await factory.WithDbAsync(db => db.WebhookDeliveries.CountAsync()));
    }

    // ---- webhook ----------------------------------------------------------------------------

    // S7.7 requires the webhook URL to be HTTPS. A security review found that only emptiness was
    // checked here, so http://, file:/// or junk would be persisted and later posted to, carrying
    // the client's name and email in cleartext. A value that is not an absolute HTTPS URI is now
    // treated as "not configured": completion still succeeds, but nothing is queued.
    [Theory]
    [InlineData("http://hooks.example.test/fileinvite")]
    [InlineData("http://localhost:9000/hook")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://hooks.example.test/x")]
    [InlineData("hooks.example.test/fileinvite")]
    [InlineData("/relative/hook")]
    [InlineData("not a url at all")]
    public async Task Completion_WithANonHttpsWebhookUrl_QueuesNothingAndStillCompletes_S7_7(string url)
    {
        using var factory = new ReviewFactory(url);
        var seeded = await factory.SeedReviewAsync([RequiredUploaded, OptionalUploaded]);
        var client = await factory.StaffClientAsync();

        (await client.ApproveAsync(seeded.Items[0].Id, new { comment = "ok" })).EnsureSuccessStatusCode();

        var request = await factory.WithDbAsync(db => db.DocumentRequests.SingleAsync());
        Assert.Equal(RequestStatus.Complete, request.Status);
        Assert.Empty(await factory.WithDbAsync(db => db.WebhookDeliveries.ToListAsync()));

        var actions = await factory.WithDbAsync(db => db.AuditEvents.Select(e => e.Action).ToListAsync());
        Assert.Contains(AuditAction.RequestCompleted, actions);
        Assert.DoesNotContain(AuditAction.WebhookQueued, actions);
    }

    [Fact]
    public async Task Completion_WithAPaddedHttpsUrl_StoresItTrimmed_S7_7()
    {
        using var factory = new ReviewFactory($"  {ReviewFactory.ConfiguredWebhookUrl}  ");
        var seeded = await factory.SeedReviewAsync([RequiredUploaded, OptionalUploaded]);
        var client = await factory.StaffClientAsync();

        (await client.ApproveAsync(seeded.Items[0].Id, new { comment = "ok" })).EnsureSuccessStatusCode();

        var delivery = await factory.WithDbAsync(db => db.WebhookDeliveries.SingleAsync());
        Assert.Equal(ReviewFactory.ConfiguredWebhookUrl, delivery.Url);
    }

    [Fact]
    public async Task Completion_WithAWebhookConfigured_QueuesADeliveryInTheSameTransaction_S7_1()
    {
        using var factory = new ReviewFactory(ReviewFactory.ConfiguredWebhookUrl);
        var seeded = await factory.SeedReviewAsync([RequiredUploaded, OptionalUploaded]);
        var client = await factory.StaffClientAsync();

        (await client.ApproveAsync(seeded.Items[0].Id, new { comment = "ok" })).EnsureSuccessStatusCode();

        var delivery = await factory.WithDbAsync(db => db.WebhookDeliveries.SingleAsync());
        Assert.Equal(seeded.RequestId, delivery.RequestId);
        Assert.Equal(ReviewFactory.ConfiguredWebhookUrl, delivery.Url);
        Assert.Equal(0, delivery.Attempts);
        Assert.Null(delivery.DeliveredAt);
        Assert.Null(delivery.LastStatus);

        // The hash is SHA-256 of the contract's payload, serialised camelCase, items by id.
        var decision = await factory.WithDbAsync(db => db.ReviewDecisions.SingleAsync());
        var completedAt = new DateTime(decision.ReviewedAt.Ticks - decision.ReviewedAt.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var items = seeded.Items.OrderBy(i => i.Id).Select(i => new
        {
            id = i.Id,
            title = $"Item {seeded.Items.ToList().IndexOf(i) + 1}",
            required = seeded.Items.ToList().IndexOf(i) == 0,
            outcome = i.Id == seeded.Items[0].Id ? "Approved" : "Uploaded",
        });
        var expectedJson = JsonSerializer.Serialize(
            new
            {
                @event = "request.completed",
                requestId = seeded.RequestId,
                clientName = "Jane Client",
                clientEmail = "jane@example.com",
                completedAt,
                items,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expectedJson)));
        Assert.Equal(64, delivery.PayloadHash.Length);
        Assert.Equal(expectedHash, delivery.PayloadHash);

        var queued = await factory.WithDbAsync(db => db.AuditEvents.SingleAsync(e => e.Action == AuditAction.WebhookQueued));
        Assert.Equal(delivery.Id, queued.EntityId);
        Assert.Equal(ActorType.Staff, queued.ActorType);
        Assert.DoesNotContain("hooks.example.test", queued.Metadata ?? string.Empty);
    }

    [Fact]
    public async Task Completion_WithNoWebhookConfigured_StillSucceedsAndQueuesNothing_S7_1()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded]);
        var client = await factory.StaffClientAsync();

        var response = await client.ApproveAsync(seeded.Items[0].Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RequestStatus.Complete, await RequestStatusAsync(factory, seeded.RequestId));
        Assert.Equal(1, await factory.AuditCountAsync(AuditAction.RequestCompleted));
        Assert.Equal(0, await factory.WithDbAsync(db => db.WebhookDeliveries.CountAsync()));
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.WebhookQueued));
    }

    // ---- atomicity --------------------------------------------------------------------------

    private static async Task AssertApproveRolledBackAsync(ReviewFactory factory, Seeded seeded, HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("forced", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", raw, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(ItemStatus.Uploaded, await ItemStatusAsync(factory, seeded.Items[0].Id));
        Assert.Equal(RequestStatus.InProgress, await RequestStatusAsync(factory, seeded.RequestId));
        Assert.Equal(0, await factory.WithDbAsync(db => db.ReviewDecisions.CountAsync()));
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.ItemApproved));
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.RequestCompleted));
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.WebhookQueued));
        Assert.Equal(0, await factory.WithDbAsync(db => db.WebhookDeliveries.CountAsync()));
    }

    [Theory]
    [InlineData("ReviewDecisions", null)]
    [InlineData("AuditEvents", "ItemApproved")]
    [InlineData("AuditEvents", "RequestCompleted")]
    [InlineData("AuditEvents", "WebhookQueued")]
    [InlineData("WebhookDeliveries", null)]
    [InlineData("ChecklistItems", null)]
    public async Task Approve_WhenAnyPartOfTheCompletingWriteFails_RollsBackEverything_S4_8(string table, string? action)
    {
        using var factory = new ReviewFactory(ReviewFactory.ConfiguredWebhookUrl);
        var seeded = await factory.SeedReviewAsync([RequiredUploaded]);
        var client = await factory.StaffClientAsync();
        var verb = table == "ChecklistItems" ? "UPDATE" : "INSERT";
        var when = action is null ? string.Empty : $"WHEN NEW.\"Action\" = '{action}'";
        await factory.ExecAsync($"""
            CREATE TRIGGER forced_failure BEFORE {verb} ON "{table}" {when}
            BEGIN SELECT RAISE(ABORT, 'forced'); END;
            """);

        var response = await client.ApproveAsync(seeded.Items[0].Id);

        await AssertApproveRolledBackAsync(factory, seeded, response);
    }

    [Theory]
    [InlineData("ReviewDecisions", null)]
    [InlineData("AuditEvents", "ItemRejected")]
    [InlineData("ChecklistItems", null)]
    public async Task Reject_WhenAnyPartOfTheWriteFails_RollsBackEverything_S4_8(string table, string? action)
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded]);
        var client = await factory.StaffClientAsync();
        var verb = table == "ChecklistItems" ? "UPDATE" : "INSERT";
        var when = action is null ? string.Empty : $"WHEN NEW.\"Action\" = '{action}'";
        await factory.ExecAsync($"""
            CREATE TRIGGER forced_failure BEFORE {verb} ON "{table}" {when}
            BEGIN SELECT RAISE(ABORT, 'forced'); END;
            """);

        var response = await client.RejectAsync(seeded.Items[0].Id, new { comment = "No" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ItemStatus.Uploaded, await ItemStatusAsync(factory, seeded.Items[0].Id));
        Assert.Equal(0, await factory.WithDbAsync(db => db.ReviewDecisions.CountAsync()));
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.ItemRejected));
    }

    // ---- credentials and scoping --------------------------------------------------------------

    [Fact]
    public async Task AllThreeRoutes_WithoutAJwt_Return401_S1_7()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded]);
        var anonymous = factory.CreateClient();
        var itemId = seeded.Items[0].Id;

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/files/{seeded.Items[0].FileId}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.ApproveAsync(itemId)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.RejectAsync(itemId, new { comment = "x" })).StatusCode);
        Assert.Equal(ItemStatus.Uploaded, await ItemStatusAsync(factory, itemId));
    }

    [Fact]
    public async Task AllThreeRoutes_WithAPortalSessionToken_Return401_S3_14()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([RequiredUploaded]);
        var portalSeed = await PortalTestKit.SeedAsync(factory);
        var (portal, _) = await factory.PortalClientAsync(portalSeed.Token);
        var itemId = seeded.Items[0].Id;

        Assert.Equal(HttpStatusCode.Unauthorized, (await portal.GetAsync($"/api/files/{seeded.Items[0].FileId}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await portal.ApproveAsync(itemId)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await portal.RejectAsync(itemId, new { comment = "x" })).StatusCode);
        Assert.Equal(ItemStatus.Uploaded, await ItemStatusAsync(factory, itemId));
        Assert.Equal(0, await factory.WithDbAsync(db => db.ReviewDecisions.CountAsync()));
    }

    [Fact]
    public async Task ApproveAndReject_UnknownItem_Return404()
    {
        using var factory = new ReviewFactory();
        var client = await factory.StaffClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.ApproveAsync(Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.RejectAsync(Guid.NewGuid(), new { comment = "x" })).StatusCode);
    }

    // ---- S6.6 -------------------------------------------------------------------------------

    [Fact]
    public async Task Decisions_WriteNoStorageKeyTokenOrFileContentToAuditMetadataOrLogs_S6_6()
    {
        using var factory = new ReviewFactory(ReviewFactory.ConfiguredWebhookUrl);
        var seeded = await factory.SeedReviewAsync([RequiredUploaded, RequiredUploaded]);
        var client = await factory.StaffClientAsync();
        var jwt = client.DefaultRequestHeaders.Authorization!.Parameter!;

        (await client.RejectAsync(seeded.Items[0].Id, new { comment = "Cut off" })).EnsureSuccessStatusCode();
        (await client.ApproveAsync(seeded.Items[0].Id)).EnsureSuccessStatusCode();
        (await client.ApproveAsync(seeded.Items[1].Id)).EnsureSuccessStatusCode();

        var metadata = (await factory.WithDbAsync(db => db.AuditEvents.Select(e => e.Metadata).ToListAsync()))
            .Where(m => m is not null).Select(m => m!).ToList();

        foreach (var item in seeded.Items)
        {
            var guidPart = item.StorageKey!.Split('/')[^1];
            Assert.DoesNotContain(metadata, m => m.Contains(guidPart, StringComparison.Ordinal));
            factory.Logs.AssertNoneContain(guidPart, "storage key");
        }

        Assert.DoesNotContain(metadata, m => m.Contains("review-body-marker", StringComparison.Ordinal));
        Assert.DoesNotContain(metadata, m => m.Contains(jwt, StringComparison.Ordinal));
        factory.Logs.AssertNoneContain(jwt, "staff JWT");
        factory.Logs.AssertNoneContain("review-body-marker", "file content");
    }
}
