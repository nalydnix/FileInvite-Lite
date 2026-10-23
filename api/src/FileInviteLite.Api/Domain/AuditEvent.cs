namespace FileInviteLite.Api.Domain;

public enum ActorType
{
    Staff,
    Client,
    System,
}

/// <summary>
/// Append-only. Nothing in application code updates or deletes one of these (S6.3). The single
/// declared exception is the GDPR redaction path described in ADR 0006, which is not reachable
/// from feature code and does not exist yet.
/// </summary>
public sealed class AuditEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>UTC. Never DateTimeOffset — the SQLite provider cannot translate it (ADR 0001).</summary>
    public required DateTime OccurredAt { get; init; }

    public required ActorType ActorType { get; init; }

    /// <summary>
    /// The staff user's id, or — for a client — the request id. A client has no user identity, so
    /// the actor is pseudonymous by construction, which is most of what makes erasure tractable
    /// (ADR 0006).
    /// </summary>
    public Guid? ActorId { get; init; }

    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public Guid? EntityId { get; init; }

    /// <summary>
    /// JSON. Never a token, password, JWT, storage key or file content (S6.6).
    /// </summary>
    public string? Metadata { get; init; }
}

/// <summary>
/// The action names written to <see cref="AuditEvent.Action"/>. Constants rather than an enum:
/// these are read by humans in an audit trail and must survive being added to by parallel feature
/// tasks without renumbering anything.
/// </summary>
public static class AuditAction
{
    public const string StaffLoggedIn = nameof(StaffLoggedIn);
    public const string StaffLoginFailed = nameof(StaffLoginFailed);
}
