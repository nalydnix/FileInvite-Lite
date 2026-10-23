namespace FileInviteLite.Api.Domain;

/// <summary>
/// Metadata for one file a client uploaded against a <see cref="ChecklistItem"/>. The bytes live
/// in file storage under <see cref="StorageKey"/>; everything a download needs to reapply
/// (name, type, size, hash) lives here (S3.9, ADR 0005).
/// </summary>
public sealed class UploadedFile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ChecklistItemId { get; init; }

    /// <summary>As the client sent it. Display only — never used to build a path (S3.8).</summary>
    public required string OriginalName { get; init; }

    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }

    /// <summary>Hex-encoded SHA-256 of the content. Indexed but not unique (ADR 0005).</summary>
    public required string Sha256 { get; init; }

    /// <summary>Opaque (<c>yyyy/MM/{guid}</c>) and unique. Never in audit metadata or a response.</summary>
    public required string StorageKey { get; init; }

    /// <summary>UTC. Never DateTimeOffset (ADR 0001).</summary>
    public required DateTime UploadedAt { get; init; }
}
