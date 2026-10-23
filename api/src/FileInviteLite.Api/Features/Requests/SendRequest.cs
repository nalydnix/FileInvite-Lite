using FileInviteLite.Api.Common;
using FileInviteLite.Api.Common.Results;
using FileInviteLite.Api.Common.Security;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Services.Audit;
using FileInviteLite.Api.Services.Email;
using FileInviteLite.Api.Services.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FileInviteLite.Api.Features.Requests;

public sealed class SendRequest : IEndpoint
{
    public void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/requests/{id}/send", Handle)
           .RequireAuthorization(AuthPolicies.Staff)
           .WithName(nameof(SendRequest));

    /// <summary>
    /// The only response in the API that carries a magic-link token, inside <see cref="Url"/>'s
    /// fragment. Nothing reads it back later (S1.12).
    /// </summary>
    public sealed record Response(string Url, DateTime ExpiresAt);

    // The id is bound as text and parsed here, so that an id which is not a GUID is a 404 from the
    // handler — after authorisation has run — rather than a routing 404 that would answer an
    // unauthenticated caller differently from an authenticated one (S1.7).
    private static async Task<IResult> Handle(
        string id,
        AppDbContext db,
        IAuditWriter audit,
        CurrentStaff staff,
        IMagicLinkTokens tokens,
        IEmailSender email,
        IOptions<PortalOptions> portal,
        TimeProvider clock,
        ILogger<SendRequest> logger,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var requestId))
        {
            return ProblemResults.NotFound();
        }

        var request = await db.DocumentRequests
            .Include(r => r.Items)
            .SingleOrDefaultAsync(r => r.Id == requestId, ct);
        if (request is null)
        {
            return ProblemResults.NotFound();
        }

        if (request.Items.Count == 0)
        {
            return ProblemResults.Validation("items", "A request with no items cannot be sent.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var expiresAt = now.AddDays(portal.Value.MagicLinkLifetimeDays);
        var issued = tokens.Generate();

        // Revoking the live links is a read-modify-write, so the read belongs inside a transaction
        // with the write. Without one, two overlapping sends each read the live set before the
        // other commits, each revokes only what it saw, and both insert a live link — leaving a
        // link the caller believes is revoked still granting access, against S1.14. SQLite refuses
        // the second writer rather than interleaving, which is the outcome we want: one send
        // succeeds and the other fails loudly. api/CLAUDE.md asks a multi-write handler to wrap its
        // work this way.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Everything below is tracked and then committed by the one SaveChangesAsync: the revoked
        // links, the new link, the status change and every audit event (S1.13, S1.14).
        var previous = await db.MagicLinks
            .Where(l => l.RequestId == request.Id && l.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var old in previous)
        {
            old.RevokedAt = now;
            audit.Add(AuditAction.MagicLinkRevoked, ActorType.Staff, staff.Id,
                      nameof(MagicLink), old.Id, new { requestId = request.Id });
        }

        var link = new MagicLink { RequestId = request.Id, TokenHash = issued.Hash, ExpiresAt = expiresAt };
        db.MagicLinks.Add(link);

        // Derived, never chosen: a request a client has already started stays InProgress.
        request.Status = request.RollUpStatus(magicLinkIssued: true);

        // Metadata names the link by its own id. Neither the token nor its hash goes in (S1.13).
        audit.Add(AuditAction.RequestSent, ActorType.Staff, staff.Id,
                  nameof(DocumentRequest), request.Id, new { magicLinkId = link.Id, expiresAt });
        audit.Add(AuditAction.MagicLinkIssued, ActorType.Staff, staff.Id,
                  nameof(MagicLink), link.Id, new { requestId = request.Id, expiresAt });

        await db.SaveChangesAsync(ct);          // links + status + audit events: one transaction
        await tx.CommitAsync(ct);

        // The fragment keeps the token out of every server log and Referer header (ADR 0004).
        var url = $"{portal.Value.BaseUrl.TrimEnd('/')}/portal#t={issued.Token}";

        // After the commit, and not allowed to undo it: the link is already issued and is about to
        // be handed to the caller, so a failing sink must not turn that into a 500. Only the
        // exception type is logged — never the message or the link.
        try
        {
            await email.SendMagicLinkAsync(
                new MagicLinkEmail(request.ClientEmail, request.ClientName, url, expiresAt), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                "Sending the magic link for request {RequestId} failed ({ExceptionType}).",
                request.Id, ex.GetType().Name);
        }

        return Results.Ok(new Response(url, expiresAt));
    }
}
