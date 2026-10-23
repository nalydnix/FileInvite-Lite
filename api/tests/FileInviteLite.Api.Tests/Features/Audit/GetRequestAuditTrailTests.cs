using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Features.Portal;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FileInviteLite.Api.Tests.Features.Portal.PortalTestKit;
using static FileInviteLite.Api.Tests.Features.Requests.RequestsTestKit;

namespace FileInviteLite.Api.Tests.Features.Audit;

public sealed class GetRequestAuditTrailTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    private static readonly DateTime Base = new(2030, 3, 15, 11, 0, 0, DateTimeKind.Utc);

    private static string Url(Guid requestId, string query = "") => $"/api/requests/{requestId}/audit{query}";

    private async Task<HttpClient> StaffAsync()
    {
        var client = factory.CreateClient();
        await client.AuthenticateAsStaffAsync();
        return client;
    }

    private Task<int> AddEventAsync(
        Guid? entityId, DateTime at, string action = "TestAction", string entityType = "Test",
        string? metadata = null) =>
        factory.WithDbAsync(async db =>
        {
            db.AuditEvents.Add(new AuditEvent
            {
                OccurredAt = at,
                ActorType = ActorType.System,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Metadata = metadata,
            });
            return await db.SaveChangesAsync();
        });

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<string?> Actions(JsonElement body) =>
        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToList();

    [Fact]
    public async Task Trail_ReturnsEventsNewestFirstInTheContractShape_S6_4()
    {
        var request = await factory.SeedAsync();
        await AddEventAsync(request.RequestId, Base, "Older", "DocumentRequest", """{"a":1}""");
        await AddEventAsync(request.RequestId, Base.AddMinutes(5), "Newer", "DocumentRequest",
            """{"originalName":"passport.pdf","sizeBytes":184320}""");
        await AddEventAsync(request.ItemIds[0], Base.AddMinutes(2), "Middle", "ChecklistItem");

        var client = await StaffAsync();
        var body = await ReadAsync(await client.GetAsync(Url(request.RequestId)));

        Assert.Equal(1, body.GetProperty("page").GetInt32());
        Assert.Equal(50, body.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, body.GetProperty("total").GetInt32());
        Assert.Equal(["Newer", "Middle", "Older"], Actions(body));

        var first = body.GetProperty("items")[0];
        foreach (var field in new[] { "id", "occurredAt", "actorType", "actorId", "action", "entityType", "entityId", "metadata" })
        {
            Assert.True(first.TryGetProperty(field, out _), $"missing {field}");
        }

        Assert.Equal("System", first.GetProperty("actorType").GetString());
        var metadata = first.GetProperty("metadata");
        Assert.Equal(JsonValueKind.Object, metadata.ValueKind);
        Assert.Equal("passport.pdf", metadata.GetProperty("originalName").GetString());
        Assert.Equal(184320, metadata.GetProperty("sizeBytes").GetInt32());
    }

    [Fact]
    public async Task Trail_PaginatesAndReportsTotal_S6_4()
    {
        var request = await factory.SeedAsync();
        for (var i = 0; i < 5; i++)
        {
            await AddEventAsync(request.RequestId, Base.AddMinutes(i), $"E{i}");
        }

        var client = await StaffAsync();
        var second = await ReadAsync(await client.GetAsync(Url(request.RequestId, "?page=2&pageSize=2")));

        Assert.Equal(2, second.GetProperty("page").GetInt32());
        Assert.Equal(2, second.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, second.GetProperty("total").GetInt32());
        Assert.Equal(["E2", "E1"], Actions(second));
    }

    [Fact]
    public async Task Trail_UnknownRequest_Returns404_S6_5()
    {
        var client = await StaffAsync();

        var response = await client.GetAsync(Url(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Trail_WithoutJwt_Returns401_S6_5()
    {
        var request = await factory.SeedAsync();

        var response = await factory.CreateClient().GetAsync(Url(request.RequestId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Trail_WithPortalSessionToken_Returns401_S2_7()
    {
        var request = await factory.SeedAsync();
        var (portal, _) = await factory.PortalClientAsync(request.Token);

        var response = await portal.GetAsync(Url(request.RequestId));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Trail_IncludesItemAndFileEventsAndExcludesOtherRequests()
    {
        var mine = await factory.SeedAsync("Mine Client");
        var other = await factory.SeedAsync("Other Client");
        var fileId = await factory.SeedFileAsync(mine.ItemIds[0], "scan.pdf", Base);

        await AddEventAsync(mine.ItemIds[0], Base.AddMinutes(1), "MineItem", "ChecklistItem");
        await AddEventAsync(fileId, Base.AddMinutes(2), "MineFile", "UploadedFile");
        await AddEventAsync(mine.RequestId, Base.AddMinutes(3), "MineRequest", "DocumentRequest");
        await AddEventAsync(other.ItemIds[0], Base.AddMinutes(4), "OtherItem", "ChecklistItem");
        await AddEventAsync(other.RequestId, Base.AddMinutes(5), "OtherRequest", "DocumentRequest");
        await AddEventAsync(null, Base.AddMinutes(6), "NoEntity", "Nothing");

        var client = await StaffAsync();

        var mineActions = Actions(await ReadAsync(await client.GetAsync(Url(mine.RequestId))));
        var otherActions = Actions(await ReadAsync(await client.GetAsync(Url(other.RequestId))));

        Assert.Equal(["MineRequest", "MineFile", "MineItem"], mineActions);
        Assert.Equal(["OtherRequest", "OtherItem"], otherActions);
    }

    [Fact]
    public async Task Trail_ShowsARealUploadOnlyInItsOwnRequest()
    {
        var mine = await factory.SeedAsync("Upload Mine");
        var other = await factory.SeedAsync("Upload Other");
        var (portal, _) = await factory.PortalClientAsync(mine.Token);

        var upload = await portal.UploadAsync(mine.ItemIds[0], "passport.pdf", Pdf());
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        var client = await StaffAsync();
        var inMine = Actions(await ReadAsync(await client.GetAsync(Url(mine.RequestId))));
        var inOther = Actions(await ReadAsync(await client.GetAsync(Url(other.RequestId))));

        Assert.Contains("ItemUploaded", inMine);
        Assert.Contains("PortalAccessed", inMine);
        Assert.Empty(inOther);
    }

    [Fact]
    public async Task Trail_PagingIsStableWhenEventsShareAnOccurredAt()
    {
        var request = await factory.SeedAsync();
        for (var i = 0; i < 7; i++)
        {
            await AddEventAsync(request.RequestId, Base, $"Tie{i}");
        }

        var client = await StaffAsync();
        var ids = new List<string>();
        for (var page = 1; page <= 4; page++)
        {
            var body = await ReadAsync(await client.GetAsync(Url(request.RequestId, $"?page={page}&pageSize=2")));
            ids.AddRange(body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!));
        }

        Assert.Equal(7, ids.Count);
        Assert.Equal(7, ids.Distinct().Count());

        var all = await ReadAsync(await client.GetAsync(Url(request.RequestId, "?pageSize=200")));
        Assert.Equal(
            all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()),
            ids);
    }

    [Fact]
    // ListRequests answers an out-of-range pageSize with a 400 rather than clamping it, and the API
    // should not have two conventions for the same mistake. The task file asked for clamping on the
    // mistaken basis that ListRequests clamped; it does not.
    public async Task Trail_PageSizeAboveMaximum_Returns400NamingPageSize()
    {
        var request = await factory.SeedAsync();
        var client = await StaffAsync();

        var response = await client.GetAsync(Url(request.RequestId, "?pageSize=100000"));

        await AssertValidationProblemAsync(response, "pageSize");
    }

    [Fact]
    public async Task Trail_PageSizeAtTheMaximum_IsAccepted()
    {
        var request = await factory.SeedAsync();
        var client = await StaffAsync();

        var response = await client.GetAsync(Url(request.RequestId, "?pageSize=200"));

        var body = await ReadAsync(response);
        Assert.Equal(200, body.GetProperty("pageSize").GetInt32());
    }

    [Theory]
    [InlineData("page=abc", "page")]
    [InlineData("page=0", "page")]
    [InlineData("page=2147483647", "page")]
    [InlineData("pageSize=abc", "pageSize")]
    [InlineData("pageSize=0", "pageSize")]
    public async Task Trail_MalformedPaging_Returns400InTheSharedShape(string query, string field)
    {
        var request = await factory.SeedAsync();
        var client = await StaffAsync();

        var response = await client.GetAsync(Url(request.RequestId, "?" + query));

        await AssertValidationProblemAsync(response, field);
    }

    [Fact]
    public async Task Trail_NullAndMalformedMetadataReadAsNull()
    {
        var request = await factory.SeedAsync();
        await AddEventAsync(request.RequestId, Base, "NullMeta", metadata: null);
        await AddEventAsync(request.RequestId, Base.AddMinutes(1), "BadMeta", metadata: "{not json");

        var client = await StaffAsync();
        var body = await ReadAsync(await client.GetAsync(Url(request.RequestId)));

        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Equal(JsonValueKind.Null, i.GetProperty("metadata").ValueKind));
    }

    [Fact]
    public async Task Trail_TimestampsAreUtcIso8601WithZ_S10_3()
    {
        var request = await factory.SeedAsync();
        await AddEventAsync(request.RequestId, Base);

        var client = await StaffAsync();
        var raw = await (await client.GetAsync(Url(request.RequestId))).Content.ReadAsStringAsync();

        Assert.Matches(new Regex("\"occurredAt\":\"2030-03-15T11:00:00(\\.0+)?Z\""), raw);
    }

    [Fact]
    public async Task Trail_ReadingWritesNoAuditEvent()
    {
        var request = await factory.SeedAsync();
        var client = await StaffAsync();
        var before = await factory.WithDbAsync(db => db.AuditEvents.CountAsync());

        await ReadAsync(await client.GetAsync(Url(request.RequestId)));

        Assert.Equal(before, await factory.WithDbAsync(db => db.AuditEvents.CountAsync()));
    }

    [Fact]
    public async Task Trail_HappyPathMetadataLeaksNoSecrets_S6_6()
    {
        var staff = await StaffAsync();
        var requestId = await staff.CreateAsync();

        // Sending is a later task's endpoint, so the link is added directly, as PortalTestKit does.
        var token = NewToken();
        await factory.WithDbAsync(async db =>
        {
            db.MagicLinks.Add(new MagicLink
            {
                RequestId = requestId,
                TokenHash = HashToken(token),
                ExpiresAt = DateTime.UtcNow.AddDays(14),
            });
            return await db.SaveChangesAsync();
        });
        var itemId = await factory.WithDbAsync(db =>
            db.ChecklistItems.Where(i => i.RequestId == requestId).Select(i => i.Id).FirstAsync());

        var (portal, session) = await factory.PortalClientAsync(token);
        const string marker = "SECRET-FILE-CONTENT-MARKER";
        Assert.Equal(HttpStatusCode.Created, (await portal.UploadAsync(itemId, "passport.pdf", Pdf(marker))).StatusCode);

        var storageKey = await factory.WithDbAsync(db =>
            db.UploadedFiles.Where(f => f.ChecklistItemId == itemId).Select(f => f.StorageKey).FirstAsync());
        var raw = await (await staff.GetAsync(Url(requestId))).Content.ReadAsStringAsync();

        var actions = Actions(JsonDocument.Parse(raw).RootElement);
        Assert.Contains("RequestCreated", actions);
        Assert.Contains("PortalAccessed", actions);
        Assert.Contains("ItemUploaded", actions);

        Assert.DoesNotContain(token, raw);
        Assert.DoesNotContain(HashToken(token), raw);
        Assert.DoesNotContain(session.SessionToken, raw);
        Assert.DoesNotContain(storageKey, raw);
        Assert.DoesNotContain(marker, raw);
        Assert.DoesNotContain(ApiFactory.DemoPassword, raw);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageKey", raw, StringComparison.OrdinalIgnoreCase);
    }
}
