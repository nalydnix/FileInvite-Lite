namespace FileInviteLite.Api.Services.Files;

/// <summary>
/// Decides whether an upload is what its extension claims. The extension is checked against an
/// allow-list and the content against the type that extension names (ADR 0005, rule 5).
/// </summary>
public interface IContentSniffer
{
    /// <summary>
    /// The content type stored for an allowed extension (with the dot, any case), or null when the
    /// extension is not allowed. The type comes from the extension, never from the client.
    /// </summary>
    string? ContentTypeFor(string extension);

    /// <summary>True when the leading bytes (and, for Office files, the ZIP entries) match the extension.</summary>
    /// <param name="content">A seekable stream positioned at the start.</param>
    Task<bool> MatchesAsync(string extension, Stream content, CancellationToken ct);
}
