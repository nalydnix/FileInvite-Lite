using System.Linq.Expressions;

namespace FileInviteLite.Api.Domain;

public enum RequestStatus
{
    Draft,
    Sent,
    InProgress,
    Complete,
}

/// <summary>
/// One client, one due date, one checklist. Owns the two rules that are derived rather than
/// stored — the status roll-up and "overdue" — so that no handler or query restates them.
/// </summary>
public sealed class DocumentRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string ClientName { get; set; }
    public required string ClientEmail { get; set; }

    /// <summary>A calendar date, not an instant. Never DateTimeOffset (ADR 0001).</summary>
    public required DateOnly DueDate { get; set; }

    public RequestStatus Status { get; set; } = RequestStatus.Draft;
    public required Guid CreatedBy { get; init; }

    /// <summary>UTC. Never DateTimeOffset — the SQLite provider cannot translate it (ADR 0001).</summary>
    public required DateTime CreatedAt { get; init; }

    public List<ChecklistItem> Items { get; init; } = [];

    /// <summary>
    /// The status rule from docs/spec.md, over <see cref="Items"/> (which must be loaded). The
    /// status is derived on write: callers assign the result, they never choose a status by hand.
    /// </summary>
    /// <param name="magicLinkIssued">
    /// Whether a magic link has been issued. There is no link entity yet, so the caller supplies
    /// it; the send task passes the real answer.
    /// </param>
    public RequestStatus RollUpStatus(bool magicLinkIssued)
    {
        var required = Items.Where(i => i.Required).ToList();

        // A request with no required items is never Complete: "all required items approved" is
        // vacuously true there, and a brand-new request must start as Draft (S1.5).
        if (required.Count > 0 && required.All(i => i.Status == ItemStatus.Approved))
        {
            return RequestStatus.Complete;
        }

        if (Items.Any(i => i.Status != ItemStatus.Outstanding))
        {
            return RequestStatus.InProgress;
        }

        return magicLinkIssued ? RequestStatus.Sent : RequestStatus.Draft;
    }

    /// <summary>
    /// "Overdue" is never stored: the due date is before <paramref name="today"/> (UTC) and the
    /// request is not Complete. An expression, so EF translates it to SQL and a filter never
    /// pulls rows into memory to apply it — compile it once to evaluate it on loaded rows.
    /// </summary>
    public static Expression<Func<DocumentRequest, bool>> IsOverdue(DateOnly today) =>
        r => r.DueDate < today && r.Status != RequestStatus.Complete;

    /// <summary>
    /// The exact complement of <see cref="IsOverdue"/>, derived from it so the rule is still
    /// written once.
    /// </summary>
    public static Expression<Func<DocumentRequest, bool>> IsNotOverdue(DateOnly today)
    {
        var overdue = IsOverdue(today);
        return Expression.Lambda<Func<DocumentRequest, bool>>(
            Expression.Not(overdue.Body), overdue.Parameters);
    }
}
