using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Results;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Common.Validation;
using FileInviteLite.Api.Data;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FileInviteLite.Api.Features.Audit;

public sealed class GetRequestAuditTrail : IEndpoint
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    // Keeps (page - 1) * pageSize inside int, so the offset can never wrap to a negative.
    private const int MaxPage = int.MaxValue / MaxPageSize;

    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/requests/{id:guid}/audit", Handle)
           .RequireAuthorization(AuthPolicies.Staff)
           .AddEndpointFilter<ValidationFilter<Query>>()
           .WithName(nameof(GetRequestAuditTrail));

    /// <summary>
    /// The route id and the raw query string. Paging values are bound as text so that a malformed
    /// one (<c>page=abc</c>) reaches <see cref="Validator"/> and leaves in the shared error shape,
    /// the same convention as <c>ListRequests</c>.
    /// </summary>
    public sealed record Query(Guid Id, string? Page, string? PageSize)
    {
        public int PageNumber => ParseInt(Page) ?? 1;

        // Rejected above the maximum, not clamped: ListRequests already answers an out-of-range
        // pageSize with a 400, and the API should not have two conventions for the same mistake.
        public int PageSizeValue => ParseInt(PageSize) ?? DefaultPageSize;

        internal static int? ParseInt(string? value) =>
            int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                ? n
                : null;
    }

    public sealed record Row(
        Guid Id,
        DateTime OccurredAt,
        string ActorType,
        Guid? ActorId,
        string Action,
        string EntityType,
        Guid? EntityId,
        JsonNode? Metadata);

    public sealed record Response(IReadOnlyList<Row> Items, int Page, int PageSize, int Total);

    /// <summary>Found by assembly scan and run by <see cref="ValidationFilter{T}"/>.</summary>
    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator()
        {
            RuleFor(q => q.Page)
                .Must(p => Query.ParseInt(p) is >= 1 and <= MaxPage)
                .When(q => q.Page is not null)
                .WithMessage($"Page must be a whole number from 1 to {MaxPage}.");

            RuleFor(q => q.PageSize)
                .Must(p => Query.ParseInt(p) is >= 1 and <= MaxPageSize)
                .When(q => q.PageSize is not null)
                .WithMessage($"PageSize must be a whole number from 1 to {MaxPageSize}.");
        }
    }

    private static async Task<IResult> Handle(
        [AsParameters] Query query, AppDbContext db, CancellationToken ct)
    {
        // An unknown request is a 404 before any audit row is read (S6.5).
        var requestId = query.Id;
        var exists = await db.DocumentRequests.AsNoTracking().AnyAsync(r => r.Id == requestId, ct);
        if (!exists)
        {
            return ProblemResults.NotFound();
        }

        // "For that request" is the request's own events and its children's: an ItemUploaded event
        // carries the checklist item's id, a FileDownloaded event the file's. The child ids are
        // resolved as subqueries, so the whole filter is one SQL statement and no audit row is
        // loaded to be tested in memory.
        var events = db.AuditEvents.AsNoTracking().Where(e =>
            e.EntityId == requestId
            || db.ChecklistItems.Any(i => i.Id == e.EntityId && i.RequestId == requestId)
            || db.UploadedFiles.Any(f => f.Id == e.EntityId
                && db.ChecklistItems.Any(i => i.Id == f.ChecklistItemId && i.RequestId == requestId))
            || db.ReviewDecisions.Any(d => d.Id == e.EntityId
                && db.ChecklistItems.Any(i => i.Id == d.ChecklistItemId && i.RequestId == requestId))
            || db.MagicLinks.Any(m => m.Id == e.EntityId && m.RequestId == requestId)
            || db.Reminders.Any(r => r.Id == e.EntityId && r.RequestId == requestId)
            || db.WebhookDeliveries.Any(w => w.Id == e.EntityId && w.RequestId == requestId));

        var total = await events.CountAsync(ct);

        var page = query.PageNumber;
        var pageSize = query.PageSizeValue;

        var rows = await events
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)   // a stable order, so a row cannot repeat or vanish across pages
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = rows
            .Select(e => new Row(
                e.Id,
                DateTime.SpecifyKind(e.OccurredAt, DateTimeKind.Utc),   // serialised with a Z (S10.3)
                e.ActorType.ToString(),
                e.ActorId,
                e.Action,
                e.EntityType,
                e.EntityId,
                ParseMetadata(e.Metadata)))
            .ToList();

        return Results.Ok(new Response(items, page, pageSize, total));
    }

    /// <summary>Metadata is stored as text; a row that will not parse reads as null, not as a 500.</summary>
    private static JsonNode? ParseMetadata(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(metadata);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
