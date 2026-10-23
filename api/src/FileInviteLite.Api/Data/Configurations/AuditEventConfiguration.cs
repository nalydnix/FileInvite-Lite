using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileInviteLite.Api.Data.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.OccurredAt).IsRequired();

        // Stored as a string so that reordering the enum cannot silently reinterpret existing
        // rows — which in an audit table would be undetectable after the fact.
        builder.Property(e => e.ActorType)
               .HasConversion<string>()
               .HasMaxLength(16)
               .IsRequired();

        builder.Property(e => e.Action).HasMaxLength(64).IsRequired();
        builder.Property(e => e.EntityType).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Metadata).HasColumnType("TEXT");

        builder.HasIndex(e => e.EntityId);
        builder.HasIndex(e => e.OccurredAt);

        // No navigation properties, deliberately: nothing can cascade into the audit table.
    }
}
