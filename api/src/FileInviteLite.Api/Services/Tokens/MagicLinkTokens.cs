using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace FileInviteLite.Api.Services.Tokens;

public sealed class MagicLinkTokens : IMagicLinkTokens
{
    private const int TokenBytes = 32;   // 256 bits (S2.1)

    public IssuedToken Generate()
    {
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));
        return new IssuedToken(token, Hash(token));
    }

    // Lowercase hex, decided here and nowhere else: ADR 0004 says "hex" without a case, and a
    // lookup that has to guess which one was written is a lookup that can silently miss.
    public string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
