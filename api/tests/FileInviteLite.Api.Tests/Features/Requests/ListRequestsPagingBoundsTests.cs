using System.Net;
using FileInviteLite.Api.Tests.Infrastructure;
using static FileInviteLite.Api.Tests.Features.Requests.RequestsTestKit;

namespace FileInviteLite.Api.Tests.Features.Requests;

public sealed class ListRequestsPagingBoundsTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("page=2147483647&pageSize=20")]
    [InlineData("page=21474837&pageSize=100")]
    public async Task List_PageThatWouldOverflowTheOffset_Returns400(string query)
    {
        var client = factory.CreateClient();
        await client.AuthenticateAsStaffAsync();

        var response = await client.GetAsync("/api/requests?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertValidationProblemAsync(response, "page");
    }
}
