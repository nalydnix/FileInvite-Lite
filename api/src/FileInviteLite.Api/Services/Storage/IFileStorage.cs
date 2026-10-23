namespace FileInviteLite.Api.Services.Storage;

/// <summary>
/// Where uploaded files live (ADR 0005). Keys are opaque and chosen by the storage, never derived
/// from anything the client sent.
/// </summary>
public interface IFileStorage
{
    /// <summary>
    /// Streams <paramref name="content"/> to storage and returns its key. If the stream throws
    /// partway, nothing is left behind.
    /// </summary>
    Task<string> SaveAsync(Stream content, CancellationToken ct);

    Task<Stream> OpenReadAsync(string key, CancellationToken ct);

    Task<bool> ExistsAsync(string key, CancellationToken ct);

    /// <summary>
    /// Removes a blob that <see cref="SaveAsync"/> wrote but that no committed database row refers
    /// to: an upload that failed content validation, or whose commit failed (ADR 0005, rule 8).
    /// It is never a way to delete a file a row points at; nothing in the MVP does that.
    /// </summary>
    Task DiscardAsync(string key, CancellationToken ct);
}
