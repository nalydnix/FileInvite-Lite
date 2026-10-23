using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FileInviteLite.Api.Domain;
using static FileInviteLite.Api.Tests.Features.Portal.PortalTestKit;

namespace FileInviteLite.Api.Tests.Features.Portal;

public sealed class GetPortalRequestTests : IClassFixture<PortalFactory>
{
    private readonly PortalFactory _factory;

    public GetPortalRequestTests(PortalFactory factory) => _factory = factory;

    [Fact]
    public async Task GetRequest_ReturnsClientNameDueDateAndEachItem_S3_1()
    {
        var due = new DateOnly(2031, 4, 1);
        var seeded = await _factory.SeedAsync(
            "Jane Client", [ItemStatus.Outstanding, ItemStatus.Uploaded, ItemStatus.Approved], dueDate: due);
        var (client, _) = await _factory.PortalClientAsync(seeded.Token);

        var response = await client.GetAsync("/api/portal/request");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Jane Client", body.GetProperty("clientName").GetString());
        Assert.Equal("2031-04-01", body.GetProperty("dueDate").GetString());
        Assert.False(body.GetProperty("isOverdue").GetBoolean());

        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(seeded.ItemIds.Order(), items.Select(i => i.GetProperty("id").GetGuid()).Order());

        var first = items.Single(i => i.GetProperty("id").GetGuid() == seeded.ItemIds[0]);
        Assert.Equal("Jane Client item 1", first.GetProperty("title").GetString());
        Assert.Equal("Jane Client description", first.GetProperty("description").GetString());
        Assert.True(first.GetProperty("required").GetBoolean());
        Assert.Equal("Outstanding", first.GetProperty("status").GetString());

        var statuses = items.ToDictionary(i => i.GetProperty("id").GetGuid(), i => i.GetProperty("status").GetString());
        Assert.Equal("Uploaded", statuses[seeded.ItemIds[1]]);
        Assert.Equal("Approved", statuses[seeded.ItemIds[2]]);
    }

    [Fact]
    public async Task GetRequest_FlagsAnOverdueRequest_S3_1()
    {
        var seeded = await _factory.SeedAsync(dueDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3));
        var (client, _) = await _factory.PortalClientAsync(seeded.Token);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/portal/request");

        Assert.True(body.GetProperty("isOverdue").GetBoolean());
    }

    [Fact]
    public async Task GetRequest_ExposesNoStaffIdentityAuditHistoryOrStorageKey_S3_2()
    {
        var seeded = await _factory.SeedAsync(itemStatuses: [ItemStatus.Uploaded, ItemStatus.Outstanding]);
        var fileId = await _factory.SeedFileAsync(seeded.ItemIds[0], "passport.pdf", DateTime.UtcNow.AddMinutes(-5));
        var storageKey = await _factory.WithDbAsync(async db =>
            (await db.UploadedFiles.FindAsync(fileId))!.StorageKey);
        var staffId = await _factory.WithDbAsync(async db =>
            (await db.DocumentRequests.FindAsync(seeded.RequestId))!.CreatedBy);

        var (client, _) = await _factory.PortalClientAsync(seeded.Token);   // writes a PortalAccessed audit event

        var raw = await client.GetStringAsync("/api/portal/request");
        var body = JsonSerializer.Deserialize<JsonElement>(raw);

        // The shape is exactly the contract's: nothing extra rides along.
        Assert.Equal(
            ["clientName", "dueDate", "isOverdue", "items"],
            body.EnumerateObject().Select(p => p.Name).Order());

        var allowedItemFields = new[]
        {
            "description", "id", "latestUpload", "rejectionComment", "required", "status", "title",
        };
        foreach (var item in body.GetProperty("items").EnumerateArray())
        {
            Assert.Subset(allowedItemFields.ToHashSet(), item.EnumerateObject().Select(p => p.Name).ToHashSet());
        }

        var upload = body.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == seeded.ItemIds[0]).GetProperty("latestUpload");
        Assert.Equal(["originalName", "uploadedAt"], upload.EnumerateObject().Select(p => p.Name).Order());

        // Nothing that identifies staff, the request's own id, the client's email, the audit
        // trail, or where a file lives.
        Assert.DoesNotContain(storageKey, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("2020/01", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(staffId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.RequestId.ToString(), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("client@example.com", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactoryDemoEmail, raw, StringComparison.OrdinalIgnoreCase);
        foreach (var word in new[] { "audit", "createdBy", "storageKey", "sha256", "PortalAccessed", "token", "email" })
        {
            Assert.DoesNotContain(word, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task GetRequest_OnlyRejectedItemsCanCarryARejectionComment_S3_2()
    {
        var seeded = await _factory.SeedAsync(
            itemStatuses: [ItemStatus.Outstanding, ItemStatus.Uploaded, ItemStatus.Approved, ItemStatus.Rejected]);
        var (client, _) = await _factory.PortalClientAsync(seeded.Token);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/portal/request");

        // Review decisions do not exist until the phase 2 schema, so no comment can be stored yet.
        // What must already hold is that a non-rejected item never carries the field.
        foreach (var item in body.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("status").GetString() != "Rejected")
            {
                Assert.False(item.TryGetProperty("rejectionComment", out _));
            }
        }
    }

    [Fact]
    public async Task GetRequest_OffersTheLatestUploadAsLatestUpload_S3_13()
    {
        var seeded = await _factory.SeedAsync(itemStatuses: [ItemStatus.Uploaded]);
        await _factory.SeedFileAsync(seeded.ItemIds[0], "old.pdf", DateTime.UtcNow.AddDays(-2));
        await _factory.SeedFileAsync(seeded.ItemIds[0], "newest.pdf", DateTime.UtcNow.AddMinutes(-1));
        await _factory.SeedFileAsync(seeded.ItemIds[0], "middle.pdf", DateTime.UtcNow.AddDays(-1));
        var (client, _) = await _factory.PortalClientAsync(seeded.Token);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/portal/request");

        var latest = body.GetProperty("items")[0].GetProperty("latestUpload");
        Assert.Equal("newest.pdf", latest.GetProperty("originalName").GetString());
        AssertIsUtc(latest.GetProperty("uploadedAt"));
    }

    [Fact]
    public async Task GetRequest_WithoutASession_Returns401_S3_1()
    {
        var response = await _factory.CreateClient().GetAsync("/api/portal/request");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private const string ApiFactoryDemoEmail = Infrastructure.ApiFactory.DemoEmail;

    private static void AssertIsUtc(JsonElement value)
    {
        Assert.EndsWith("Z", value.GetString());
    }
}
