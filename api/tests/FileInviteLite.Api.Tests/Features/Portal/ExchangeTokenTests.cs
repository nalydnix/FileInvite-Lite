using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;
using static FileInviteLite.Api.Tests.Features.Portal.PortalTestKit;

namespace FileInviteLite.Api.Tests.Features.Portal;

public sealed class ExchangeTokenTests : IClassFixture<PortalFactory>
{
    private readonly PortalFactory _factory;

    public ExchangeTokenTests(PortalFactory factory) => _factory = factory;

    [Fact]
    public async Task Exchange_WithAValidToken_ReturnsASessionAndTheRequestId_S2_3()
    {
        var seeded = await _factory.SeedAsync();
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/portal/exchange", new { token = seeded.Token });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(raw);

        Assert.Equal(seeded.RequestId, body.GetProperty("requestId").GetGuid());
        var session = body.GetProperty("sessionToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(session));

        // The long-lived credential is never echoed back.
        Assert.DoesNotContain(seeded.Token, raw, StringComparison.Ordinal);

        // 30 minutes, UTC.
        var expiresAt = body.GetProperty("expiresAt");
        Assert.EndsWith("Z", expiresAt.GetString());
        var lifetime = expiresAt.GetDateTime().ToUniversalTime() - DateTime.UtcNow;
        Assert.InRange(lifetime, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));

        // A signed token carrying exactly one request id, with the portal's own issuer and audience.
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(session);
        Assert.Equal(PortalIssuer, jwt.Issuer);
        Assert.Equal(PortalIssuer, Assert.Single(jwt.Audiences));
        Assert.Equal(seeded.RequestId.ToString(), Assert.Single(jwt.Claims, c => c.Type == "request_id").Value);

        // And it actually works against a portal endpoint.
        client.DefaultRequestHeaders.Authorization = new("Portal", session);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/portal/request")).StatusCode);
    }

    [Fact]
    public async Task Exchange_ReturnsSameBodyForExpiredRevokedUnknownAndMalformed_S2_4_S2_5_S2_6()
    {
        var expired = await _factory.SeedAsync(linkExpiresAt: DateTime.UtcNow.AddMinutes(-1));
        var revoked = await _factory.SeedAsync(linkRevoked: true);
        var client = _factory.CreateClient();

        var attempts = new Dictionary<string, string>
        {
            ["expired"] = expired.Token,
            ["revoked"] = revoked.Token,
            ["unknown"] = NewToken(),
            ["malformed"] = "not a token!! é <>",
            ["too long"] = new string('A', 5000),
            ["hash of a real token"] = HashToken(expired.Token),   // the stored value is not a credential
        };

        var results = new Dictionary<string, (HttpStatusCode Status, string? ContentType, string Body)>();
        foreach (var (name, token) in attempts)
        {
            var response = await client.PostAsJsonAsync("/api/portal/exchange", new { token });
            results[name] = (response.StatusCode, response.Content.Headers.ContentType?.MediaType,
                await NormalisedBodyAsync(response));
        }

        Assert.All(results, r => Assert.Equal(HttpStatusCode.Unauthorized, r.Value.Status));
        Assert.Single(results.Values.Select(r => r.ContentType).Distinct());
        Assert.Equal("application/problem+json", results["expired"].ContentType);

        var distinctBodies = results.Values.Select(r => r.Body).Distinct().ToList();
        Assert.True(distinctBodies.Count == 1,
            "Expired, revoked, unknown and malformed tokens must produce the same body:\n" +
            string.Join("\n", results.Select(r => $"{r.Key}: {r.Value.Body}")));
    }

    [Fact]
    public async Task Exchange_WithAMissingOrEmptyToken_Returns400_S2_6()
    {
        var client = _factory.CreateClient();

        foreach (var body in new[] { "{}", """{"token":""}""", """{"token":"   "}""", """{"token":null}""" })
        {
            var response = await client.PostAsync(
                "/api/portal/exchange", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task Exchange_WritesAPortalAccessedAuditEventWithoutTheToken_S2_11()
    {
        var seeded = await _factory.SeedAsync();
        var client = _factory.CreateClient();

        var session = await client.ExchangeAsync(seeded.Token);

        var events = await _factory.WithDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == AuditAction.PortalAccessed && e.ActorId == seeded.RequestId)
            .ToListAsync());

        var audit = Assert.Single(events);
        Assert.Equal(ActorType.Client, audit.ActorType);
        Assert.Equal(seeded.RequestId, audit.ActorId);
        Assert.Equal(seeded.RequestId, audit.EntityId);

        var everything = $"{audit.Metadata}|{audit.EntityType}|{audit.Action}";
        Assert.DoesNotContain(seeded.Token, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(HashToken(seeded.Token), everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(session.SessionToken, everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exchange_WithARejectedToken_WritesNoPortalAccessedEvent_S2_11()
    {
        var before = await _factory.WithDbAsync(db =>
            db.AuditEvents.CountAsync(e => e.Action == AuditAction.PortalAccessed));

        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/portal/exchange", new { token = NewToken() });

        var after = await _factory.WithDbAsync(db =>
            db.AuditEvents.CountAsync(e => e.Action == AuditAction.PortalAccessed));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Portal_EveryResponseCarriesNoReferrer_S2_12()
    {
        var seeded = await _factory.SeedAsync();
        var anonymous = _factory.CreateClient();

        // Exchange: success, 401 and 400.
        var ok = await anonymous.PostAsJsonAsync("/api/portal/exchange", new { token = seeded.Token });
        AssertNoReferrer(ok);
        AssertNoReferrer(await anonymous.PostAsJsonAsync("/api/portal/exchange", new { token = NewToken() }));
        AssertNoReferrer(await anonymous.PostAsJsonAsync("/api/portal/exchange", new { token = "" }));

        // The other portal endpoints, with and without a session.
        AssertNoReferrer(await anonymous.GetAsync("/api/portal/request"));

        var (client, _) = await _factory.PortalClientAsync(seeded.Token);
        AssertNoReferrer(await client.GetAsync("/api/portal/request"));
        AssertNoReferrer(await client.UploadAsync(seeded.ItemIds[0], "a.pdf", Pdf()));
        AssertNoReferrer(await client.UploadAsync(seeded.ItemIds[1], "a.exe", Exe()));
        AssertNoReferrer(await client.UploadAsync(Guid.NewGuid(), "a.pdf", Pdf()));
    }
}

/// <summary>Its own factory, with the production permit limit restored: this class is the limiter test.</summary>
public sealed class ExchangeRateLimitTests : IClassFixture<ExchangeRateLimitTests.RateLimitedFactory>
{
    private readonly RateLimitedFactory _factory;

    public ExchangeRateLimitTests(RateLimitedFactory factory) => _factory = factory;

    public sealed class RateLimitedFactory : PortalFactory
    {
        protected override IDictionary<string, string?> Configuration =>
            new Dictionary<string, string?> { ["RateLimits:ExchangePermitLimit"] = "10" };
    }

    [Fact]
    public async Task Exchange_PastTenAttemptsInAMinute_Returns429_S2_10()
    {
        var client = _factory.CreateClient();

        var responses = new List<HttpResponseMessage>();
        for (var attempt = 0; attempt < 13; attempt++)
        {
            responses.Add(await client.PostAsJsonAsync("/api/portal/exchange", new { token = NewToken() }));
        }

        var statuses = responses.Select(r => r.StatusCode).ToList();
        Assert.Equal(10, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));

        // The ten permitted attempts come first; nothing past the limit gets through.
        Assert.All(statuses.Take(10), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.All(statuses.Skip(10), s => Assert.Equal(HttpStatusCode.TooManyRequests, s));

        // A throttled response is still a portal response (S2.12).
        AssertNoReferrer(responses[^1]);
    }
}
