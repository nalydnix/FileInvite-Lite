using System.Security.Claims;

namespace FileInviteLite.Api.Common.Security;

/// <summary>
/// The signed-in staff user, read from the validated JWT. Injected into handlers so no handler
/// digs through claims itself, and so a handler cannot accidentally take an actor id from the
/// request body.
/// </summary>
public sealed class CurrentStaff(IHttpContextAccessor accessor)
{
    public Guid Id =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException(
                "CurrentStaff was resolved outside an authenticated staff request. " +
                "The endpoint is missing .RequireAuthorization(AuthPolicies.Staff).");

    public string Email =>
        accessor.HttpContext?.User.FindFirstValue(ClaimTypes.Email)
        ?? throw new InvalidOperationException("No email claim on the current principal.");
}
