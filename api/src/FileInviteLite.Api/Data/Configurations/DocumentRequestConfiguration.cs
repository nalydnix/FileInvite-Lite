using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileInviteLite.Api.Data.Configurations;

public sealed class DocumentRequestConfiguration : IEntityTypeConfiguration<DocumentRequest>
{
    public void Configure(EntityTypeBuilder<DocumentRequest> builder)
    {
        builder.ToTable("DocumentRequests");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.ClientName).HasMaxLength(200).IsRequired();
        builder.Property(r => r.ClientEmail).HasMaxLength(320).IsRequired();
        builder.Property(r => r.DueDate).IsRequired();

        // SQLite hands back a DateTime of Kind Unspecified, which serialises without the trailing
        // Z the API contract promises. Everything stored here is UTC, so say so on the way out.
        builder.Property(r => r.CreatedAt)
               .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
               .IsRequired();

        // Stored as a string so that reordering the enum cannot silently reinterpret existing rows.
        builder.Property(r => r.Status)
               .HasConversion<string>()
               .HasMaxLength(16)
               .IsRequired();

        // No cascade anywhere: nothing in the MVP deletes a domain row (ADR 0006).
        builder.HasOne<StaffUser>()
               .WithMany()
               .HasForeignKey(r => r.CreatedBy)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(r => r.Items)
               .WithOne()
               .HasForeignKey(i => i.RequestId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => r.DueDate);
        builder.HasIndex(r => r.Status);
    }
}
