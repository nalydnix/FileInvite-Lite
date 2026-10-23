namespace FileInviteLite.Api.Domain;

public enum ReviewOutcome
{
    Approve,
    Reject,
}

/// <summary>
/// One staff verdict on a <see cref="ChecklistItem"/>. Rows are only ever added: a later decision
/// supersedes an earlier one without rewriting it (S4.8).
/// </summary>
public sealed class ReviewDecision
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ChecklistItemId { get; init; }
    public required ReviewOutcome Decision { get; init; }

    /// <summary>
    /// Required on reject, but that is an endpoint rule (S4.5); the column stays nullable.
    /// </summary>
    public string? Comment { get; init; }

    /// <summary>The staff user's id.</summary>
    public required Guid ReviewedBy { get; init; }

    /// <summary>UTC. Never DateTimeOffset (ADR 0001).</summary>
    public required DateTime ReviewedAt { get; init; }
}
