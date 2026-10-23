using System.Net;
using FileInviteLite.Api.Tests.Infrastructure;

namespace FileInviteLite.Api.Tests.Features.Auth;

/// <summary>
/// Its own factory, with the production permit limit restored. Every other test class raises the
/// limit so an unrelated test cannot trip it; this one is the test.
/// </summary>
public sealed class LoginRateLimitTests : IClassFixture<LoginRateLimitTests.RateLimitedFactory>
{
    private readonly RateLimitedFactory _factory;

    public LoginRateLimitTests(RateLimitedFactory factory) => _factory = factory;

    public sealed class RateLimitedFactory : ApiFactory
    {
        protected override IDictionary<string, string?> Configuration =>
            new Dictionary<string, string?> { ["RateLimits:LoginPermitLimit"] = "5" };
    }

    [Fact]
    public async Task Login_PastFiveAttemptsInAMinute_Returns429_S8_4()
    {
        var client = _factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt < 7; attempt++)
        {
            var response = await client.LoginAsync(ApiFactory.DemoEmail, "wrong-password");
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }
}
