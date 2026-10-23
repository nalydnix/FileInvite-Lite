using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Services.Storage;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FileInviteLite.Api.Tests.Features.Review;

/// <summary>
/// The host for review tests. A webhook URL switches the completion webhook on; with none, the
/// setting is absent altogether (S7.1).
/// </summary>
public sealed class ReviewFactory(string? webhookUrl = null) : ApiFactory
{
    public const string ConfiguredWebhookUrl = "https://hooks.example.test/fileinvite";

    public LogCapture Logs { get; } = new();

    protected override IDictionary<string, string?> Configuration
    {
        get
        {
            var settings = new Dictionary<string, string?> { ["RateLimits:ExchangePermitLimit"] = "1000" };
            if (webhookUrl is not null)
            {
                settings["Webhook:Url"] = webhookUrl;
            }

            return settings;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureLogging(Logs.AddTo);
    }

    public string StorageRoot => Services.GetRequiredService<IConfiguration>()["Storage:RootPath"]!;
}

internal static class ReviewTestKit
{
    public static readonly byte[] PdfBytes =
        Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Note (review-body-marker) >>\nendobj\n%%EOF\n");

    /// <param name="Status">The item's status.</param>
    /// <param name="Required">Whether the item is required.</param>
    /// <param name="WithFile">Whether a file row (and blob) exists for the item.</param>
    public sealed record ItemSpec(ItemStatus Status, bool Required = true, bool WithFile = true);

    public sealed record SeededItem(Guid Id, Guid? FileId, string? StorageKey);

    public sealed record Seeded(Guid RequestId, IReadOnlyList<SeededItem> Items);

    public static async Task<HttpClient> StaffClientAsync(this ApiFactory factory)
    {
        var client = factory.CreateClient();
        await client.AuthenticateAsStaffAsync();
        return client;
    }

    public static async Task<Seeded> SeedReviewAsync(
        this ApiFactory factory,
        ItemSpec[] items,
        RequestStatus status = RequestStatus.InProgress,
        string originalName = "passport.pdf",
        string contentType = "application/pdf")
    {
        var storage = factory.Services.GetRequiredService<IFileStorage>();

        return await factory.WithDbAsync(async db =>
        {
            var staffId = await db.StaffUsers.Where(u => u.Email == ApiFactory.DemoEmail)
                .Select(u => u.Id).SingleAsync();

            var request = new DocumentRequest
            {
                ClientName = "Jane Client",
                ClientEmail = "jane@example.com",
                DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14),
                Status = status,
                CreatedBy = staffId,
                CreatedAt = DateTime.UtcNow,
            };

            var seeded = new List<SeededItem>();
            for (var i = 0; i < items.Length; i++)
            {
                var spec = items[i];
                var item = new ChecklistItem
                {
                    RequestId = request.Id,
                    Title = $"Item {i + 1}",
                    Required = spec.Required,
                    Status = spec.Status,
                };
                request.Items.Add(item);

                Guid? fileId = null;
                string? key = null;
                if (spec.WithFile)
                {
                    using var content = new MemoryStream(PdfBytes);
                    key = await storage.SaveAsync(content, CancellationToken.None);
                    var file = new UploadedFile
                    {
                        ChecklistItemId = item.Id,
                        OriginalName = originalName,
                        ContentType = contentType,
                        SizeBytes = PdfBytes.Length,
                        Sha256 = Convert.ToHexStringLower(SHA256.HashData(PdfBytes)),
                        StorageKey = key,
                        UploadedAt = DateTime.UtcNow,
                    };
                    item.Files.Add(file);
                    fileId = file.Id;
                }

                seeded.Add(new SeededItem(item.Id, fileId, key));
            }

            db.DocumentRequests.Add(request);
            await db.SaveChangesAsync();
            return new Seeded(request.Id, seeded);
        });
    }

    public static Task<HttpResponseMessage> ApproveAsync(this HttpClient client, Guid itemId, object? body = null) =>
        client.PostAsJsonAsync($"/api/items/{itemId}/approve", body ?? new { });

    public static Task<HttpResponseMessage> RejectAsync(this HttpClient client, Guid itemId, object body) =>
        client.PostAsJsonAsync($"/api/items/{itemId}/reject", body);

    public static Task<int> ExecAsync(this ApiFactory factory, string sql) =>
        factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql));

    public static async Task<JsonElement> JsonBodyAsync(this HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());

    public static Task<int> AuditCountAsync(this ApiFactory factory, string action) =>
        factory.WithDbAsync(db => db.AuditEvents.CountAsync(e => e.Action == action));
}
