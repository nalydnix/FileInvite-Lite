using System.Globalization;
using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Common.Validation;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace FileInviteLite.Api.Features.Requests;

public sealed class ListRequests : IEndpoint
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/api/requests", Handle)
           .RequireAuthorization(AuthPolicies.Staff)
           .AddEndpointFilter<ValidationFilter<Query>>()
           .WithName(nameof(ListRequests));

    /// <summary>
    /// The raw query string. Every value is bound as text so that a malformed one (<c>page=abc</c>)
    /// reaches <see cref="Validator"/> and leaves in the shared error shape, instead of being
    /// rejected by the framework's own binder with a different body.
    /// </summary>
    public sealed record Query(
        string? Status, string? Overdue, string? Page, string? PageSize, string? Sort)
    {
        // Only meaningful once the validator has passed; each falls back to its documented default.
        public RequestStatus? StatusFilter => ParseStatus(Status);
        public bool? OverdueFilter => bool.TryParse(Overdue, out var overdue) ? overdue : null;
        public int PageNumber => ParseInt(Page) ?? 1;
        public int PageSizeValue => ParseInt(PageSize) ?? DefaultPageSize;
        public bool SortByCreatedAt => string.Equals(Sort, "createdAt", StringComparison.OrdinalIgnoreCase);

        internal static RequestStatus? ParseStatus(string? value) =>
            Enum.GetValues<RequestStatus>()
                .Cast<RequestStatus?>()
                .FirstOrDefault(s => string.Equals(s.ToString(), value, StringComparison.OrdinalIgnoreCase));

        internal static int? ParseInt(string? value) =>
            int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
                ? n
                : null;
    }

    public sealed record Row(
        Guid Id,
        string ClientName,
        DateOnly DueDate,
        string Status,
        int ItemCount,
        int ApprovedCount,
        bool IsOverdue,
        DateTime CreatedAt);

    public sealed record Response(IReadOnlyList<Row> Items, int Page, int PageSize, int Total);

    /// <summary>Found by assembly scan and run by <see cref="ValidationFilter{T}"/>.</summary>
    public sealed class Validator : AbstractValidator<Query>
    {
        public Validator()
        {
            RuleFor(q => q.Status)
                .Must(s => Query.ParseStatus(s) is not null)
                .When(q => q.Status is not null)
                .WithMessage("Status must be one of Draft, Sent, InProgress, Complete.");

            RuleFor(q => q.Overdue)
                .Must(o => bool.TryParse(o, out _))
                .When(q => q.Overdue is not null)
                .WithMessage("Overdue must be true or false.");

            RuleFor(q => q.Page)
                .Must(p => Query.ParseInt(p) is >= 1)
                .When(q => q.Page is not null)
                .WithMessage("Page must be a whole number of 1 or more.");

            RuleFor(q => q.PageSize)
                .Must(p => Query.ParseInt(p) is >= 1 and <= MaxPageSize)
                .When(q => q.PageSize is not null)
                .WithMessage($"PageSize must be a whole number from 1 to {MaxPageSize}.");

            RuleFor(q => q.Sort)
                .Must(s => s is not null
                           && (s.Equals("dueDate", StringComparison.OrdinalIgnoreCase)
                               || s.Equals("createdAt", StringComparison.OrdinalIgnoreCase)))
                .When(q => q.Sort is not null)
                .WithMessage("Sort must be dueDate or createdAt.");
        }
    }

    private static async Task<IResult> Handle(
        [AsParameters] Query query,
        AppDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var requests = db.DocumentRequests.AsNoTracking();

        if (query.StatusFilter is { } status)
        {
            requests = requests.Where(r => r.Status == status);
        }

        // Applied in SQL: the predicate is an expression, so EF translates it and no row is
        // pulled into memory to be tested (ADR 0001).
        if (query.OverdueFilter is { } overdue)
        {
            requests = requests.Where(overdue
                ? DocumentRequest.IsOverdue(today)
                : DocumentRequest.IsNotOverdue(today));
        }

        // Counted after the filters and before the page, so `total` describes the filtered set.
        var total = await requests.CountAsync(ct);

        var ordered = query.SortByCreatedAt
            ? requests.OrderBy(r => r.CreatedAt)
            : requests.OrderBy(r => r.DueDate);

        var page = query.PageNumber;
        var pageSize = query.PageSizeValue;

        var rows = await ordered
            .ThenBy(r => r.Id)   // a stable order, so a row cannot repeat or vanish across pages
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new
            {
                Request = r,
                ItemCount = r.Items.Count,
                ApprovedCount = r.Items.Count(i => i.Status == ItemStatus.Approved),
            })
            .ToListAsync(ct);

        // The same rule as the filter above, compiled once and applied to the rows on the page.
        var isOverdue = DocumentRequest.IsOverdue(today).Compile();

        var items = rows
            .Select(row => new Row(
                row.Request.Id,
                row.Request.ClientName,
                row.Request.DueDate,
                row.Request.Status.ToString(),
                row.ItemCount,
                row.ApprovedCount,
                isOverdue(row.Request),
                row.Request.CreatedAt))
            .ToList();

        return Results.Ok(new Response(items, page, pageSize, total));
    }
}
