using System.Text.Json;
using FileInviteLite.Api.Data;
using FileInviteLite.Api.Domain;

namespace FileInviteLite.Api.Services.Audit;

public sealed class AuditWriter(AppDbContext db, TimeProvider clock) : IAuditWriter
{
    private static readonly JsonSerializerOptions MetadataJson = new(JsonSerializerDefaults.Web);

    public void Add(
        string action,
        ActorType actorType,
        Guid? actorId,
        string entityType,
        Guid? entityId,
        object? metadata = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            OccurredAt = clock.GetUtcNow().UtcDateTime,
            ActorType = actorType,
            ActorId = actorId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata, MetadataJson),
        });
    }
}
