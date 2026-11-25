using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Umbraco.Community.FormsAuditTrail.Models;

namespace Umbraco.Community.FormsAuditTrail.Persistence;

/// <summary>
/// Audit trail database context. Never registered directly; the composer registers a
/// provider-specific derived context (<see cref="SqliteFormsAuditDbContext"/> or
/// <see cref="SqlServerFormsAuditDbContext"/>) matching the configured Umbraco database provider,
/// mapped to this type for consumers.
/// </summary>
public abstract class FormsAuditDbContext : DbContext
{
    public const string EntriesTableName = "formsAuditTrailEntries";
    public const string ChangesTableName = "formsAuditTrailChanges";
    public const string AccessTableName = "formsAuditTrailAccess";
    public const string AccessRecordsTableName = "formsAuditTrailAccessRecords";
    public const string EntryChangesTableName = "formsAuditTrailEntryChanges";
    public const string StateTableName = "formsAuditTrailState";
    public const string MigrationsHistoryTableName = "formsAuditTrailMigrationsHistory";

    protected FormsAuditDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<FormAuditEntry> AuditEntries => Set<FormAuditEntry>();
    public DbSet<FormAuditChange> AuditChanges => Set<FormAuditChange>();
    public DbSet<FormAccessEntry> AccessEntries => Set<FormAccessEntry>();
    public DbSet<FormAccessRecord> AccessRecords => Set<FormAccessRecord>();
    public DbSet<FormEntryChange> EntryChanges => Set<FormEntryChange>();
    public DbSet<AuditStateEntry> State => Set<AuditStateEntry>();

    private static readonly ValueConverter<DateTime, DateTime> UtcConverter =
        new(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcConverter =
        new(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

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
            // DateTimeKind.Unspecified, so restore the UTC kind so they serialize with a Z suffix.
            entity.Property(e => e.Timestamp).HasConversion(UtcConverter);

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

        modelBuilder.Entity<FormAccessEntry>(entity =>
        {
            entity.ToTable(AccessTableName);
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.Timestamp).HasConversion(UtcConverter);
            entity.Property(e => e.FormName).HasMaxLength(AccessColumnLimits.FormName);
            entity.Property(e => e.UserName).HasMaxLength(AccessColumnLimits.UserName);

            entity.HasIndex(e => e.Timestamp);
            entity.HasIndex(e => e.FormId);
            entity.HasIndex(e => e.RecordId);
            entity.HasIndex(e => e.UserKey);
            entity.HasIndex(e => e.CorrelationId);
            // The common filter is one form, newest first.
            entity.HasIndex(e => new { e.FormId, e.Timestamp });

            entity.HasMany(e => e.Records)
                  .WithOne(r => r.Access)
                  .HasForeignKey(r => r.AccessId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.Changes)
                  .WithOne(c => c.Access)
                  .HasForeignKey(c => c.AccessId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FormAccessRecord>(entity =>
        {
            entity.ToTable(AccessRecordsTableName);
            entity.HasKey(e => new { e.AccessId, e.RecordId });
            // "Which accesses contained this record" runs on RecordId alone, which the composite key does not serve.
            entity.HasIndex(e => e.RecordId);
        });

        modelBuilder.Entity<FormEntryChange>(entity =>
        {
            entity.ToTable(EntryChangesTableName);
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.FieldCaption).HasMaxLength(AccessColumnLimits.FieldCaption);
            entity.Property(e => e.Cleared).HasDefaultValue(false);
            entity.Property(e => e.ValuesScrubbedAt).HasConversion(NullableUtcConverter);
            entity.HasIndex(e => e.AccessId);
        });

        modelBuilder.Entity<AuditStateEntry>(entity =>
        {
            entity.ToTable(StateTableName);
            entity.HasKey(e => e.Key);
            entity.Property(e => e.Key).HasMaxLength(AccessColumnLimits.StateKey);
            entity.Property(e => e.Value).HasMaxLength(AccessColumnLimits.StateValue);
            entity.Property(e => e.UpdatedUtc).HasConversion(UtcConverter);
        });
    }
}
