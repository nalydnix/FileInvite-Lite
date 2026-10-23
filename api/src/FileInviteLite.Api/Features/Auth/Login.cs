using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Results;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Services.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FileInviteLite.Api.Features.Auth;

public sealed class Login : IEndpoint
{
    /// <summary>Rate-limiter policy name, applied in Program.cs (S8.4).</summary>
    public const string RateLimitPolicy = "login";

    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/auth/login", Handle)
           .AllowAnonymous()
           .RequireRateLimiting(RateLimitPolicy)
           .WithName(nameof(Login));

    public sealed record Body(string Email, string Password);
    public sealed record Response(string Token, DateTime ExpiresAt, string Name);

    private static async Task<IResult> Handle(
        Body body,
        AppDbContext db,
        IAuditWriter audit,
        JwtTokenIssuer issuer,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrWhiteSpace(body.Password))
        {
            return ProblemResults.Validation("email", "Email and password are required.");
        }

        var user = await db.StaffUsers.SingleOrDefaultAsync(u => u.Email == body.Email, ct);

        // Hash even when the user does not exist, so an unknown email and a wrong password take
        // roughly the same time. Combined with the identical response body, this is what stops
        // the endpoint confirming which addresses are real (S8.2).
        var hasher = new PasswordHasher<StaffUser>();
        var verification = user is null
            ? VerifyDummy(hasher)
            : hasher.VerifyHashedPassword(user, user.PasswordHash, body.Password);

        if (user is null || verification == PasswordVerificationResult.Failed)
        {
            // The attempted email is recorded; the password never is (S8.8).
            audit.Add(AuditAction.StaffLoginFailed, ActorType.System, null,
                      nameof(StaffUser), user?.Id, new { attemptedEmail = body.Email });
            await db.SaveChangesAsync(ct);

            return ProblemResults.Unauthorized();
        }

        var (token, expiresAt) = issuer.Issue(user);

        audit.Add(AuditAction.StaffLoggedIn, ActorType.Staff, user.Id, nameof(StaffUser), user.Id);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new Response(token, expiresAt, user.Name));
    }

    private static PasswordVerificationResult VerifyDummy(PasswordHasher<StaffUser> hasher)
    {
        hasher.VerifyHashedPassword(DummyUser, DummyHash, "not-the-password");
        return PasswordVerificationResult.Failed;
    }

    private static readonly StaffUser DummyUser =
        new() { Email = "", Name = "", PasswordHash = "" };

    /// <summary>
    /// A genuine PBKDF2 hash of a throwaway value, computed once at startup. It exists only so
    /// that an unknown email burns the same work as a real verification; it guards nothing and
    /// unlocks nothing.
    /// </summary>
    private static readonly string DummyHash =
        new PasswordHasher<StaffUser>().HashPassword(DummyUser, Guid.NewGuid().ToString());
}
