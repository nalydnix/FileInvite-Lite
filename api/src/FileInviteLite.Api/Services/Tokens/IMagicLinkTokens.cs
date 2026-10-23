namespace FileInviteLite.Api.Services.Tokens;

/// <summary>
/// The only place a magic-link token is created or hashed (S2.1, S2.2, ADR 0004). Nothing else in
/// the codebase hashes a token, so there is exactly one stored format to keep in step.
/// </summary>
public interface IMagicLinkTokens
{
    /// <summary>A fresh token and the hash that may be stored. Only the hash is ever persisted.</summary>
    IssuedToken Generate();

    /// <summary>
    /// The stored form of a presented token: SHA-256, as <b>lowercase</b> hex (64 characters).
    /// Lookup hashes what the caller sent with this and compares it to <c>MagicLink.TokenHash</c>.
    /// </summary>
    string Hash(string token);
}

/// <summary>The raw token (to be shown or sent once, never stored or logged) and its stored hash.</summary>
public sealed record IssuedToken(string Token, string Hash)
{
    // A record's generated ToString prints every property; this one holds a credential.
    public override string ToString() => $"{nameof(IssuedToken)} {{ Token = ***, Hash = *** }}";
}
