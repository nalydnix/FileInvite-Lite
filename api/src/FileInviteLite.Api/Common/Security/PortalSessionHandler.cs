using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace FileInviteLite.Api.Common.Security;

/// <summary>
/// The portal session token: what it is, how it is issued, and how it is validated (ADR 0004).
/// </summary>
/// <remarks>
/// A signed JWT rather than a database row. Stateless, so there is no session table and no way to
/// revoke one early; that is acceptable because it lives 30 minutes and the magic link it came from
/// is revocable. It is signed with the same key as the staff JWT, so the two are kept apart by
/// issuer and audience (which each scheme validates) and by the header scheme word.
/// </remarks>
public static class PortalSession
{
    /// <summary>Both the issuer and the audience. Never equal to the staff values.</summary>
    public const string Issuer = "fileinvite-lite-portal";

    public const string RequestIdClaim = "request_id";

    /// <summary>The word in <c>Authorization: Portal &lt;token&gt;</c>.</summary>
    public const string HeaderScheme = "Portal";

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public static (string Token, DateTime ExpiresAtUtc) Issue(Guid requestId, JwtOptions jwt, TimeProvider clock)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(Lifetime);

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Issuer,
            claims:
            [
                new Claim(RequestIdClaim, requestId.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            ],
            notBefore: now,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(SigningKey(jwt), SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    public static TokenValidationParameters ValidationParameters(JwtOptions jwt) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Issuer,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey(jwt),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Every portal response carries <c>Referrer-Policy: no-referrer</c> (S2.12). Called wherever a
    /// portal response can begin, so that a 401 or a 429 carries it as well as a 200.
    /// </summary>
    public static void ApplyResponseHeaders(HttpResponse response) =>
        response.Headers["Referrer-Policy"] = "no-referrer";

    private static SymmetricSecurityKey SigningKey(JwtOptions jwt) =>
        new(Encoding.UTF8.GetBytes(jwt.SigningKey.Length > 0
            ? jwt.SigningKey
            : new string('0', 32)));   // never used: ValidateOnStart fails first
}

/// <summary>
/// Authenticates <c>Authorization: Portal &lt;token&gt;</c>. It reads only that header scheme, so a
/// <c>Bearer</c> staff JWT is never even looked at, and the token it accepts must carry the portal
/// issuer and audience, so a staff JWT presented under the <c>Portal</c> word fails validation too
/// (S2.7, S2.8).
/// </summary>
/// <remarks>
/// Every failure is <see cref="AuthenticateResult.NoResult"/> with no exception attached. The
/// framework logs an exception it is given, and a validation exception can quote the token.
/// </remarks>
public sealed class PortalSessionHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<JwtOptions> jwt)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const int MaxTokenLength = 4096;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        PortalSession.ApplyResponseHeaders(Response);

        var header = Request.Headers.Authorization.ToString();
        var prefix = PortalSession.HeaderScheme + " ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = header[prefix.Length..].Trim();
        if (token.Length is 0 or > MaxTokenLength)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(token, PortalSession.ValidationParameters(jwt.Value), out _);

            // Exactly one request id, and a real one. Anything else is not a portal session.
            var requestIds = principal.FindAll(PortalSession.RequestIdClaim).ToList();
            if (requestIds.Count != 1 || !Guid.TryParse(requestIds[0].Value, out var requestId) || requestId == Guid.Empty)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            // Rebuilt from scratch: the identity carries the request id and nothing else.
            var identity = new ClaimsIdentity(
                [new Claim(PortalSession.RequestIdClaim, requestId.ToString())], Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        PortalSession.ApplyResponseHeaders(Response);
        Response.Headers.WWWAuthenticate = PortalSession.HeaderScheme;
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
