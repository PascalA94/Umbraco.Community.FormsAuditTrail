using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Community.FormsAuditTrail.BackgroundJobs;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class AuditRetentionJobTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    private readonly CommandCounter _counter = new();
    private readonly TestDatabase _db;
    private readonly IFormsRecordReader _reader = Substitute.For<IFormsRecordReader>();
    private readonly IAuditStateStore _state = Substitute.For<IAuditStateStore>();
    private readonly ListLogger _logger = new();
    private readonly FormsAuditTrailOptions _options = new();
    private readonly List<ServiceProvider> _providers = new();

    public AuditRetentionJobTests()
    {
        _db = new TestDatabase(_counter);
        // Everything exists unless a test says otherwise.
        _reader.FindExistingAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlySet<Guid>>(call.Arg<IReadOnlyCollection<Guid>>().ToHashSet()));
    }

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    private sealed class ListLogger : ILogger<AuditRetentionJob>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private AuditRetentionJob Job()
    {
        ServiceProvider services = new ServiceCollection()
            .AddSingleton<FormsAuditDbContext>(_db.Context)
            .AddSingleton(_reader)
            .BuildServiceProvider();
        _providers.Add(services);
        IOptionsMonitor<FormsAuditTrailOptions> monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        monitor.CurrentValue.Returns(_options);
        return new AuditRetentionJob(
            services.GetRequiredService<IServiceScopeFactory>(), monitor, _state, new FixedTime(Now), _logger);
    }

    private async Task<JobRunState> LastRunAsync()
    {
        var json = (string)_state.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IAuditStateStore.SetAsync) && (string)c.GetArguments()[0]! == "job.lastRun")
            .GetArguments()[1]!;
        await Task.CompletedTask;
        return JsonSerializer.Deserialize<JobRunState>(json)!;
    }

    private void SeedAccess(int count, DateTime timestamp)
    {
        _db.Context.AccessEntries.AddRange(Enumerable.Range(0, count).Select(_ => new FormAccessEntry
        {
            Timestamp = timestamp,
            EventType = (int)AccessEventType.DetailViewed,
            FormId = Guid.NewGuid(),
            FormName = "F",
            UserKey = Guid.NewGuid(),
            UserName = "U",
        }));
        _db.Context.SaveChanges();
    }

    private FormAccessEntry SeedEdit(DateTime timestamp, Guid? recordId = null, int changes = 1) =>
        _db.AddAccess(
            AccessEventType.Edited,
            Guid.NewGuid(),
            timestamp,
            recordId: recordId ?? Guid.NewGuid(),
            changes: Enumerable.Range(0, changes).Select(_ => new FormEntryChange
            {
                FieldId = Guid.NewGuid(),
                FieldCaption = "Email",
                OldValue = "old",
                NewValue = "new",
            }).ToList());

    [Fact]
    public async Task DeleteEntriesOlderThan_RemovesOldEntriesAndCascadesChanges()
    {
        var formId = Guid.NewGuid();
        _db.AddEntry(formId, "Old form", DateTime.UtcNow.AddDays(-400), changeCount: 2);
        FormAuditEntry recent = _db.AddEntry(formId, "Recent form", DateTime.UtcNow.AddDays(-5), changeCount: 1);

        var deleted = await AuditRetentionJob.DeleteEntriesOlderThanAsync(_db.Context, DateTime.UtcNow.AddDays(-365));

        Assert.Equal(1, deleted);
        List<FormAuditEntry> remaining = await _db.Context.AuditEntries.ToListAsync();
        Assert.Equal(recent.Id, Assert.Single(remaining).Id);
        // The old entry's change rows must be gone too (database-level cascade)
        Assert.Equal(1, await _db.Context.AuditChanges.CountAsync());
    }

    [Fact]
    public async Task DeleteEntriesOlderThan_WithNothingOld_DeletesNothing()
    {
        _db.AddEntry(Guid.NewGuid(), "Form", DateTime.UtcNow.AddDays(-5));

        var deleted = await AuditRetentionJob.DeleteEntriesOlderThanAsync(_db.Context, DateTime.UtcNow.AddDays(-365));

        Assert.Equal(0, deleted);
        Assert.Equal(1, await _db.Context.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task DeleteAccessEntriesOlderThan_CascadesRecordsAndChanges()
    {
        FormAccessEntry old = _db.AddAccess(
            AccessEventType.Edited, Guid.NewGuid(), Now.AddDays(-400), recordId: Guid.NewGuid(),
            childRecordIds: [Guid.NewGuid()], changes: [new FormEntryChange { FieldId = Guid.NewGuid(), FieldCaption = "F" }]);
        FormAccessEntry recent = _db.AddAccess(AccessEventType.DetailViewed, Guid.NewGuid(), Now.AddDays(-1), childRecordIds: [Guid.NewGuid()]);

        var deleted = await AuditRetentionJob.DeleteAccessEntriesOlderThanAsync(_db.Context, Now.AddDays(-365));

        Assert.Equal(1, deleted);
        Assert.Equal(recent.Id, (await _db.Context.AccessEntries.SingleAsync()).Id);
        Assert.Equal(0, await _db.Context.EntryChanges.CountAsync());
        Assert.Equal(recent.Id, (await _db.Context.AccessRecords.SingleAsync()).AccessId);
        Assert.NotEqual(old.Id, recent.Id);
    }

    [Fact]
    public async Task DeleteAccessEntriesOlderThan_12000Rows_ThreeBatchedStatements()
    {
        SeedAccess(12000, Now.AddDays(-400));
        _counter.Commands.Clear();

        var deleted = await AuditRetentionJob.DeleteAccessEntriesOlderThanAsync(_db.Context, Now.AddDays(-365), batchSize: 5000);

        Assert.Equal(12000, deleted);
        Assert.Equal(3, _counter.Count("DELETE"));
        Assert.Equal(0, await _db.Context.AccessEntries.CountAsync());
    }

    private static readonly Guid SweepField = Guid.NewGuid();

    private FormAccessEntry SeedFieldEdit(Guid record, Guid field, string? oldValue, string? newValue, bool cleared, DateTime at) =>
        _db.AddAccess(
            AccessEventType.Edited, Guid.NewGuid(), at, recordId: record,
            changes: [new FormEntryChange { FieldId = field, FieldCaption = "Email", OldValue = oldValue, NewValue = newValue, Cleared = cleared }]);

    [Fact]
    public async Task ClearedValueSweep_ScrubsEarlierRowsOfAClearedField_OnlyThatRecordAndField() // @M4-FIX R73 item 2
    {
        Guid record = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        FormAccessEntry earlier = SeedFieldEdit(record, SweepField, "a", "b", false, Now.AddDays(-3));
        SeedFieldEdit(record, SweepField, null, null, true, Now.AddDays(-2));
        FormAccessEntry otherField = SeedFieldEdit(record, Guid.NewGuid(), "x", "y", false, Now.AddDays(-3));
        FormAccessEntry otherRecord = SeedFieldEdit(other, SweepField, "m", "n", false, Now.AddDays(-3));
        FormAccessEntry afterClear = SeedFieldEdit(record, SweepField, null, "refilled", false, Now.AddDays(-1));

        await Job().RunJobAsync();

        _db.Context.ChangeTracker.Clear();
        Dictionary<long, FormEntryChange> rows = await _db.Context.EntryChanges.AsNoTracking().ToDictionaryAsync(c => c.AccessId);
        Assert.Null(rows[earlier.Id].NewValue);
        Assert.Equal(Now, rows[earlier.Id].ValuesScrubbedAt);
        Assert.Equal("y", rows[otherField.Id].NewValue);
        Assert.Equal("n", rows[otherRecord.Id].NewValue);
        Assert.Equal("refilled", rows[afterClear.Id].NewValue);
        Assert.Equal("ok:1", (await LastRunAsync()).Steps["clearedValueSweep"]);
    }

    [Fact]
    public async Task ClearedValueSweep_ScrubsTheClearingRowsKeptOldValue() // @M4-FIX R77
    {
        Guid record = Guid.NewGuid();
        FormAccessEntry clearing = SeedFieldEdit(record, SweepField, "kept", null, true, Now.AddDays(-2));
        SeedFieldEdit(Guid.NewGuid(), SweepField, null, null, true, Now.AddDays(-2));

        await Job().RunJobAsync();

        _db.Context.ChangeTracker.Clear();
        FormEntryChange row = await _db.Context.EntryChanges.AsNoTracking().SingleAsync(c => c.AccessId == clearing.Id);
        Assert.Null(row.OldValue);
        Assert.Equal(Now, row.ValuesScrubbedAt);
        Assert.True(row.Cleared);
        Assert.Equal("ok:1", (await LastRunAsync()).Steps["clearedValueSweep"]);
        Assert.Equal(0, await AuditRetentionJob.ScrubEarlierValuesOfClearedFieldsAsync(_db.Context, Now));
    }

    [Fact]
    public async Task ClearedValueSweep_KeepClearedValues_LeavesTheClearingRowsOldValue() // @M4-FIX R77
    {
        _options.EntryAudit.KeepClearedValues = true;
        FormAccessEntry clearing = SeedFieldEdit(Guid.NewGuid(), SweepField, "kept", null, true, Now.AddDays(-2));

        await Job().RunJobAsync();

        _db.Context.ChangeTracker.Clear();
        FormEntryChange row = await _db.Context.EntryChanges.AsNoTracking().SingleAsync(c => c.AccessId == clearing.Id);
        Assert.Equal("kept", row.OldValue);
        Assert.Null(row.ValuesScrubbedAt);
    }

    [Fact]
    public async Task ClearedValueSweep_12000Rows_ThreeBatchedStatements() // @M4-FIX item 22
    {
        Guid record = Guid.NewGuid();
        _db.AddAccess(
            AccessEventType.Edited, Guid.NewGuid(), Now.AddDays(-3), recordId: record,
            changes: Enumerable.Range(0, 12000).Select(_ => new FormEntryChange
            {
                FieldId = SweepField, FieldCaption = "Email", OldValue = "a", NewValue = "b",
            }).ToList());
        SeedFieldEdit(record, SweepField, "b", null, true, Now.AddDays(-2));
        _counter.Commands.Clear();

        var swept = await AuditRetentionJob.ScrubEarlierValuesOfClearedFieldsAsync(_db.Context, Now, batchSize: 5000);

        // 12000 earlier rows plus the clearing row's kept old value "b" (R77).
        Assert.Equal(12001, swept);
        Assert.Equal(3, _counter.Count("UPDATE"));
        Assert.Equal(0, await _db.Context.EntryChanges.CountAsync(c => c.NewValue != null));
        Assert.Equal(0, await AuditRetentionJob.ScrubEarlierValuesOfClearedFieldsAsync(_db.Context, Now, batchSize: 5000));
    }

    [Fact]
    public async Task ClearedValueSweep_NothingPending_IsANoOp() // @M4-FIX R73 item 2
    {
        SeedFieldEdit(Guid.NewGuid(), SweepField, "a", "b", false, Now.AddDays(-3));

        await Job().RunJobAsync();

        Assert.Equal("ok:0", (await LastRunAsync()).Steps["clearedValueSweep"]);
        _db.Context.ChangeTracker.Clear();
        Assert.Equal("b", (await _db.Context.EntryChanges.AsNoTracking().SingleAsync()).NewValue);
    }

    [Fact]
    public async Task ClearedValueSweep_KeepClearedValues_IsSkipped() // @M4-FIX R73 item 2
    {
        _options.EntryAudit.KeepClearedValues = true;
        Guid record = Guid.NewGuid();
        FormAccessEntry earlier = SeedFieldEdit(record, SweepField, "a", "b", false, Now.AddDays(-3));
        SeedFieldEdit(record, SweepField, "b", null, true, Now.AddDays(-2));

        await Job().RunJobAsync();

        Assert.Equal("skipped", (await LastRunAsync()).Steps["clearedValueSweep"]);
        _db.Context.ChangeTracker.Clear();
        Assert.Equal("b", (await _db.Context.EntryChanges.AsNoTracking().SingleAsync(c => c.AccessId == earlier.Id)).NewValue);
    }

    [Fact]
    public async Task ExpireEditValues_NullsValuesOlderThanCutoff_KeepsRows()
    {
        FormAccessEntry old = SeedEdit(Now.AddDays(-100));
        FormAccessEntry recent = SeedEdit(Now.AddDays(-10));

        var expired = await AuditRetentionJob.ExpireEditValuesAsync(_db.Context, Now.AddDays(-90), Now);

        Assert.Equal(1, expired);
        _db.Context.ChangeTracker.Clear();
        List<FormEntryChange> rows = await _db.Context.EntryChanges.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        FormEntryChange expiredRow = rows.Single(r => r.AccessId == old.Id);
        Assert.Null(expiredRow.OldValue);
        Assert.Null(expiredRow.NewValue);
        Assert.Equal(Now, expiredRow.ValuesScrubbedAt);
        FormEntryChange newer = rows.Single(r => r.AccessId == recent.Id);
        Assert.Equal("new", newer.NewValue);
        Assert.Null(newer.ValuesScrubbedAt);
    }

    [Fact]
    public async Task ExpireEditValues_12000Rows_ThreeBatchedStatements()
    {
        SeedEdit(Now.AddDays(-100), changes: 12000);
        _counter.Commands.Clear();

        var expired = await AuditRetentionJob.ExpireEditValuesAsync(_db.Context, Now.AddDays(-90), Now, batchSize: 5000);

        Assert.Equal(12000, expired);
        Assert.Equal(3, _counter.Count("UPDATE"));
        Assert.Equal(0, await _db.Context.EntryChanges.CountAsync(c => c.ValuesScrubbedAt == null));
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task RetentionSteps_Batched_OnSqlServer()
    {
        using var database = SqlServerTestDatabase.Create();
        await using SqlServerFormsAuditDbContext context = database.CreateContext();
        await context.Database.MigrateAsync();
        // Five rows to expire (old) and five to scrub by record id (recent, so expiry leaves them alone).
        var scrubIds = new List<Guid>();
        for (var i = 0; i < 10; i++)
        {
            var old = i < 5;
            var recordId = Guid.NewGuid();
            if (!old)
            {
                scrubIds.Add(recordId);
            }

            var access = new FormAccessEntry
            {
                Timestamp = old ? Now.AddDays(-400) : Now.AddDays(-1),
                EventType = (int)AccessEventType.Edited,
                FormId = Guid.NewGuid(),
                FormName = "F",
                RecordId = recordId,
                UserKey = Guid.NewGuid(),
                UserName = "U",
            };
            access.Changes.Add(new FormEntryChange { FieldId = Guid.NewGuid(), FieldCaption = "C", OldValue = "o", NewValue = "n" });
            context.AccessEntries.Add(access);
        }

        await context.SaveChangesAsync();

        var expired = await AuditRetentionJob.ExpireEditValuesAsync(context, Now.AddDays(-90), Now, batchSize: 2);
        Assert.Equal(5, expired);
        Assert.Equal(5, await context.EntryChanges.CountAsync(c => c.NewValue != null));

        // 1,200 ids span three ErasureScrubber statements; ids that match nothing must not disturb the matching ones.
        List<Guid> toScrub = scrubIds.Concat(Enumerable.Range(0, 1195).Select(_ => Guid.NewGuid())).ToList();
        Assert.Equal(1200, toScrub.Count);
        Assert.Equal(5, await ErasureScrubber.ScrubRecordsAsync(context, toScrub, Now.AddMinutes(1), CancellationToken.None));
        Assert.Equal(0, await context.EntryChanges.CountAsync(c => c.NewValue != null));
        Assert.Equal(5, await context.EntryChanges.CountAsync(c => c.ValuesScrubbedAt == Now.AddMinutes(1)));

        var deleted = await AuditRetentionJob.DeleteAccessEntriesOlderThanAsync(context, Now.AddDays(-365), batchSize: 2);
        Assert.Equal(5, deleted);
        Assert.Equal(5, await context.AccessEntries.CountAsync());
        Assert.Equal(5, await context.EntryChanges.CountAsync());
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ClearedValueSweep_Batched_OnSqlServer() // @M4-FIX item 22
    {
        using var database = SqlServerTestDatabase.Create();
        await using SqlServerFormsAuditDbContext context = database.CreateContext();
        await context.Database.MigrateAsync();
        Guid record = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        Guid field = Guid.NewGuid();

        long Seed(Guid rec, Guid fld, string? oldValue, string? newValue, bool cleared, DateTime at)
        {
            var access = new FormAccessEntry
            {
                Timestamp = at, EventType = (int)AccessEventType.Edited, FormId = Guid.NewGuid(), FormName = "F",
                RecordId = rec, UserKey = Guid.NewGuid(), UserName = "U",
            };
            access.Changes.Add(new FormEntryChange { FieldId = fld, FieldCaption = "C", OldValue = oldValue, NewValue = newValue, Cleared = cleared });
            context.AccessEntries.Add(access);
            context.SaveChanges();
            return access.Id;
        }

        // Several earlier rows so batchSize 2 needs more than one pass.
        long[] earlier = Enumerable.Range(0, 5).Select(i => Seed(record, field, "a", "b", false, Now.AddDays(-5 + i))).ToArray();
        long otherRecord = Seed(other, field, "m", "n", false, Now.AddDays(-5));
        long clearing = Seed(record, field, "b", null, true, Now.AddDays(-2));
        long clearingNoValue = Seed(Guid.NewGuid(), field, null, null, true, Now.AddDays(-2));
        long refilled = Seed(record, field, null, "refilled", false, Now.AddDays(-1));
        context.ChangeTracker.Clear();

        // Five earlier rows plus the clearing row's kept old value (R77).
        Assert.Equal(6, await AuditRetentionJob.ScrubEarlierValuesOfClearedFieldsAsync(context, Now, batchSize: 2));

        context.ChangeTracker.Clear();
        Dictionary<long, FormEntryChange> rows = await context.EntryChanges.AsNoTracking().ToDictionaryAsync(c => c.AccessId);
        Assert.Null(rows[clearing].OldValue);
        Assert.Equal(Now, rows[clearing].ValuesScrubbedAt);
        Assert.Null(rows[clearingNoValue].ValuesScrubbedAt);
        foreach (var id in earlier)
        {
            Assert.Null(rows[id].NewValue);
            Assert.Equal(Now, rows[id].ValuesScrubbedAt);
        }

        Assert.Equal("n", rows[otherRecord].NewValue);
        Assert.Equal("refilled", rows[refilled].NewValue);
        Assert.Equal(0, await AuditRetentionJob.ScrubEarlierValuesOfClearedFieldsAsync(context, Now, batchSize: 2));
    }

    private async Task SeedUnscrubbedEditsAsync(int count)
    {
        _db.Context.EntryChanges.AddRange(Enumerable.Range(0, count).Select(_ => new FormEntryChange
        {
            FieldId = Guid.NewGuid(),
            FieldCaption = "C",
            NewValue = "n",
            Access = new FormAccessEntry
            {
                Timestamp = Now,
                EventType = (int)AccessEventType.Edited,
                FormId = Guid.NewGuid(),
                FormName = "F",
                RecordId = Guid.NewGuid(),
                UserKey = Guid.NewGuid(),
                UserName = "U",
            },
        }));
        await _db.Context.SaveChangesAsync();
    }

    private void ReaderFindsNothing() =>
        _reader.FindExistingAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()));

    private void ProbeReturns(Guid? probe, bool probeFound)
    {
        _reader.FindAnyExistingIdAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(probe));
        _reader.FindExistingAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => probe != null && ids.Count == 1 && ids.Contains(probe.Value)), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<Guid>>(probeFound ? new HashSet<Guid> { probe!.Value } : new HashSet<Guid>()));
    }

    [Fact]
    public async Task ReconcileErasures_ManyCandidatesAllMissingAndProbeFound_ScrubsAll()
    {
        await SeedUnscrubbedEditsAsync(75);
        ReaderFindsNothing();
        ProbeReturns(Guid.NewGuid(), probeFound: true);

        Assert.Equal(75, await AuditRetentionJob.ReconcileErasuresAsync(_db.Context, _reader, Now));
    }

    [Fact]
    public async Task ReconcileErasures_ProbeNotFound_ThrowsAndScrubsNothing()
    {
        await SeedUnscrubbedEditsAsync(75);
        ReaderFindsNothing();
        ProbeReturns(Guid.NewGuid(), probeFound: false);

        await Assert.ThrowsAsync<ImplausibleReconciliationException>(
            () => AuditRetentionJob.ReconcileErasuresAsync(_db.Context, _reader, Now));

        _db.Context.ChangeTracker.Clear();
        Assert.Equal(0, await _db.Context.EntryChanges.CountAsync(c => c.ValuesScrubbedAt != null));
    }

    [Fact]
    public async Task ReconcileErasures_EmptyFormsTable_ScrubsAll()
    {
        await SeedUnscrubbedEditsAsync(75);
        ReaderFindsNothing();
        ProbeReturns(null, probeFound: false);

        Assert.Equal(75, await AuditRetentionJob.ReconcileErasuresAsync(_db.Context, _reader, Now));
    }

    [Fact]
    public async Task ReconcileErasures_NothingMissing_DoesNotProbe()
    {
        await SeedUnscrubbedEditsAsync(3);

        Assert.Equal(0, await AuditRetentionJob.ReconcileErasuresAsync(_db.Context, _reader, Now));

        await _reader.DidNotReceive().FindAnyExistingIdAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileErasures_ProbeReadThrows_ScrubsNothing()
    {
        await SeedUnscrubbedEditsAsync(3);
        ReaderFindsNothing();
        _reader.FindAnyExistingIdAsync(Arg.Any<CancellationToken>()).Returns<Task<Guid?>>(_ => throw new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => AuditRetentionJob.ReconcileErasuresAsync(_db.Context, _reader, Now));

        _db.Context.ChangeTracker.Clear();
        Assert.Equal(0, await _db.Context.EntryChanges.CountAsync(c => c.ValuesScrubbedAt != null));
    }

    [Fact]
    public async Task ReconcileErasures_RecordEditedManyTimes_QueriesEachPageOnceAndKeepsIdsDistinct()
    {
        var recordId = Guid.NewGuid();
        SeedEdit(Now.AddDays(-1), recordId, changes: 1200);
        var seen = new List<int>();
        _reader.FindExistingAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seen.Add(call.Arg<IReadOnlyCollection<Guid>>().Count);
                return Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());
            });

        Assert.Equal(1200, await AuditRetentionJob.ReconcileErasuresAsync(_db.Context, _reader, Now));
        Assert.All(seen, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task RunJob_ImplausibleReconciliation_RecordsFailedStepAndScrubsNothing()
    {
        await SeedUnscrubbedEditsAsync(5);
        ReaderFindsNothing();
        ProbeReturns(Guid.NewGuid(), probeFound: false);

        await Job().RunJobAsync();

        Assert.Equal("failed:ImplausibleReconciliationException", (await LastRunAsync()).Steps["reconciliation"]);
        Assert.Equal(0, await _db.Context.EntryChanges.CountAsync(c => c.ValuesScrubbedAt != null));
    }

    [Fact]
    public async Task ReconcileErasures_ScrubsOnlyRecordsMissingFromForms()
    {
        var present = Guid.NewGuid();
        var gone = Guid.NewGuid();
        SeedEdit(Now.AddDays(-1), present);
        SeedEdit(Now.AddDays(-1), gone);
        _reader.FindExistingAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid> { present }));

        var scrubbed = await AuditRetentionJob.ReconcileErasuresAsync(_db.Context, _reader, Now);

        Assert.Equal(1, scrubbed);
        _db.Context.ChangeTracker.Clear();
        List<FormEntryChange> rows = await _db.Context.EntryChanges.AsNoTracking().Include(c => c.Access).ToListAsync();
        Assert.Equal("new", rows.Single(r => r.Access!.RecordId == present).NewValue);
        Assert.Null(rows.Single(r => r.Access!.RecordId == gone).NewValue);
    }

    [Fact]
    public async Task ReconcileErasures_QueriesInBatchesOf500()
    {
        _db.Context.EntryChanges.AddRange(Enumerable.Range(0, 1200).Select(_ => new FormEntryChange
        {
            FieldId = Guid.NewGuid(),
            FieldCaption = "C",
            NewValue = "n",
            Access = new FormAccessEntry
            {
                Timestamp = Now,
                EventType = (int)AccessEventType.Edited,
                FormId = Guid.NewGuid(),
                FormName = "F",
                RecordId = Guid.NewGuid(),
                UserKey = Guid.NewGuid(),
                UserName = "U",
            },
        }));
        await _db.Context.SaveChangesAsync();

        await AuditRetentionJob.ReconcileErasuresAsync(_db.Context, _reader, Now);

        await _reader.Received(3).FindExistingAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        await _reader.Received(1).FindExistingAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 200), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileErasures_ReaderThrows_ScrubsNothingAndMarksStepFailed()
    {
        SeedEdit(Now.AddDays(-1));
        _reader.FindExistingAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlySet<Guid>>>(_ => throw new InvalidOperationException("db down"));

        await Job().RunJobAsync();

        _db.Context.ChangeTracker.Clear();
        Assert.Equal("new", (await _db.Context.EntryChanges.AsNoTracking().SingleAsync()).NewValue);
        Assert.Equal("failed:InvalidOperationException", (await LastRunAsync()).Steps["reconciliation"]);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ReconcileErasures_ReaderThrowsOnSecondBatch_ScrubsNothing()
    {
        _db.Context.EntryChanges.AddRange(Enumerable.Range(0, 700).Select(_ => new FormEntryChange
        {
            FieldId = Guid.NewGuid(),
            FieldCaption = "C",
            NewValue = "n",
            Access = new FormAccessEntry
            {
                Timestamp = Now,
                EventType = (int)AccessEventType.Edited,
                FormId = Guid.NewGuid(),
                FormName = "F",
                RecordId = Guid.NewGuid(),
                UserKey = Guid.NewGuid(),
                UserName = "U",
            },
        }));
        await _db.Context.SaveChangesAsync();
        var calls = 0;
        _reader.FindExistingAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>())
                : throw new InvalidOperationException("db down"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => AuditRetentionJob.ReconcileErasuresAsync(_db.Context, _reader, Now));

        _db.Context.ChangeTracker.Clear();
        Assert.Equal(0, await _db.Context.EntryChanges.CountAsync(c => c.ValuesScrubbedAt != null));
    }

    [Fact]
    public async Task RunJob_WithDefinitionRetentionOff_StillRunsEntryAuditSteps()
    {
        _options.RetentionDays = 0;
        SeedAccess(1, Now.AddDays(-400));

        await Job().RunJobAsync();

        Assert.Equal(0, await _db.Context.AccessEntries.CountAsync());
        JobRunState run = await LastRunAsync();
        Assert.Equal("skipped", run.Steps["definitionRetention"]);
        Assert.Equal("ok:1", run.Steps["accessRetention"]);
    }

    [Fact]
    public async Task RunJob_EntryAuditRetentionZero_KeepsAccessRowsForever()
    {
        _options.EntryAudit.RetentionDays = 0;
        SeedAccess(1, Now.AddDays(-4000));

        await Job().RunJobAsync();

        Assert.Equal(1, await _db.Context.AccessEntries.CountAsync());
        Assert.Equal("skipped", (await LastRunAsync()).Steps["accessRetention"]);
    }

    [Fact]
    public async Task RunJob_EntryAuditDisabled_StillRunsRetention()
    {
        _options.EntryAudit.Enabled = false;
        SeedAccess(1, Now.AddDays(-400));

        await Job().RunJobAsync();

        Assert.Equal(0, await _db.Context.AccessEntries.CountAsync());
    }

    [Fact]
    public async Task RunJob_OneStepThrows_OtherStepsStillRun()
    {
        FormAccessEntry old = SeedEdit(Now.AddDays(-100));
        // Still inside the expiry window, so reconciliation has a candidate and reaches the throwing reader.
        SeedEdit(Now.AddDays(-1));
        _reader.FindExistingAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlySet<Guid>>>(_ => throw new InvalidOperationException("db down"));

        await Job().RunJobAsync();

        Assert.Equal(Now, (await _db.Context.EntryChanges.AsNoTracking().SingleAsync(c => c.AccessId == old.Id)).ValuesScrubbedAt);
        JobRunState run = await LastRunAsync();
        Assert.Equal("ok:1", run.Steps["valueExpiry"]);
        Assert.StartsWith("failed:", run.Steps["reconciliation"]);
    }

    [Fact]
    public async Task RunJob_DeletesOver1000AccessRows_LogsWarning()
    {
        SeedAccess(1001, Now.AddDays(-400));

        await Job().RunJobAsync();

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("1001"));
    }

    [Fact]
    public async Task RunJob_WritesJobLastRunStateWithStepOutcomes()
    {
        _options.RetentionDays = 30;
        _db.AddEntry(Guid.NewGuid(), "Old", Now.AddDays(-60));

        await Job().RunJobAsync();

        JobRunState run = await LastRunAsync();
        Assert.Equal(Now, run.StartedUtc);
        Assert.Equal(Now, run.EndedUtc);
        Assert.Equal("ok:1", run.Steps["definitionRetention"]);
        Assert.Equal("ok:0", run.Steps["accessRetention"]);
        Assert.Equal("ok:0", run.Steps["valueExpiry"]);
        Assert.Equal("ok:0", run.Steps["reconciliation"]);
    }

    [Fact]
    public async Task RunJob_StateStoreThrows_DoesNotFailTheJob()
    {
        _state.SetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("state down"));

        await Job().RunJobAsync();

        Assert.Contains(_logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    public void Dispose()
    {
        foreach (ServiceProvider provider in _providers)
        {
            provider.Dispose();
        }

        _db.Dispose();
    }
}
