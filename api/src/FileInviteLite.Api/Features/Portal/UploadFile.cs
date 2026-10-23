using System.Security.Cryptography;
using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Results;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Services.Audit;
using FileInviteLite.Api.Services.Files;
using FileInviteLite.Api.Services.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace FileInviteLite.Api.Features.Portal;

public sealed class UploadFile : IEndpoint
{
    public const long MaxFileBytes = 20 * 1024 * 1024;

    // Multipart boundaries and part headers, and any small non-file parts, ride on top of the file.
    private const long MultipartOverheadBytes = 256 * 1024;
    private const int MaxFileNameLength = 255;
    private const int MaxBoundaryLength = 128;

    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/portal/items/{itemId:guid}/files", Handle)
           .RequireAuthorization(AuthPolicies.Portal)
           .WithName(nameof(UploadFile));

    public sealed record Response(Guid Id, string OriginalName, long SizeBytes, DateTime UploadedAt);

    private static async Task<IResult> Handle(
        Guid itemId,
        HttpContext http,
        AppDbContext db,
        IAuditWriter audit,
        CurrentPortal portal,
        IFileStorage storage,
        IContentSniffer sniffer,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Scope first, and before the body is read: the session's request, then the item inside it.
        // An item on any other request is indistinguishable from one that does not exist (S2.9).
        var request = await db.DocumentRequests
            .Include(r => r.Items)
            .Where(r => r.Id == portal.RequestId)
            .SingleOrDefaultAsync(ct);
        var item = request?.Items.SingleOrDefault(i => i.Id == itemId);
        if (request is null || item is null)
        {
            return ProblemResults.NotFound();
        }

        if (item.Status == ItemStatus.Approved)
        {
            return ProblemResults.Conflict("This item has already been approved and cannot take another file.");
        }

        if (!MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return ProblemResults.UnsupportedMediaType("The upload must be multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > MaxBoundaryLength)
        {
            return ProblemResults.Validation("file", "The multipart boundary is missing or invalid.");
        }

        // A backstop for the server's own limit; the counting below does not depend on it, and
        // Content-Length is never read.
        var sizeLimit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeLimit is { IsReadOnly: false })
        {
            sizeLimit.MaxRequestBodySize = MaxFileBytes + MultipartOverheadBytes;
        }

        string? storedKey = null;
        var committed = false;
        try
        {
            // Every byte of the request body is counted, so a part that is not the file cannot be a
            // way round the limit either.
            var body = new CountingReadStream(http.Request.Body, MaxFileBytes + MultipartOverheadBytes, hash: null);
            var reader = new MultipartReader(boundary, body);

            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                var disposition = section.GetContentDispositionHeader();
                if (disposition is null
                    || !disposition.IsFileDisposition()
                    || HeaderUtilities.RemoveQuotes(disposition.Name).Value != "file")
                {
                    continue;
                }

                var fileName = CleanFileName(
                    HeaderUtilities.RemoveQuotes(disposition.FileName).Value
                    ?? HeaderUtilities.RemoveQuotes(disposition.FileNameStar).Value);
                if (fileName is null)
                {
                    return ProblemResults.Validation("file", "The file must have a name.");
                }

                if (fileName.Length > MaxFileNameLength)
                {
                    return ProblemResults.Validation("file", $"The file name must be at most {MaxFileNameLength} characters.");
                }

                // Extension against the allow-list before a byte is stored. A leading dot is a
                // dotfile with no extension, not an extension.
                var dot = fileName.LastIndexOf('.');
                var extension = dot > 0 ? fileName[dot..].ToLowerInvariant() : string.Empty;
                var storedContentType = sniffer.ContentTypeFor(extension);
                if (storedContentType is null)
                {
                    return ProblemResults.UnsupportedMediaType("That file type is not accepted.");
                }

                // Streamed to storage while counted and hashed; past the limit the read throws and
                // the storage removes its partial file.
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var content = new CountingReadStream(section.Body, MaxFileBytes, sha256);
                storedKey = await storage.SaveAsync(content, ct);

                await using (var stored = await storage.OpenReadAsync(storedKey, ct))
                {
                    if (!await sniffer.MatchesAsync(extension, stored, ct))
                    {
                        return ProblemResults.UnsupportedMediaType("The file content does not match its type.");
                    }
                }

                var uploaded = new UploadedFile
                {
                    ChecklistItemId = item.Id,
                    OriginalName = fileName,
                    ContentType = storedContentType,
                    SizeBytes = content.BytesRead,
                    Sha256 = Convert.ToHexStringLower(sha256.GetHashAndReset()),
                    StorageKey = storedKey,
                    UploadedAt = clock.GetUtcNow().UtcDateTime,
                };
                db.UploadedFiles.Add(uploaded);

                // Re-upload after a rejection supersedes the previous file: that row is left alone
                // and the newest is the one offered for review (S3.13).
                item.Status = ItemStatus.Uploaded;
                request.Status = request.RollUpStatus(magicLinkIssued: true);

                // Ids and a size only: never the storage key, the name or anything of the content (S6.6).
                audit.Add(AuditAction.ItemUploaded, ActorType.Client, portal.RequestId,
                          nameof(ChecklistItem), item.Id, new { fileId = uploaded.Id, sizeBytes = uploaded.SizeBytes });

                await db.SaveChangesAsync(ct);   // file row, status change and audit event, one transaction (S3.11)
                committed = true;

                return Results.Json(
                    new Response(uploaded.Id, uploaded.OriginalName, uploaded.SizeBytes, uploaded.UploadedAt),
                    statusCode: StatusCodes.Status201Created);
            }

            return ProblemResults.Validation("file", "A file part named 'file' is required.");
        }
        catch (UploadTooLargeException)
        {
            return ProblemResults.PayloadTooLarge("The file is larger than the 20 MB limit.");
        }
        catch (BadHttpRequestException bad) when (bad.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return ProblemResults.PayloadTooLarge("The file is larger than the 20 MB limit.");
        }
        catch (InvalidDataException)
        {
            return ProblemResults.Validation("file", "The upload could not be read.");
        }
        finally
        {
            // Nothing is left behind: a blob with no committed row is removed, whether the upload
            // was refused, the commit failed, or the client went away.
            if (storedKey is not null && !committed)
            {
                await storage.DiscardAsync(storedKey, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// The bare name the client sent: any directory part dropped, control characters removed. It is
    /// only ever stored as text and echoed back; it never forms part of a path.
    /// </summary>
    private static string? CleanFileName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var name = raw[(raw.LastIndexOfAny(['/', '\\']) + 1)..];
        name = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        return name.Length == 0 ? null : name;
    }

    private sealed class UploadTooLargeException : Exception;

    /// <summary>
    /// Counts the bytes that pass through it and throws once they exceed <paramref name="limit"/>,
    /// optionally hashing them on the way. This is the size enforcement: it reads what was actually
    /// sent, not what the request claimed.
    /// </summary>
    private sealed class CountingReadStream(Stream inner, long limit, IncrementalHash? hash) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read;
            try
            {
                read = await inner.ReadAsync(buffer, cancellationToken);
            }
            catch (IOException)
            {
                // A body cut short or a connection dropped: the upload is unreadable, not the server broken.
                throw new InvalidDataException("The request body could not be read.");
            }

            Count(buffer.Span[..read]);
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Uploads are read asynchronously.");

        private void Count(ReadOnlySpan<byte> read)
        {
            BytesRead += read.Length;
            if (BytesRead > limit)
            {
                throw new UploadTooLargeException();
            }

            hash?.AppendData(read);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
