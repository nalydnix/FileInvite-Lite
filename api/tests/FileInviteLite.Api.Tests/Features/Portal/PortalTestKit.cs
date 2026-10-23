using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace FileInviteLite.Api.Tests.Features.Portal;

/// <summary>
/// The host for portal tests: the exchange limit is raised so a class not testing the limiter cannot
/// trip it, and every log entry at every level is captured (S2.13).
/// </summary>
public class PortalFactory : ApiFactory
{
    public LogCapture Logs { get; } = new();

    protected override IDictionary<string, string?> Configuration =>
        new Dictionary<string, string?> { ["RateLimits:ExchangePermitLimit"] = "1000" };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(Logs.AddTo);
    }

    /// <summary>Every file currently under the storage root, temporary files included.</summary>
    public string[] FilesOnDisk()
    {
        var root = Services.GetRequiredService<IConfiguration>()["Storage:RootPath"]!;
        return Directory.Exists(root)
            ? Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            : [];
    }
}

internal static class PortalTestKit
{
    public const string SigningKey = "test-signing-key-at-least-32-characters-long";
    public const string PortalIssuer = "fileinvite-lite-portal";

    public sealed record SeededRequest(
        Guid RequestId, string Token, string ClientName, IReadOnlyList<Guid> ItemIds);

    // ---- tokens ---------------------------------------------------------------------------

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The storage format from ADR 0004: SHA-256 of the token, hex. Done inline because the shared
    /// token service belongs to task 011.
    /// </summary>
    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ---- seeding --------------------------------------------------------------------------

    /// <summary>
    /// Creates a request with a magic link directly in the database. The link row is built here
    /// because sending a request is task 011's endpoint, not this one's.
    /// </summary>
    public static Task<SeededRequest> SeedAsync(
        this ApiFactory factory,
        string clientName = "Jane Client",
        ItemStatus[]? itemStatuses = null,
        RequestStatus status = RequestStatus.Sent,
        DateTime? linkExpiresAt = null,
        bool linkRevoked = false,
        DateOnly? dueDate = null)
    {
        var token = NewToken();
        var statuses = itemStatuses ?? [ItemStatus.Outstanding, ItemStatus.Outstanding];

        return factory.WithDbAsync(async db =>
        {
            var staffId = await db.StaffUsers.Where(u => u.Email == ApiFactory.DemoEmail)
                .Select(u => u.Id).SingleAsync();

            var request = new DocumentRequest
            {
                ClientName = clientName,
                ClientEmail = "client@example.com",
                DueDate = dueDate ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14),
                Status = status,
                CreatedBy = staffId,
                CreatedAt = DateTime.UtcNow,
            };

            for (var i = 0; i < statuses.Length; i++)
            {
                request.Items.Add(new ChecklistItem
                {
                    RequestId = request.Id,
                    Title = $"{clientName} item {i + 1}",
                    Description = i == 0 ? $"{clientName} description" : null,
                    Required = i == 0,
                    Status = statuses[i],
                });
            }

            request.MagicLinks.Add(new MagicLink
            {
                RequestId = request.Id,
                TokenHash = HashToken(token),
                ExpiresAt = linkExpiresAt ?? DateTime.UtcNow.AddDays(14),
                RevokedAt = linkRevoked ? DateTime.UtcNow.AddMinutes(-5) : null,
            });

            db.DocumentRequests.Add(request);
            await db.SaveChangesAsync();

            return new SeededRequest(request.Id, token, clientName, request.Items.Select(i => i.Id).ToList());
        });
    }

    /// <summary>Adds a file row directly, for the cases that need an earlier upload to exist.</summary>
    public static Task<Guid> SeedFileAsync(
        this ApiFactory factory, Guid itemId, string originalName, DateTime uploadedAt) =>
        factory.WithDbAsync(async db =>
        {
            var file = new UploadedFile
            {
                ChecklistItemId = itemId,
                OriginalName = originalName,
                ContentType = "application/pdf",
                SizeBytes = 10,
                Sha256 = new string('a', 64),
                StorageKey = $"2020/01/{Guid.NewGuid():N}",
                UploadedAt = uploadedAt,
            };
            db.UploadedFiles.Add(file);
            await db.SaveChangesAsync();
            return file.Id;
        });

    public static Task<int> ExecAsync(this ApiFactory factory, string sql) =>
        factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql));

    // ---- exchange and clients -------------------------------------------------------------

    public sealed record Exchanged(string SessionToken, DateTime ExpiresAt, Guid RequestId);

    public static async Task<Exchanged> ExchangeAsync(this HttpClient client, string token)
    {
        var response = await client.PostAsJsonAsync("/api/portal/exchange", new { token });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new Exchanged(
            body.GetProperty("sessionToken").GetString()!,
            body.GetProperty("expiresAt").GetDateTime(),
            body.GetProperty("requestId").GetGuid());
    }

    /// <summary>A client holding a portal session for the request the token belongs to.</summary>
    public static async Task<(HttpClient Client, Exchanged Session)> PortalClientAsync(
        this ApiFactory factory, string magicToken)
    {
        var client = factory.CreateClient();
        var session = await client.ExchangeAsync(magicToken);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Portal", session.SessionToken);
        return (client, session);
    }

    public static HttpRequestMessage Request(HttpMethod method, string url, string? scheme = null, string? credential = null)
    {
        var message = new HttpRequestMessage(method, url);
        if (scheme is not null)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue(scheme, credential);
        }

        return message;
    }

    // ---- minted tokens (for forgery tests) -------------------------------------------------

    public static string MintJwt(
        Guid? requestId,
        string issuer = PortalIssuer,
        string audience = PortalIssuer,
        string key = SigningKey,
        DateTime? expires = null,
        string? requestIdValue = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()) };
        if (requestIdValue is not null)
        {
            claims.Add(new Claim("request_id", requestIdValue));
        }
        else if (requestId is { } id)
        {
            claims.Add(new Claim("request_id", id.ToString()));
        }

        var exp = expires ?? now.AddMinutes(30);
        var token = new JwtSecurityToken(
            issuer, audience, claims,
            notBefore: exp.AddMinutes(-30) < now ? exp.AddMinutes(-30) : now,
            expires: exp,
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ---- uploads --------------------------------------------------------------------------

    public static MultipartFormDataContent FileForm(string fileName, byte[] content)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        return form;
    }

    public static Task<HttpResponseMessage> UploadAsync(
        this HttpClient client, Guid itemId, string fileName, byte[] content) =>
        client.PostAsync($"/api/portal/items/{itemId}/files", FileForm(fileName, content));

    // ---- response helpers -----------------------------------------------------------------

    /// <summary>A problem body with the per-request <c>traceId</c> removed, for byte comparison.</summary>
    public static async Task<string> NormalisedBodyAsync(HttpResponseMessage response)
    {
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.False(string.IsNullOrWhiteSpace(node["traceId"]?.GetValue<string>()));
        node.Remove("traceId");
        return node.ToJsonString();
    }

    public static void AssertNoReferrer(HttpResponseMessage response) =>
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));

    // ---- file fixtures (real magic bytes) -------------------------------------------------

    public static byte[] Pdf(string marker = "pdf-body") =>
        Encoding.ASCII.GetBytes($"%PDF-1.4\n1 0 obj\n<< /Note ({marker}) >>\nendobj\ntrailer\n<< >>\n%%EOF\n");

    public static byte[] Png() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1];

    public static byte[] Jpeg() =>
        [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0];

    public static byte[] Docx() => Zip("[Content_Types].xml", "word/document.xml");

    public static byte[] Xlsx() => Zip("[Content_Types].xml", "xl/workbook.xml");

    /// <summary>A real ZIP container holding the named entries, and nothing that makes it an Office file.</summary>
    public static byte[] Zip(params string[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("<x/>");
            }
        }

        return stream.ToArray();
    }

    /// <summary>A Windows executable: the MZ header and the DOS stub text. Nothing like a PDF.</summary>
    public static byte[] Exe()
    {
        var bytes = new byte[512];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        Encoding.ASCII.GetBytes("This program cannot be run in DOS mode.").CopyTo(bytes, 0x4E);
        return bytes;
    }

    /// <summary>A PDF of exactly <paramref name="totalBytes"/>: a valid header, then zeros.</summary>
    public static byte[] PdfOfSize(long totalBytes)
    {
        var bytes = new byte[totalBytes];
        Encoding.ASCII.GetBytes("%PDF-1.4\n").CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>
    /// A request body that claims a short Content-Length while sending far more. The wrapped
    /// multipart content supplies the boundary and the real bytes; only the length header lies (S3.7).
    /// </summary>
    public sealed class LyingLengthContent : HttpContent
    {
        private readonly HttpContent _inner;

        public LyingLengthContent(HttpContent inner, long claimedLength)
        {
            _inner = inner;
            Headers.ContentType = inner.Headers.ContentType;
            Headers.ContentLength = claimedLength;
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            _inner.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength ?? 0;
            return true;
        }
    }
}
