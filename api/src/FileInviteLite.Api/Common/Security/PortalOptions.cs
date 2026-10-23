using System.ComponentModel.DataAnnotations;

namespace FileInviteLite.Api.Common.Security;

/// <summary>
/// Where the client's page lives and how long a magic link stays valid. Validated at startup so a
/// malformed base URL fails the host rather than producing links that go nowhere.
/// </summary>
public sealed class PortalOptions
{
    public const string SectionName = "Portal";

    [Required(AllowEmptyStrings = false, ErrorMessage =
        "Portal:BaseUrl is not configured. Set it to the public origin of the web app, e.g. https://app.example.com.")]
    public string BaseUrl { get; init; } = string.Empty;

    [Range(1, 365, ErrorMessage = "Portal:MagicLinkLifetimeDays must be between 1 and 365.")]
    public int MagicLinkLifetimeDays { get; init; } = 14;

    public static bool IsAbsoluteHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
