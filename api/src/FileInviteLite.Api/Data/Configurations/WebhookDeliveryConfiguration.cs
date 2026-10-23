using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileInviteLite.Api.Data.Configurations;

public sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("WebhookDeliveries");
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Url).HasMaxLength(2048).IsRequired();

        // SHA-256, hex-encoded. Deliberately not unique: two requests may hash identically.
        builder.Property(d => d.PayloadHash).HasMaxLength(64).IsRequired();
        builder.Property(d => d.Attempts).HasDefaultValue(0).IsRequired();
        builder.Property(d => d.LastStatus);

        // SQLite hands back a DateTime of Kind Unspecified; everything stored here is UTC.
        builder.Property(d => d.DeliveredAt)
               .HasConversion(
                   v => v,
                   v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        // No cascade anywhere: nothing in the MVP deletes a domain row (ADR 0006).
        builder.HasOne<DocumentRequest>()
               .WithMany(r => r.WebhookDeliveries)
               .HasForeignKey(d => d.RequestId)
               .OnDelete(DeleteBehavior.Restrict);

        // The delivery job's pending query filters on DeliveredAt (S7.6).
        builder.HasIndex(d => d.DeliveredAt);
    }
}
