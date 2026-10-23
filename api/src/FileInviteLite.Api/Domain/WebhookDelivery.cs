namespace FileInviteLite.Api.Domain;

/// <summary>
/// An outbound completion webhook for a <see cref="DocumentRequest"/>, retried until it succeeds
/// (S7.6). Only a hash of the payload is kept, never the payload or the signing secret.
/// </summary>
public sealed class WebhookDelivery
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid RequestId { get; init; }
    public required string Url { get; init; }

    /// <summary>SHA-256 of the payload, hex-encoded: 64 characters. Not unique.</summary>
    public required string PayloadHash { get; init; }

    public int Attempts { get; set; }

    /// <summary>HTTP status of the latest attempt; null until the first attempt.</summary>
    public int? LastStatus { get; set; }

    /// <summary>UTC; set only on success. Never DateTimeOffset (ADR 0001).</summary>
    public DateTime? DeliveredAt { get; set; }
}
