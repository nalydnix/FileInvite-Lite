using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileInviteLite.Api.Data.Configurations;

public sealed class UploadedFileConfiguration : IEntityTypeConfiguration<UploadedFile>
{
    public void Configure(EntityTypeBuilder<UploadedFile> builder)
    {
        builder.ToTable("UploadedFiles");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.OriginalName).HasMaxLength(260).IsRequired();
        builder.Property(f => f.ContentType).HasMaxLength(128).IsRequired();
        builder.Property(f => f.SizeBytes).IsRequired();
        builder.Property(f => f.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(f => f.StorageKey).HasMaxLength(128).IsRequired();

        // SQLite hands back a DateTime of Kind Unspecified; everything stored here is UTC.
        builder.Property(f => f.UploadedAt)
               .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
               .IsRequired();

        // No cascade anywhere: nothing in the MVP deletes a domain row (ADR 0006).
        builder.HasOne<ChecklistItem>()
               .WithMany(i => i.Files)
               .HasForeignKey(f => f.ChecklistItemId)
               .OnDelete(DeleteBehavior.Restrict);

        // The same key must never be reused (ADR 0005).
        builder.HasIndex(f => f.StorageKey).IsUnique();

        // Not unique: two clients may legitimately upload the same file and must stay
        // independently erasable (ADR 0005).
        builder.HasIndex(f => f.Sha256);
        builder.HasIndex(f => f.ChecklistItemId);
    }
}
