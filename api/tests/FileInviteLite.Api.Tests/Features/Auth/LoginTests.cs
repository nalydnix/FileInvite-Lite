using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FileInviteLite.Api.Tests.Features.Auth;

public sealed class LoginTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Login_WithCorrectCredentials_ReturnsJwtExpiringIn60Minutes_S8_1()
    {
        var response = await factory.CreateClient()
            .LoginAsync(ApiFactory.DemoEmail, ApiFactory.DemoPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuthHelpers.LoginResponse>();
        Assert.NotNull(body);
        Assert.NotEmpty(body.Token);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.Token);
        var lifetime = jwt.ValidTo - jwt.ValidFrom;
        Assert.Equal(60, lifetime.TotalMinutes, precision: 0);
    }

    [Fact]
    public async Task Login_WrongPasswordAndUnknownEmail_AreIndistinguishable_S8_2()
    {
        var client = factory.CreateClient();

        var wrongPassword = await client.LoginAsync(ApiFactory.DemoEmail, "not-the-password");
        var unknownEmail = await client.LoginAsync("nobody@test.local", "not-the-password");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);

        // Identical bodies, so the endpoint cannot be used to discover which addresses are real.
        // traceId is excluded: it is per-request by design (S10.1) and carries no information
        // about the account. Everything else — type, title, status, detail — must match exactly.
        Assert.Equal(
            await WithoutTraceIdAsync(wrongPassword),
            await WithoutTraceIdAsync(unknownEmail));
    }

    private static async Task<string> WithoutTraceIdAsync(HttpResponseMessage response)
    {
        var fields = await response.Content.ReadFromJsonAsync<SortedDictionary<string, JsonElement>>();
        Assert.NotNull(fields);

        Assert.True(fields.Remove("traceId"), "Every problem response must carry a traceId (S10.1).");

        return JsonSerializer.Serialize(fields);
    }

    [Fact]
    public async Task Login_MissingFields_ReturnsValidationProblem()
    {
        var response = await factory.CreateClient().LoginAsync("", "");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Login_Success_WritesStaffLoggedInAuditEvent_S8_8()
    {
        await factory.CreateClient().AuthenticateAsStaffAsync();

        var events = await factory.WithDbAsync(db => db.AuditEvents
            .Where(e => e.Action == AuditAction.StaffLoggedIn)
            .ToListAsync());

        Assert.NotEmpty(events);
        Assert.All(events, e => Assert.Equal(ActorType.Staff, e.ActorType));
    }

    [Fact]
    public async Task Login_Failure_WritesAuditEventWithEmailButNeverThePassword_S8_8()
    {
        const string password = "a-very-distinctive-wrong-password";
        await factory.CreateClient().LoginAsync("someone@test.local", password);

        var failures = await factory.WithDbAsync(db => db.AuditEvents
            .Where(e => e.Action == AuditAction.StaffLoginFailed)
            .ToListAsync());

        var failure = Assert.Single(failures, e => e.Metadata!.Contains("someone@test.local"));
        Assert.DoesNotContain(password, failure.Metadata);
    }

    [Fact]
    public async Task Login_NeverReturnsThePasswordHash_S8_3()
    {
        var response = await factory.CreateClient()
            .LoginAsync(ApiFactory.DemoEmail, ApiFactory.DemoPassword);

        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("hash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiFactory.DemoPassword, raw, StringComparison.Ordinal);
    }
}
