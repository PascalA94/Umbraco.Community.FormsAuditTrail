using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Forms.Core.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>Child record ids and export correlation (Task 11b). Uses a file database so each scope has its own connection.</summary>
public sealed class EntryAuditWriterBatchTests : IDisposable
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid FormId = Guid.NewGuid();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"formsaudit-batch-{Guid.NewGuid():N}.db");
    private readonly FormsAuditTrailOptions _options = new();
    private readonly ChildBatchProbe _probe = new();
    private readonly UpdateFailure _updateFailure = new();
    private readonly SelectFailure _selectFailure = new();
    private readonly ServiceProvider _provider;
    private readonly EntryAuditHealth _health;

    public EntryAuditWriterBatchTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<FormsAuditDbContext>(_ => Open());
        services.AddScoped(_ => Substitute.For<IFormService>());
        _provider = services.BuildServiceProvider();
        using (SqliteFormsAuditDbContext setup = Open())
        {
            setup.Database.EnsureCreated();
        }

        IOptionsMonitor<FormsAuditTrailOptions> monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        monitor.CurrentValue.Returns(_options);
        var dedupe = new EntryAuditDeduplicator(monitor, TimeProvider.System);
        _health = new EntryAuditHealth(Substitute.For<IAuditStateStore>(), TimeProvider.System);
        Writer = new EntryAuditWriter(_provider.GetRequiredService<IServiceScopeFactory>(), dedupe, _health, monitor, TimeProvider.System, NullLogger<EntryAuditWriter>.Instance);
    }

    private EntryAuditWriter Writer { get; }

    private SqliteFormsAuditDbContext Open() =>
        new(new DbContextOptionsBuilder<SqliteFormsAuditDbContext>().UseSqlite($"Data Source={_dbPath}").AddInterceptors(_probe, _updateFailure, _selectFailure).Options);

    private static AccessWrite Write(
        AccessEventType type,
        IReadOnlyCollection<Guid>? ids = null,
        Guid? user = null,
        string? fileName = null,
        Guid? record = null) =>
        new(type, FormId, "Form", record, ids?.Count ?? 0, user ?? User, "Editor",
            fileName is null ? null : new EntryAuditDetail { FileName = fileName }, ids ?? [], []);

    private static List<Guid> Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToList();

    [Fact]
    public async Task Write_ListViewedWithIds_StoresChildRows() // @M2-T11b
    {
        _options.EntryAudit.LogListRecordIds = true;
        List<Guid> ids = Ids(3);

        AccessWriteResult result = await Writer.WriteAsync(Write(AccessEventType.ListViewed, ids));

        Assert.True(result.RecordIdsCaptured);
        using SqliteFormsAuditDbContext db = Open();
        Assert.Equal(ids.Order(), await db.AccessRecords.Where(r => r.AccessId == result.AccessId).Select(r => r.RecordId).OrderBy(r => r).ToListAsync());
    }

    [Fact]
    public async Task Write_ListViewedOver500Ids_KeepsFirst500AndFlagsIncomplete() // @M2-T12 R36(2) SEC-10
    {
        _options.EntryAudit.LogListRecordIds = true;
        List<Guid> ids = Ids(EntryAuditWriter.MaxListRecordIds + 1);

        AccessWriteResult result = await Writer.WriteAsync(Write(AccessEventType.ListViewed, ids));

        Assert.Equal(AccessWriteStatus.Written, result.Status);
        Assert.False(result.RecordIdsCaptured);
        using SqliteFormsAuditDbContext db = Open();
        List<Guid> stored = await db.AccessRecords.Where(r => r.AccessId == result.AccessId).Select(r => r.RecordId).ToListAsync();
        Assert.Equal(EntryAuditWriter.MaxListRecordIds, stored.Count);
        Assert.All(stored, id => Assert.Contains(id, ids.Take(EntryAuditWriter.MaxListRecordIds)));
        FormAccessEntry parent = await db.AccessEntries.SingleAsync(a => a.Id == result.AccessId);
        Assert.Contains("\"recordIdsCaptured\":false", parent.Detail);
        Assert.Equal(ids.Count, parent.RecordCount);
    }

    [Fact]
    public async Task Write_ListViewedOver500Ids_DetailCorrectionFails_StillWrittenWithAllIdsAndNoHealthFailure() // @M2-T12 R37(7) R41
    {
        _options.EntryAudit.LogListRecordIds = true;
        _updateFailure.Fail = true;

        AccessWriteResult result = await Writer.WriteAsync(Write(AccessEventType.ListViewed, Ids(EntryAuditWriter.MaxListRecordIds + 1)));

        Assert.Equal(AccessWriteStatus.Written, result.Status);
        Assert.False(result.RecordIdsCaptured);
        // Every batch landed; only the stored flag could not be corrected, which the writer logs on its own.
        Assert.Equal(0, _health.WriteFailuresSinceStart);
        using SqliteFormsAuditDbContext db = Open();
        Assert.Equal(EntryAuditWriter.MaxListRecordIds, await db.AccessRecords.CountAsync(r => r.AccessId == result.AccessId));
    }

    [Fact]
    public async Task Write_Exported_SetsCorrelationAndChildRows()
    {
        AccessWriteResult result = await Writer.WriteAsync(Write(AccessEventType.Exported, Ids(5), fileName: "a.csv"));

        using SqliteFormsAuditDbContext db = Open();
        FormAccessEntry row = await db.AccessEntries.Include(a => a.Records).SingleAsync(a => a.Id == result.AccessId);
        Assert.NotNull(row.CorrelationId);
        Assert.NotEqual(Guid.Empty, row.CorrelationId);
        Assert.Equal(5, row.Records.Count);
        Assert.True(result.RecordIdsCaptured);
    }

    [Fact]
    public async Task Write_Download_CopiesCorrelationFromMatchingExport()
    {
        await Writer.WriteAsync(Write(AccessEventType.Exported, fileName: "other.csv"));
        AccessWriteResult export = await Writer.WriteAsync(Write(AccessEventType.Exported, fileName: "a.csv"));
        AccessWriteResult download = await Writer.WriteAsync(Write(AccessEventType.ExportDownloaded, fileName: "a.csv"));

        using SqliteFormsAuditDbContext db = Open();
        Guid? expected = (await db.AccessEntries.SingleAsync(a => a.Id == export.AccessId)).CorrelationId;
        Assert.NotNull(expected);
        Assert.Equal(expected, (await db.AccessEntries.SingleAsync(a => a.Id == download.AccessId)).CorrelationId);
    }

    [Fact]
    public async Task Write_Download_NoMatchingExport_NullCorrelation() // E6
    {
        await Writer.WriteAsync(Write(AccessEventType.Exported, user: Guid.NewGuid(), fileName: "a.csv"));
        AccessWriteResult download = await Writer.WriteAsync(Write(AccessEventType.ExportDownloaded, fileName: "a.csv"));

        using SqliteFormsAuditDbContext db = Open();
        Assert.Null((await db.AccessEntries.SingleAsync(a => a.Id == download.AccessId)).CorrelationId);
    }

    [Fact]
    public async Task Write_Download_DoesNotMatchOtherFileName()
    {
        await Writer.WriteAsync(Write(AccessEventType.Exported, fileName: "a.csv"));
        AccessWriteResult download = await Writer.WriteAsync(Write(AccessEventType.ExportDownloaded, fileName: "b.csv"));

        using SqliteFormsAuditDbContext db = Open();
        Assert.Null((await db.AccessEntries.SingleAsync(a => a.Id == download.AccessId)).CorrelationId);
    }

    [Fact]
    public async Task Write_ExportWithTenThousandRecordIds_StoresAll() // Review Focus 5
    {
        AccessWriteResult result = await Writer.WriteAsync(Write(AccessEventType.Exported, Ids(10_000), fileName: "big.csv"));

        Assert.True(result.RecordIdsCaptured);
        using SqliteFormsAuditDbContext db = Open();
        Assert.Equal(10_000, await db.AccessRecords.CountAsync(r => r.AccessId == result.AccessId));
    }

    [Fact]
    public async Task Write_Export50k_ChildFailure_KeepsParentWithRecordIdsCapturedFalse() // T5
    {
        _probe.FailOnChildBatch = 3;

        AccessWriteResult result = await Writer.WriteAsync(Write(AccessEventType.Exported, Ids(50_000), fileName: "huge.csv"));

        Assert.Equal(AccessWriteStatus.Written, result.Status);
        Assert.False(result.RecordIdsCaptured);
        Assert.Equal(1, _health.WriteFailuresSinceStart);
        using SqliteFormsAuditDbContext db = Open();
        // The lost child ids are an outage like any other, so the gap row records them once the parent has landed.
        Assert.Equal(1, await db.AccessEntries.CountAsync(a => a.EventType == (int)AccessEventType.AuditGap));
        FormAccessEntry parent = await db.AccessEntries.SingleAsync(a => a.Id == result.AccessId && a.EventType == (int)AccessEventType.Exported);
        Assert.Contains("\"recordIdsCaptured\":false", parent.Detail);
        Assert.Contains("\"fileName\":\"huge.csv\"", parent.Detail);
        Assert.Equal(2000, await db.AccessRecords.CountAsync(r => r.AccessId == result.AccessId));
    }

    [Fact]
    public async Task Write_ChildIds_SavedInBatchesOf1000()
    {
        await Writer.WriteAsync(Write(AccessEventType.Exported, Ids(2_500), fileName: "a.csv"));

        // One parent save, then 1,000 + 1,000 + 500.
        Assert.Equal(3, _probe.ChildSaves);
    }

    [Fact]
    public async Task Write_BulkDeleted_OneParentWithChildRows() // P8
    {
        List<Guid> ids = Ids(3);
        AccessWriteResult result = await Writer.WriteAsync(Write(AccessEventType.Deleted, ids));

        using SqliteFormsAuditDbContext db = Open();
        FormAccessEntry row = await db.AccessEntries.Include(a => a.Records).SingleAsync(a => a.Id == result.AccessId);
        Assert.Equal(3, row.RecordCount);
        Assert.Null(row.RecordId);
        Assert.Equal(3, row.Records.Count);
        Assert.Equal(1, await db.AccessEntries.CountAsync());
    }

    [Fact]
    public async Task Write_LoggerThrowsAfterSave_StillReportsWritten() // Task 11 follow-up
    {
        _options.EntryAudit.EmitLogEvents = true;
        IOptionsMonitor<FormsAuditTrailOptions> monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        monitor.CurrentValue.Returns(_options);
        var writer = new EntryAuditWriter(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new EntryAuditDeduplicator(monitor, TimeProvider.System),
            _health,
            monitor,
            TimeProvider.System,
            new ThrowingLogger());

        AccessWriteResult result = await writer.WriteAsync(Write(AccessEventType.Exported, fileName: "a.csv"));

        Assert.Equal(AccessWriteStatus.Written, result.Status);
        Assert.Equal(0, _health.WriteFailuresSinceStart);
        using SqliteFormsAuditDbContext db = Open();
        Assert.Equal(1, await db.AccessEntries.CountAsync());
    }

    private EntryAuditWriter WriterWith(Microsoft.Extensions.Logging.ILogger<EntryAuditWriter> logger, IOptionsMonitor<FormsAuditTrailOptions>? dedupeOptions = null)
    {
        IOptionsMonitor<FormsAuditTrailOptions> monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        monitor.CurrentValue.Returns(_options);
        return new EntryAuditWriter(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new EntryAuditDeduplicator(dedupeOptions ?? monitor, TimeProvider.System),
            _health,
            monitor,
            TimeProvider.System,
            logger);
    }

    [Fact]
    public async Task Write_ChildBatchFails_LoggerThrows_StillWritten() // @M2-FIX A3
    {
        _probe.FailOnChildBatch = 1;

        AccessWriteResult result = await WriterWith(new ThrowingLogger()).WriteAsync(Write(AccessEventType.Exported, Ids(3), fileName: "a.csv"));

        Assert.Equal(AccessWriteStatus.Written, result.Status);
        Assert.False(result.RecordIdsCaptured);
        // The lost child ids are counted once, by the batch catch, and not again as a failed write.
        Assert.Equal(1, _health.WriteFailuresSinceStart);
    }

    [Fact]
    public async Task Write_DedupeThrows_LoggerThrows_DoesNotEscapeAndStillWrites() // @M2-FIX A3
    {
        IOptionsMonitor<FormsAuditTrailOptions> broken = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        broken.CurrentValue.Returns(_ => throw new InvalidOperationException("options"));

        AccessWriteResult result = await WriterWith(new ThrowingLogger(), broken).WriteAsync(Write(AccessEventType.DetailViewed, record: Guid.NewGuid()));

        Assert.Equal(AccessWriteStatus.Written, result.Status);
    }

    [Fact]
    public async Task Write_StoreFails_LoggerThrows_ReturnsFailed() // @M2-FIX A3
    {
        _probe.FailParent = true;

        AccessWriteResult result = await WriterWith(new ThrowingLogger()).WriteAsync(Write(AccessEventType.Exported, fileName: "a.csv"));

        Assert.Equal(AccessWriteStatus.Failed, result.Status);
        Assert.Equal(1, _health.WriteFailuresSinceStart);
    }

    [Fact]
    public async Task Write_CorrelationLookupThrows_ParentWrittenWithNoCorrelation() // @M2-FIX B8 (L2)
    {
        await Writer.WriteAsync(Write(AccessEventType.Exported, fileName: "a.csv"));
        _selectFailure.Fail = true;

        AccessWriteResult download = await Writer.WriteAsync(Write(AccessEventType.ExportDownloaded, fileName: "a.csv"));

        _selectFailure.Fail = false;
        Assert.Equal(AccessWriteStatus.Written, download.Status);
        Assert.Equal(0, _health.WriteFailuresSinceStart);
        using SqliteFormsAuditDbContext db = Open();
        FormAccessEntry row = await db.AccessEntries.SingleAsync(a => a.Id == download.AccessId);
        Assert.Equal((int)AccessEventType.ExportDownloaded, row.EventType);
        Assert.Null(row.CorrelationId);
    }

    [Fact]
    public async Task Write_OverLengthNameEndingInEmoji_DoesNotSplitSurrogatePair() // Task 11 follow-up
    {
        // 499 letters then an emoji (two UTF-16 units) puts the high surrogate at the last allowed position.
        string name = new string('a', AccessColumnLimits.FormName - 1) + "\U0001F600";
        AccessWriteResult result = await Writer.WriteAsync(new AccessWrite(
            AccessEventType.Exported, FormId, name, null, 0, User, "Editor", null, [], []));

        using SqliteFormsAuditDbContext db = Open();
        FormAccessEntry row = await db.AccessEntries.SingleAsync(a => a.Id == result.AccessId);
        Assert.Equal(AccessColumnLimits.FormName - 1, row.FormName.Length);
        Assert.False(char.IsHighSurrogate(row.FormName[^1]));
        Assert.Contains($"\"formName\":{name.Length}", row.Detail);
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnectionCleanup();
    }

    private void SqliteConnectionCleanup()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string file in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    /// <summary>Counts saves that insert child rows and can fail the nth one.</summary>
    private sealed class ChildBatchProbe : SaveChangesInterceptor
    {
        public int ChildSaves;
        public int FailOnChildBatch;
        public bool FailParent;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailParent && eventData.Context!.ChangeTracker.Entries<FormAccessEntry>().Any(e => e.State == EntityState.Added))
            {
                throw new InvalidOperationException("parent save failed");
            }

            if (eventData.Context!.ChangeTracker.Entries<FormAccessRecord>().Any(e => e.State == EntityState.Added))
            {
                int n = Interlocked.Increment(ref ChildSaves);
                if (FailOnChildBatch == n)
                {
                    throw new InvalidOperationException("child batch failed");
                }
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger<EntryAuditWriter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger broke");
    }

    /// <summary>Fails plain queries (the correlation lookup) and nothing else: inserts on SQLite use RETURNING, not SELECT.</summary>
    private sealed class SelectFailure : DbCommandInterceptor
    {
        public bool Fail;

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Fail && command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("lookup failed");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>Fails the set-based detail correction (an UPDATE) and nothing else.</summary>
    private sealed class UpdateFailure : DbCommandInterceptor
    {
        public bool Fail;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fail && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("update failed");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
