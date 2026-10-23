using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileInviteLite.Api.Common.Results;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;
using FileInviteLite.Api.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace FileInviteLite.Api.Features.Review;

/// <summary>
/// What approving and rejecting have in common, so the two endpoints cannot drift apart: load,
/// guard, write the decision, roll the request up, and commit it all once. Private to this feature
/// folder; <c>ApproveItem</c> and <c>RejectItem</c> own their routes, bodies and validators.
/// </summary>
internal static class ReviewDecisionWriter
{
    public const int MaxCommentLength = 2000;

    public sealed record Outcome(Guid ItemId, string ItemStatus, string RequestStatus);

    public static async Task<IResult> ApplyAsync(
        Guid itemId,
        ReviewOutcome outcome,
        string? comment,
        AppDbContext db,
        IAuditWriter audit,
        Guid staffId,
        TimeProvider clock,
        IConfiguration configuration,
        CancellationToken ct)
    {
        // One staff tenant: any signed-in staff user may review any item, so the only 404 is an
        // item that does not exist. The request is loaded with all its items because the roll-up
        // reads every one of them.
        var request = await db.DocumentRequests
            .Include(r => r.Items)
            .Where(r => r.Items.Any(i => i.Id == itemId))
            .SingleOrDefaultAsync(ct);
        var item = request?.Items.SingleOrDefault(i => i.Id == itemId);
        if (request is null || item is null)
        {
            return ProblemResults.NotFound();
        }

        // S4.7: there is nothing to review until the client has uploaded something.
        var hasFile = await db.UploadedFiles.AnyAsync(f => f.ChecklistItemId == itemId, ct);
        if (!hasFile)
        {
            return ProblemResults.Conflict("This item has no uploaded file to review.");
        }

        // S4.11. Rejecting an Approved item is refused as well: it would quietly un-complete a
        // request whose completion webhook may already have gone out.
        if (item.Status == ItemStatus.Approved)
        {
            return ProblemResults.Conflict(outcome == ReviewOutcome.Approve
                ? "This item has already been approved."
                : "This item has already been approved and cannot be rejected.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var wasComplete = request.Status == RequestStatus.Complete;

        var decision = new ReviewDecision
        {
            ChecklistItemId = item.Id,
            Decision = outcome,
            Comment = comment,
            ReviewedBy = staffId,
            ReviewedAt = now,
        };
        db.ReviewDecisions.Add(decision);

        item.Status = outcome == ReviewOutcome.Approve ? ItemStatus.Approved : ItemStatus.Rejected;

        // The rule lives on the entity (S4.9, S4.10, S4.12); this only assigns its answer. An item
        // with a file means an upload happened, which means a magic link was issued.
        request.Status = request.RollUpStatus(magicLinkIssued: true);

        // Ids only: never the comment text, a storage key or anything of the file (S6.6).
        audit.Add(
            outcome == ReviewOutcome.Approve ? AuditAction.ItemApproved : AuditAction.ItemRejected,
            ActorType.Staff, staffId, nameof(ChecklistItem), item.Id,
            new { decisionId = decision.Id, requestId = request.Id });

        if (request.Status == RequestStatus.Complete && !wasComplete)
        {
            audit.Add(AuditAction.RequestCompleted, ActorType.Staff, staffId,
                      nameof(DocumentRequest), request.Id, new { decisionId = decision.Id });

            // A prototype with no webhook configured must still be able to complete a request.
            var url = configuration["Webhook:Url"];
            if (!string.IsNullOrWhiteSpace(url))
            {
                var delivery = new WebhookDelivery
                {
                    RequestId = request.Id,
                    Url = url,
                    PayloadHash = CompletionPayload.Hash(request, now),
                };
                db.WebhookDeliveries.Add(delivery);

                audit.Add(AuditAction.WebhookQueued, ActorType.Staff, staffId,
                          nameof(WebhookDelivery), delivery.Id, new { requestId = request.Id });
            }
        }

        // Decision, status change, audit event(s) and delivery row: one transaction.
        await db.SaveChangesAsync(ct);

        return Results.Ok(new Outcome(item.Id, item.Status.ToString(), request.Status.ToString()));
    }
}

/// <summary>
/// The body of the completion webhook, as documented in docs/api-contract.md §Webhook. Task 025
/// sends it; this feature only hashes it, to record what is going to be sent.
/// </summary>
/// <remarks>
/// The hash is only meaningful if task 025 serialises byte-identical JSON. The rules, all of which
/// 025 must follow: <c>JsonSerializerDefaults.Web</c> (camelCase), properties in the order below,
/// compact (no indentation), items ordered by <c>id</c> ascending, <c>outcome</c> as the item's
/// status name, UTF-8, and <c>completedAt</c> as the completing decision's
/// <c>ReviewedAt</c> truncated to whole seconds, UTC.
/// </remarks>
internal static class CompletionPayload
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Item(Guid Id, string Title, bool Required, string Outcome);

    public sealed record Body(
        string Event,
        Guid RequestId,
        string ClientName,
        string ClientEmail,
        DateTime CompletedAt,
        IReadOnlyList<Item> Items);

    public static byte[] Serialize(DocumentRequest request, DateTime completedAt)
    {
        var body = new Body(
            "request.completed",
            request.Id,
            request.ClientName,
            request.ClientEmail,
            new DateTime(completedAt.Ticks - completedAt.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc),
            [.. request.Items.OrderBy(i => i.Id).Select(i => new Item(i.Id, i.Title, i.Required, i.Status.ToString()))]);

        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, Json));
    }

    public static string Hash(DocumentRequest request, DateTime completedAt) =>
        Convert.ToHexStringLower(SHA256.HashData(Serialize(request, completedAt)));
}
