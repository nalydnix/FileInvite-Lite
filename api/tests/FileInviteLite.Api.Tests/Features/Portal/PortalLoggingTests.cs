using System.Net;
using System.Net.Http.Json;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FileInviteLite.Api.Tests.Features.Portal.PortalTestKit;

namespace FileInviteLite.Api.Tests.Features.Portal;

public sealed class PortalLoggingTests
{
    [Fact]
    public async Task HappyPath_LogsNoTokenSessionJwtPasswordOrFileContentAtAnyLevel_S2_13()
    {
        // Its own host: the capture must hold only this flow's entries.
        using var factory = new PortalFactory();
        var seeded = await factory.SeedAsync();
        var fileMarker = $"FILE-CONTENT-{Guid.NewGuid():N}";
        var badToken = NewToken();

        // Staff side: the password and the staff JWT are in play too.
        var staff = factory.CreateClient();
        var staffJwt = await staff.AuthenticateAsStaffAsync();

        // The whole portal flow, including the failure paths a client could hit.
        var client = factory.CreateClient();
        var wrongExchange = await client.PostAsJsonAsync("/api/portal/exchange", new { token = badToken });
        Assert.Equal(HttpStatusCode.Unauthorized, wrongExchange.StatusCode);

        var session = await client.ExchangeAsync(seeded.Token);
        client.DefaultRequestHeaders.Authorization = new("Portal", session.SessionToken);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/portal/request")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.UploadAsync(seeded.ItemIds[0], "a.pdf", Pdf(fileMarker))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await client.UploadAsync(seeded.ItemIds[1], "a.pdf", Exe())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.UploadAsync(Guid.NewGuid(), "a.pdf", Pdf(fileMarker))).StatusCode);

        // A credential of the wrong kind, and a forged one, so the rejection paths log too.
        var wrongKind = new HttpRequestMessage(HttpMethod.Get, "/api/portal/request");
        wrongKind.Headers.Authorization = new("Bearer", staffJwt);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(wrongKind)).StatusCode);

        var forged = MintJwt(seeded.RequestId, key: "a-different-key-that-is-also-32-chars-long");
        var forgedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/portal/request");
        forgedRequest.Headers.Authorization = new("Portal", forged);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(forgedRequest)).StatusCode);

        var portalOnStaff = new HttpRequestMessage(HttpMethod.Get, "/api/requests");
        portalOnStaff.Headers.Authorization = new("Portal", session.SessionToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(portalOnStaff)).StatusCode);

        // The capture is real: it saw the host work, at the lowest level, before anything is
        // asserted about what it did not see.
        var entries = factory.Logs.Entries;
        Assert.True(entries.Count > 20, $"Only {entries.Count} log entries captured; the provider is not attached.");
        Assert.Contains(entries, e => e.StartsWith("[Trace]", StringComparison.Ordinal)
                                      || e.StartsWith("[Debug]", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Contains("/api/portal/exchange", StringComparison.Ordinal));

        var hash = HashToken(seeded.Token);
        factory.Logs.AssertNoneContain(seeded.Token, "magic-link token");
        factory.Logs.AssertNoneContain(badToken, "rejected magic-link token");
        factory.Logs.AssertNoneContain(hash, "magic-link token hash");
        factory.Logs.AssertNoneContain(hash.ToUpperInvariant(), "magic-link token hash");
        factory.Logs.AssertNoneContain(session.SessionToken, "portal session token");
        factory.Logs.AssertNoneContain(session.SessionToken.Split('.')[1], "portal session token payload");
        factory.Logs.AssertNoneContain(forged, "forged session token");
        factory.Logs.AssertNoneContain(staffJwt, "staff JWT");
        factory.Logs.AssertNoneContain(ApiFactory.DemoPassword, "password");
        factory.Logs.AssertNoneContain(fileMarker, "uploaded file content");

        // Nor the storage key of the stored file (CLAUDE.md rule 11 and the audit rule).
        var key = await factory.WithDbAsync(async db => (await db.UploadedFiles.SingleAsync()).StorageKey);
        factory.Logs.AssertNoneContain(key, "storage key");
        factory.Logs.AssertNoneContain(key.Split('/')[^1], "storage key");
    }
}
