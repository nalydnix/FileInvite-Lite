using System.Net;

using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Tests.Features.Portal;
using FileInviteLite.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static FileInviteLite.Api.Tests.Features.Requests.RequestsTestKit;
using static FileInviteLite.Api.Tests.Features.Review.ReviewTestKit;

namespace FileInviteLite.Api.Tests.Features.Review;

public sealed class DownloadFileTests
{
    private static readonly ItemSpec Uploaded = new(ItemStatus.Uploaded);

    [Fact]
    public async Task Download_ReturnsBytesAsAttachmentWithStoredTypeAndName_S4_1()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([Uploaded], originalName: "passport.pdf");
        var client = await factory.StaffClientAsync();

        var response = await client.GetAsync($"/api/files/{seeded.Items[0].FileId}/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PdfBytes, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("passport.pdf", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
    }

    [Fact]
    public async Task Download_WithoutAJwt_Returns401_S4_1()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([Uploaded]);

        var response = await factory.CreateClient().GetAsync($"/api/files/{seeded.Items[0].FileId}/content");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Download_WritesAFileDownloadedAuditEvent_S4_2()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([Uploaded]);
        var client = await factory.StaffClientAsync();
        var staffId = await factory.Services.DemoStaffIdAsync();

        (await client.GetAsync($"/api/files/{seeded.Items[0].FileId}/content")).EnsureSuccessStatusCode();

        var events = await factory.WithDbAsync(db => db.AuditEvents
            .Where(e => e.Action == AuditAction.FileDownloaded).ToListAsync());
        var audit = Assert.Single(events);
        Assert.Equal(ActorType.Staff, audit.ActorType);
        Assert.Equal(staffId, audit.ActorId);
        Assert.Equal(seeded.Items[0].FileId, audit.EntityId);
    }

    [Fact]
    public async Task Download_AuditEventIsCommittedBeforeTheBytesAreRead_S4_2()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([Uploaded]);
        var client = await factory.StaffClientAsync();

        // Headers only, body unread: the client "disconnects" without consuming the stream.
        using var response = await client.GetAsync(
            $"/api/files/{seeded.Items[0].FileId}/content", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, await factory.AuditCountAsync(AuditAction.FileDownloaded));
    }

    [Fact]
    public async Task Download_AuditMetadataAndLogsNeverContainTheStorageKey_S6_6()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([Uploaded]);
        var client = await factory.StaffClientAsync();
        var key = seeded.Items[0].StorageKey!;

        var response = await client.GetAsync($"/api/files/{seeded.Items[0].FileId}/content");
        response.EnsureSuccessStatusCode();

        var metadata = await factory.WithDbAsync(db => db.AuditEvents.Select(e => e.Metadata).ToListAsync());
        Assert.DoesNotContain(metadata, m => m is not null && m.Contains(key, StringComparison.Ordinal));
        Assert.DoesNotContain(metadata, m => m is not null && m.Contains(key.Split('/')[^1], StringComparison.Ordinal));
        Assert.DoesNotContain(response.Headers.Concat(response.Content.Headers).SelectMany(h => h.Value),
            v => v.Contains(key.Split('/')[^1], StringComparison.Ordinal));
        factory.Logs.AssertNoneContain(key.Split('/')[^1], "storage key");
    }

    [Fact]
    public async Task Download_UnknownFile_Returns404_AndWritesNoAuditEvent()
    {
        using var factory = new ReviewFactory();
        var client = await factory.StaffClientAsync();

        var response = await client.GetAsync($"/api/files/{Guid.NewGuid()}/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.FileDownloaded));
    }

    [Fact]
    public async Task Download_WhenTheBlobIsMissing_Returns404NotAServerError_AndLogsNoKey()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([Uploaded]);
        var client = await factory.StaffClientAsync();
        var key = seeded.Items[0].StorageKey!;
        File.Delete(Path.Combine(factory.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar)));

        var response = await client.GetAsync($"/api/files/{seeded.Items[0].FileId}/content");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(key.Split('/')[^1], raw, StringComparison.Ordinal);
        factory.Logs.AssertNoneContain(key.Split('/')[^1], "storage key");
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.FileDownloaded));
    }

    [Fact]
    public async Task Download_NeverRemovesTheBlob_ADR0005()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([Uploaded]);
        var client = await factory.StaffClientAsync();

        (await client.GetAsync($"/api/files/{seeded.Items[0].FileId}/content")).EnsureSuccessStatusCode();

        var key = seeded.Items[0].StorageKey!;
        Assert.True(File.Exists(Path.Combine(factory.StorageRoot, key.Replace('/', Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task Download_WhenTheAuditWriteFails_ServesNoBytes_S4_2()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([Uploaded]);
        var client = await factory.StaffClientAsync();
        await factory.ExecAsync("""
            CREATE TRIGGER fail_download_audit BEFORE INSERT ON "AuditEvents"
            WHEN NEW."Action" = 'FileDownloaded'
            BEGIN SELECT RAISE(ABORT, 'forced'); END;
            """);

        var response = await client.GetAsync($"/api/files/{seeded.Items[0].FileId}/content");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("review-body-marker", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Storage_IsNotReachableAsStaticFilesOrAnonymously_S4_3()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([Uploaded]);
        var key = seeded.Items[0].StorageKey!;
        var anonymous = factory.CreateClient();
        var root = new DirectoryInfo(factory.StorageRoot).Name;

        string[] paths =
        [
            $"/{key}", $"/{root}/{key}", $"/files/{key}", $"/storage/{key}", $"/uploads/{key}",
            $"/api/files/{key}", $"/api/files/{seeded.Items[0].FileId}", $"/{root}/", "/files/", "/storage/",
        ];

        foreach (var path in paths)
        {
            var response = await anonymous.GetAsync(path);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("review-body-marker", await response.Content.ReadAsStringAsync());
        }

        // And the one route that does serve it refuses an anonymous caller.
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync($"/api/files/{seeded.Items[0].FileId}/content")).StatusCode);
    }

    [Fact]
    public async Task Download_WithAPortalSessionToken_Returns401_S3_14()
    {
        using var factory = new ReviewFactory();
        var seeded = await factory.SeedReviewAsync([Uploaded]);
        var portalSeed = await PortalTestKit.SeedAsync(factory);
        var (portal, _) = await factory.PortalClientAsync(portalSeed.Token);

        var response = await portal.GetAsync($"/api/files/{seeded.Items[0].FileId}/content");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await factory.AuditCountAsync(AuditAction.FileDownloaded));
    }
}
