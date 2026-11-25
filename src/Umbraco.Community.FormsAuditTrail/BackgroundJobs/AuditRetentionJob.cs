using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;

namespace Umbraco.Community.FormsAuditTrail.BackgroundJobs;

/// <summary>
/// What the last run did, stored under <see cref="AuditRetentionJob.LastRunStateKey"/> so the health signal can report a
/// step that keeps failing. Step values are <c>ok:&lt;count&gt;</c>, <c>skipped</c> or <c>failed:&lt;exception type&gt;</c>;
/// the exception message is left out because it can carry data.
/// </summary>
internal sealed record JobRunState(DateTime StartedUtc, DateTime EndedUtc, IReadOnlyDictionary<string, string> Steps);

/// <summary>
/// Daily clean-up in four independent steps (design 7.4): definition audit retention, access row retention, edit value
/// expiry and erasure reconciliation. A step whose days setting is zero is skipped; one step failing never stops the
/// others. The two audit features are independent, so turning <c>EntryAudit:Enabled</c> off does not stop clean-up of
/// rows already written.
/// </summary>
public class AuditRetentionJob : IRecurringBackgroundJob
{
    internal const string LastRunStateKey = "job.lastRun";

    private const int AccessDeleteWarningThreshold = 1000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<FormsAuditTrailOptions> _options;
    private readonly IAuditStateStore _stateStore;
    private readonly TimeProvider _time;
    private readonly ILogger<AuditRetentionJob> _logger;

    public AuditRetentionJob(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<FormsAuditTrailOptions> options,
        IAuditStateStore stateStore,
        TimeProvider time,
        ILogger<AuditRetentionJob> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _stateStore = stateStore;
        _time = time;
        _logger = logger;
    }

    public TimeSpan Period => TimeSpan.FromHours(24);

    public TimeSpan Delay => TimeSpan.FromMinutes(5);

    // Cleanup must only run on one server in a load-balanced setup
    public ServerRole[] ServerRoles => [ServerRole.Single, ServerRole.SchedulingPublisher];

    public event EventHandler PeriodChanged { add { } remove { } }

    public async Task RunJobAsync()
    {
        FormsAuditTrailOptions options = _options.CurrentValue;
        DateTime started = _time.GetUtcNow().UtcDateTime;
        var steps = new Dictionary<string, string>();

        // A scope per step: a failed step can leave its DbContext in a state the next step should not inherit.
        await RunStepAsync(steps, "definitionRetention", options.RetentionDays, async (scope, now) =>
        {
            var deleted = await DeleteEntriesOlderThanAsync(Db(scope), now.AddDays(-options.RetentionDays));
            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Forms Audit Trail retention: deleted {Count} audit entries older than {RetentionDays} days.",
                    deleted,
                    options.RetentionDays);
            }

            return deleted;
        });

        await RunStepAsync(steps, "accessRetention", options.EntryAudit.RetentionDays, async (scope, now) =>
        {
            var deleted = await DeleteAccessEntriesOlderThanAsync(Db(scope), now.AddDays(-options.EntryAudit.RetentionDays));
            if (deleted > AccessDeleteWarningThreshold)
            {
                // A sudden large delete is what a retention misconfiguration looks like (SEC-07).
                _logger.LogWarning(
                    "Forms Audit Trail retention: deleted {Count} access rows older than {RetentionDays} days, more than {Threshold} in one run.",
                    deleted,
                    options.EntryAudit.RetentionDays,
                    AccessDeleteWarningThreshold);
            }
            else if (deleted > 0)
            {
                _logger.LogInformation(
                    "Forms Audit Trail retention: deleted {Count} access rows older than {RetentionDays} days.",
                    deleted,
                    options.EntryAudit.RetentionDays);
            }

            return deleted;
        });

        await RunStepAsync(steps, "valueExpiry", options.EntryAudit.EditValueRetentionDays, async (scope, now) =>
        {
            var expired = await ExpireEditValuesAsync(Db(scope), now.AddDays(-options.EntryAudit.EditValueRetentionDays), now);
            if (expired > 0)
            {
                _logger.LogInformation(
                    "Forms Audit Trail retention: removed the stored values from {Count} edits older than {Days} days.",
                    expired,
                    options.EntryAudit.EditValueRetentionDays);
            }

            return expired;
        });

        // No days setting: erasure follows Forms, not the clock, and it is not gated by EntryAudit:Enabled because rows
        // written while the feature was on still hold personal data that an erasure must reach.
        await RunStepAsync(steps, "reconciliation", enabledDays: 1, async (scope, now) =>
        {
            IFormsRecordReader reader = scope.ServiceProvider.GetRequiredService<IFormsRecordReader>();
            var scrubbed = await ReconcileErasuresAsync(Db(scope), reader, now);
            if (scrubbed > 0)
            {
                _logger.LogInformation(
                    "Forms Audit Trail reconciliation: removed the stored values from {Count} edits of entries deleted from Forms.",
                    scrubbed);
            }

            return scrubbed;
        });

        // Finishes a cleared-field scrub the writer could not complete (SEC-03). Skipped when the operator keeps cleared values.
        await RunStepAsync(steps, "clearedValueSweep", options.EntryAudit.KeepClearedValues ? 0 : 1, async (scope, now) =>
        {
            var swept = await ScrubEarlierValuesOfClearedFieldsAsync(Db(scope), now);
            if (swept > 0)
            {
                _logger.LogInformation(
                    "Forms Audit Trail sweep: removed the stored values from {Count} earlier edits of fields that were later cleared.",
                    swept);
            }

            return swept;
        });

        await WriteLastRunAsync(new JobRunState(started, _time.GetUtcNow().UtcDateTime, steps));
    }

    private static FormsAuditDbContext Db(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();

    private async Task RunStepAsync(
        Dictionary<string, string> steps,
        string name,
        int enabledDays,
        Func<AsyncServiceScope, DateTime, Task<int>> step)
    {
        if (enabledDays <= 0)
        {
            steps[name] = "skipped";
            return;
        }

        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            steps[name] = $"ok:{await step(scope, _time.GetUtcNow().UtcDateTime)}";
        }
        catch (Exception ex)
        {
            steps[name] = $"failed:{ex.GetType().Name}";
            _logger.LogError(ex, "Forms Audit Trail retention step {Step} failed.", name);
        }
    }

    private async Task WriteLastRunAsync(JobRunState state)
    {
        try
        {
            await _stateStore.SetAsync(LastRunStateKey, JsonSerializer.Serialize(state), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Losing the health signal must not turn a successful clean-up into a failed job.
            _logger.LogWarning(ex, "Forms Audit Trail retention could not record its last run.");
        }
    }

    // Child change rows are removed by the database-level cascade delete
    internal static Task<int> DeleteEntriesOlderThanAsync(FormsAuditDbContext db, DateTime cutoffUtc) =>
        db.AuditEntries.Where(e => e.Timestamp < cutoffUtc).ExecuteDeleteAsync();

    // Batched so a first run over millions of rows is never one statement or one transaction (P8). Records and
    // changes go with the access row through the database-level cascade.
    internal static async Task<int> DeleteAccessEntriesOlderThanAsync(FormsAuditDbContext db, DateTime cutoffUtc, int batchSize = 5000)
    {
        var total = 0;
        int affected;
        do
        {
            affected = await db.AccessEntries
                .Where(e => e.Timestamp < cutoffUtc)
                .OrderBy(e => e.Id)
                .Take(batchSize)
                .ExecuteDeleteAsync();
            total += affected;
        }
        while (affected >= batchSize);

        return total;
    }

    // The rows stay (who and when is the audit); only the quoted values go. Rows already stamped are left alone so the
    // first scrub time survives.
    internal static async Task<int> ExpireEditValuesAsync(FormsAuditDbContext db, DateTime cutoffUtc, DateTime nowUtc, int batchSize = 5000)
    {
        var total = 0;
        int affected;
        do
        {
            affected = await db.EntryChanges
                .Where(c => c.ValuesScrubbedAt == null && c.Access!.Timestamp < cutoffUtc)
                .OrderBy(c => c.Id)
                .Take(batchSize)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.OldValue, (string?)null)
                    .SetProperty(c => c.NewValue, (string?)null)
                    .SetProperty(c => c.ValuesScrubbedAt, (DateTime?)nowUtc));
            total += affected;
        }
        while (affected >= batchSize);

        return total;
    }

    // Set-based and idempotent: any change row that still holds a value although a later change of the same record and
    // field was a clear, plus a clearing row that still holds its old value. That old value was kept while
    // KeepClearedValues was true; once the setting is switched off, a blanked field must keep no old value (SEC-03),
    // so rows written under the old setting are scrubbed too. Rows with nothing stored are not touched, so a second
    // run affects nothing. Batched by change id like the other steps so a backlog is never one statement or one
    // transaction (P8); a scrubbed row stops matching, so each pass sees the next unscrubbed batch and the loop ends
    // on the first short one.
    internal static async Task<int> ScrubEarlierValuesOfClearedFieldsAsync(FormsAuditDbContext db, DateTime nowUtc, int batchSize = 5000)
    {
        var total = 0;
        int affected;
        do
        {
            affected = await db.EntryChanges
                .Where(c => c.ValuesScrubbedAt == null
                    && ((c.Cleared && c.OldValue != null)
                        || ((c.OldValue != null || c.NewValue != null)
                            && c.Access!.RecordId != null
                            && db.EntryChanges.Any(n => n.Cleared && n.Id > c.Id && n.FieldId == c.FieldId && n.Access!.RecordId == c.Access!.RecordId))))
                .OrderBy(c => c.Id)
                .Take(batchSize)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.OldValue, (string?)null)
                    .SetProperty(c => c.NewValue, (string?)null)
                    .SetProperty(c => c.ValuesScrubbedAt, (DateTime?)nowUtc));
            total += affected;
        }
        while (affected >= batchSize);

        return total;
    }

    // Pages through the unscrubbed edits by change id and asks Forms about each page's record ids, keeping only the
    // missing ones, so memory is bounded by one page plus the erased ids however many unscrubbed edits exist (an
    // EditValueRetentionDays of 0, values kept forever, would otherwise make that unbounded). Nothing is scrubbed
    // until every page has been read: a read that fails halfway must not leave a partly scrubbed state that looks like a completed
    // run (SEC-I1). Paging by change id rather than record id because a Guid has no translatable ordering comparison.
    internal static async Task<int> ReconcileErasuresAsync(
        FormsAuditDbContext db,
        IFormsRecordReader reader,
        DateTime nowUtc,
        int batchSize = 500,
        CancellationToken ct = default,
        Guid? formId = null)
    {
        // formId narrows the pass to one form, for a permanent form delete: only that form's edits are candidates
        // and only they can be scrubbed, but every other safeguard, the probe included, is the same.
        IQueryable<FormEntryChange> unscrubbed = db.EntryChanges
            .Where(c => c.ValuesScrubbedAt == null && c.Access!.RecordId != null && (formId == null || c.Access!.FormId == formId));

        var missing = new HashSet<Guid>();
        long lastId = 0;
        while (true)
        {
            var page = await unscrubbed
                .Where(c => c.Id > lastId)
                .OrderBy(c => c.Id)
                .Take(batchSize)
                .Select(c => new { c.Id, RecordId = c.Access!.RecordId!.Value })
                .ToListAsync(ct);
            if (page.Count == 0)
            {
                break;
            }

            lastId = page[^1].Id;
            Guid[] ids = page.Select(r => r.RecordId).Distinct().ToArray();
            IReadOnlySet<Guid> existing = await reader.FindExistingAsync(ids, ct);
            missing.UnionWith(ids.Where(id => !existing.Contains(id)));
        }

        if (missing.Count == 0)
        {
            return 0;
        }

        // Self-test before the irreversible step: a record the table really holds must be found by the same matching
        // path, otherwise "missing" only means "the lookup is wrong here" (format, case, table or database) and every
        // live entry would be scrubbed. Unlike a count threshold this never blocks a genuine mass erasure, such as a
        // permanently deleted form: the probe still matches, so the absence is real and the scrub goes ahead. An empty
        // table has nothing to probe and every candidate is genuinely gone. If the probed record is deleted in the
        // moment between the two reads the step fails and simply retries tomorrow.
        Guid? probe = await reader.FindAnyExistingIdAsync(ct);
        if (probe is { } probeId && !(await reader.FindExistingAsync([probeId], ct)).Contains(probeId))
        {
            throw new ImplausibleReconciliationException(missing.Count);
        }

        return await ErasureScrubber.ScrubRecordsAsync(db, missing.ToArray(), nowUtc, ct, formId);
    }
}

/// <summary>The reader could not find a record the table holds, so reconciliation refused to scrub (see the job).</summary>
internal sealed class ImplausibleReconciliationException(int missingCount)
    : InvalidOperationException($"The Forms record lookup failed its self-test; {missingCount} records were not scrubbed.");
