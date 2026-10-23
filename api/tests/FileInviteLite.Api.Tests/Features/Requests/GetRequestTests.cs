using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Features.Portal;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FileInviteLite.Api.Tests.Features.Requests.RequestsTestKit;
using static FileInviteLite.Api.Tests.Features.Requests.SendRequestTestKit;

namespace FileInviteLite.Api.Tests.Features.Requests;

public sealed class GetRequestTests(SendFactory factory) : IClassFixture<SendFactory>
{
    private async Task<HttpClient> StaffClientAsync()
    {
        var client = factory.CreateClient();
        await client.AuthenticateAsStaffAsync();
        return client;
    }

    // ---- S1.10 and the contract shape ------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheRequestWithItsItemsAndFieldsExactlyAsTheContract_S1_10()
    {
        var client = await StaffClientAsync();
        var due = UtcToday.AddDays(20);
        var id = await client.CreateAsync(Body(
            clientName: "Contract Client", clientEmail: "contract@example.com", dueDate: due,
            items: [Item("Passport", "Photo page", true), Item("Payslip", null, false)]));
        var staff = await factory.Services.RunDbAsync(db =>
            db.StaffUsers.AsNoTracking().SingleAsync(u => u.Email == ApiFactory.DemoEmail));

        var response = await client.GetAsync($"/api/requests/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            ["clientEmail", "clientName", "createdAt", "createdBy", "dueDate", "id", "isOverdue", "items", "magicLink", "status"],
            PropertyNames(root));
        Assert.Equal(id, root.GetProperty("id").GetGuid());
        Assert.Equal("Contract Client", root.GetProperty("clientName").GetString());
        Assert.Equal("contract@example.com", root.GetProperty("clientEmail").GetString());
        Assert.Equal(due.ToString("yyyy-MM-dd"), root.GetProperty("dueDate").GetString());   // S10.3
        Assert.Equal("Draft", root.GetProperty("status").GetString());
        Assert.False(root.GetProperty("isOverdue").GetBoolean());
        AssertIsUtcTimestamp(root.GetProperty("createdAt"));

        var createdBy = root.GetProperty("createdBy");
        Assert.Equal(["id", "name"], PropertyNames(createdBy));
        Assert.Equal(staff.Id, createdBy.GetProperty("id").GetGuid());
        Assert.Equal(staff.Name, createdBy.GetProperty("name").GetString());

        var items = root.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        foreach (var item in items)
        {
            Assert.Equal(
                ["description", "id", "latestDecision", "latestFile", "required", "status", "title"],
                PropertyNames(item));
            Assert.Equal("Outstanding", item.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, item.GetProperty("latestFile").ValueKind);
            Assert.Equal(JsonValueKind.Null, item.GetProperty("latestDecision").ValueKind);   // kept in the shape; Phase 2 fills it
        }

        var passport = Assert.Single(items, i => i.GetProperty("title").GetString() == "Passport");
        Assert.Equal("Photo page", passport.GetProperty("description").GetString());
        Assert.True(passport.GetProperty("required").GetBoolean());
        var payslip = Assert.Single(items, i => i.GetProperty("title").GetString() == "Payslip");
        Assert.Equal(JsonValueKind.Null, payslip.GetProperty("description").ValueKind);
        Assert.False(payslip.GetProperty("required").GetBoolean());
    }

    [Fact]
    public async Task Get_ReportsAnOverdueRequestAsOverdue_S1_10()
    {
        var client = await StaffClientAsync();
        var id = await factory.Services.SeedAsync(UtcToday.AddDays(-3), RequestStatus.Sent);

        var root = await client.GetJsonAsync($"/api/requests/{id}");

        Assert.True(root.GetProperty("isOverdue").GetBoolean());
    }

    // ---- uploaded file metadata ------------------------------------------------------------

    [Fact]
    public async Task Get_ReturnsTheMostRecentUploadPerItemAsMetadataOnly_S3_13()
    {
        var client = await StaffClientAsync();
        var seeded = await factory.SeedAsync(itemStatuses: [ItemStatus.Uploaded, ItemStatus.Outstanding]);
        var itemId = seeded.ItemIds[0];
        var older = await factory.SeedFileAsync(itemId, "old.pdf", DateTime.UtcNow.AddDays(-2));
        var newer = await factory.SeedFileAsync(itemId, "new.pdf", DateTime.UtcNow.AddDays(-1));

        var response = await client.GetAsync($"/api/requests/{seeded.RequestId}");
        var raw = await response.Content.ReadAsStringAsync();
        var root = JsonSerializer.Deserialize<JsonElement>(raw);

        var item = root.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == itemId);
        var latest = item.GetProperty("latestFile");
        Assert.Equal(["contentType", "id", "originalName", "sizeBytes", "uploadedAt"], PropertyNames(latest));
        Assert.Equal(newer, latest.GetProperty("id").GetGuid());
        Assert.NotEqual(older, latest.GetProperty("id").GetGuid());
        Assert.Equal("new.pdf", latest.GetProperty("originalName").GetString());
        Assert.Equal("application/pdf", latest.GetProperty("contentType").GetString());
        Assert.Equal(10, latest.GetProperty("sizeBytes").GetInt64());
        AssertIsUtcTimestamp(latest.GetProperty("uploadedAt"));

        var other = root.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == seeded.ItemIds[1]);
        Assert.Equal(JsonValueKind.Null, other.GetProperty("latestFile").ValueKind);

        // Never a storage key, a hash, or a path.
        var storageKey = await factory.Services.RunDbAsync(db =>
            db.UploadedFiles.Where(f => f.Id == newer).Select(f => f.StorageKey).SingleAsync());
        Assert.DoesNotContain(storageKey, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 64), raw, StringComparison.Ordinal);
        Assert.DoesNotContain("storageKey", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sha256", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_AfterARealUpload_ReturnsMetadataButNeverTheFileContents_S1_10()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();
        var sent = await client.SendOkAsync(id);
        var (portal, session) = await factory.PortalClientAsync(sent.Token);
        Assert.Equal(id, session.RequestId);
        var itemId = await factory.Services.RunDbAsync(db =>
            db.ChecklistItems.Where(i => i.RequestId == id).Select(i => i.Id).FirstAsync());
        var marker = $"FILE-CONTENT-{Guid.NewGuid():N}";
        var upload = await portal.UploadAsync(itemId, "scan.pdf", PortalTestKit.Pdf(marker));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        var response = await client.GetAsync($"/api/requests/{id}");
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(marker, raw, StringComparison.Ordinal);
        var root = JsonSerializer.Deserialize<JsonElement>(raw);
        var item = root.GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("id").GetGuid() == itemId);
        Assert.Equal("Uploaded", item.GetProperty("status").GetString());
        Assert.Equal("scan.pdf", item.GetProperty("latestFile").GetProperty("originalName").GetString());
    }

    // ---- magicLink: S1.12 ------------------------------------------------------------------

    [Fact]
    public async Task Get_MagicLinkIsNullBeforeTheRequestIsSent_S1_12()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();

        var root = await client.GetJsonAsync($"/api/requests/{id}");

        Assert.True(root.TryGetProperty("magicLink", out var link));
        Assert.Equal(JsonValueKind.Null, link.ValueKind);
    }

    [Fact]
    public async Task Get_AfterSending_ReportsOnlyIssuedAtExpiresAtAndRevoked_NeverTheTokenOrUrl_S1_12()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();
        var sent = await client.SendOkAsync(id);

        var response = await client.GetAsync($"/api/requests/{id}");
        var raw = await response.Content.ReadAsStringAsync();
        var root = JsonSerializer.Deserialize<JsonElement>(raw);

        var link = root.GetProperty("magicLink");
        Assert.Equal(["expiresAt", "issuedAt", "revoked"], PropertyNames(link));
        Assert.False(link.GetProperty("revoked").GetBoolean());
        AssertIsUtcTimestamp(link.GetProperty("issuedAt"));
        AssertIsUtcTimestamp(link.GetProperty("expiresAt"));
        Assert.Equal(sent.ExpiresAt, link.GetProperty("expiresAt").GetDateTime());

        // issuedAt is not a column: it is the expiry minus the configured lifetime.
        Assert.Equal(
            sent.ExpiresAt.AddDays(-SendFactory.LifetimeDays), link.GetProperty("issuedAt").GetDateTime());

        // The token appears nowhere in the response, in any form.
        Assert.DoesNotContain(sent.Token, raw, StringComparison.Ordinal);
        Assert.DoesNotContain(Sha256Hex(sent.Token), raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("portal#", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(SendFactory.BaseUrl, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"url\"", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Get_AfterAReSend_ReportsTheNewestLink_NotTheRevokedOne_S1_14()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();
        await client.SendOkAsync(id);
        var second = await client.SendOkAsync(id);

        var root = await client.GetJsonAsync($"/api/requests/{id}");

        var link = root.GetProperty("magicLink");
        Assert.False(link.GetProperty("revoked").GetBoolean());
        Assert.Equal(second.ExpiresAt, link.GetProperty("expiresAt").GetDateTime());
    }

    [Fact]
    public async Task Get_PrefersTheLiveLinkOverAnOlderRevokedOne_EvenWhenTheRevokedOneExpiresLater_S1_14()
    {
        // The configured lifetime can be shortened between sends, so a later expiry does not mean
        // a later link. The live link is the newest one (re-sending revokes the rest).
        var client = await StaffClientAsync();
        var seeded = await factory.SeedAsync(linkRevoked: true, linkExpiresAt: DateTime.UtcNow.AddDays(30));
        var liveExpiry = DateTime.UtcNow.AddDays(3);
        await factory.Services.RunDbAsync(async db =>
        {
            db.MagicLinks.Add(new MagicLink
            {
                RequestId = seeded.RequestId,
                TokenHash = Sha256Hex(PortalTestKit.NewToken()),
                ExpiresAt = liveExpiry,
            });
            await db.SaveChangesAsync();
            return 0;
        });

        var root = await client.GetJsonAsync($"/api/requests/{seeded.RequestId}");

        var link = root.GetProperty("magicLink");
        Assert.False(link.GetProperty("revoked").GetBoolean());
        Assert.InRange(link.GetProperty("expiresAt").GetDateTime(), liveExpiry.AddSeconds(-1), liveExpiry.AddSeconds(1));
    }

    [Fact]
    public async Task Get_AFullyRevokedLinkIsReportedAsRevoked_S1_12()
    {
        var client = await StaffClientAsync();
        var seeded = await factory.SeedAsync(linkRevoked: true);

        var root = await client.GetJsonAsync($"/api/requests/{seeded.RequestId}");

        Assert.True(root.GetProperty("magicLink").GetProperty("revoked").GetBoolean());
    }

    // ---- authentication and existence ------------------------------------------------------

    [Fact]
    public async Task Get_WithoutAJwt_Returns401_S1_7()
    {
        var staff = await StaffClientAsync();
        var id = await staff.CreateAsync();

        var response = await factory.CreateClient().GetAsync($"/api/requests/{id}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("00000000-0000-4000-8000-000000000001")]
    [InlineData("not-a-guid")]
    public async Task Get_WithoutAJwt_Returns401_NotA404_ForAnIdThatDoesNotExist_S1_7(string id)
    {
        var response = await factory.CreateClient().GetAsync($"/api/requests/{id}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("00000000-0000-4000-8000-000000000001")]
    [InlineData("not-a-guid")]
    public async Task Get_ForAnUnknownRequest_Returns404(string id)
    {
        var client = await StaffClientAsync();

        var response = await client.GetAsync($"/api/requests/{id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Get_WithAPortalSessionToken_Returns401_S2_7()
    {
        var staff = await StaffClientAsync();
        var id = await staff.CreateAsync();
        var sent = await staff.SendOkAsync(id);
        var (portal, _) = await factory.PortalClientAsync(sent.Token);

        var own = await portal.GetAsync($"/api/requests/{id}");
        var unknown = await portal.GetAsync($"/api/requests/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, own.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);   // no oracle either way
    }

    // ---- audit ------------------------------------------------------------------------------

    [Fact]
    public async Task Get_WritesNoAuditEvent()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();
        await client.SendOkAsync(id);
        var before = await factory.Services.RunDbAsync(db => db.AuditEvents.CountAsync());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/requests/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/requests/{Guid.NewGuid()}")).StatusCode);

        var after = await factory.Services.RunDbAsync(db => db.AuditEvents.CountAsync());
        Assert.Equal(before, after);
    }
}
