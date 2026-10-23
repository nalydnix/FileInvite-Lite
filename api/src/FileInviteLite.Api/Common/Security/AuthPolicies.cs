namespace FileInviteLite.Api.Common.Security;

/// <summary>
/// Two credentials, two schemes, two policies, no overlap. A staff JWT must never authorise a
/// portal endpoint and a portal token must never authorise a staff endpoint (S2.7, S2.8), so each
/// policy pins its own authentication scheme rather than accepting whatever authenticated first.
/// </summary>
public static class AuthPolicies
{
    public const string Staff = "Staff";
    public const string Portal = "Portal";

    public static class Schemes
    {
        public const string StaffJwt = "StaffJwt";
        public const string PortalSession = "PortalSession";
    }
}
