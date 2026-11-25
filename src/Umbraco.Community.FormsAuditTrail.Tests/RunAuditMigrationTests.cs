using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class RunAuditMigrationTests : IDisposable
{
    [Fact]
    public async Task Migration_Succeeds_MigrationsPendingFalse()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var context = NewContext(connection);
        EntryAuditHealth health = NewHealth();
        health.MigrationsPending = true;

        await Handler(context, health).HandleAsync(null!, CancellationToken.None);

        Assert.False(health.MigrationsPending);
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Migration_Throws_AppKeepsRunning_MigrationsPendingTrue() // T15
    {
        // A path in a directory that does not exist makes SQLite fail when the context first connects.
        await using var context = NewContext(new SqliteConnection("Data Source=/nonexistent-dir-for-test/audit.db"));
        EntryAuditHealth health = NewHealth();

        await Handler(context, health).HandleAsync(null!, CancellationToken.None);

        Assert.True(health.MigrationsPending);
    }

    [Fact]
    public async Task Migration_MigrateThrowsButNothingIsPending_MigrationsPendingFalse() // R34
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var context = NewContext(connection, migrator: true);
        EntryAuditHealth health = NewHealth();
        // The migrator applies everything, then fails (as it can while writing history): nothing is left pending.
        ThrowingMigrator.AfterApply = async () =>
        {
            await using var plain = NewContext(connection);
            await plain.Database.MigrateAsync();
        };

        await Handler(context, health).HandleAsync(null!, CancellationToken.None);

        Assert.False(health.MigrationsPending);
    }

    [Fact]
    public async Task Migration_MigrateThrowsAndMigrationsRemain_MigrationsPendingTrue() // R34
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var context = NewContext(connection, migrator: true);
        EntryAuditHealth health = NewHealth();
        ThrowingMigrator.AfterApply = null;

        await Handler(context, health).HandleAsync(null!, CancellationToken.None);

        Assert.True(health.MigrationsPending);
    }

    [Fact]
    public async Task Migration_RuntimeNotAtRunLevel_LeavesHealthUntouched()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        await using var context = NewContext(connection);
        EntryAuditHealth health = NewHealth();

        await Handler(context, health, RuntimeLevel.Install).HandleAsync(null!, CancellationToken.None);

        Assert.False(health.MigrationsPending);
    }

    private static EntryAuditHealth NewHealth() => new(Substitute.For<IAuditStateStore>(), TimeProvider.System);

    private static SqliteFormsAuditDbContext NewContext(SqliteConnection connection, bool migrator = false)
    {
        DbContextOptionsBuilder<SqliteFormsAuditDbContext> builder = new DbContextOptionsBuilder<SqliteFormsAuditDbContext>()
            .UseSqlite(connection, o => o.MigrationsHistoryTable(FormsAuditDbContext.MigrationsHistoryTableName));
        if (migrator)
        {
            builder.ReplaceService<IMigrator, ThrowingMigrator>();
        }

        return new SqliteFormsAuditDbContext(builder.Options);
    }

    // The hook is static, so it is cleared after every test rather than leaking into the next one.
    public void Dispose() => ThrowingMigrator.AfterApply = null;

    /// <summary>Fails MigrateAsync after an optional side effect, standing in for a provider error after the work is done.</summary>
    private sealed class ThrowingMigrator : IMigrator
    {
        // Static because EF builds the service itself; the tests in this class do not run in parallel with each other.
        public static Func<Task>? AfterApply { get; set; }

        public void Migrate(string? targetMigration = null) => throw new NotSupportedException();

        public bool HasPendingModelChanges() => false;

        public async Task MigrateAsync(string? targetMigration = null, CancellationToken cancellationToken = default)
        {
            if (AfterApply is not null)
            {
                await AfterApply();
            }

            throw new InvalidOperationException("migrate failed");
        }

        public string GenerateScript(string? fromMigration = null, string? toMigration = null, MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default) => string.Empty;
    }

    private static RunAuditMigration Handler(FormsAuditDbContext context, EntryAuditHealth health, RuntimeLevel level = RuntimeLevel.Run)
    {
        // Registered as an instance so the handler's own scope does not dispose the context the test still reads.
        ServiceProvider services = new ServiceCollection().AddSingleton(context).BuildServiceProvider();
        IRuntimeState runtime = Substitute.For<IRuntimeState>();
        runtime.Level.Returns(level);
        return new RunAuditMigration(services.GetRequiredService<IServiceScopeFactory>(), runtime, NullLogger<RunAuditMigration>.Instance, health);
    }
}
