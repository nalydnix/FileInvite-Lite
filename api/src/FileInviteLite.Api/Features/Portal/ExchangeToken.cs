using System.Security.Cryptography;
using System.Text;
using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Results;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileInviteLite.Api.Features.Portal;

public sealed class ExchangeToken : IEndpoint
{
    /// <summary>Rate-limiter policy name, applied in Program.cs (S2.10).</summary>
    public const string RateLimitPolicy = "portal-exchange";

    // A real token is 43 characters. Anything much longer is not one, and is refused before it is hashed.
    private const int MaxTokenLength = 512;

    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/portal/exchange", Handle)
           .AllowAnonymous()
           .RequireRateLimiting(RateLimitPolicy)
           .WithName(nameof(ExchangeToken));

    public sealed record Body(string? Token);
    public sealed record Response(string SessionToken, DateTime ExpiresAt, Guid RequestId);

    private static async Task<IResult> Handle(
        Body body,
        AppDbContext db,
        IAuditWriter audit,
        IOptions<JwtOptions> jwt,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Token))
        {
            return ProblemResults.Validation("token", "Token is required.");
        }

        // Unknown, malformed, expired and revoked all leave here, through the same call, so the
        // body is identical and a caller learns nothing about which it was (S2.4-S2.6).
        if (body.Token.Length > MaxTokenLength)
        {
            return ProblemResults.Unauthorized();
        }

        // Only the hash is ever compared; the token itself never reaches the database (S2.2).
        // TODO(011): replace with IMagicLinkTokens.Hash once task 011 lands. Until then the lookup
        // accepts either hex case, so it does not depend on which one the issuer wrote.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body.Token)));
        var upperHash = hash.ToUpperInvariant();

        var link = await db.MagicLinks
            .AsNoTracking()
            .Where(l => l.TokenHash == hash || l.TokenHash == upperHash)
            .Select(l => new { l.Id, l.RequestId, l.ExpiresAt, l.RevokedAt })
            .SingleOrDefaultAsync(ct);

        var now = clock.GetUtcNow().UtcDateTime;
        if (link is null || link.RevokedAt is not null || link.ExpiresAt <= now)
        {
            return ProblemResults.Unauthorized();
        }

        var (sessionToken, expiresAt) = PortalSession.Issue(link.RequestId, jwt.Value, clock);

        // The client has no user id, so the request id is the actor (ADR 0006). The metadata names
        // the link by its own id; the token and its hash stay out of it (S2.11).
        audit.Add(AuditAction.PortalAccessed, ActorType.Client, link.RequestId,
                  nameof(DocumentRequest), link.RequestId, new { magicLinkId = link.Id });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new Response(sessionToken, expiresAt, link.RequestId));
    }
}
