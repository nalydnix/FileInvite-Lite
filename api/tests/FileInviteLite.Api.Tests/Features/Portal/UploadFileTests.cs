using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;
using static FileInviteLite.Api.Tests.Features.Portal.PortalTestKit;

namespace FileInviteLite.Api.Tests.Features.Portal;

public sealed class UploadFileTests : IDisposable
{
    private const int Limit = 20 * 1024 * 1024;

    private readonly PortalFactory _factory;

    // A host per test: these tests assert absolute counts of rows and files on disk.
    public UploadFileTests() => _factory = new PortalFactory();

    public void Dispose() => _factory.Dispose();

    private async Task<(SeededRequest Seeded, HttpClient Client)> StartAsync(
        ItemStatus[]? statuses = null, RequestStatus status = RequestStatus.Sent)
    {
        var seeded = await _factory.SeedAsync(itemStatuses: statuses, status: status);
        var (client, _) = await _factory.PortalClientAsync(seeded.Token);
        return (seeded, client);
    }

    private Task<List<UploadedFile>> FilesOfAsync(Guid itemId) =>
        _factory.WithDbAsync(db => db.UploadedFiles.AsNoTracking()
            .Where(f => f.ChecklistItemId == itemId).OrderBy(f => f.UploadedAt).ToListAsync());

    private Task<ItemStatus> ItemStatusAsync(Guid itemId) =>
        _factory.WithDbAsync(async db => (await db.ChecklistItems.AsNoTracking().SingleAsync(i => i.Id == itemId)).Status);

    private Task<RequestStatus> RequestStatusAsync(Guid requestId) =>
        _factory.WithDbAsync(async db => (await db.DocumentRequests.AsNoTracking().SingleAsync(r => r.Id == requestId)).Status);

    private Task<int> UploadAuditCountAsync() =>
        _factory.WithDbAsync(db => db.AuditEvents.CountAsync(e => e.Action == AuditAction.ItemUploaded));

    /// <summary>Asserts a rejected or failed upload left no row, no event, no status change and no file.</summary>
    private async Task AssertNothingStoredAsync(SeededRequest seeded, ItemStatus expectedItemStatus = ItemStatus.Outstanding)
    {
        Assert.Empty(await FilesOfAsync(seeded.ItemIds[0]));
        Assert.Equal(expectedItemStatus, await ItemStatusAsync(seeded.ItemIds[0]));
        Assert.Equal(0, await UploadAuditCountAsync());
        Assert.Empty(_factory.FilesOnDisk());
    }

    // ---- S3.3 and what is recorded ----------------------------------------------------------

    [Fact]
    public async Task Upload_StoresTheFileAndReturnsItsMetadata_S3_3()
    {
        var (seeded, client) = await StartAsync();
        var content = Pdf("hello");

        var response = await client.UploadAsync(seeded.ItemIds[0], "Passport scan.pdf", content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(raw);

        Assert.Equal(["id", "originalName", "sizeBytes", "uploadedAt"], body.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("Passport scan.pdf", body.GetProperty("originalName").GetString());
        Assert.Equal(content.Length, body.GetProperty("sizeBytes").GetInt64());
        Assert.EndsWith("Z", body.GetProperty("uploadedAt").GetString());

        var row = Assert.Single(await FilesOfAsync(seeded.ItemIds[0]));
        Assert.Equal(row.Id, body.GetProperty("id").GetGuid());

        // The response never shows where the file lives.
        Assert.DoesNotContain(row.StorageKey, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_RecordsNameTypeSizeHashKeyAndTime_S3_9()
    {
        var (seeded, client) = await StartAsync();
        var content = Pdf("record me");

        await client.UploadAsync(seeded.ItemIds[0], "statement.PDF", content);

        var row = Assert.Single(await FilesOfAsync(seeded.ItemIds[0]));
        Assert.Equal("statement.PDF", row.OriginalName);
        Assert.Equal("application/pdf", row.ContentType);
        Assert.Equal(content.Length, row.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), row.Sha256.ToLowerInvariant());
        Assert.False(string.IsNullOrWhiteSpace(row.StorageKey));
        Assert.InRange(row.UploadedAt, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));

        // The stored bytes are the uploaded bytes.
        var onDisk = Assert.Single(_factory.FilesOnDisk());
        Assert.Equal(content, await File.ReadAllBytesAsync(onDisk));
    }

    [Theory]
    [InlineData("a.pdf")]
    [InlineData("a.jpg")]
    [InlineData("a.jpeg")]
    [InlineData("a.png")]
    [InlineData("a.docx")]
    [InlineData("a.xlsx")]
    [InlineData("A.PNG")]
    public async Task Upload_AcceptsEachAllowedType_S3_3(string fileName)
    {
        var (seeded, client) = await StartAsync();
        var content = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => Pdf(),
            ".png" => Png(),
            ".docx" => Docx(),
            ".xlsx" => Xlsx(),
            _ => Jpeg(),
        };

        var response = await client.UploadAsync(seeded.ItemIds[0], fileName, content);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ---- S3.5, S3.6: type ------------------------------------------------------------------

    [Theory]
    [InlineData("malware.exe")]
    [InlineData("script.js")]
    [InlineData("page.html")]
    [InlineData("notes.txt")]
    [InlineData("archive.zip")]
    [InlineData("noextension")]
    [InlineData("trailing.pdf.")]
    [InlineData("a.pdf.exe")]
    [InlineData(".pdf")]
    public async Task Upload_WithADisallowedExtension_Returns415AndStoresNothing_S3_5(string fileName)
    {
        var (seeded, client) = await StartAsync();

        var response = await client.UploadAsync(seeded.ItemIds[0], fileName, Pdf());

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        await AssertNothingStoredAsync(seeded);
    }

    [Fact]
    public async Task Upload_OfAnExecutableRenamedToPdf_Returns415AndStoresNothing_S3_6()
    {
        var (seeded, client) = await StartAsync();

        var response = await client.UploadAsync(seeded.ItemIds[0], "invoice.pdf", Exe());

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        await AssertNothingStoredAsync(seeded);
    }

    [Theory]
    [InlineData("a.png", "pdf")]
    [InlineData("a.jpg", "png")]
    [InlineData("a.pdf", "jpeg")]
    [InlineData("a.docx", "pdf")]
    [InlineData("a.docx", "plainzip")]
    [InlineData("a.docx", "xlsx")]
    [InlineData("a.xlsx", "docx")]
    [InlineData("a.pdf", "empty")]
    public async Task Upload_WhoseContentDoesNotMatchItsExtension_Returns415AndStoresNothing_S3_6(
        string fileName, string actual)
    {
        var (seeded, client) = await StartAsync();
        byte[] content = actual switch
        {
            "pdf" => Pdf(),
            "png" => Png(),
            "jpeg" => Jpeg(),
            "xlsx" => Xlsx(),
            "docx" => Docx(),
            "plainzip" => Zip("hello.txt"),   // PK header, but no word/document.xml
            _ => [],
        };

        var response = await client.UploadAsync(seeded.ItemIds[0], fileName, content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        await AssertNothingStoredAsync(seeded);
    }

    // ---- S3.7: size ------------------------------------------------------------------------

    [Fact]
    public async Task Upload_AcceptsAFileOfExactlyTwentyMegabytes_S3_7()
    {
        var (seeded, client) = await StartAsync();

        var response = await client.UploadAsync(seeded.ItemIds[0], "big.pdf", PdfOfSize(Limit));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Limit, Assert.Single(await FilesOfAsync(seeded.ItemIds[0])).SizeBytes);
    }

    [Fact]
    public async Task Upload_OverTwentyMegabytes_Returns413AndStoresNothing_S3_7()
    {
        var (seeded, client) = await StartAsync();

        var response = await client.UploadAsync(seeded.ItemIds[0], "big.pdf", PdfOfSize(Limit + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await AssertNothingStoredAsync(seeded);
    }

    [Fact]
    public async Task Upload_WithALyingContentLength_DoesNotGetPastTheLimit_S3_7()
    {
        var (seeded, client) = await StartAsync();

        // Claims 1 KB, sends 21 MB.
        using var form = FileForm("big.pdf", PdfOfSize(Limit + 1024 * 1024));
        using var lying = new LyingLengthContent(form, claimedLength: 1024);

        var response = await client.PostAsync($"/api/portal/items/{seeded.ItemIds[0]}/files", lying);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await AssertNothingStoredAsync(seeded);
    }

    [Fact]
    public async Task Upload_WithAnOversizeNonFilePart_Returns413AndStoresNothing_S3_7()
    {
        var (seeded, client) = await StartAsync();

        // A second oversize file part must not be a way round the limit: the first part is fine,
        // the request as a whole keeps counting.
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(PdfOfSize(Limit + 512 * 1024)), "padding", "padding.bin");
        form.Add(new ByteArrayContent(Pdf()), "file", "a.pdf");

        var response = await client.PostAsync($"/api/portal/items/{seeded.ItemIds[0]}/files", form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await AssertNothingStoredAsync(seeded);
    }

    // ---- S3.8: storage key -----------------------------------------------------------------

    [Fact]
    public async Task Upload_StorageKeyContainsNothingFromTheOriginalNameOrTheIds_S3_8()
    {
        var (seeded, client) = await StartAsync();
        const string name = "Bob-Smith-Passport-2024.pdf";

        await client.UploadAsync(seeded.ItemIds[0], name, Pdf());

        var row = Assert.Single(await FilesOfAsync(seeded.ItemIds[0]));
        var key = row.StorageKey;

        Assert.Matches(@"^\d{4}/\d{2}/[0-9a-f]{32}$", key);
        Assert.DoesNotContain("Bob", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pdf", key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".", key, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.ItemIds[0].ToString("N"), key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.RequestId.ToString("N"), key, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(row.Id.ToString("N"), key, StringComparison.OrdinalIgnoreCase);

        // The on-disk path is the key under the root, with no name fragment either.
        var onDisk = Assert.Single(_factory.FilesOnDisk());
        Assert.EndsWith(key.Replace('/', Path.DirectorySeparatorChar), onDisk);
        Assert.DoesNotContain("Bob", Path.GetFileName(onDisk), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upload_AHostileFileName_NeverTouchesThePath_S3_8()
    {
        var (seeded, client) = await StartAsync();

        foreach (var name in new[] { "../../evil.pdf", @"..\..\evil.pdf", "C:\\Windows\\evil.pdf", "/etc/passwd.pdf" })
        {
            var response = await client.UploadAsync(seeded.ItemIds[0], name, Pdf());

            // Either refused or stored under an opaque key; never written outside the storage root.
            Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.BadRequest
                            or HttpStatusCode.UnsupportedMediaType,
                $"{name}: {response.StatusCode}");
        }

        var root = Path.GetFullPath(
            ((Microsoft.Extensions.Configuration.IConfiguration)_factory.Services
                .GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration))!)["Storage:RootPath"]!);
        Assert.All(_factory.FilesOnDisk(), f => Assert.StartsWith(root, Path.GetFullPath(f)));

        // Whatever was stored kept only a bare name, with no directory part.
        var rows = await FilesOfAsync(seeded.ItemIds[0]);
        Assert.All(rows, r =>
        {
            Assert.DoesNotContain('/', r.OriginalName);
            Assert.DoesNotContain('\\', r.OriginalName);
            Assert.DoesNotContain('\0', r.OriginalName);
        });
    }

    // ---- S3.10: status ---------------------------------------------------------------------

    [Fact]
    public async Task Upload_MovesTheItemToUploadedAndTheRequestToInProgress_S3_10()
    {
        var (seeded, client) = await StartAsync(status: RequestStatus.Sent);

        await client.UploadAsync(seeded.ItemIds[0], "a.pdf", Pdf());

        Assert.Equal(ItemStatus.Uploaded, await ItemStatusAsync(seeded.ItemIds[0]));
        Assert.Equal(ItemStatus.Outstanding, await ItemStatusAsync(seeded.ItemIds[1]));
        Assert.Equal(RequestStatus.InProgress, await RequestStatusAsync(seeded.RequestId));

        var body = await client.GetFromJsonAsync<JsonElement>("/api/portal/request");
        Assert.Contains(body.GetProperty("items").EnumerateArray(), i =>
            i.GetProperty("id").GetGuid() == seeded.ItemIds[0] && i.GetProperty("status").GetString() == "Uploaded");
    }

    // ---- S3.11: one transaction ------------------------------------------------------------

    [Fact]
    public async Task Upload_WritesItemUploadedInTheSameTransaction_S3_11()
    {
        var (seeded, client) = await StartAsync();

        var response = await client.UploadAsync(seeded.ItemIds[0], "a.pdf", Pdf());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var row = Assert.Single(await FilesOfAsync(seeded.ItemIds[0]));
        var events = await _factory.WithDbAsync(db => db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == AuditAction.ItemUploaded).ToListAsync());

        var audit = Assert.Single(events);
        Assert.Equal(ActorType.Client, audit.ActorType);
        Assert.Equal(seeded.RequestId, audit.ActorId);
        Assert.Equal(seeded.ItemIds[0], audit.EntityId);

        // Never the storage key, the original name, or anything file-derived beyond the ids.
        Assert.DoesNotContain(row.StorageKey, audit.Metadata ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(row.StorageKey.Split('/')[^1], audit.Metadata ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(row.Sha256, audit.Metadata ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("UploadedFiles", "INSERT", null)]
    [InlineData("AuditEvents", "INSERT", "ItemUploaded")]
    [InlineData("ChecklistItems", "UPDATE", null)]
    [InlineData("DocumentRequests", "UPDATE", null)]
    public async Task Upload_WhenAnyPartOfTheWriteFails_PersistsNoneOfIt_S3_11(
        string table, string operation, string? auditAction)
    {
        // A fresh host per case: the trigger must not leak into another test's database.
        using var factory = new PortalFactory();
        var seeded = await factory.SeedAsync(status: RequestStatus.Sent);
        var (client, _) = await factory.PortalClientAsync(seeded.Token);

        var when = auditAction is null ? "" : $"WHEN NEW.\"Action\" = '{auditAction}'";
        await factory.ExecAsync($"""
            CREATE TRIGGER forced_failure BEFORE {operation} ON "{table}" {when}
            BEGIN SELECT RAISE(ABORT, 'forced'); END;
            """);

        var response = await client.UploadAsync(seeded.ItemIds[0], "a.pdf", Pdf("atomicity"));

        // A 500 carrying only a traceId, never the failure's text.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.False(string.IsNullOrWhiteSpace(JsonSerializer.Deserialize<JsonElement>(raw).GetProperty("traceId").GetString()));
        Assert.DoesNotContain("forced", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SQLite", raw, StringComparison.OrdinalIgnoreCase);

        await factory.ExecAsync("DROP TRIGGER forced_failure;");

        var counts = await factory.WithDbAsync(async db => (
            Files: await db.UploadedFiles.CountAsync(),
            Events: await db.AuditEvents.CountAsync(e => e.Action == AuditAction.ItemUploaded),
            Item: (await db.ChecklistItems.AsNoTracking().SingleAsync(i => i.Id == seeded.ItemIds[0])).Status,
            Request: (await db.DocumentRequests.AsNoTracking().SingleAsync(r => r.Id == seeded.RequestId)).Status));

        Assert.Equal(0, counts.Files);
        Assert.Equal(0, counts.Events);
        Assert.Equal(ItemStatus.Outstanding, counts.Item);
        Assert.Equal(RequestStatus.Sent, counts.Request);

        // And the blob that was already on disk when the commit failed has been removed.
        Assert.Empty(factory.FilesOnDisk());

        // The failure left the host usable.
        var retry = await client.UploadAsync(seeded.ItemIds[0], "a.pdf", Pdf("atomicity"));
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
    }

    // ---- S3.12, S3.13 ----------------------------------------------------------------------

    [Fact]
    public async Task Upload_ToAnApprovedItem_Returns409AndStoresNothing_S3_12()
    {
        var (seeded, client) = await StartAsync([ItemStatus.Approved, ItemStatus.Outstanding]);

        var response = await client.UploadAsync(seeded.ItemIds[0], "a.pdf", Pdf());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertNothingStoredAsync(seeded, ItemStatus.Approved);
    }

    [Fact]
    public async Task Upload_AfterRejection_IsAllowedAndSupersedesWithoutDeletingThePrevious_S3_13()
    {
        var (seeded, client) = await StartAsync([ItemStatus.Rejected, ItemStatus.Outstanding], RequestStatus.InProgress);
        var oldId = await _factory.SeedFileAsync(seeded.ItemIds[0], "first-try.pdf", DateTime.UtcNow.AddDays(-1));

        var response = await client.UploadAsync(seeded.ItemIds[0], "second-try.pdf", Pdf());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(ItemStatus.Uploaded, await ItemStatusAsync(seeded.ItemIds[0]));

        var rows = await FilesOfAsync(seeded.ItemIds[0]);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Id == oldId && r.OriginalName == "first-try.pdf");   // the previous row survives
        Assert.Equal("second-try.pdf", rows[^1].OriginalName);                              // the latest is last

        var view = await client.GetFromJsonAsync<JsonElement>("/api/portal/request");
        var item = view.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == seeded.ItemIds[0]);
        Assert.Equal("second-try.pdf", item.GetProperty("latestUpload").GetProperty("originalName").GetString());
    }

    [Fact]
    public async Task Upload_AgainToAnUploadedItem_KeepsEveryRow_S3_13()
    {
        var (seeded, client) = await StartAsync();

        await client.UploadAsync(seeded.ItemIds[0], "v1.pdf", Pdf("v1"));
        await client.UploadAsync(seeded.ItemIds[0], "v2.pdf", Pdf("v2"));

        Assert.Equal(2, (await FilesOfAsync(seeded.ItemIds[0])).Count);
        Assert.Equal(2, _factory.FilesOnDisk().Length);
        Assert.Equal(ItemStatus.Uploaded, await ItemStatusAsync(seeded.ItemIds[0]));
    }

    // ---- S3.14 -----------------------------------------------------------------------------

    [Fact]
    public async Task NoPortalRouteServesFileContent_S3_14()
    {
        var (seeded, client) = await StartAsync();
        var uploaded = await client.UploadAsync(seeded.ItemIds[0], "mine.pdf", Pdf("secret-content"));
        var fileId = JsonSerializer.Deserialize<JsonElement>(await uploaded.Content.ReadAsStringAsync())
            .GetProperty("id").GetGuid();
        var key = Assert.Single(await FilesOfAsync(seeded.ItemIds[0])).StorageKey;

        var paths = new[]
        {
            $"/api/portal/files/{fileId}",
            $"/api/portal/files/{fileId}/download",
            $"/api/portal/items/{seeded.ItemIds[0]}/files/{fileId}",
            $"/api/portal/items/{seeded.ItemIds[0]}/files/{fileId}/download",
            $"/files/{key}",
            $"/uploads/{key}",
            $"/{key}",
            $"/api/files/{fileId}",
            $"/api/requests/{seeded.RequestId}/files/{fileId}",
        };

        foreach (var path in paths)
        {
            var response = await client.GetAsync(path);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
                $"{path} returned {response.StatusCode}");
            Assert.DoesNotContain("secret-content", await response.Content.ReadAsStringAsync());
        }

        // The only file route is the upload, and a GET on it serves no bytes.
        var onUploadRoute = await client.GetAsync($"/api/portal/items/{seeded.ItemIds[0]}/files");
        Assert.True(onUploadRoute.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    // ---- request shape ---------------------------------------------------------------------

    [Fact]
    public async Task Upload_WithoutAFilePart_Returns400AndStoresNothing_S3_3()
    {
        var (seeded, client) = await StartAsync();

        using var form = new MultipartFormDataContent { { new StringContent("hello"), "note" } };
        var response = await client.PostAsync($"/api/portal/items/{seeded.ItemIds[0]}/files", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingStoredAsync(seeded);
    }

    [Fact]
    public async Task Upload_ThatIsNotMultipart_IsRefusedAndStoresNothing_S3_3()
    {
        var (seeded, client) = await StartAsync();

        var response = await client.PostAsync(
            $"/api/portal/items/{seeded.ItemIds[0]}/files",
            new ByteArrayContent(Pdf()) { Headers = { ContentType = new MediaTypeHeaderValue("application/pdf") } });

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType, response.StatusCode.ToString());
        await AssertNothingStoredAsync(seeded);
    }

    [Fact]
    public async Task Upload_WithAMalformedItemId_Returns404_S2_9()
    {
        var (_, client) = await StartAsync();

        var response = await client.UploadAsync(Guid.Empty, "a.pdf", Pdf());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var notAGuid = await client.PostAsync("/api/portal/items/not-a-guid/files", FileForm("a.pdf", Pdf()));
        Assert.Equal(HttpStatusCode.NotFound, notAGuid.StatusCode);
    }
}
