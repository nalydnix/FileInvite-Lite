using System.Net;
using System.Net.Http.Json;
using FileInviteLite.Api.Tests.Infrastructure;

namespace FileInviteLite.Api.Tests.Features.Health;

public sealed class GetHealthTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_IsAnonymousAndReportsHealthy_S10_5()
    {
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.NotNull(body);
        Assert.Equal("healthy", body["status"]);
    }

    [Fact]
    public async Task Health_RevealsNothingBeyondStatus_S10_5()
    {
        var raw = await factory.CreateClient().GetStringAsync("/health");

        // The endpoint is unauthenticated, so anything it returns is public. Version numbers,
        // environment names and dependency detail are all reconnaissance.
        Assert.DoesNotContain("version", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Development", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Data Source", raw, StringComparison.OrdinalIgnoreCase);
    }
}
