using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileInviteLite.Api.Data.Configurations;

public sealed class MagicLinkConfiguration : IEntityTypeConfiguration<MagicLink>
{
    public void Configure(EntityTypeBuilder<MagicLink> builder)
    {
        builder.ToTable("MagicLinks");
        builder.HasKey(l => l.Id);

        // SHA-256, hex-encoded. There is deliberately no column that could hold the token itself
        // (S2.2, ADR 0004).
        builder.Property(l => l.TokenHash).HasMaxLength(64).IsRequired();

        // SQLite hands back a DateTime of Kind Unspecified; everything stored here is UTC, so say
        // so on the way out. ExpiresAt is compared in SQL, hence DateTime and not DateTimeOffset.
        builder.Property(l => l.ExpiresAt)
               .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
               .IsRequired();
        builder.Property(l => l.RevokedAt)
               .HasConversion(
                   v => v,
                   v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        // No cascade anywhere: nothing in the MVP deletes a domain row (ADR 0006).
        builder.HasOne<DocumentRequest>()
               .WithMany(r => r.MagicLinks)
               .HasForeignKey(l => l.RequestId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(l => l.TokenHash).IsUnique();
        builder.HasIndex(l => l.RequestId);
    }
}
