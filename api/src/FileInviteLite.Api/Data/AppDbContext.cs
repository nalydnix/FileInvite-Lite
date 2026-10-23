using FileInviteLite.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FileInviteLite.Api.Data;

/// <summary>
/// DbSets and nothing else. Entity configuration lives in one
/// <see cref="IEntityTypeConfiguration{TEntity}"/> per entity under Data/Configurations and is
/// loaded by assembly scan, so adding an entity's configuration never edits this file (ADR 0002).
/// This file is a sequential-task hotspot — see CLAUDE.md rule 2.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<DocumentRequest> DocumentRequests => Set<DocumentRequest>();
    public DbSet<ChecklistItem> ChecklistItems => Set<ChecklistItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
