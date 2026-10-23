using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Results;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Services.Audit;
using FileInviteLite.Api.Services.Storage;
using Microsoft.EntityFrameworkCore;

namespace FileInviteLite.Api.Features.Review;

public sealed class DownloadFile : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/files/{fileId:guid}/content", Handle)
           .RequireAuthorization(AuthPolicies.Staff)
           .WithName(nameof(DownloadFile));

    private static async Task<IResult> Handle(
        Guid fileId,
        HttpContext http,
        AppDbContext db,
        IAuditWriter audit,
        CurrentStaff staff,
        IFileStorage storage,
        ILogger<DownloadFile> logger,
        CancellationToken ct)
    {
        var file = await db.UploadedFiles
            .AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new { f.Id, f.ChecklistItemId, f.OriginalName, f.ContentType, f.StorageKey })
            .SingleOrDefaultAsync(ct);
        if (file is null)
        {
            return ProblemResults.NotFound();
        }

        // Opened first so a row whose blob has gone is a 404 and leaves no "downloaded" event for a
        // download that never happened. The key is never logged, only the file id.
        Stream content;
        try
        {
            content = await storage.OpenReadAsync(file.StorageKey, ct);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or ArgumentException)
        {
            logger.LogWarning("File {FileId} has a database row but no readable blob.", file.Id);
            return ProblemResults.NotFound();
        }

        try
        {
            // Committed before a byte goes out (S4.2): a client that drops mid-download is still on
            // the record. Ids only; never the storage key or the name.
            audit.Add(AuditAction.FileDownloaded, ActorType.Staff, staff.Id,
                      nameof(UploadedFile), file.Id, new { itemId = file.ChecklistItemId });
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }

        // Never rendered in the app's origin, never cached (ADR 0005).
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.XContentTypeOptions = "nosniff";

        // Results.Stream disposes the stream when the response completes or is aborted.
        return Results.Stream(content, file.ContentType, fileDownloadName: file.OriginalName);
    }
}
