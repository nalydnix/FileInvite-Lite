using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FileInviteLite.Api.Tests.Features.Requests;

/// <summary>
/// The host for the send and read-back tests: a known portal base URL and link lifetime, and every
/// log entry at every level captured, so the token can be hunted for in logs (ADR 0004 §12).
/// </summary>
public sealed class SendFactory : ApiFactory
{
    public const string BaseUrl = "https://app.test.example";
    public const int LifetimeDays = 7;

    public LogCapture Logs { get; } = new();

    protected override IDictionary<string, string?> Configuration => new Dictionary<string, string?>
    {
        ["Portal:BaseUrl"] = BaseUrl + "/",   // the trailing slash is deliberate: it must not double up
        ["Portal:MagicLinkLifetimeDays"] = LifetimeDays.ToString(),
        ["RateLimits:ExchangePermitLimit"] = "1000",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(Logs.AddTo);
    }
}

internal static class SendRequestTestKit
{
    public const string FragmentMarker = "#t=";

    public static Task<HttpResponseMessage> SendAsync(this HttpClient client, Guid id) =>
        client.PostAsync($"/api/requests/{id}/send", content: null);

    public sealed record Sent(string Url, string Token, DateTime ExpiresAt);

    /// <summary>Sends and returns the link, failing the test if it was not a 200.</summary>
    public static async Task<Sent> SendOkAsync(this HttpClient client, Guid id)
    {
        var response = await client.SendAsync(id);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var url = body.GetProperty("url").GetString()!;
        return new Sent(url, TokenFromUrl(url), body.GetProperty("expiresAt").GetDateTime());
    }

    public static string TokenFromUrl(string url) =>
        url[(url.IndexOf(FragmentMarker, StringComparison.Ordinal) + FragmentMarker.Length)..];

    /// <summary>The storage format from ADR 0004: SHA-256 of the token, lowercase hex.</summary>
    public static string Sha256Hex(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static Task<JsonElement> GetJsonAsync(this HttpClient client, string url) =>
        client.GetFromJsonAsync<JsonElement>(url);

    public static string[] PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Every value in every table, as text, so "the database never holds the token" is checked
    /// against the whole file and not just the columns we expect it in (S2.2).
    /// </summary>
    public static async Task<List<string>> EveryStoredStringAsync(this ApiFactory factory)
    {
        return await factory.WithDbAsync(async db =>
        {
            var connection = (SqliteConnection)db.Database.GetDbConnection();
            await connection.OpenAsync();
            var values = new List<string>();

            var tables = new List<string>();
            await using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                await using var reader = await list.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    tables.Add(reader.GetString(0));
                }
            }

            foreach (var table in tables)
            {
                await using var select = connection.CreateCommand();
                select.CommandText = $"SELECT * FROM \"{table}\"";
                await using var reader = await select.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        if (!reader.IsDBNull(i))
                        {
                            values.Add(Convert.ToString(
                                reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "");
                        }
                    }
                }
            }

            return values;
        });
    }
}
