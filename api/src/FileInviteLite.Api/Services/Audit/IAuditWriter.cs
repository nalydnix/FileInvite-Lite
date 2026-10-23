using FileInviteLite.Api.Domain;

namespace FileInviteLite.Api.Services.Audit;

/// <summary>
/// The only way to write an audit event.
/// </summary>
/// <remarks>
/// <see cref="Add"/> attaches the event to the same change tracker as the state change that
/// caused it, so the caller's single <c>SaveChangesAsync</c> commits both in one transaction
/// (S6.1). A handler that calls <c>SaveChangesAsync</c> twice — once for state, once for audit —
/// has broken that guarantee and will be rejected in review.
/// </remarks>
public interface IAuditWriter
{
    void Add(
        string action,
        ActorType actorType,
        Guid? actorId,
        string entityType,
        Guid? entityId,
        object? metadata = null);
}
