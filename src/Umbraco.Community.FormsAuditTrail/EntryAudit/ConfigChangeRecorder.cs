using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Records changes to the entry audit options as <c>ConfigChanged</c> rows. It exists because switching the audit off in
/// configuration would otherwise leave a silent gap with nothing to show who or when (SEC-07, E17). It writes even when
/// <c>EntryAudit:Enabled</c> is false, because the switch-off itself is the event that has to be evidenced.
/// </summary>
/// <remarks>
/// Singleton: it consumes the singleton writer and state store. Three mechanisms keep the evidence from being lost:
/// <list type="bullet">
/// <item>It does nothing until migrations have run (the composer registers it after <c>RunAuditMigration</c>, and it also
/// checks the runtime level and migration health), so a missing table is "retry later" and never a write failure.</item>
/// <item>A transition that cannot be written is queued in memory with its own timestamp and retried before the next
/// comparison and on a timer. Without the queue, a failed "off" followed by an "on" would write only "on" and lose the
/// off period. The stored snapshot, one per server, only guards against duplicates across restarts.</item>
/// <item>Every recorded value is capped (<see cref="MaxValueLength"/>), so the detail never grows past the writer's
/// limit and loses the Config that is the whole point of the row.</item>
/// </list>
/// Each server records its own configuration, hence the machine name in the snapshot key.
/// </remarks>
internal sealed class ConfigChangeRecorder : INotificationAsyncHandler<UmbracoApplicationStartedNotification>, IDisposable
{
    internal const string SnapshotKey = "config.snapshot";
    internal const int MaxValueLength = 200;
    internal const int MaxPending = 100;
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    /// <summary>How often a skip that health does not already report is logged again; the retry runs every five minutes.</summary>
    internal static readonly TimeSpan NotReadyWarningInterval = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions SnapshotJson = new();

    private readonly IOptionsMonitor<FormsAuditTrailOptions> _options;
    private readonly IAuditStateStore _state;
    private readonly IEntryAuditWriter _writer;
    private readonly EntryAuditHealth _health;
    private readonly IRuntimeState _runtime;
    private readonly TimeProvider _time;
    private readonly ILogger<ConfigChangeRecorder> _logger;
    private readonly string _server;
    private bool _notReadyWarned;
    private DateTimeOffset? _lastNotReadyWarning;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private readonly Queue<PendingChange> _pending = new();
    private Dictionary<string, string?>? _baseline;
    private bool _snapshotDirty;
    private IDisposable? _subscription;
    private ITimer? _retryTimer;
    private Task _last = Task.CompletedTask;
    private bool _disposed;

    public ConfigChangeRecorder(
        IOptionsMonitor<FormsAuditTrailOptions> options,
        IAuditStateStore state,
        IEntryAuditWriter writer,
        EntryAuditHealth health,
        IRuntimeState runtime,
        TimeProvider time,
        ILogger<ConfigChangeRecorder> logger,
        string? machineName = null)
    {
        _options = options;
        _state = state;
        _writer = writer;
        _health = health;
        _runtime = runtime;
        _time = time;
        _logger = logger;
        // Capped like every other recorded value; a machine name is short in practice but is operator-controlled.
        _server = Cap(machineName ?? Environment.MachineName)!;
    }

    private sealed record PendingChange(DateTime OccurredUtc, Dictionary<string, ConfigValueChange> Changes);

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            // Once only: a repeat notification must not stack a second subscription and double every comparison.
            if (!_disposed && _subscription is null)
            {
                _subscription = _options.OnChange((_, _) => Schedule());
            }
        }

        await CompareAsync(cancellationToken);
    }

    /// <summary>Waits for the comparison started by the latest options change or retry. Tests use it; production never needs to.</summary>
    internal Task IdleAsync()
    {
        lock (_lock)
        {
            return _last;
        }
    }

    internal int PendingCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                // A second Dispose must not log the same pending items again.
                return;
            }

            if (_pending.Count > 0)
            {
                // The queue is memory only because the database is what is down. After this the Error log is the only
                // trace of these transitions, so it names the options and times (never more than the option values).
                string summary = string.Join("; ", _pending.Select(p =>
                    $"{p.OccurredUtc:O} {string.Join(',', p.Changes.Select(c => $"{c.Key}:{c.Value.Old ?? "(not set)"}->{c.Value.New ?? "(not set)"}"))}"));
                _logger.LogError(
                    "FormsAuditTrail is shutting down with {Count} configuration changes not yet written; they will be lost: {Transitions}",
                    _pending.Count,
                    summary);
            }

            _disposed = true;
            _subscription?.Dispose();
            _subscription = null;
            _retryTimer?.Dispose();
            _retryTimer = null;
        }

        _gate.Dispose();
    }

    private void Schedule()
    {
        // CompareAsync catches everything, so the discarded task cannot fault unobserved.
        Task run = CompareAsync(CancellationToken.None);
        lock (_lock)
        {
            _last = run;
        }
    }

    private void ArmRetry()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _retryTimer?.Dispose();
            _retryTimer = _time.CreateTimer(_ => Schedule(), null, RetryInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void LogNotReady()
    {
        // Pending migrations are already in the health report, so one line is enough. A runtime that is not at Run is
        // not reported anywhere else, so a long upgrade or a site stuck below Run must not go silent: one line an hour.
        if (_health.MigrationsPending)
        {
            if (_notReadyWarned)
            {
                return;
            }

            _notReadyWarned = true;
        }
        else
        {
            DateTimeOffset now = _time.GetUtcNow();
            if (_lastNotReadyWarning is { } last && now - last < NotReadyWarningInterval)
            {
                return;
            }

            _lastNotReadyWarning = now;
        }

        _logger.LogWarning("FormsAuditTrail is not recording configuration changes yet: the runtime is not at Run or migrations are pending. It will retry.");
    }

    internal async Task CompareAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        bool incomplete = false;
        try
        {
            if (_runtime.Level != RuntimeLevel.Run || _health.MigrationsPending)
            {
                // Not a failure: the tables may not exist yet. Nothing is read, written or counted against health.
                incomplete = true;
                LogNotReady();

                return;
            }

            _notReadyWarned = false;
            _lastNotReadyWarning = null;
            Dictionary<string, string?> current = Snapshot(_options.CurrentValue);
            _baseline ??= await ReadStoredAsync(cancellationToken);

            Dictionary<string, ConfigValueChange> changes = Diff(_baseline, current);
            bool changed = changes.Count > 0;
            if (changed)
            {
                LogChanges(changes);
                EnqueueBounded(new PendingChange(_time.GetUtcNow().UtcDateTime, changes));
                // Set here, not only on a failed save: a drain deferred to the timer finds no new change, and without this
                // flag it would write the row and never save the snapshot, so a restart would write the row again.
                _snapshotDirty = true;
            }

            // Advanced even when the write below fails: the queued item now carries that transition, so the next
            // comparison diffs from here and an off then on becomes two rows rather than one lost one.
            _baseline = current;

            await DrainAsync();
            if (PendingCount > 0)
            {
                incomplete = true;
                return;
            }

            if (_snapshotDirty)
            {
                incomplete = !await TrySaveSnapshotAsync(current);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            incomplete = true;
            // Never thrown: a start-up handler or options callback must not take the host down over an audit row.
            _health.RecordWriteFailure(AccessEventType.ConfigChanged, Guid.Empty);
            _logger.LogError("FormsAuditTrail could not record an entry audit configuration change ({ExceptionType}).", ex.GetType().Name);
        }
        finally
        {
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Shut down while a comparison was running; nothing left to release.
            }

            if (incomplete)
            {
                ArmRetry();
            }
        }
    }

    private void EnqueueBounded(PendingChange change)
    {
        lock (_lock)
        {
            if (_pending.Count >= MaxPending)
            {
                _pending.Dequeue();
                _logger.LogError("FormsAuditTrail dropped the oldest unwritten configuration change: more than {Max} are waiting.", MaxPending);
            }

            _pending.Enqueue(change);
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            PendingChange? next;
            lock (_lock)
            {
                next = _pending.Count > 0 ? _pending.Peek() : null;
            }

            if (next is null || !await TryWriteAsync(next))
            {
                return;
            }

            lock (_lock)
            {
                _pending.Dequeue();
            }
        }
    }

    private void LogChanges(Dictionary<string, ConfigValueChange> changes)
    {
        foreach (KeyValuePair<string, ConfigValueChange> change in changes)
        {
            // Values are option settings only (booleans, numbers, capped group aliases), never form data. Warning so an
            // operator reading the log sees a switch-off even if the audit database is the thing that is down.
            _logger.LogWarning(
                "FormsAuditTrail entry audit setting {Key} changed from {OldValue} to {NewValue}.",
                change.Key,
                change.Value.Old ?? "(not set)",
                change.Value.New ?? "(not set)");
        }
    }

    private async Task<bool> TryWriteAsync(PendingChange pending)
    {
        var write = new AccessWrite(
            AccessEventType.ConfigChanged,
            Guid.Empty,
            null,
            null,
            0,
            Guid.Empty,
            AuditUserNames.System,
            new EntryAuditDetail { Config = pending.Changes, Server = _server },
            [],
            [])
        {
            // The writer's dedupe hashes the detail's filter, which a config row lacks, so every config row would share
            // one key and a second change within the window (switch off, on, off) would be silently dropped.
            DedupeFilterHash = Guid.NewGuid().ToString("N"),
            OccurredUtc = pending.OccurredUtc,
        };

        try
        {
            AccessWriteResult result = await _writer.WriteAsync(write);
            // The writer has already recorded a Failed status in health and logged it.
            return result.Status == AccessWriteStatus.Written;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _health.RecordWriteFailure(AccessEventType.ConfigChanged, Guid.Empty);
            _logger.LogError("FormsAuditTrail could not store a configuration change row ({ExceptionType}).", ex.GetType().Name);
            return false;
        }
    }

    private async Task<bool> TrySaveSnapshotAsync(Dictionary<string, string?> current)
    {
        try
        {
            await _state.SetAsync(SnapshotKey, JsonSerializer.Serialize(current, SnapshotJson), CancellationToken.None);
            _snapshotDirty = false;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The row is already stored, so this is not a lost write and health is not touched. The snapshot is retried
            // on the next comparison; the worst case is one repeated row after a restart, never a missing one.
            _snapshotDirty = true;
            _logger.LogError("FormsAuditTrail could not save its configuration snapshot ({ExceptionType}).", ex.GetType().Name);
            return false;
        }
    }

    private async Task<Dictionary<string, string?>> ReadStoredAsync(CancellationToken cancellationToken)
    {
        string? json = await _state.GetAsync(SnapshotKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json, SnapshotJson) ?? [];
        }
        catch (JsonException)
        {
            // A corrupt snapshot is treated as missing: recording the current values is safer than recording nothing.
            return [];
        }
    }

    private static Dictionary<string, ConfigValueChange> Diff(Dictionary<string, string?> stored, Dictionary<string, string?> current)
    {
        var changes = new Dictionary<string, ConfigValueChange>(StringComparer.Ordinal);
        foreach (string key in EntryAuditOptions.RecordedKeys)
        {
            stored.TryGetValue(key, out string? oldValue);
            current.TryGetValue(key, out string? newValue);
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                // camelCase to match every other key in the detail JSON; the snapshot keeps the property names.
                changes[JsonNamingPolicy.CamelCase.ConvertName(key)] = new ConfigValueChange(oldValue, newValue);
            }
        }

        return changes;
    }

    private static Dictionary<string, string?> Snapshot(FormsAuditTrailOptions all)
    {
        EntryAuditOptions options = all.EntryAudit;
        var snapshot = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (string key in EntryAuditOptions.RecordedKeys)
        {
            PropertyInfo property = typeof(EntryAuditOptions).GetProperty(key)!;
            // The csv switch is recorded as the effective value: while the nested one is unset, the top-level
            // EnableCsvExport decides, and changing it must still leave a ConfigChanged row.
            object? value = key == nameof(EntryAuditOptions.EnableCsvExport) ? all.EntryAuditCsvEnabled : property.GetValue(options);
            snapshot[key] = Cap(Format(value));
        }

        return snapshot;
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        // Group aliases are order-insensitive, so sort to keep a reordered list from reading as a change.
        IEnumerable<string> list => string.Join(',', list.Order(StringComparer.Ordinal)),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>
    /// A long value becomes a length and a short hash. Left whole it could push the detail past the writer's size limit,
    /// where truncation drops Config first and the row would no longer say that auditing was switched off. The hash
    /// still changes whenever the value does, so the change is detected.
    /// </summary>
    private static string? Cap(string? value)
    {
        if (value is null || value.Length <= MaxValueLength)
        {
            return value;
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
        int items = value.Count(c => c == ',') + 1;
        return $"{items} items, sha256:{hash}";
    }
}
