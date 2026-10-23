using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace FileInviteLite.Api.Tests.Infrastructure;

public static class AuthHelpers
{
    public sealed record LoginResponse(string Token, DateTime ExpiresAt, string Name);

    /// <summary>Logs in as the seeded demo user and returns the raw response.</summary>
    public static Task<HttpResponseMessage> LoginAsync(
        this HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password });

    /// <summary>Logs in as the seeded demo user and attaches the bearer token to the client.</summary>
    public static async Task<string> AuthenticateAsStaffAsync(this HttpClient client)
    {
        var response = await client.LoginAsync(ApiFactory.DemoEmail, ApiFactory.DemoPassword);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>()
                   ?? throw new InvalidOperationException("Login returned an empty body.");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.Token);
        return body.Token;
    }
}
