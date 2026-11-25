using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class AuditStateStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    private readonly TestDatabase _db = new();
    private readonly ServiceProvider _services;
    private readonly AuditStateStore _store;

    public AuditStateStoreTests()
    {
        // A singleton instance is not disposed with the scope, so the in-memory database survives each call.
        _services = new ServiceCollection().AddSingleton<FormsAuditDbContext>(_db.Context).BuildServiceProvider();
        _store = new AuditStateStore(_services.GetRequiredService<IServiceScopeFactory>(), new FixedTime(Now));
    }

    [Fact]
    public async Task Set_ThenGet_RoundTrips()
    {
        await _store.SetAsync("k", "{\"a\":1}", CancellationToken.None);

        Assert.Equal("{\"a\":1}", await _store.GetAsync("k", CancellationToken.None));
    }

    [Fact]
    public async Task Get_UnknownKey_ReturnsNull() =>
        Assert.Null(await _store.GetAsync("missing", CancellationToken.None));

    [Fact]
    public async Task Set_SameKeyTwice_Upserts()
    {
        await _store.SetAsync("k", "one", CancellationToken.None);
        await _store.SetAsync("k", "two", CancellationToken.None);

        Assert.Equal("two", await _store.GetAsync("k", CancellationToken.None));
        Assert.Equal(1, await _db.Context.State.CountAsync());
    }

    [Fact]
    public async Task Set_TooLong_Throws()
    {
        var tooLong = new string('x', AccessColumnLimits.StateValue + 1);

        await Assert.ThrowsAsync<ArgumentException>(() => _store.SetAsync("k", tooLong, CancellationToken.None));
        Assert.Equal(0, await _db.Context.State.CountAsync());
    }

    [Fact]
    public async Task Set_AtTheLimit_IsAccepted() =>
        await _store.SetAsync("k", new string('x', AccessColumnLimits.StateValue), CancellationToken.None);

    [Fact]
    public async Task UpdatedUtc_ReadsBackAsUtc()
    {
        await _store.SetAsync("k", "v", CancellationToken.None);

        // Without clearing, the tracked instance would be returned and the converter would never run.
        _db.Context.ChangeTracker.Clear();
        AuditStateEntry row = await _db.Context.State.AsNoTracking().SingleAsync();

        Assert.Equal(DateTimeKind.Utc, row.UpdatedUtc.Kind);
        Assert.Equal(Now, row.UpdatedUtc);
    }

    // Interceptor that lets a second store insert the same key at the moment the first one is about to, which is the
    // window two servers race in.
    private sealed class InsertRaceInterceptor(Func<Task> rival) : DbCommandInterceptor
    {
        private int _fired;

        // EF may send an insert as a reader or as a non-query depending on the provider, so both are hooked.
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await FireOnFirstInsertAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await FireOnFirstInsertAsync(command);
            return result;
        }

        private async Task FireOnFirstInsertAsync(DbCommand command)
        {
            if (command.CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await rival();
            }
        }
    }

    [Fact]
    public async Task Set_TwoStoresInsertTheSameNewKey_RetriesAsUpdateAndDoesNotThrow()
    {
        var file = Path.Combine(Path.GetTempPath(), "formsaudit-state-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var connectionString = $"Data Source={file};Pooling=False";
            using (var create = new SqliteFormsAuditDbContext(new DbContextOptionsBuilder<SqliteFormsAuditDbContext>().UseSqlite(connectionString).Options))
            {
                create.Database.EnsureCreated();
            }

            ServiceProvider Provider(params IInterceptor[] interceptors) => new ServiceCollection()
                .AddScoped<FormsAuditDbContext>(_ => new SqliteFormsAuditDbContext(
                    new DbContextOptionsBuilder<SqliteFormsAuditDbContext>().UseSqlite(connectionString).AddInterceptors(interceptors).Options))
                .BuildServiceProvider();

            using ServiceProvider rivalServices = Provider();
            var rival = new AuditStateStore(rivalServices.GetRequiredService<IServiceScopeFactory>(), new FixedTime(Now));
            using ServiceProvider firstServices = Provider(new InsertRaceInterceptor(() => rival.SetAsync("k", "b", CancellationToken.None)));
            var first = new AuditStateStore(firstServices.GetRequiredService<IServiceScopeFactory>(), new FixedTime(Now));

            await first.SetAsync("k", "a", CancellationToken.None);

            Assert.Contains(await rival.GetAsync("k", CancellationToken.None), new[] { "a", "b" });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }
}
