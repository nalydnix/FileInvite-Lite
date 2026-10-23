using System.Security.Claims;

namespace FileInviteLite.Api.Common.Security;

/// <summary>
/// The request the current portal session is scoped to, read from the validated session token.
/// It is the only source of a request id on a portal endpoint: no route, query or body carries one,
/// so a handler cannot be talked into reading another request's data (S2.9).
/// </summary>
public sealed class CurrentPortal(IHttpContextAccessor accessor)
{
    public Guid RequestId
    {
        get
        {
            // Only an identity produced by the portal scheme counts. A principal from any other
            // scheme is never read, whatever claims it carries.
            var claim = accessor.HttpContext?.User.Identities
                .Where(identity => identity.AuthenticationType == AuthPolicies.Schemes.PortalSession)
                .SelectMany(identity => identity.FindAll(PortalSession.RequestIdClaim))
                .SingleOrDefault();

            return Guid.TryParse(claim?.Value, out var id) && id != Guid.Empty
                ? id
                : throw new InvalidOperationException(
                    "CurrentPortal was resolved outside a portal session. " +
                    "The endpoint is missing .RequireAuthorization(AuthPolicies.Portal).");
        }
    }
}
