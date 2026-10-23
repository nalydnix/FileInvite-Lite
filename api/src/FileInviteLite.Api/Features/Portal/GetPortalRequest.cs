using System.Text.Json.Serialization;
using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Results;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FileInviteLite.Api.Features.Portal;

public sealed class GetPortalRequest : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/portal/request", Handle)
           .RequireAuthorization(AuthPolicies.Portal)
           .WithName(nameof(GetPortalRequest));

    /// <summary>What the client sees. No staff identity, audit history, storage key or request id (S3.2).</summary>
    public sealed record Response(string ClientName, DateOnly DueDate, bool IsOverdue, IReadOnlyList<ItemView> Items);

    public sealed record ItemView(
        Guid Id,
        string Title,
        string? Description,
        bool Required,
        string Status,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RejectionComment,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] UploadView? LatestUpload);

    public sealed record UploadView(string OriginalName, DateTime UploadedAt);

    private static async Task<IResult> Handle(
        AppDbContext db, CurrentPortal portal, TimeProvider clock, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        // The session's request id is the first and only filter: there is no id to take from anywhere else.
        var row = await db.DocumentRequests
            .AsNoTracking()
            .Where(r => r.Id == portal.RequestId)
            .Select(r => new
            {
                Request = r,
                Items = r.Items.Select(i => new
                {
                    i.Id,
                    i.Title,
                    i.Description,
                    i.Required,
                    i.Status,
                    Latest = i.Files
                        .OrderByDescending(f => f.UploadedAt)
                        .ThenByDescending(f => f.Id)
                        .Select(f => new { f.OriginalName, f.UploadedAt })
                        .FirstOrDefault(),
                }).ToList(),
            })
            .SingleOrDefaultAsync(ct);

        if (row is null)
        {
            return ProblemResults.NotFound();
        }

        // The same overdue rule the staff list uses, applied to the one row.
        var isOverdue = DocumentRequest.IsOverdue(today).Compile()(row.Request);

        // RejectionComment stays null until review decisions exist (phase 2 schema); the field is
        // already in the contract and is only ever written for a Rejected item.
        var items = row.Items
            .Select(i => new ItemView(
                i.Id, i.Title, i.Description, i.Required, i.Status.ToString(),
                RejectionComment: null,
                LatestUpload: i.Latest is null ? null : new UploadView(i.Latest.OriginalName, i.Latest.UploadedAt)))
            .ToList();

        return Results.Ok(new Response(row.Request.ClientName, row.Request.DueDate, isOverdue, items));
    }
}
