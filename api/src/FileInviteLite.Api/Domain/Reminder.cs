namespace FileInviteLite.Api.Domain;

/// <summary>
/// A record that a reminder was sent for a <see cref="DocumentRequest"/>. The cool-down in S5.2 is
/// answered from the latest <see cref="SentAt"/> per request.
/// </summary>
public sealed class Reminder
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid RequestId { get; init; }

    /// <summary>UTC. Compared in SQL, so never DateTimeOffset (ADR 0001).</summary>
    public required DateTime SentAt { get; init; }

    public required string Reason { get; init; }
}
