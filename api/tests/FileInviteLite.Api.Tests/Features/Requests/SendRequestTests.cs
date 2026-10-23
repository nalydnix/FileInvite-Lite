using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Features.Portal;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using static FileInviteLite.Api.Tests.Features.Requests.RequestsTestKit;
using static FileInviteLite.Api.Tests.Features.Requests.SendRequestTestKit;

namespace FileInviteLite.Api.Tests.Features.Requests;

public sealed class SendRequestTests(SendFactory factory) : IClassFixture<SendFactory>
{
    private async Task<HttpClient> StaffClientAsync()
    {
        var client = factory.CreateClient();
        await client.AuthenticateAsStaffAsync();
        return client;
    }

    // ---- S1.11 ----------------------------------------------------------------------------

    [Fact]
    public async Task Send_Returns200WithOnlyUrlAndExpiresAt_S1_11()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();

        var response = await client.SendAsync(id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["expiresAt", "url"], PropertyNames(body));
        AssertIsUtcTimestamp(body.GetProperty("expiresAt"));   // S10.3
    }

    [Fact]
    public async Task Send_MovesTheRequestToSent_S1_11()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();

        await client.SendOkAsync(id);

        var status = await factory.Services.RunDbAsync(db =>
            db.DocumentRequests.AsNoTracking().Where(r => r.Id == id).Select(r => r.Status).SingleAsync());
        Assert.Equal(RequestStatus.Sent, status);
    }

    [Fact]
    public async Task Send_OnAnInProgressRequest_DoesNotMoveItBackToSent_S1_11()
    {
        // The status is derived, never chosen: a request a client has already started stays so.
        var client = await StaffClientAsync();
        var id = await factory.Services.SeedAsync(UtcToday.AddDays(5), RequestStatus.InProgress, itemCount: 2);
        await factory.Services.RunDbAsync(db => db.ChecklistItems.Where(i => i.RequestId == id).Take(1)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ItemStatus.Uploaded)));

        await client.SendOkAsync(id);

        var status = await factory.Services.RunDbAsync(db =>
            db.DocumentRequests.AsNoTracking().Where(r => r.Id == id).Select(r => r.Status).SingleAsync());
        Assert.Equal(RequestStatus.InProgress, status);
    }

    [Fact]
    public async Task Send_UrlIsTheConfiguredBaseUrlWithTheTokenInTheFragment_ADR0004()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();

        var sent = await client.SendOkAsync(id);

        Assert.Equal($"{SendFactory.BaseUrl}/portal{FragmentMarker}{sent.Token}", sent.Url);
        Assert.DoesNotContain('?', sent.Url);   // never in the query string
    }

    [Fact]
    public async Task Send_ExpiresAtIsNowPlusTheConfiguredLifetime_ADR0004()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();
        var before = DateTime.UtcNow;

        var sent = await client.SendOkAsync(id);

        var after = DateTime.UtcNow;
        Assert.InRange(sent.ExpiresAt,
            before.AddDays(SendFactory.LifetimeDays).AddSeconds(-2),
            after.AddDays(SendFactory.LifetimeDays).AddSeconds(2));

        var stored = await factory.Services.RunDbAsync(db =>
            db.MagicLinks.AsNoTracking().Where(l => l.RequestId == id).Select(l => l.ExpiresAt).SingleAsync());
        Assert.Equal(sent.ExpiresAt, stored);
    }

    [Fact]
    public async Task Send_TokenIs256BitsOfBase64Url_S2_1()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();

        var first = await client.SendOkAsync(id);
        var second = await client.SendOkAsync(id);

        foreach (var token in new[] { first.Token, second.Token })
        {
            Assert.Matches("^[A-Za-z0-9_-]{43}$", token);   // 32 bytes, unpadded Base64Url
        }

        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public async Task Send_WithAnEmptyBodyAndNoContentType_Works_S1_11()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();

        var response = await client.PostAsync($"/api/requests/{id}/send", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- S1.12 / S2.2 ---------------------------------------------------------------------

    [Fact]
    public async Task Send_StoresOnlyTheSha256HexOfTheToken_S2_2()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();

        var sent = await client.SendOkAsync(id);

        var link = await factory.Services.RunDbAsync(db =>
            db.MagicLinks.AsNoTracking().SingleAsync(l => l.RequestId == id));
        Assert.Equal(Sha256Hex(sent.Token), link.TokenHash);   // lowercase hex, 64 characters
        Assert.Equal(64, link.TokenHash.Length);
        Assert.NotEqual(sent.Token, link.TokenHash);

        // Nowhere in the database, in any table or column, does a value equal or contain the token.
        var everything = await factory.EveryStoredStringAsync();
        Assert.NotEmpty(everything);
        Assert.DoesNotContain(everything, v => v.Contains(sent.Token, StringComparison.Ordinal));
    }

    // ---- S1.13 ----------------------------------------------------------------------------

    [Fact]
    public async Task Send_WritesRequestSentAndMagicLinkIssued_NeitherHoldingTheToken_S1_13()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();
        var staffId = await factory.Services.DemoStaffIdAsync();

        var sent = await client.SendOkAsync(id);

        var link = await factory.Services.RunDbAsync(db =>
            db.MagicLinks.AsNoTracking().SingleAsync(l => l.RequestId == id));
        var events = await factory.Services.RunDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == AuditAction.RequestSent || e.Action == AuditAction.MagicLinkIssued)
            .Where(e => e.EntityId == id || e.EntityId == link.Id)
            .ToListAsync());

        var requestSent = Assert.Single(events, e => e.Action == AuditAction.RequestSent);
        Assert.Equal(ActorType.Staff, requestSent.ActorType);
        Assert.Equal(staffId, requestSent.ActorId);
        Assert.Equal(id, requestSent.EntityId);

        var issued = Assert.Single(events, e => e.Action == AuditAction.MagicLinkIssued);
        Assert.Equal(ActorType.Staff, issued.ActorType);
        Assert.Equal(staffId, issued.ActorId);

        foreach (var evt in events)
        {
            Assert.DoesNotContain(sent.Token, evt.Metadata ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(link.TokenHash, evt.Metadata ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("portal#", evt.Metadata ?? "", StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Send_WhenTheLinkInsertFails_LeavesNoAuditEventAndNoStatusChange_S1_13()
    {
        using var isolated = new SendFactory();
        var client = isolated.CreateClient();
        await client.AuthenticateAsStaffAsync();
        var id = await client.CreateAsync();

        await isolated.Services.RunDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_link_insert BEFORE INSERT ON "MagicLinks"
            BEGIN SELECT RAISE(ABORT, 'forced'); END;
            """));

        var response = await client.SendAsync(id);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertNothingSentAsync(isolated, id);
    }

    [Fact]
    public async Task Send_WhenTheAuditInsertFails_LeavesNoLinkAndNoStatusChange_S1_13()
    {
        using var isolated = new SendFactory();
        var client = isolated.CreateClient();
        await client.AuthenticateAsStaffAsync();
        var id = await client.CreateAsync();

        await isolated.Services.RunDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_audit_insert BEFORE INSERT ON "AuditEvents"
            WHEN NEW."Action" = 'MagicLinkIssued'
            BEGIN SELECT RAISE(ABORT, 'forced'); END;
            """));

        var response = await client.SendAsync(id);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("forced", raw, StringComparison.OrdinalIgnoreCase);
        await AssertNothingSentAsync(isolated, id);
    }

    private static async Task AssertNothingSentAsync(ApiFactory host, Guid id)
    {
        var state = await host.Services.RunDbAsync(async db =>
            (Links: await db.MagicLinks.CountAsync(l => l.RequestId == id),
             Status: await db.DocumentRequests.Where(r => r.Id == id).Select(r => r.Status).SingleAsync(),
             Events: await db.AuditEvents.CountAsync(e =>
                 e.Action == AuditAction.RequestSent || e.Action == AuditAction.MagicLinkIssued)));

        Assert.Equal(0, state.Links);
        Assert.Equal(RequestStatus.Draft, state.Status);
        Assert.Equal(0, state.Events);
    }

    // ---- S1.14 ----------------------------------------------------------------------------

    [Fact]
    public async Task Send_ReSendRevokesThePreviousLink_S1_14()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();

        var first = await client.SendOkAsync(id);
        var second = await client.SendOkAsync(id);

        var links = await factory.Services.RunDbAsync(db =>
            db.MagicLinks.AsNoTracking().Where(l => l.RequestId == id).ToListAsync());
        Assert.Equal(2, links.Count);

        // The old token's hash no longer matches a live row; the new one does.
        var live = links.Where(l => l.RevokedAt is null && l.ExpiresAt > DateTime.UtcNow).ToList();
        var onlyLive = Assert.Single(live);
        Assert.Equal(Sha256Hex(second.Token), onlyLive.TokenHash);
        var old = Assert.Single(links, l => l.TokenHash == Sha256Hex(first.Token));
        Assert.NotNull(old.RevokedAt);

        var revoked = await factory.Services.RunDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == AuditAction.MagicLinkRevoked && e.EntityId == old.Id).ToListAsync());
        var evt = Assert.Single(revoked);
        Assert.Equal(ActorType.Staff, evt.ActorType);
        Assert.DoesNotContain(first.Token, evt.Metadata ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(old.TokenHash, evt.Metadata ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Send_ReSend_TheOldTokenNoLongerExchanges_TheNewOneDoes_S1_14()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();
        var first = await client.SendOkAsync(id);
        var second = await client.SendOkAsync(id);

        var anonymous = factory.CreateClient();
        var oldExchange = await anonymous.PostAsJsonAsync("/api/portal/exchange", new { token = first.Token });
        var newExchange = await anonymous.PostAsJsonAsync("/api/portal/exchange", new { token = second.Token });

        Assert.Equal(HttpStatusCode.Unauthorized, oldExchange.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newExchange.StatusCode);
    }

    [Fact]
    public async Task Send_FirstSendWritesNoRevokedEvent_AndEachResendWritesExactlyOne_S1_14()
    {
        var client = await StaffClientAsync();
        var id = await client.CreateAsync();

        async Task<int> RevokedEventsAsync() => await factory.Services.RunDbAsync(db =>
            db.AuditEvents.CountAsync(e => e.Action == AuditAction.MagicLinkRevoked
                && db.MagicLinks.Any(l => l.Id == e.EntityId && l.RequestId == id)));

        await client.SendOkAsync(id);
        Assert.Equal(0, await RevokedEventsAsync());
        await client.SendOkAsync(id);
        Assert.Equal(1, await RevokedEventsAsync());
        await client.SendOkAsync(id);
        Assert.Equal(2, await RevokedEventsAsync());
    }

    [Fact]
    public async Task Send_ReSendWhenTheAuditInsertFails_LeavesThePreviousLinkLive_S1_14()
    {
        // Revoke, issue and audit are one SaveChangesAsync: a failure half-way undoes all of it.
        using var isolated = new SendFactory();
        var client = isolated.CreateClient();
        await client.AuthenticateAsStaffAsync();
        var id = await client.CreateAsync();
        var first = await client.SendOkAsync(id);

        await isolated.Services.RunDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_revoked_audit BEFORE INSERT ON "AuditEvents"
            WHEN NEW."Action" = 'MagicLinkRevoked'
            BEGIN SELECT RAISE(ABORT, 'forced'); END;
            """));

        var response = await client.SendAsync(id);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var links = await isolated.Services.RunDbAsync(db =>
            db.MagicLinks.AsNoTracking().Where(l => l.RequestId == id).ToListAsync());
        var only = Assert.Single(links);
        Assert.Equal(Sha256Hex(first.Token), only.TokenHash);
        Assert.Null(only.RevokedAt);
    }

    // ---- S1.16 ----------------------------------------------------------------------------

    [Fact]
    public async Task Send_ARequestWithNoItems_Returns400AndWritesNothing_S1_16()
    {
        var client = await StaffClientAsync();
        var id = await factory.Services.SeedAsync(UtcToday.AddDays(5), itemCount: 0);

        var response = await client.SendAsync(id);

        await AssertValidationProblemAsync(response, "items");
        var state = await factory.Services.RunDbAsync(async db =>
            (Links: await db.MagicLinks.CountAsync(l => l.RequestId == id),
             Events: await db.AuditEvents.CountAsync(e => e.EntityId == id && e.Action == AuditAction.RequestSent)));
        Assert.Equal(0, state.Links);
        Assert.Equal(0, state.Events);
    }

    // ---- S1.15 ----------------------------------------------------------------------------

    [Fact]
    public async Task Send_InDevelopment_WritesAFileContainingTheLinkToTheSink_S1_15()
    {
        using var host = new SendFactory();
        var sink = host.Services.GetRequiredService<IConfiguration>()["Email:Sink"]!;
        var client = host.CreateClient();
        await client.AuthenticateAsStaffAsync();
        var id = await client.CreateAsync();

        var sent = await client.SendOkAsync(id);

        var file = Assert.Single(Directory.GetFiles(sink));
        Assert.EndsWith(".txt", file);
        Assert.Contains(sent.Url, await File.ReadAllTextAsync(file));
        // The token is a credential: it must not leak into a path either.
        Assert.DoesNotContain(sent.Token, Path.GetFileName(file), StringComparison.Ordinal);
        Assert.Equal("DevSinkEmailSender", EmailSenderTypeName(host.Services));
    }

    [Fact]
    public async Task Send_InProduction_WritesNoFileAndAttemptsNoSend_S1_15()
    {
        using var root = new SendFactory();
        using var host = root.WithWebHostBuilder(b => b.UseEnvironment("Production"));
        var sink = host.Services.GetRequiredService<IConfiguration>()["Email:Sink"]!;
        var client = host.CreateClient();
        await client.AuthenticateAsStaffAsync();
        var id = await client.CreateAsync();

        var sent = await client.SendOkAsync(id);

        Assert.NotEmpty(sent.Url);   // the send itself still works
        Assert.Empty(Directory.Exists(sink) ? Directory.GetFiles(sink, "*", SearchOption.AllDirectories) : []);
        Assert.Equal("NoOpEmailSender", EmailSenderTypeName(host.Services));   // by registration
    }

    private static string EmailSenderTypeName(IServiceProvider services)
    {
        var serviceType = typeof(Program).Assembly.GetType("FileInviteLite.Api.Services.Email.IEmailSender");
        Assert.NotNull(serviceType);
        using var scope = services.CreateScope();
        return scope.ServiceProvider.GetRequiredService(serviceType).GetType().Name;
    }

    // ---- authentication and existence -----------------------------------------------------

    [Fact]
    public async Task Send_WithoutAJwt_Returns401_S1_7()
    {
        var staff = await StaffClientAsync();
        var id = await staff.CreateAsync();

        var response = await factory.CreateClient().SendAsync(id);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Send_WithoutAJwt_Returns401EvenForAnUnknownId_S1_7()
    {
        var response = await factory.CreateClient().SendAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Send_ForAnUnknownRequest_Returns404()
    {
        var client = await StaffClientAsync();

        var response = await client.SendAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Send_ForAnIdThatIsNotAGuid_Returns404()
    {
        var client = await StaffClientAsync();

        var response = await client.PostAsync("/api/requests/not-a-guid/send", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Send_WithAPortalSessionToken_Returns401_S2_7()
    {
        var staff = await StaffClientAsync();
        var id = await staff.CreateAsync();
        var sent = await staff.SendOkAsync(id);
        var (portal, _) = await factory.PortalClientAsync(sent.Token);

        var own = await portal.SendAsync(id);
        var unknown = await portal.SendAsync(Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Unauthorized, own.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);   // 401, never 404
    }

    // ---- no token in logs -----------------------------------------------------------------

    [Fact]
    public async Task Send_ASuccessfulSendAndResend_LogsNeitherTokenNorHashAtAnyLevel_ADR0004()
    {
        using var host = new SendFactory();   // its own capture: only this flow's entries
        var client = host.CreateClient();
        await client.AuthenticateAsStaffAsync();
        var id = await client.CreateAsync();

        var first = await client.SendOkAsync(id);
        var second = await client.SendOkAsync(id);
        var read = await client.GetAsync($"/api/requests/{id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        // The capture is real: it saw the host work, at the lowest level.
        var entries = host.Logs.Entries;
        Assert.True(entries.Count > 20, $"Only {entries.Count} log entries captured; the provider is not attached.");
        Assert.Contains(entries, e => e.Contains($"/api/requests/{id}/send", StringComparison.Ordinal));

        foreach (var token in new[] { first.Token, second.Token })
        {
            host.Logs.AssertNoneContain(token, "magic-link token");
            host.Logs.AssertNoneContain(Sha256Hex(token), "magic-link token hash");
        }

        host.Logs.AssertNoneContain(first.Url, "magic-link URL");
    }
}
