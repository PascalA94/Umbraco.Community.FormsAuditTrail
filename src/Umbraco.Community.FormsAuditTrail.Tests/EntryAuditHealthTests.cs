using System.Text.Json;
using NSubstitute;
using Umbraco.Community.FormsAuditTrail.BackgroundJobs;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class EntryAuditHealthTests
{
    private static readonly DateTime Start = new(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc);

    private readonly Clock _clock = new(Start);
    private readonly IAuditStateStore _state = Substitute.For<IAuditStateStore>();
    private readonly EntryAuditHealth _health;

    public EntryAuditHealthTests() => _health = new EntryAuditHealth(_state, _clock);

    private Task<EntryAuditHealthReport> Report() => _health.GetReportAsync(_clock.UtcNow, CancellationToken.None);

    private void StoreLastRun(DateTime ended, params (string Step, string Value)[] steps) =>
        _state.GetAsync(AuditRetentionJob.LastRunStateKey, Arg.Any<CancellationToken>()).Returns(JsonSerializer.Serialize(
            new JobRunState(ended.AddMinutes(-1), ended, steps.ToDictionary(s => s.Step, s => s.Value))));

    [Fact]
    public async Task Health_NoFailures_Healthy()
    {
        StoreLastRun(Start, ("definitionRetention", "ok:0"), ("reconciliation", "skipped"));

        EntryAuditHealthReport report = await Report();

        Assert.True(report.Healthy);
        Assert.Empty(report.Reasons);
        Assert.Equal(Start, report.LastJobRunUtc);
    }

    [Theory]
    [InlineData("{\"startedUtc\":\"2026-11-20T07:59:00Z\",\"endedUtc\":\"2026-11-20T08:00:00Z\"}")]
    [InlineData("{\"startedUtc\":\"2026-11-20T07:59:00Z\",\"endedUtc\":\"2026-11-20T08:00:00Z\",\"steps\":null}")]
    public async Task Health_StoredRunWithoutSteps_TreatedAsNoRun_DoesNotThrow(string json) // R34
    {
        _state.GetAsync(AuditRetentionJob.LastRunStateKey, Arg.Any<CancellationToken>()).Returns(json);

        EntryAuditHealthReport report = await Report();

        Assert.True(report.Healthy);
        Assert.Null(report.LastJobRunUtc);
    }

    [Fact]
    public async Task Health_CancelledStateRead_Propagates()
    {
        _state.GetAsync(AuditRetentionJob.LastRunStateKey, Arg.Any<CancellationToken>()).Returns<string?>(_ => throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(Report);
    }

    [Fact]
    public async Task Health_RestoredGap_StaysUnhealthyUntilTaken() // R34
    {
        StoreLastRun(Start);
        _health.RecordWriteFailure(AccessEventType.Exported, Guid.NewGuid());
        AuditGapInfo gap = _health.TakePendingGap()!;
        _health.RecordWriteSuccess();
        _health.RestorePendingGap(gap);

        Assert.Contains(EntryAuditHealth.WriteFailuresReason, (await Report()).Reasons);
        Assert.Equal(1, _health.TakePendingGap()!.Count);
        Assert.DoesNotContain(EntryAuditHealth.WriteFailuresReason, (await Report()).Reasons);
    }

    [Fact]
    public async Task Health_FailureAfterSuccess_UnhealthyWithWriteFailures() // T8
    {
        StoreLastRun(Start);
        _health.RecordWriteSuccess();
        _clock.Advance(TimeSpan.FromSeconds(5));
        _health.RecordWriteFailure(AccessEventType.DetailViewed, Guid.NewGuid());

        EntryAuditHealthReport report = await Report();

        Assert.False(report.Healthy);
        Assert.Equal(["writeFailures"], report.Reasons);
        Assert.Equal(1, report.WriteFailuresSinceStart);
        Assert.Equal(_clock.UtcNow, report.LastWriteFailureUtc);
    }

    [Fact]
    public async Task Health_FailureInTheSameTickAsAnEarlierSuccess_StaysUnhealthy()
    {
        StoreLastRun(Start);
        _health.RecordWriteSuccess();
        _health.RecordWriteFailure(AccessEventType.Edited, Guid.NewGuid());

        Assert.Contains("writeFailures", (await Report()).Reasons);
    }

    [Fact]
    public async Task Health_SuccessAfterFailure_HealthyAgain_GapReturnedOnce()
    {
        StoreLastRun(Start);
        _health.RecordWriteFailure(AccessEventType.ListViewed, Guid.NewGuid());
        DateTime first = _clock.UtcNow;
        _clock.Advance(TimeSpan.FromSeconds(10));
        _health.RecordWriteFailure(AccessEventType.Exported, Guid.NewGuid());
        _health.RecordWriteFailure(AccessEventType.ListViewed, Guid.NewGuid());
        DateTime last = _clock.UtcNow;
        _clock.Advance(TimeSpan.FromSeconds(10));
        _health.RecordWriteSuccess();

        AuditGapInfo? gap = _health.TakePendingGap();

        Assert.NotNull(gap);
        Assert.Equal(3, gap.Count);
        Assert.Equal(first, gap.FirstUtc);
        Assert.Equal(last, gap.LastUtc);
        Assert.Equal(["ListViewed", "Exported"], gap.EventTypes);
        Assert.Null(_health.TakePendingGap());
        EntryAuditHealthReport report = await Report();
        Assert.True(report.Healthy);
        Assert.Equal(3, report.WriteFailuresSinceStart);
    }

    [Fact]
    public async Task Health_MigrationsPending_Unhealthy() // T15
    {
        StoreLastRun(Start);
        _health.MigrationsPending = true;

        EntryAuditHealthReport report = await Report();

        Assert.Equal(["migrationsPending"], report.Reasons);
    }

    [Fact]
    public async Task Health_JobNotRunFor48h_Unhealthy() // SEC-I1
    {
        // Missing record: fine for the first 48 hours after start-up, red after.
        _clock.Advance(TimeSpan.FromHours(47));
        Assert.True((await Report()).Healthy);
        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(["jobNotRun"], (await Report()).Reasons);

        // A record older than 48 hours is the same signal.
        StoreLastRun(_clock.UtcNow.AddHours(-49), ("definitionRetention", "ok:0"));
        Assert.Equal(["jobNotRun"], (await Report()).Reasons);
    }

    [Fact]
    public async Task Health_JobStepFailed_Unhealthy() // SEC-I1
    {
        StoreLastRun(Start, ("definitionRetention", "ok:2"), ("reconciliation", "failed:ImplausibleReconciliationException"));

        EntryAuditHealthReport report = await Report();

        Assert.Equal(["jobStepFailed"], report.Reasons);
    }

    [Fact]
    public async Task Health_StateStoreThrows_DoesNotThrowAndTreatsRunAsMissing()
    {
        _state.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns<string?>(_ => throw new InvalidOperationException("db down"));

        Assert.True((await Report()).Healthy);
    }

    [Fact]
    public async Task Health_ConcurrentFailures_CountIsExact()
    {
        StoreLastRun(Start);

        Parallel.For(0, 1000, _ => _health.RecordWriteFailure(AccessEventType.DetailViewed, Guid.NewGuid()));

        Assert.Equal(1000, _health.WriteFailuresSinceStart);
        Assert.Equal(1000, _health.TakePendingGap()!.Count);
        Assert.Equal(1000, (await Report()).WriteFailuresSinceStart);
    }

    private sealed class Clock(DateTime start) : TimeProvider
    {
        public DateTime UtcNow { get; private set; } = start;

        public void Advance(TimeSpan by) => UtcNow += by;

        public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
    }
}
