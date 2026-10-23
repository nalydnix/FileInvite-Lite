using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileInviteLite.Api.Data.Configurations;

public sealed class ReviewDecisionConfiguration : IEntityTypeConfiguration<ReviewDecision>
{
    public void Configure(EntityTypeBuilder<ReviewDecision> builder)
    {
        builder.ToTable("ReviewDecisions");
        builder.HasKey(d => d.Id);

        // Stored as a string so that reordering the enum cannot silently reinterpret existing rows.
        builder.Property(d => d.Decision).HasConversion<string>().HasMaxLength(16).IsRequired();

        // Nullable at the column: "required on reject" is an endpoint rule (S4.5).
        builder.Property(d => d.Comment).HasMaxLength(2000);
        builder.Property(d => d.ReviewedBy).IsRequired();

        // SQLite hands back a DateTime of Kind Unspecified; everything stored here is UTC.
        builder.Property(d => d.ReviewedAt)
               .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
               .IsRequired();

        // No cascade anywhere: nothing in the MVP deletes a domain row (ADR 0006).
        builder.HasOne<ChecklistItem>()
               .WithMany(i => i.Decisions)
               .HasForeignKey(d => d.ChecklistItemId)
               .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(d => d.ChecklistItemId);
    }
}
