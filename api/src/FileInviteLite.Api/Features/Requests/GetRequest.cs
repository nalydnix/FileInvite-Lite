using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Results;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileInviteLite.Api.Features.Requests;

public sealed class GetRequest : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/requests/{id}", Handle)
           .RequireAuthorization(AuthPolicies.Staff)
           .WithName(nameof(GetRequest));

    public sealed record Staff(Guid Id, string Name);

    /// <summary>Metadata only. Never the token, the URL or the hash (S1.12).</summary>
    public sealed record MagicLinkView(DateTime IssuedAt, DateTime ExpiresAt, bool Revoked);

    /// <summary>Metadata only. Never a storage key, a hash or the content (S1.10).</summary>
    public sealed record FileView(
        Guid Id, string OriginalName, string ContentType, long SizeBytes, DateTime UploadedAt);

    /// <summary>Always null until review decisions exist (Phase 2); kept so the shape holds.</summary>
    public sealed record DecisionView(string Decision, string? Comment, DateTime ReviewedAt);

    public sealed record ItemView(
        Guid Id,
        string Title,
        string? Description,
        bool Required,
        string Status,
        FileView? LatestFile,
        DecisionView? LatestDecision);

    public sealed record Response(
        Guid Id,
        string ClientName,
        string ClientEmail,
        DateOnly DueDate,
        string Status,
        bool IsOverdue,
        DateTime CreatedAt,
        Staff? CreatedBy,
        MagicLinkView? MagicLink,
        IReadOnlyList<ItemView> Items);

    // Bound as text and parsed here so that an id which is not a GUID is a 404 after authorisation,
    // not a routing 404 an unauthenticated caller could tell apart from a 401 (S1.7).
    private static async Task<IResult> Handle(
        string id,
        AppDbContext db,
        IOptions<PortalOptions> portal,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var requestId))
        {
            return ProblemResults.NotFound();
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var row = await db.DocumentRequests
            .AsNoTracking()
            .Where(r => r.Id == requestId)
            .Select(r => new
            {
                Request = r,
                CreatedBy = db.StaffUsers
                    .Where(u => u.Id == r.CreatedBy)
                    .Select(u => new Staff(u.Id, u.Name))
                    .FirstOrDefault(),

                // Only the two columns the response needs: the hash stays in the database.
                // Live links first, then the latest expiry. Re-sending revokes every earlier link, so
                // at most one is live and it is the newest; the expiry only orders a request whose
                // links are all revoked.
                Link = r.MagicLinks
                    .OrderBy(l => l.RevokedAt != null)
                    .ThenByDescending(l => l.ExpiresAt)
                    .Select(l => new { l.ExpiresAt, l.RevokedAt })
                    .FirstOrDefault(),

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
                        .Select(f => new FileView(f.Id, f.OriginalName, f.ContentType, f.SizeBytes, f.UploadedAt))
                        .FirstOrDefault(),
                }).ToList(),
            })
            .SingleOrDefaultAsync(ct);

        if (row is null)
        {
            return ProblemResults.NotFound();
        }

        // issuedAt is not stored: the schema is closed, and a link's life is fixed by configuration,
        // so it is the expiry less that lifetime. If the lifetime setting changes, links issued
        // before the change report a shifted issuedAt; expiresAt is always exact.
        var link = row.Link is null
            ? null
            : new MagicLinkView(
                row.Link.ExpiresAt.AddDays(-portal.Value.MagicLinkLifetimeDays),
                row.Link.ExpiresAt,
                row.Link.RevokedAt is not null);

        var isOverdue = DocumentRequest.IsOverdue(today).Compile()(row.Request);

        var items = row.Items
            .Select(i => new ItemView(
                i.Id, i.Title, i.Description, i.Required, i.Status.ToString(), i.Latest, LatestDecision: null))
            .ToList();

        return Results.Ok(new Response(
            row.Request.Id,
            row.Request.ClientName,
            row.Request.ClientEmail,
            row.Request.DueDate,
            row.Request.Status.ToString(),
            isOverdue,
            row.Request.CreatedAt,
            row.CreatedBy,
            link,
            items));
    }
}
