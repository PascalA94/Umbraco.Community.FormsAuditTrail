using Microsoft.EntityFrameworkCore;
using Umbraco.Community.FormsAuditTrail.Models;

namespace Umbraco.Community.FormsAuditTrail.Persistence;

/// <summary>
/// Audit trail database context. Never registered directly — the composer registers a
/// provider-specific derived context (<see cref="SqliteFormsAuditDbContext"/> or
/// <see cref="SqlServerFormsAuditDbContext"/>) matching the configured Umbraco database provider,
/// mapped to this type for consumers.
/// </summary>
public abstract class FormsAuditDbContext : DbContext
{
    public const string EntriesTableName = "formsAuditTrailEntries";
    public const string ChangesTableName = "formsAuditTrailChanges";
    public const string MigrationsHistoryTableName = "formsAuditTrailMigrationsHistory";

    protected FormsAuditDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<FormAuditEntry> AuditEntries => Set<FormAuditEntry>();
    public DbSet<FormAuditChange> AuditChanges => Set<FormAuditChange>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<FormAuditEntry>(entity =>
        {
            entity.ToTable(EntriesTableName);
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.FormName).HasMaxLength(500);
            entity.Property(e => e.UserName).HasMaxLength(500);

            // Timestamps are written as DateTime.UtcNow but come back from the database with
            // DateTimeKind.Unspecified — restore the UTC kind so they serialize with a Z suffix.
            entity.Property(e => e.Timestamp)
                  .HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.FormId);
            entity.HasIndex(e => e.UserKey);
            entity.HasMany(e => e.Changes)
                  .WithOne(c => c.AuditEntry)
                  .HasForeignKey(c => c.AuditEntryId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FormAuditChange>(entity =>
        {
            entity.ToTable(ChangesTableName);
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.PropertyPath).HasMaxLength(1000);
            entity.Property(e => e.FriendlyDescription).HasMaxLength(2000);
        });
    }
}
