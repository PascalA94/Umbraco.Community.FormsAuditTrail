using System.Text.Json;
using Umbraco.Community.FormsAuditTrail.BackgroundJobs;
using Umbraco.Community.FormsAuditTrail.Models;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// The reasons are fixed strings because the dashboard shows them as they are.
/// </summary>
public sealed record EntryAuditHealthReport(
    bool Healthy,
    IReadOnlyList<string> Reasons,
    DateTime? LastWriteFailureUtc,
    int WriteFailuresSinceStart,
    DateTime? LastJobRunUtc);

/// <summary>
/// Whether the entry audit is recording (design E18, SEC-02, SEC-09, SEC-I1). Write failures are held in memory on
/// purpose: a database that cannot be written cannot store its own failure. They reach the database afterwards as an
/// <c>AuditGap</c> row, and the daily job's outcome comes from <c>job.lastRun</c>, so a restart loses nothing that
/// matters. Per server, so on a load-balanced site each node reports its own writes.
/// </summary>
public sealed class EntryAuditHealth
{
    public const string WriteFailuresReason = "writeFailures";
    public const string MigrationsPendingReason = "migrationsPending";
    public const string JobNotRunReason = "jobNotRun";
    public const string JobStepFailedReason = "jobStepFailed";

    private static readonly TimeSpan JobStaleAfter = TimeSpan.FromHours(48);

    private readonly IAuditStateStore _stateStore;
    private readonly TimeProvider _time;
    private readonly DateTime _startedUtc;
    private readonly object _gate = new();

    private int _writeFailuresSinceStart;
    private int _migrationsPending;
    private DateTime? _lastFailureUtc;
    private DateTime? _lastSuccessUtc;
    private int _gapCount;
    private DateTime _gapFirstUtc;
    private readonly HashSet<AccessEventType> _gapTypes = [];

    public EntryAuditHealth(IAuditStateStore stateStore, TimeProvider time)
    {
        _stateStore = stateStore;
        _time = time;
        _startedUtc = time.GetUtcNow().UtcDateTime;
    }

    public bool MigrationsPending
    {
        get => Volatile.Read(ref _migrationsPending) == 1;
        set => Volatile.Write(ref _migrationsPending, value ? 1 : 0);
    }

    public int WriteFailuresSinceStart => Volatile.Read(ref _writeFailuresSinceStart);

    public DateTime? LastWriteFailureUtc
    {
        get { lock (_gate) { return _lastFailureUtc; } }
    }

    /// <remarks>
    /// Contract: a writer calls this after every successful write and then <see cref="TakePendingGap"/>. Health reads as
    /// recovered only once the pending failures have been taken, so skipping the second call leaves it red.
    /// </remarks>
    public void RecordWriteSuccess()
    {
        lock (_gate)
        {
            _lastSuccessUtc = _time.GetUtcNow().UtcDateTime;
        }
    }

    /// <remarks>
    /// The form id is accepted so callers cannot be asked to drop it later, but it is not kept: the gap row records
    /// counts, times and event types, and holding ids for an unbounded outage would grow without limit.
    /// </remarks>
    public void RecordWriteFailure(AccessEventType type, Guid formId)
    {
        _ = formId;
        Interlocked.Increment(ref _writeFailuresSinceStart);
        lock (_gate)
        {
            DateTime now = _time.GetUtcNow().UtcDateTime;
            if (_gapCount == 0)
            {
                _gapFirstUtc = now;
            }

            _gapCount++;
            _gapTypes.Add(type);
            _lastFailureUtc = now;
        }
    }

    /// <summary>
    /// Returns and clears the failures since the last success. Call it after every successful write, straight after
    /// <see cref="RecordWriteSuccess"/>. If the gap row then cannot be stored, hand the gap back with
    /// <see cref="RestorePendingGap"/> so the outage is not forgotten.
    /// </summary>
    public AuditGapInfo? TakePendingGap()
    {
        lock (_gate)
        {
            if (_gapCount == 0)
            {
                return null;
            }

            var gap = new AuditGapInfo(_gapCount, _gapFirstUtc, _lastFailureUtc!.Value, _gapTypes.OrderBy(t => (int)t).Select(t => t.ToString()).ToArray());
            _gapCount = 0;
            _gapTypes.Clear();
            return gap;
        }
    }

    /// <summary>
    /// Puts a gap back after its row could not be written. Health stays unhealthy (<c>writeFailures</c>) until a later
    /// write stores the gap, which is deliberate: an unrecorded outage is exactly what the signal exists to flag.
    /// Does not count as a new write failure.
    /// </summary>
    public void RestorePendingGap(AuditGapInfo gap)
    {
        ArgumentNullException.ThrowIfNull(gap);
        lock (_gate)
        {
            if (_gapCount == 0 || gap.FirstUtc < _gapFirstUtc)
            {
                _gapFirstUtc = gap.FirstUtc;
            }

            _gapCount += gap.Count;
            foreach (string name in gap.EventTypes)
            {
                if (Enum.TryParse(name, out AccessEventType type))
                {
                    _gapTypes.Add(type);
                }
            }

            if (_lastFailureUtc is null || gap.LastUtc > _lastFailureUtc)
            {
                _lastFailureUtc = gap.LastUtc;
            }
        }
    }

    public async Task<EntryAuditHealthReport> GetReportAsync(DateTime nowUtc, CancellationToken ct)
    {
        var reasons = new List<string>();
        DateTime? lastFailure;
        lock (_gate)
        {
            lastFailure = _lastFailureUtc;
            // Failures counted since the last success, rather than comparing two timestamps: the two can be equal on a
            // coarse clock, and a failure that lands in the same tick as a success must not read as recovered.
            if (_gapCount > 0 || (_lastFailureUtc.HasValue && _lastFailureUtc.Value > (_lastSuccessUtc ?? DateTime.MinValue)))
            {
                reasons.Add(WriteFailuresReason);
            }
        }

        if (MigrationsPending)
        {
            reasons.Add(MigrationsPendingReason);
        }

        JobRunState? lastRun = await ReadLastRunAsync(ct);
        if (lastRun is null ? nowUtc - _startedUtc > JobStaleAfter : nowUtc - lastRun.EndedUtc > JobStaleAfter)
        {
            reasons.Add(JobNotRunReason);
        }

        if (lastRun is not null && lastRun.Steps.Values.Any(v => v.StartsWith("failed", StringComparison.Ordinal)))
        {
            reasons.Add(JobStepFailedReason);
        }

        return new EntryAuditHealthReport(reasons.Count == 0, reasons, lastFailure, WriteFailuresSinceStart, lastRun?.EndedUtc);
    }

    private async Task<JobRunState?> ReadLastRunAsync(CancellationToken ct)
    {
        try
        {
            var json = await _stateStore.GetAsync(AuditRetentionJob.LastRunStateKey, ct);
            JobRunState? state = json is null ? null : JsonSerializer.Deserialize<JobRunState>(json);
            // A row without steps deserialises with Steps null. Reading it would throw outside this try, so it counts as no run.
            return state?.Steps is null ? null : state;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cancellation is rethrown so a caller that gave up is not told the state was unreadable.
            // The health check must not become a second failure: an unreadable state row is treated as no run yet, which
            // only turns red once the site has been up for 48 hours. The write and migration reasons cover a down database.
            return null;
        }
    }
}
