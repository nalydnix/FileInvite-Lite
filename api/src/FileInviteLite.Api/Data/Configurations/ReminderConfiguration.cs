using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FileInviteLite.Api.Data.Configurations;

public sealed class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
    public void Configure(EntityTypeBuilder<Reminder> builder)
    {
        builder.ToTable("Reminders");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Reason).HasMaxLength(200).IsRequired();

        // SQLite hands back a DateTime of Kind Unspecified; everything stored here is UTC.
        builder.Property(r => r.SentAt)
               .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
               .IsRequired();

        // No cascade anywhere: nothing in the MVP deletes a domain row (ADR 0006).
        builder.HasOne<DocumentRequest>()
               .WithMany(r => r.Reminders)
               .HasForeignKey(r => r.RequestId)
               .OnDelete(DeleteBehavior.Restrict);

        // The cool-down query (S5.2) reads the latest reminder per request, in SQL.
        builder.HasIndex(r => new { r.RequestId, r.SentAt });
    }
}
