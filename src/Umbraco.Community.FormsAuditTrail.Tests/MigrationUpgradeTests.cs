using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class MigrationUpgradeTests
{
    private const string SqliteInitialCreate = "20260715114743_InitialCreate";
    private const string SqlServerInitialCreate = "20260715114751_InitialCreate";

    [Fact]
    public async Task Upgrade_From1802Schema_KeepsDefinitionRowsAndAddsAccessTables()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var context = new SqliteFormsAuditDbContext(
            new DbContextOptionsBuilder<SqliteFormsAuditDbContext>()
                .UseSqlite(connection, o => o.MigrationsHistoryTable(FormsAuditDbContext.MigrationsHistoryTableName))
                .Options);

        // An 18.0.x site has only InitialCreate; EnsureCreated would hide the upgrade path this test exists for.
        await context.GetService<IMigrator>().MigrateAsync(SqliteInitialCreate);
        SeedDefinitionRows(connection);
        var before = ReadDefinitionRows(connection);

        await context.Database.MigrateAsync();

        Assert.Equal(before, ReadDefinitionRows(connection));
        await AssertAccessTablesQueryableAndEmpty(context);
        Assert.EndsWith("_AddEntryAccessAudit", (await context.Database.GetAppliedMigrationsAsync()).Last());
    }

    [Fact]
    public void AddEntryAccessAudit_IsPurelyAdditive()
    {
        AssertPurelyAdditive<SqliteFormsAuditDbContext>(
            new DbContextOptionsBuilder<SqliteFormsAuditDbContext>().UseSqlite("DataSource=:memory:").Options,
            o => new SqliteFormsAuditDbContext(o));
        AssertPurelyAdditive<SqlServerFormsAuditDbContext>(
            new DbContextOptionsBuilder<SqlServerFormsAuditDbContext>().UseSqlServer("Server=unused").Options,
            o => new SqlServerFormsAuditDbContext(o));
    }

    [Fact]
    public async Task Model_CascadesChildRowsAndRoundTripsUtc()
    {
        using var db = new TestDatabase();
        var formId = Guid.NewGuid();
        var timestamp = new DateTime(2026, 11, 1, 10, 30, 0, DateTimeKind.Utc);
        var access = db.AddAccess(
            AccessEventType.Exported,
            formId,
            timestamp,
            childRecordIds: new[] { Guid.NewGuid(), Guid.NewGuid() },
            changes: new[]
            {
                new FormEntryChange { FieldId = Guid.NewGuid(), FieldCaption = "Email", OldValue = "a", NewValue = "b", ValuesScrubbedAt = timestamp },
            });
        Assert.Equal(2, await db.Context.AccessRecords.CountAsync());
        Assert.Equal(1, await db.Context.EntryChanges.CountAsync());

        await db.Context.AccessEntries.Where(a => a.Id == access.Id).ExecuteDeleteAsync();

        Assert.Equal(0, await db.Context.AccessRecords.CountAsync());
        Assert.Equal(0, await db.Context.EntryChanges.CountAsync());

        var second = db.AddAccess(
            AccessEventType.Edited,
            formId,
            timestamp,
            changes: new[] { new FormEntryChange { FieldId = Guid.NewGuid(), FieldCaption = "Name", ValuesScrubbedAt = timestamp } });
        db.Context.ChangeTracker.Clear();
        var reloaded = await db.Context.AccessEntries.Include(a => a.Changes).SingleAsync(a => a.Id == second.Id);
        Assert.Equal(DateTimeKind.Utc, reloaded.Timestamp.Kind);
        Assert.Equal(DateTimeKind.Utc, reloaded.Changes.Single().ValuesScrubbedAt!.Value.Kind);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Upgrade_From1802Schema_OnSqlServer()
    {
        using var database = SqlServerTestDatabase.Create();
        await using var context = database.CreateContext();

        await context.GetService<IMigrator>().MigrateAsync(SqlServerInitialCreate);
        // ExecuteSqlRawAsync treats braces as format placeholders, so the empty JSON snapshot is doubled.
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO formsAuditTrailEntries (FormId, FormName, UserKey, UserName, Timestamp, EventType, BeforeSnapshot) " +
            "VALUES (NEWID(), 'F1', NEWID(), 'U', '2026-07-01T10:00:00', 1, '{{}}'), (NEWID(), 'F2', NEWID(), 'U', '2026-07-02T10:00:00', 1, '{{}}')");
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO formsAuditTrailChanges (AuditEntryId, ChangeType, PropertyPath, FriendlyDescription) " +
            "SELECT TOP 1 Id, 1, 'a', 'x' FROM formsAuditTrailEntries ORDER BY Id");
        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO formsAuditTrailChanges (AuditEntryId, ChangeType, PropertyPath, FriendlyDescription) " +
            "SELECT TOP 2 Id, 1, 'b', 'y' FROM formsAuditTrailEntries ORDER BY Id DESC");

        await context.Database.MigrateAsync();

        Assert.Equal(2, await context.AuditEntries.CountAsync());
        Assert.Equal(3, await context.AuditChanges.CountAsync());
        await AssertAccessTablesQueryableAndEmpty(context);
        Assert.EndsWith("_AddEntryAccessAudit", (await context.Database.GetAppliedMigrationsAsync()).Last());
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Model_CascadesChildRowsAndRoundTripsUtc_OnSqlServer()
    {
        using var database = SqlServerTestDatabase.Create();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var timestamp = new DateTime(2026, 11, 1, 10, 30, 0, DateTimeKind.Utc);
        var access = new FormAccessEntry
        {
            Timestamp = timestamp,
            EventType = (int)AccessEventType.Edited,
            FormId = Guid.NewGuid(),
            FormName = "F",
            UserKey = Guid.NewGuid(),
            UserName = "U",
        };
        access.Records.Add(new FormAccessRecord { RecordId = Guid.NewGuid() });
        access.Changes.Add(new FormEntryChange { FieldId = Guid.NewGuid(), FieldCaption = "Email", ValuesScrubbedAt = timestamp });
        context.AccessEntries.Add(access);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var reloaded = await context.AccessEntries.Include(a => a.Changes).SingleAsync();
        Assert.Equal(DateTimeKind.Utc, reloaded.Timestamp.Kind);
        Assert.Equal(DateTimeKind.Utc, reloaded.Changes.Single().ValuesScrubbedAt!.Value.Kind);

        await context.AccessEntries.ExecuteDeleteAsync();

        Assert.Equal(0, await context.AccessRecords.CountAsync());
        Assert.Equal(0, await context.EntryChanges.CountAsync());
    }

    private static void AssertPurelyAdditive<TContext>(DbContextOptions<TContext> options, Func<DbContextOptions<TContext>, TContext> factory)
        where TContext : DbContext
    {
        using var context = factory(options);
        var migrationsAssembly = context.GetService<IMigrationsAssembly>();
        var migration = migrationsAssembly.Migrations
            .Where(m => m.Key.EndsWith("_AddEntryAccessAudit", StringComparison.Ordinal))
            .Select(m => migrationsAssembly.CreateMigration(m.Value, context.Database.ProviderName!))
            .Single();

        Assert.NotEmpty(migration.UpOperations);
        Assert.All(migration.UpOperations, op => Assert.True(op is CreateTableOperation or CreateIndexOperation, op.GetType().Name));
        var touched = migration.UpOperations.OfType<CreateIndexOperation>().Select(o => o.Table)
            .Concat(migration.UpOperations.OfType<CreateTableOperation>().Select(o => o.Name));
        Assert.DoesNotContain(FormsAuditDbContext.EntriesTableName, touched);
        Assert.DoesNotContain(FormsAuditDbContext.ChangesTableName, touched);
    }

    private static async Task AssertAccessTablesQueryableAndEmpty(FormsAuditDbContext context)
    {
        Assert.Empty(await context.AccessEntries.ToListAsync());
        Assert.Empty(await context.AccessRecords.ToListAsync());
        Assert.Empty(await context.EntryChanges.ToListAsync());
        Assert.Empty(await context.State.ToListAsync());
    }

    private static void SeedDefinitionRows(SqliteConnection connection)
    {
        Execute(connection,
            "INSERT INTO formsAuditTrailEntries (FormId, FormName, UserKey, UserName, Timestamp, EventType, BeforeSnapshot) VALUES " +
            "('11111111-1111-1111-1111-111111111111', 'F1', '22222222-2222-2222-2222-222222222222', 'U', '2026-07-01 10:00:00', 1, '{}')," +
            "('33333333-3333-3333-3333-333333333333', 'F2', '22222222-2222-2222-2222-222222222222', 'U', '2026-07-02 10:00:00', 1, '{}')");
        Execute(connection,
            "INSERT INTO formsAuditTrailChanges (AuditEntryId, ChangeType, PropertyPath, FriendlyDescription) VALUES (1, 1, 'a', 'x'), (2, 1, 'b', 'y'), (2, 1, 'c', 'z')");
    }

    private static List<string> ReadDefinitionRows(SqliteConnection connection)
    {
        var rows = new List<string>();
        foreach (var sql in new[] { "SELECT * FROM formsAuditTrailEntries ORDER BY Id", "SELECT * FROM formsAuditTrailChanges ORDER BY Id" })
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "<null>" : reader.GetValue(i).ToString())));
            }
        }

        return rows;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
