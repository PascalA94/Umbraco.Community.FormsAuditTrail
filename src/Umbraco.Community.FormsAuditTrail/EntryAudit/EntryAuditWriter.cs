using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Forms.Core.Services;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Writes the parent access row and its field changes in one save (spec 7.1 step 5). It is called from a request
/// pipeline and from notification handlers, so it must never make the caller's operation fail: every failure is caught,
/// counted in <see cref="EntryAuditHealth"/> and logged once.
/// </summary>
/// <remarks>
/// Adapted from UserActivity <c>Recorder/RecorderWriter.cs</c> (swallow, log the exception type, never block the caller),
/// changed to log an Error with the event's identifiers because here a lost row is a compliance gap, not a nuisance.
/// </remarks>
internal sealed class EntryAuditWriter(
    IServiceScopeFactory scopeFactory,
    EntryAuditDeduplicator deduplicator,
    EntryAuditHealth health,
    IOptionsMonitor<FormsAuditTrailOptions> options,
    TimeProvider time,
    ILogger<EntryAuditWriter> logger) : IEntryAuditWriter
{
    internal const string UnresolvedFormName = "(unresolved form)";
    internal const string AccessLogName = "(access log)";
    internal const string MemberLookupFormName = "(member lookup)";
    internal const string DeletedFormName = "(deleted form)";

    public async Task<AccessWriteResult> WriteAsync(AccessWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        string filterHash = write.DedupeFilterHash ?? write.Detail?.FilterHash() ?? string.Empty;

        try
        {
            if (!write.SkipDedupe && !deduplicator.ShouldWrite(write.EventType, write.UserKey, write.FormId, write.RecordId, filterHash))
            {
                return new AccessWriteResult(AccessWriteStatus.Deduplicated, null, false);
            }
        }
        catch (Exception ex)
        {
            // Failing towards writing: a broken dedupe must not lose the event.
            SafeLog.Run(() => logger.LogWarning("Entry audit dedupe failed ({ExceptionType}); writing the event.", ex.GetType().Name));
        }

        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            FormsAuditDbContext db = scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();

            FormAccessEntry entry = ToEntry(write, scope.ServiceProvider.GetRequiredService<IFormService>(), out EntryAuditDetail? detail);
            entry.CorrelationId = await TryResolveCorrelationAsync(db, write);
            db.AccessEntries.Add(entry);
            // The parent goes in on its own so a failing child batch can never cost the evidence (SEC-02).
            await db.SaveChangesAsync(CancellationToken.None);
            long accessId = entry.Id;

            await ScrubEarlierValuesOfClearedFieldsAsync(db, write, accessId, detail);

            bool recordIdsCaptured = await WriteChildrenAsync(db, accessId, write, detail);

            // M7 publishes its access-recorded notification here, after the parent row is saved (spec 7.1).

            health.RecordWriteSuccess();
            // Past the parent save nothing may turn Written into Failed: that would raise a false gap and a duplicate on retry.
            TryLogEvent(accessId, write);
            await TryWriteGapAsync(db);
            return new AccessWriteResult(AccessWriteStatus.Written, accessId, recordIdsCaptured);
        }
        catch (Exception ex)
        {
            health.RecordWriteFailure(write.EventType, write.FormId);
            if (!write.SkipDedupe)
            {
                // Released so the next identical view is written rather than suppressed by an event that never landed.
                // A skipped write never took a key, so releasing one would free another request's.
                deduplicator.Release(write.EventType, write.UserKey, write.FormId, write.RecordId, filterHash);
            }

            // Identifiers only. The exception message can carry the values being written, so it is not logged.
            SafeLog.Run(() => logger.LogError(
                "FormsAuditTrail could not store an access event: user {UserKey}, event {EventType}, form {FormId}, records {RecordCount} ({ExceptionType}).",
                write.UserKey,
                write.EventType,
                write.FormId,
                write.RecordCount,
                ex.GetType().Name));
            return new AccessWriteResult(AccessWriteStatus.Failed, null, false);
        }
    }

    /// <summary>
    /// SEC-03: blanking a field must not leave the value it held in the earlier edit rows of the same record, where the
    /// old value was stored as that edit's new value. One set-based statement for all cleared fields of the write. A
    /// failure does not fail the write (the Cleared row is already saved) but is counted so the gap is reported.
    /// </summary>
    private async Task ScrubEarlierValuesOfClearedFieldsAsync(FormsAuditDbContext db, AccessWrite write, long accessId, EntryAuditDetail? detail)
    {
        if (write.EventType != AccessEventType.Edited || write.RecordId is not { } recordId || options.CurrentValue.EntryAudit.KeepClearedValues)
        {
            return;
        }

        Guid[] fieldIds = write.Changes.Where(c => c.Cleared).Select(c => c.FieldId).Distinct().ToArray();
        if (fieldIds.Length == 0)
        {
            return;
        }

        try
        {
            DateTime nowUtc = time.GetUtcNow().UtcDateTime;
            await db.EntryChanges
                .Where(c => c.ValuesScrubbedAt == null
                    && c.Access!.Id != accessId
                    && c.Access!.RecordId == recordId
                    && fieldIds.Contains(c.FieldId))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.OldValue, (string?)null)
                        .SetProperty(c => c.NewValue, (string?)null)
                        .SetProperty(c => c.ValuesScrubbedAt, (DateTime?)nowUtc),
                    CancellationToken.None);
        }
        catch (Exception ex)
        {
            health.RecordWriteFailure(write.EventType, write.FormId);
            SafeLog.Run(() => logger.LogError(
                "FormsAuditTrail stored access {AccessId} but could not scrub the earlier values of a cleared field: user {UserKey}, form {FormId} ({ExceptionType}).",
                accessId, write.UserKey, write.FormId, ex.GetType().Name));
            // The retention job's sweep finishes the scrub; the flag tells a reader the values may linger until then.
            try
            {
                string flagged = ((detail ?? new EntryAuditDetail()) with { ValuesScrubIncomplete = true }).ToJson(EntryAuditDetail.DefaultMaxBytes);
                await db.AccessEntries.Where(a => a.Id == accessId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Detail, flagged), CancellationToken.None);
            }
            catch (Exception flagEx)
            {
                SafeLog.Run(() => logger.LogError("FormsAuditTrail could not flag access {AccessId} as having an incomplete scrub ({ExceptionType}).", accessId, flagEx.GetType().Name));
            }
        }
    }

    /// <summary>The most record ids one list-view row keeps (SEC-10, design 6.2 E1).</summary>
    internal const int MaxListRecordIds = 500;

    private const int ChildBatchSize = 1000;
    private const int CorrelationCandidates = 50;

    /// <summary>
    /// Stores the child record ids after the parent is saved. Returns whether the ids the caller supplied are all held:
    /// false when a batch failed, or when the caller flagged the capture as incomplete or list-view ids are switched off.
    /// A failed batch is not a failed write: the parent stays, its detail says <c>recordIdsCaptured: false</c>, and
    /// health counts the failure so the gap is reported.
    /// </summary>
    private async Task<bool> WriteChildrenAsync(FormsAuditDbContext db, long accessId, AccessWrite write, EntryAuditDetail? detail)
    {
        bool callerFlaggedIncomplete = write.Detail?.RecordIdsCaptured == false;

        // D7: list views keep a count only, unless the operator opted in. Every other event type always stores its ids.
        if (write.EventType == AccessEventType.ListViewed && !options.CurrentValue.EntryAudit.LogListRecordIds)
        {
            return write.RecordIds.Count == 0 && !callerFlaggedIncomplete;
        }

        if (write.RecordIds.Count == 0)
        {
            return !callerFlaggedIncomplete;
        }

        // Distinct: the key is (access, record), so a repeated id would fail the whole batch.
        IReadOnlyList<Guid> ids = write.RecordIds.Distinct().ToList();
        bool capped = false;
        // SEC-10, last line of defence: the capture core already caps list views at 500, but the writer is public to
        // other callers, and a list page is the one place an operator option turns a view into thousands of child rows.
        if (write.EventType == AccessEventType.ListViewed && ids.Count > MaxListRecordIds)
        {
            ids = ids.Take(MaxListRecordIds).ToList();
            capped = true;
        }

        bool autoDetect = db.ChangeTracker.AutoDetectChangesEnabled;
        try
        {
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            foreach (Guid[] batch in ids.Chunk(ChildBatchSize))
            {
                // Cleared first so a failed batch cannot leave rows behind to poison the detail update below.
                db.ChangeTracker.Clear();
                foreach (Guid recordId in batch)
                {
                    db.AccessRecords.Add(new FormAccessRecord { AccessId = accessId, RecordId = recordId });
                }

                await db.SaveChangesAsync(CancellationToken.None);
            }

            db.ChangeTracker.Clear();
        }
        catch (Exception ex)
        {
            db.ChangeTracker.Clear();
            // The parent row stays, so the event itself is recorded, but the ids that did not land are stored nowhere.
            // The failure counted here is taken as an AuditGap row straight after this write (or by a later write, if
            // that save fails too), and while the parent row exists that gap row, with recordIdsCaptured=false on the
            // parent, is the persistent trace that this access lost some of its child ids. Health counters alone are
            // in memory and would not survive a restart.
            health.RecordWriteFailure(write.EventType, write.FormId);
            // Guarded: past the parent save a throwing logger must not turn Written into Failed.
            SafeLog.Run(() => logger.LogError(
                "FormsAuditTrail stored access {AccessId} but not all of its record ids: user {UserKey}, event {EventType}, form {FormId}, records {RecordCount} ({ExceptionType}).",
                accessId, write.UserKey, write.EventType, write.FormId, write.RecordCount, ex.GetType().Name));
            await MarkRecordIdsIncompleteAsync(db, accessId, detail);
            return false;
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = autoDetect;
        }

        if (capped && !callerFlaggedIncomplete)
        {
            // The parent was saved before the cap was known, so its stored detail is corrected to match. Outside the
            // batch try on purpose: every child batch landed, so a failed correction is not a lost-ids failure. It
            // guards and logs itself, and the caller still gets recordIdsCaptured=false.
            await MarkRecordIdsIncompleteAsync(db, accessId, detail);
        }

        return !callerFlaggedIncomplete && !capped;
    }

    private async Task MarkRecordIdsIncompleteAsync(FormsAuditDbContext db, long accessId, EntryAuditDetail? detail)
    {
        try
        {
            string flagged = ((detail ?? new EntryAuditDetail()) with { RecordIdsCaptured = false }).ToJson(EntryAuditDetail.DefaultMaxBytes);
            await db.AccessEntries.Where(a => a.Id == accessId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Detail, flagged), CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The failure is already counted; the flag is a convenience for readers, so it must not fail the write.
            SafeLog.Run(() => logger.LogError("FormsAuditTrail could not flag access {AccessId} as missing record ids ({ExceptionType}).", accessId, ex.GetType().Name));
        }
    }

    /// <summary>
    /// SEC-02 (security checkpoint L2): the lookup runs before the parent save, so a failure here must not cost the row.
    /// The download is still stored, without a correlation, which only loses the link to its export.
    /// </summary>
    private async Task<Guid?> TryResolveCorrelationAsync(FormsAuditDbContext db, AccessWrite write)
    {
        try
        {
            return await ResolveCorrelationAsync(db, write);
        }
        catch (Exception ex)
        {
            SafeLog.Run(() => logger.LogWarning(
                "FormsAuditTrail could not match a download to its export: user {UserKey}, event {EventType}, form {FormId} ({ExceptionType}); stored without a correlation.",
                write.UserKey,
                write.EventType,
                write.FormId,
                ex.GetType().Name));
            return null;
        }
    }

    /// <summary>
    /// A new export gets a fresh id; a download borrows the id of the export that produced its file. The match is the
    /// newest of the user's exports of this form whose file name is the same (design 7.x, architect's call: Forms keeps
    /// exports in a per-user folder). Only the newest candidates are scanned, in memory, because the file name lives in
    /// the detail JSON and cannot be filtered in SQL on both providers.
    /// </summary>
    private static async Task<Guid?> ResolveCorrelationAsync(FormsAuditDbContext db, AccessWrite write)
    {
        if (write.EventType == AccessEventType.Exported)
        {
            return Guid.NewGuid();
        }

        string? fileName = write.Detail?.FileName;
        if (write.EventType != AccessEventType.ExportDownloaded || fileName is null)
        {
            return null;
        }

        int exported = (int)AccessEventType.Exported;
        var candidates = await db.AccessEntries
            .Where(a => a.EventType == exported && a.FormId == write.FormId && a.UserKey == write.UserKey && a.CorrelationId != null)
            .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id)
            .Take(CorrelationCandidates)
            .Select(a => new { a.CorrelationId, a.Detail })
            .ToListAsync(CancellationToken.None);

        foreach (var candidate in candidates)
        {
            if (FileNameOf(candidate.Detail) == fileName)
            {
                return candidate.CorrelationId;
            }
        }

        return null;
    }

    private static string? FileNameOf(string? detailJson)
    {
        if (string.IsNullOrEmpty(detailJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(detailJson);
            return doc.RootElement.TryGetProperty("fileName", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records the outage that just ended. Best effort and never recursive: if this save fails the gap is handed back to
    /// health, which then stays unhealthy until a later write manages to store it, rather than the outage vanishing.
    /// </summary>
    private async Task WriteGapAsync(FormsAuditDbContext db)
    {
        AuditGapInfo? gap = health.TakePendingGap();
        if (gap is null)
        {
            return;
        }

        FormAccessEntry? gapEntry = null;
        try
        {
            gapEntry = new FormAccessEntry
            {
                Timestamp = time.GetUtcNow().UtcDateTime,
                EventType = (int)AccessEventType.AuditGap,
                FormId = Guid.Empty,
                FormName = AccessLogName,
                UserKey = Guid.Empty,
                UserName = AuditUserNames.System,
                Detail = new EntryAuditDetail { Gap = gap }.ToJson(EntryAuditDetail.DefaultMaxBytes),
            };
            db.AccessEntries.Add(gapEntry);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (gapEntry is not null)
            {
                db.Entry(gapEntry).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
            }

            health.RestorePendingGap(gap);
            SafeLog.Run(() => logger.LogError("FormsAuditTrail could not store its audit gap row for {Count} failed writes ({ExceptionType}).", gap.Count, ex.GetType().Name));
        }
    }

    /// <summary>
    /// Only a retried configuration change may carry an earlier time (it evidences an off period that the outage delayed
    /// writing). Every other event is stamped when it is stored, and no caller can post-date a row.
    /// </summary>
    private DateTime ResolveTimestamp(AccessWrite write)
    {
        DateTime now = time.GetUtcNow().UtcDateTime;
        return write is { EventType: AccessEventType.ConfigChanged, OccurredUtc: { } occurred } && occurred < now ? occurred : now;
    }

    private FormAccessEntry ToEntry(AccessWrite write, IFormService forms, out EntryAuditDetail? finalDetail)
    {
        var lengths = new Dictionary<string, int>(write.Detail?.OriginalLengths ?? new Dictionary<string, int>(), StringComparer.Ordinal);

        string formName = Cap(ResolveFormName(write, forms), AccessColumnLimits.FormName, "formName", lengths);
        string userName = Cap(write.UserName, AccessColumnLimits.UserName, "userName", lengths);

        var entry = new FormAccessEntry
        {
            Timestamp = ResolveTimestamp(write),
            EventType = (int)write.EventType,
            FormId = write.FormId,
            FormName = formName,
            RecordId = write.RecordId,
            RecordCount = write.RecordCount,
            UserKey = write.UserKey,
            UserName = userName,
        };

        foreach (EntryFieldChange change in write.Changes)
        {
            entry.Changes.Add(new FormEntryChange
            {
                FieldId = change.FieldId,
                // The longest caption is recorded under one key: the lengths are evidence that something was cut, not a per-field map.
                FieldCaption = Cap(change.FieldCaption, AccessColumnLimits.FieldCaption, "fieldCaption", lengths),
                OldValue = change.OldValue,
                NewValue = change.NewValue,
                Masked = change.Masked,
                Cleared = change.Cleared,
            });
        }

        EntryAuditDetail? detail = write.Detail;
        if (lengths.Count > 0)
        {
            detail = (detail ?? new EntryAuditDetail()) with { OriginalLengths = lengths };
        }

        finalDetail = detail;
        entry.Detail = detail?.ToJson(EntryAuditDetail.DefaultMaxBytes);
        return entry;
    }

    private static string ResolveFormName(AccessWrite write, IFormService forms)
    {
        if (!string.IsNullOrEmpty(write.FormName))
        {
            return write.FormName;
        }

        if (write.FormId == Guid.Empty)
        {
            if (write.Detail?.FormUnresolved == true)
            {
                return UnresolvedFormName;
            }

            // A member lookup is about a member across forms, so it gets its own label rather than the log's own.
            return write.EventType == AccessEventType.MemberSummaryViewed ? MemberLookupFormName : AccessLogName;
        }

        try
        {
            return forms.Get(write.FormId)?.Name is { Length: > 0 } name ? name : DeletedFormName;
        }
        catch
        {
            // A lookup failure must not cost the row; the id is stored, so the form can still be found later.
            return DeletedFormName;
        }
    }

    private static string Cap(string? value, int limit, string key, Dictionary<string, int> lengths)
    {
        value ??= string.Empty;
        if (value.Length <= limit)
        {
            return value;
        }

        lengths[key] = Math.Max(lengths.GetValueOrDefault(key), value.Length);
        return TextTruncation.Truncate(value, limit);
    }

    private void TryLogEvent(long accessId, AccessWrite write)
    {
        try
        {
            LogEvent(accessId, write);
        }
        catch (Exception ex)
        {
            SafeLog.Run(() => logger.LogWarning("FormsAuditTrail could not emit its log event for access {AccessId} ({ExceptionType}).", accessId, ex.GetType().Name));
        }
    }

    private async Task TryWriteGapAsync(FormsAuditDbContext db)
    {
        try
        {
            await WriteGapAsync(db);
        }
        catch (Exception ex)
        {
            SafeLog.Run(() => logger.LogWarning("FormsAuditTrail gap handling failed after a stored write ({ExceptionType}).", ex.GetType().Name));
        }
    }

    private void LogEvent(long accessId, AccessWrite write)
    {
        EntryAuditOptions audit = options.CurrentValue.EntryAudit;
        if (!audit.EmitLogEvents)
        {
            return;
        }

        // Identifiers and counts only, never values or the detail JSON (SEC-08).
        if (audit.EmitLogEventRecordIds)
        {
            logger.LogInformation(
                "FormsAuditTrail.Access {AccessId} {EventType} {FormId} {UserKey} {RecordCount} {RecordIds}",
                accessId, write.EventType, write.FormId, write.UserKey, write.RecordCount, string.Join(",", write.RecordIds));
        }
        else
        {
            logger.LogInformation(
                "FormsAuditTrail.Access {AccessId} {EventType} {FormId} {UserKey} {RecordCount}",
                accessId, write.EventType, write.FormId, write.UserKey, write.RecordCount);
        }
    }
}
