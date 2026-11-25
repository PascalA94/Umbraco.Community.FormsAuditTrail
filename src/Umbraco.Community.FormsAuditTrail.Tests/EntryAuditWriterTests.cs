using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public sealed class EntryAuditWriterTests : IDisposable
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid FormId = Guid.NewGuid();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"formsaudit-writer-{Guid.NewGuid():N}.db");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero));
    private readonly CapturingLogger _logger = new();
    private readonly FormsAuditTrailOptions _options = new();
    private readonly IFormService _forms = Substitute.For<IFormService>();
    private readonly IAuditStateStore _state = Substitute.For<IAuditStateStore>();
    private readonly ServiceProvider _provider;
    private readonly EntryAuditDeduplicator _dedupe;
    private readonly EntryAuditHealth _health;
    private bool _breakDatabase;
    private bool _failGapSave;

    public EntryAuditWriterTests()
    {
        var services = new ServiceCollection();
        // A file database, not a shared in-memory connection: the concurrency test needs one connection per scope.
        services.AddScoped<FormsAuditDbContext>(_ => Open());
        services.AddScoped(_ => _forms);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using (SqliteFormsAuditDbContext setup = Open())
        {
            setup.Database.EnsureCreated();
        }

        IOptionsMonitor<FormsAuditTrailOptions> monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        monitor.CurrentValue.Returns(_options);
        _dedupe = new EntryAuditDeduplicator(monitor, _time);
        _health = new EntryAuditHealth(_state, _time);
        Writer = new EntryAuditWriter(_provider.GetRequiredService<IServiceScopeFactory>(), _dedupe, _health, monitor, _time, _logger);
    }

    private EntryAuditWriter Writer { get; }

    private SqliteFormsAuditDbContext Open()
    {
        if (_breakDatabase)
        {
            throw new InvalidOperationException("database unavailable");
        }

        return new SqliteFormsAuditDbContext(new DbContextOptionsBuilder<SqliteFormsAuditDbContext>().UseSqlite($"Data Source={_dbPath}").AddInterceptors(new GapSaveFailure(() => _failGapSave)).Options);
    }

    private SqliteFormsAuditDbContext Reader()
    {
        bool broken = _breakDatabase;
        _breakDatabase = false;
        try { return Open(); } finally { _breakDatabase = broken; }
    }

    private static AccessWrite Write(
        AccessEventType type = AccessEventType.ListViewed,
        Guid? form = null,
        string? formName = "Form",
        Guid? record = null,
        string userName = "Editor",
        EntryAuditDetail? detail = null,
        IReadOnlyCollection<Guid>? recordIds = null,
        IReadOnlyList<EntryFieldChange>? changes = null) =>
        new(type, form ?? FormId, formName, record, recordIds?.Count ?? 0, User, userName, detail, recordIds ?? [], changes ?? []);

    [Fact]
    public async Task Write_ListViewed_StoresRowWithoutChildIdsByDefault() // @M2-T11
    {
        AccessWriteResult result = await Writer.WriteAsync(Write(recordIds: [Guid.NewGuid(), Guid.NewGuid()]));

        Assert.Equal(AccessWriteStatus.Written, result.Status);
        Assert.False(result.RecordIdsCaptured);
        using SqliteFormsAuditDbContext db = Reader();
        FormAccessEntry row = await db.AccessEntries.Include(a => a.Records).SingleAsync();
        Assert.Equal(result.AccessId, row.Id);
        Assert.Equal((int)AccessEventType.ListViewed, row.EventType);
        Assert.Equal(2, row.RecordCount);
        Assert.Equal(User, row.UserKey);
        Assert.Equal(DateTimeKind.Utc, row.Timestamp.Kind);
        Assert.Empty(row.Records);
    }

    [Fact]
    public async Task Write_Edited_StoresChangesIncludingClearedFlag()
    {
        Guid field = Guid.NewGuid();
        AccessWriteResult result = await Writer.WriteAsync(Write(
            AccessEventType.Edited,
            record: Guid.NewGuid(),
            changes: [new EntryFieldChange(field, "Email", "a@b.test", "", false, true)]));

        Assert.Equal(AccessWriteStatus.Written, result.Status);
        using SqliteFormsAuditDbContext db = Reader();
        FormEntryChange change = (await db.AccessEntries.Include(a => a.Changes).SingleAsync()).Changes.Single();
        Assert.Equal(field, change.FieldId);
        Assert.Equal("Email", change.FieldCaption);
        Assert.True(change.Cleared);
    }

    [Fact]
    public async Task Write_DuplicateDetailView_ReturnsDeduplicatedAndWritesNothing()
    {
        AccessWrite view = Write(AccessEventType.DetailViewed, record: Guid.NewGuid());

        Assert.Equal(AccessWriteStatus.Written, (await Writer.WriteAsync(view)).Status);
        AccessWriteResult second = await Writer.WriteAsync(view);

        Assert.Equal(AccessWriteStatus.Deduplicated, second.Status);
        Assert.Null(second.AccessId);
        using SqliteFormsAuditDbContext db = Reader();
        Assert.Equal(1, await db.AccessEntries.CountAsync());
    }

    [Fact]
    public async Task Write_UnresolvedForm_WritesGuidEmptyWithFlag() // T7
    {
        AccessWriteResult result = await Writer.WriteAsync(Write(form: Guid.Empty, formName: null, detail: new EntryAuditDetail { FormUnresolved = true }));

        Assert.Equal(AccessWriteStatus.Written, result.Status);
        using SqliteFormsAuditDbContext db = Reader();
        FormAccessEntry row = await db.AccessEntries.SingleAsync();
        Assert.Equal(Guid.Empty, row.FormId);
        Assert.Equal("(unresolved form)", row.FormName);
        Assert.Contains("\"formUnresolved\":true", row.Detail);
    }

    [Fact]
    public async Task Write_EmptyFormWithoutFlag_UsesAccessLogName()
    {
        await Writer.WriteAsync(Write(AccessEventType.AuditViewed, form: Guid.Empty, formName: null));

        using SqliteFormsAuditDbContext db = Reader();
        Assert.Equal("(access log)", (await db.AccessEntries.SingleAsync()).FormName);
    }

    [Fact]
    public async Task Write_DbThrows_ReturnsFailed_RecordsHealth_LogsErrorWithUserEventFormCount() // T8
    {
        _breakDatabase = true;

        AccessWriteResult result = await Writer.WriteAsync(Write(AccessEventType.DetailViewed, record: Guid.NewGuid(), recordIds: [Guid.NewGuid()]));

        Assert.Equal(AccessWriteStatus.Failed, result.Status);
        Assert.Equal(1, _health.WriteFailuresSinceStart);
        (LogLevel level, string message) = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains(User.ToString(), message);
        Assert.Contains("DetailViewed", message);
        Assert.Contains(FormId.ToString(), message);
        Assert.Contains("1", message);
    }

    [Fact]
    public async Task Write_FailedWrite_ReleasesDedupeSoTheNextViewIsWritten() // R33(2)
    {
        AccessWrite view = Write(AccessEventType.DetailViewed, record: Guid.NewGuid());
        _breakDatabase = true;
        Assert.Equal(AccessWriteStatus.Failed, (await Writer.WriteAsync(view)).Status);
        _breakDatabase = false;

        Assert.Equal(AccessWriteStatus.Written, (await Writer.WriteAsync(view)).Status);
    }

    [Fact]
    public async Task Write_SuccessAfterFailures_WritesOneAuditGapRow() // E12
    {
        _breakDatabase = true;
        await Writer.WriteAsync(Write(AccessEventType.Exported));
        await Writer.WriteAsync(Write(AccessEventType.Edited));
        _breakDatabase = false;

        await Writer.WriteAsync(Write());

        using SqliteFormsAuditDbContext db = Reader();
        FormAccessEntry gap = await db.AccessEntries.SingleAsync(a => a.EventType == (int)AccessEventType.AuditGap);
        Assert.Equal(Guid.Empty, gap.FormId);
        Assert.Equal(Guid.Empty, gap.UserKey);
        Assert.Contains("\"count\":2", gap.Detail);
        Assert.Contains("Edited", gap.Detail);
        Assert.Contains("Exported", gap.Detail);
        Assert.Equal(2, await db.AccessEntries.CountAsync());

        await Writer.WriteAsync(Write(AccessEventType.Exported));
        Assert.Equal(1, await db.AccessEntries.CountAsync(a => a.EventType == (int)AccessEventType.AuditGap));
    }

    [Fact]
    public async Task Write_TimestampsComeFromTheInjectedClock() // @M2-FIX A5
    {
        _breakDatabase = true;
        await Writer.WriteAsync(Write(AccessEventType.Exported));
        _breakDatabase = false;
        _time.Advance(TimeSpan.FromMinutes(5));
        DateTime expected = _time.GetUtcNow().UtcDateTime;

        AccessWriteResult result = await Writer.WriteAsync(Write(AccessEventType.Exported));

        using SqliteFormsAuditDbContext db = Reader();
        Assert.Equal(expected, (await db.AccessEntries.SingleAsync(a => a.Id == result.AccessId)).Timestamp);
        Assert.Equal(expected, (await db.AccessEntries.SingleAsync(a => a.EventType == (int)AccessEventType.AuditGap)).Timestamp);
    }

    [Fact]
    public async Task Write_SkipDedupe_WritesEveryTimeAndLeavesTheCacheUntouched() // @M2-FIX A2
    {
        AccessWrite view = Write(AccessEventType.DetailViewed, record: Guid.NewGuid());

        Assert.Equal(AccessWriteStatus.Written, (await Writer.WriteAsync(view with { SkipDedupe = true })).Status);
        Assert.Equal(AccessWriteStatus.Written, (await Writer.WriteAsync(view with { SkipDedupe = true })).Status);

        // Had the skipped writes been recorded in the cache, this ordinary view would now be suppressed.
        Assert.Equal(AccessWriteStatus.Written, (await Writer.WriteAsync(view)).Status);
        Assert.Equal(AccessWriteStatus.Deduplicated, (await Writer.WriteAsync(view)).Status);
    }

    [Fact]
    public async Task Write_SkipDedupe_FailedWrite_DoesNotReleaseAnotherViewersEntry() // @M2-FIX A2
    {
        AccessWrite view = Write(AccessEventType.DetailViewed, record: Guid.NewGuid());
        Assert.Equal(AccessWriteStatus.Written, (await Writer.WriteAsync(view)).Status);

        _breakDatabase = true;
        Assert.Equal(AccessWriteStatus.Failed, (await Writer.WriteAsync(view with { SkipDedupe = true })).Status);
        _breakDatabase = false;

        Assert.Equal(AccessWriteStatus.Deduplicated, (await Writer.WriteAsync(view)).Status);
    }

    [Fact]
    public async Task Write_GapRowSaveFails_KeepsTheGapPendingAndHealthUnhealthy() // R34
    {
        _state.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);
        _breakDatabase = true;
        await Writer.WriteAsync(Write(AccessEventType.Exported));
        _breakDatabase = false;
        _failGapSave = true;

        AccessWriteResult result = await Writer.WriteAsync(Write(AccessEventType.ListViewed));

        // The event itself landed; only the gap row did not, and the outage must stay visible.
        Assert.Equal(AccessWriteStatus.Written, result.Status);
        using (SqliteFormsAuditDbContext db = Reader())
        {
            Assert.Equal(0, await db.AccessEntries.CountAsync(a => a.EventType == (int)AccessEventType.AuditGap));
        }

        EntryAuditHealthReport report = await _health.GetReportAsync(_time.GetUtcNow().UtcDateTime, CancellationToken.None);
        Assert.Contains(EntryAuditHealth.WriteFailuresReason, report.Reasons);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("gap row"));

        _failGapSave = false;
        await Writer.WriteAsync(Write(AccessEventType.Edited));
        using SqliteFormsAuditDbContext after = Reader();
        FormAccessEntry gap = await after.AccessEntries.SingleAsync(a => a.EventType == (int)AccessEventType.AuditGap);
        Assert.Contains("\"count\":1", gap.Detail);
        report = await _health.GetReportAsync(_time.GetUtcNow().UtcDateTime, CancellationToken.None);
        Assert.DoesNotContain(EntryAuditHealth.WriteFailuresReason, report.Reasons);
    }

    [Fact]
    public async Task Write_LongStrings_TruncatedWithOriginalLengths()
    {
        string longText = new('x', 600);

        await Writer.WriteAsync(Write(
            AccessEventType.Edited,
            formName: longText,
            userName: longText,
            record: Guid.NewGuid(),
            changes: [new EntryFieldChange(Guid.NewGuid(), longText, "a", "b", false, false)]));

        using SqliteFormsAuditDbContext db = Reader();
        FormAccessEntry row = await db.AccessEntries.Include(a => a.Changes).SingleAsync();
        Assert.Equal(AccessColumnLimits.FormName, row.FormName.Length);
        Assert.Equal(AccessColumnLimits.UserName, row.UserName.Length);
        Assert.Equal(AccessColumnLimits.FieldCaption, row.Changes.Single().FieldCaption.Length);
        Assert.Contains("\"formName\":600", row.Detail);
        Assert.Contains("\"userName\":600", row.Detail);
        Assert.Contains("\"fieldCaption\":600", row.Detail);
    }

    [Fact]
    public async Task Write_EmitLogEvents_LogsOneEventWithoutValues() // SEC-08
    {
        _options.EntryAudit.EmitLogEvents = true;
        Guid recordId = Guid.NewGuid();

        AccessWriteResult result = await Writer.WriteAsync(Write(
            AccessEventType.Edited,
            record: recordId,
            recordIds: [recordId],
            detail: new EntryAuditDetail { FileName = "secret-file.pdf" },
            changes: [new EntryFieldChange(Guid.NewGuid(), "Email", "secret-old@x.test", "secret-new@x.test", false, false)]));

        (LogLevel level, string message) = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Information, level);
        Assert.StartsWith($"FormsAuditTrail.Access {result.AccessId} Edited {FormId} {User} 1", message);
        Assert.DoesNotContain(recordId.ToString(), message);
        Assert.DoesNotContain("secret", message);
    }

    [Fact]
    public async Task Write_EmitLogEventRecordIds_AddsRecordIdsOnlyWhenEnabled()
    {
        _options.EntryAudit.EmitLogEvents = true;
        _options.EntryAudit.EmitLogEventRecordIds = true;
        Guid recordId = Guid.NewGuid();

        await Writer.WriteAsync(Write(AccessEventType.ListViewed, recordIds: [recordId]));

        Assert.Contains(recordId.ToString(), Assert.Single(_logger.Entries).Message);
    }

    [Fact]
    public async Task Write_LogEventsOff_LogsNothing()
    {
        await Writer.WriteAsync(Write());

        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task Write_FormGone_UsesDeletedFormName()
    {
        _forms.Get(FormId).Returns((Form?)null);

        await Writer.WriteAsync(Write(AccessEventType.Deleted, formName: null, record: Guid.NewGuid()));

        using SqliteFormsAuditDbContext db = Reader();
        Assert.Equal("(deleted form)", (await db.AccessEntries.SingleAsync()).FormName);
    }

    [Fact]
    public async Task Write_FormNameMissing_ResolvesFromFormService()
    {
        _forms.Get(FormId).Returns(new Form { Name = "Contact us" });

        await Writer.WriteAsync(Write(formName: null));

        using SqliteFormsAuditDbContext db = Reader();
        Assert.Equal("Contact us", (await db.AccessEntries.SingleAsync()).FormName);
    }

    [Fact]
    public async Task Write_ConcurrentWritesFromOneSingleton_EachUsesOwnScope() // P2
    {
        AccessWriteResult[] results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => Writer.WriteAsync(Write(AccessEventType.Exported, userName: $"u{i}")))));

        Assert.All(results, r => Assert.Equal(AccessWriteStatus.Written, r.Status));
        using SqliteFormsAuditDbContext db = Reader();
        Assert.Equal(20, await db.AccessEntries.CountAsync());
    }

    public void Dispose()
    {
        _dedupe.Dispose();
        _provider.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    private sealed class GapSaveFailure(Func<bool> active) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (active() && eventData.Context!.ChangeTracker.Entries<FormAccessEntry>()
                    .Any(e => e.State == EntityState.Added && e.Entity.EventType == (int)AccessEventType.AuditGap))
            {
                throw new InvalidOperationException("gap save failed");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<EntryAuditWriter>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Queue { get; } = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => Queue.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Queue.Enqueue((logLevel, formatter(state, exception)));
    }
}
