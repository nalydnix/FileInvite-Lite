using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FileInviteLite.Api.Tests.Features.Portal.PortalTestKit;
using static FileInviteLite.Api.Tests.Infrastructure.AuthHelpers;

namespace FileInviteLite.Api.Tests.Features.Portal;

/// <summary>
/// The tests that matter most: one magic link, one request, and no credential of one kind
/// working where the other belongs (S2.7, S2.8, S2.9).
/// </summary>
public sealed class PortalScopingTests : IDisposable
{
    private readonly PortalFactory _factory;

    // A host per test: these tests assert absolute counts of rows and files on disk.
    public PortalScopingTests() => _factory = new PortalFactory();

    public void Dispose() => _factory.Dispose();

    // ---- S2.7: a portal credential on a staff endpoint ------------------------------------

    [Theory]
    [InlineData("Portal")]
    [InlineData("Bearer")]
    public async Task StaffEndpoints_RejectAPortalSessionToken_S2_7(string scheme)
    {
        var seeded = await _factory.SeedAsync();
        var (_, session) = await _factory.PortalClientAsync(seeded.Token);

        foreach (var (method, url) in new[] { (HttpMethod.Get, "/api/requests"), (HttpMethod.Post, "/api/requests") })
        {
            var response = await _factory.CreateClient().SendAsync(
                Request(method, url, scheme, session.SessionToken));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task StaffEndpoints_RejectTheMagicLinkTokenItself_S2_7()
    {
        var seeded = await _factory.SeedAsync();

        foreach (var scheme in new[] { "Portal", "Bearer" })
        {
            var response = await _factory.CreateClient().SendAsync(
                Request(HttpMethod.Get, "/api/requests", scheme, seeded.Token));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // ---- S2.8: a staff credential on a portal endpoint ------------------------------------

    [Theory]
    [InlineData("Bearer")]
    [InlineData("Portal")]
    public async Task PortalEndpoints_RejectAStaffJwt_S2_8(string scheme)
    {
        var seeded = await _factory.SeedAsync();
        var staff = _factory.CreateClient();
        var jwt = await staff.AuthenticateAsStaffAsync();

        var get = await _factory.CreateClient().SendAsync(
            Request(HttpMethod.Get, "/api/portal/request", scheme, jwt));
        Assert.Equal(HttpStatusCode.Unauthorized, get.StatusCode);

        var upload = Request(HttpMethod.Post, $"/api/portal/items/{seeded.ItemIds[0]}/files", scheme, jwt);
        upload.Content = FileForm("a.pdf", Pdf());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().SendAsync(upload)).StatusCode);

        // Nothing was stored by the rejected attempt.
        Assert.Empty(_factory.FilesOnDisk());
        Assert.Equal(0, await _factory.WithDbAsync(db => db.UploadedFiles.CountAsync()));
    }

    [Fact]
    public async Task PortalEndpoints_RejectMissingAndGarbageCredentials_S2_8()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/portal/request")).StatusCode);

        foreach (var header in new[] { "Portal", "Portal ", "Portal garbage", "Portal a.b.c", "Basic abc", "Bearer garbage" })
        {
            var message = new HttpRequestMessage(HttpMethod.Get, "/api/portal/request");
            message.Headers.TryAddWithoutValidation("Authorization", header);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(message)).StatusCode);
        }
    }

    // ---- forged and malformed session tokens ----------------------------------------------

    [Fact]
    public async Task PortalEndpoints_RejectForgedExpiredAndMismatchedSessionTokens_S2_8()
    {
        var seeded = await _factory.SeedAsync();
        var client = _factory.CreateClient();

        var forgeries = new Dictionary<string, string>
        {
            ["wrong signing key"] = MintJwt(seeded.RequestId, key: "a-different-key-that-is-also-32-chars-long"),
            ["expired"] = MintJwt(seeded.RequestId, expires: DateTime.UtcNow.AddHours(-2)),
            ["staff issuer and audience"] = MintJwt(seeded.RequestId, "fileinvite-lite-tests", "fileinvite-lite-tests"),
            ["wrong audience"] = MintJwt(seeded.RequestId, audience: "somebody-else"),
            ["no request id"] = MintJwt(requestId: null),
            ["request id is not a guid"] = MintJwt(null, requestIdValue: "not-a-guid"),
            ["empty request id"] = MintJwt(null, requestIdValue: Guid.Empty.ToString()),
            ["unsigned"] = UnsignedJwt(seeded.RequestId),
        };

        foreach (var (name, token) in forgeries)
        {
            var response = await client.SendAsync(Request(HttpMethod.Get, "/api/portal/request", "Portal", token));
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{name} was accepted: {response.StatusCode}");
        }
    }

    private static string UnsignedJwt(Guid requestId)
    {
        static string B64(string s) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        return $"{B64("""{"alg":"none","typ":"JWT"}""")}.{B64($$"""{"iss":"{{PortalIssuer}}","aud":"{{PortalIssuer}}","exp":{{exp}},"request_id":"{{requestId}}"}""")}.";
    }

    // ---- S2.9: session for request A, everything belonging to request B -------------------

    private sealed record TwoRequests(
        SeededRequest A, SeededRequest B, HttpClient ClientForA, Guid BFileId);

    private async Task<TwoRequests> TwoRequestsAsync(ItemStatus[]? bStatuses = null)
    {
        var a = await _factory.SeedAsync("Alice Aardvark");
        var b = await _factory.SeedAsync("Bertram Bellwether", itemStatuses: bStatuses);
        var bFile = await _factory.SeedFileAsync(b.ItemIds[0], "bertrams-passport.pdf", DateTime.UtcNow.AddHours(-1));
        var (client, _) = await _factory.PortalClientAsync(a.Token);
        return new TwoRequests(a, b, client, bFile);
    }

    [Fact]
    public async Task Upload_ToAnotherRequestsItem_Returns404AndStoresNothing_S2_9()
    {
        var t = await TwoRequestsAsync();
        var bItemBefore = await _factory.WithDbAsync(db =>
            db.ChecklistItems.AsNoTracking().SingleAsync(i => i.Id == t.B.ItemIds[0]));
        var filesBefore = await _factory.WithDbAsync(db => db.UploadedFiles.CountAsync());

        var cross = await t.ClientForA.UploadAsync(t.B.ItemIds[0], "a.pdf", Pdf());
        var missing = await t.ClientForA.UploadAsync(Guid.NewGuid(), "a.pdf", Pdf());

        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // "Belongs to someone else" and "does not exist" are indistinguishable.
        Assert.Equal(await NormalisedBodyAsync(missing), await NormalisedBodyAsync(cross));

        Assert.Empty(_factory.FilesOnDisk());
        Assert.Equal(filesBefore, await _factory.WithDbAsync(db => db.UploadedFiles.CountAsync()));
        var bItemAfter = await _factory.WithDbAsync(db =>
            db.ChecklistItems.AsNoTracking().SingleAsync(i => i.Id == t.B.ItemIds[0]));
        Assert.Equal(bItemBefore.Status, bItemAfter.Status);
        Assert.Equal(0, await _factory.WithDbAsync(db =>
            db.AuditEvents.CountAsync(e => e.Action == AuditAction.ItemUploaded)));
    }

    [Fact]
    public async Task Upload_ToAnotherRequestsItem_Returns404BeforeAnyOtherCheck_S2_9()
    {
        // B's item is Approved (which would be 409 on the owner) and the file is the wrong type
        // (415 for the owner). A session for A must learn neither: scope is checked first.
        var t = await TwoRequestsAsync([ItemStatus.Approved, ItemStatus.Outstanding]);

        var approved = await t.ClientForA.UploadAsync(t.B.ItemIds[0], "a.pdf", Pdf());
        var wrongType = await t.ClientForA.UploadAsync(t.B.ItemIds[1], "a.exe", Exe());
        var disguised = await t.ClientForA.UploadAsync(t.B.ItemIds[1], "a.pdf", Exe());

        Assert.Equal(HttpStatusCode.NotFound, approved.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, wrongType.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, disguised.StatusCode);
    }

    [Fact]
    public async Task GetRequest_ReturnsOnlyTheSessionsRequest_S2_9()
    {
        var t = await TwoRequestsAsync();

        // Every way a caller might try to name another request: query, header, and extra route text.
        var attempts = new[]
        {
            "/api/portal/request",
            $"/api/portal/request?requestId={t.B.RequestId}",
            $"/api/portal/request?id={t.B.RequestId}&itemId={t.B.ItemIds[0]}",
        };

        foreach (var url in attempts)
        {
            var message = Request(HttpMethod.Get, url);
            message.Headers.Add("X-Request-Id", t.B.RequestId.ToString());
            var response = await t.ClientForA.SendAsync(message);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync();
            Assert.Contains("Alice Aardvark", raw);

            Assert.DoesNotContain("Bertram", raw);
            Assert.DoesNotContain("bertrams-passport", raw);
            foreach (var id in t.B.ItemIds.Append(t.B.RequestId).Append(t.BFileId))
            {
                Assert.DoesNotContain(id.ToString(), raw, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The request id is not a route segment on any portal endpoint, so these are simply absent.
        foreach (var url in new[] { $"/api/portal/request/{t.B.RequestId}", $"/api/portal/requests/{t.B.RequestId}" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await t.ClientForA.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task Files_OfAnotherRequest_Return404_S2_9()
    {
        var t = await TwoRequestsAsync();

        var paths = new[]
        {
            $"/api/portal/files/{t.BFileId}",
            $"/api/portal/items/{t.B.ItemIds[0]}/files/{t.BFileId}",
            $"/api/portal/items/{t.B.ItemIds[0]}/files/{t.BFileId}/download",
            $"/api/portal/items/{t.B.ItemIds[0]}",
        };

        foreach (var path in paths)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await t.ClientForA.GetAsync(path)).StatusCode);
        }

        // Nor can another request's file be replaced or removed by other verbs.
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete, HttpMethod.Patch })
        {
            var response = await t.ClientForA.SendAsync(
                Request(method, $"/api/portal/items/{t.B.ItemIds[0]}/files/{t.BFileId}"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal(1, await _factory.WithDbAsync(db => db.UploadedFiles.CountAsync()));
    }

    [Fact]
    public async Task EachSessionSeesItsOwnRequest_AndExchangeNeverCrossesRequests_S2_9()
    {
        var t = await TwoRequestsAsync();
        var (clientForB, sessionB) = await _factory.PortalClientAsync(t.B.Token);

        Assert.Equal(t.B.RequestId, sessionB.RequestId);

        var b = await clientForB.GetFromJsonAsync<JsonElement>("/api/portal/request");
        Assert.Equal("Bertram Bellwether", b.GetProperty("clientName").GetString());

        // B's session cannot reach A's item either.
        Assert.Equal(HttpStatusCode.NotFound,
            (await clientForB.UploadAsync(t.A.ItemIds[0], "a.pdf", Pdf())).StatusCode);
    }

    // ---- structure: no portal endpoint can take a request id, and each demands the portal policy ----

    [Fact]
    public void EveryPortalEndpoint_RequiresThePortalPolicy_AndTakesNoRequestId_S2_9()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith("/api/portal", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // exchange, request, upload: a new portal endpoint must be a conscious change to this test.
        Assert.True(endpoints.Count >= 3, "Expected the three portal endpoints to be mapped.");

        foreach (var endpoint in endpoints)
        {
            var route = endpoint.RoutePattern.RawText!;
            var parameters = endpoint.RoutePattern.Parameters.Select(p => p.Name).ToList();

            // No portal route takes the request id (or any generic id that could be one).
            Assert.DoesNotContain(parameters, p => p.Contains("request", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(parameters, p => p.Equals("id", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("{requestId", route, StringComparison.OrdinalIgnoreCase);

            var authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            var allowsAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;

            if (route.Equals("/api/portal/exchange", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(allowsAnonymous);
                continue;
            }

            Assert.False(allowsAnonymous, $"{route} must not be anonymous.");
            Assert.Contains(authorize, a => a.Policy == AuthPolicies.Portal);
            Assert.DoesNotContain(authorize, a => a.Policy == AuthPolicies.Staff);

            // No portal route serves file content (S3.14): the only file route is the upload.
            if (route.Contains("/files", StringComparison.OrdinalIgnoreCase))
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods;
                Assert.Equal(["POST"], methods);
            }
        }
    }
}
