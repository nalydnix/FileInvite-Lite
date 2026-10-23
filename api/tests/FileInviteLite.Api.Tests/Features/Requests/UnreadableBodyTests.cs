using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using FileInviteLite.Api.Tests.Infrastructure;
using static FileInviteLite.Api.Tests.Features.Requests.RequestsTestKit;

namespace FileInviteLite.Api.Tests.Features.Requests;

/// <summary>
/// A body the framework cannot bind (malformed JSON, a date that will not parse, no date at all)
/// must leave in the shared problem shape with a fixed message — in Development and in
/// Production. The two differ in how ASP.NET reports a bad request (Development throws, so the
/// exception handler sees it; Production writes its own 400), so each is run explicitly: a pass in
/// Development proves nothing about Production (S10.1).
/// </summary>
public sealed class UnreadableBodyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    public static TheoryData<string, string> Cases => new()
    {
        { "Development", "{ not json" },
        { "Production", "{ not json" },
        { "Development", """{"clientName":"Jane","clientEmail":"jane@example.com","dueDate":"next tuesday","items":[]}""" },
        { "Production", """{"clientName":"Jane","clientEmail":"jane@example.com","dueDate":"next tuesday","items":[]}""" },
        { "Development", """{"clientName":"Jane","clientEmail":"jane@example.com","dueDate":"","items":[]}""" },
        { "Production", """{"clientName":"Jane","clientEmail":"jane@example.com","dueDate":"","items":[]}""" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Create_UnreadableBody_Returns400WithAFixedMessage_InEveryEnvironment_S10_1(
        string environment, string json)
    {
        using var host = factory.WithWebHostBuilder(b => b.UseEnvironment(environment));
        var client = host.CreateClient();
        await client.AuthenticateAsStaffAsync();

        var response = await client.PostAsync(
            "/api/requests", new StringContent(json, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var root = await AssertValidationProblemAsync(response, "body");

        // The parser's wording can quote the payload and name internal types; none of it may leave.
        var raw = root.GetRawText();
        foreach (var internals in new[] { "JsonException", "DateOnly", "System.", "Path:", "BadHttpRequest", "next tuesday" })
        {
            Assert.DoesNotContain(internals, raw, StringComparison.OrdinalIgnoreCase);
        }
    }
}
