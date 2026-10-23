namespace FileInviteLite.Api.Domain;

/// <summary>
/// Grants a client access to exactly one <see cref="DocumentRequest"/>. Only the SHA-256 hash of
/// the token is stored; the token itself never reaches the database (S2.2, ADR 0004).
/// </summary>
public sealed class MagicLink
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid RequestId { get; init; }

    /// <summary>SHA-256 of the token, hex-encoded: 64 characters. Unique.</summary>
    public required string TokenHash { get; init; }

    /// <summary>UTC. Compared in SQL, so never DateTimeOffset (ADR 0001).</summary>
    public required DateTime ExpiresAt { get; init; }

    /// <summary>UTC; null while the link is live. Never DateTimeOffset (ADR 0001).</summary>
    public DateTime? RevokedAt { get; set; }
}
