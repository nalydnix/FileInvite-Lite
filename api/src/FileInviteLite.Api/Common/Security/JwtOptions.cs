using System.ComponentModel.DataAnnotations;

namespace FileInviteLite.Api.Common.Security;

/// <summary>
/// Validated at startup, so a missing or weak signing key fails the host immediately with a clear
/// message instead of producing forgeable tokens (S8.7). There is deliberately no default value
/// for <see cref="SigningKey"/> anywhere in the codebase.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required(AllowEmptyStrings = false, ErrorMessage =
        "Jwt:SigningKey is not configured. Set it with 'dotnet user-secrets set \"Jwt:SigningKey\" \"<32+ characters>\"' " +
        "locally, or as an App Service setting in production. It must never be committed.")]
    [MinLength(32, ErrorMessage = "Jwt:SigningKey must be at least 32 characters.")]
    public string SigningKey { get; init; } = string.Empty;

    [Required]
    public string Issuer { get; init; } = "fileinvite-lite";

    [Required]
    public string Audience { get; init; } = "fileinvite-lite";

    [Range(1, 1440)]
    public int ExpiryMinutes { get; init; } = 60;
}
