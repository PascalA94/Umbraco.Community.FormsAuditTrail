using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Services;
using Umbraco.Forms.Web.Api.ManagementApi.Export;
using Umbraco.Forms.Web.Api.ManagementApi.Record;
using Umbraco.Forms.Web.Models.Backoffice;
using FormsConstants = Umbraco.Forms.Core.Constants;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>What the adapter must do once the capture is complete. Only an export can ever ask for a 503.</summary>
internal sealed record CaptureOutcome(bool ReplaceWith503)
{
    internal static readonly CaptureOutcome None = new(false);
}

/// <summary>
/// The capture logic for one Forms request (spec 7.1 request flow), kept apart from MVC so an adapter only translates
/// the request in and the outcome back. It runs inside Forms' own request path, so it never throws: any failure is
/// logged and Forms' response stands, except under the opt-in <c>FailClosedOnExport</c>.
/// </summary>
/// <remarks>
/// Scoped, one per request, alongside the scoped collector it opens a window on. The user is resolved when the write
/// is built, through <see cref="AuditHandlerHelpers.TryGetCurrentUser"/>, and never held beyond the request (P2).
/// </remarks>
internal sealed class EntryAuditCapture(
    EntryAuditCollector collector,
    IEntryAuditWriter writer,
    IOptionsMonitor<FormsAuditTrailOptions> options,
    IBackOfficeSecurityAccessor securityAccessor,
    IRecordStorage recordStorage,
    IFormService formService,
    EntryAuditHealth health,
    ILogger<EntryAuditCapture> logger,
    DeletedRecordScrubber scrubber)
{
    private static readonly Guid DeleteRecordSetAction = Guid.Parse(FormsConstants.RecordSetActionTypes.DeleteRecordSet);

    private AccessEventType? _kind;

    /// <summary>
    /// The ids a bulk delete request named (S5: <c>RecordActionExecution.RecordKeys</c>), read before the action runs. Only
    /// their count is ever stored, as <c>requestedCount</c>; they are never claimed or scrubbed.
    /// </summary>
    private IReadOnlyList<Guid> _requestedDeleteIds = [];

    /// <summary>
    /// True when <see cref="TryBegin"/> failed for an export or download under <c>FailClosedOnExport</c>: the adapter
    /// must refuse the request with a 503 before the action runs, so no file is generated without its audit row (SEC-02).
    /// </summary>
    internal bool RefuseBeforeAction { get; private set; }

    /// <summary>
    /// Opens the capture window when capture is on for <paramref name="kind"/>, the event the adapter already looked up
    /// in <see cref="FormsControllerMap.Audited"/> for this controller. False means the adapter just runs the action:
    /// nothing else in this class will do any work for the request.
    /// </summary>
    internal bool TryBegin(CaptureRequest request, AccessEventType kind)
    {
        Guid? formId = null;
        try
        {
            // The record-set endpoint also runs approve and reject, which are state changes, not access to values (S5).
            // Only the Delete action is audited; it is matched by Forms' own constant so a change there breaks the build.
            // Checked before the options so that an approve can never be counted as a lost row below.
            if (request.ControllerType == typeof(ExecuteActionController) && !IsDeleteAction(request))
            {
                return false;
            }

            EntryAuditOptions audit = options.CurrentValue.EntryAudit;
            if (!audit.Enabled || (kind == AccessEventType.ListViewed && !audit.LogListViews))
            {
                return false;
            }

            // Recorded before any work that can throw, so a failure below still knows whether it must fail closed.
            _kind = kind;
            (formId, Guid? recordId, EntryAuditDetail detail) = EntryAuditRequestReader.Read(request, kind);
            if (kind == AccessEventType.Deleted)
            {
                (detail, _requestedDeleteIds) = ReadDelete(request, detail);
            }

            collector.Open(kind, formId, recordId, detail);
            if (kind == AccessEventType.Edited)
            {
                CaptureBeforeValues(formId, recordId);
            }

            return true;
        }
        catch (Exception ex)
        {
            RefuseBeforeAction = _kind is { } failedKind && FailsClosed(failedKind, options);
            _kind = null;
            // SEC-02: the request runs unrecorded, so health counts it and the next stored write leaves a gap row.
            // Counted even when the options could not be read: unreadable options cannot say capture is off.
            RecordFailure(kind, formId);
            SafeLog.Run(() => logger.LogError(
                "FormsAuditTrail could not open an entry audit window for {Controller} ({ExceptionType}): event {EventType}, form {FormId}, user {UserKey}; the request is not recorded.",
                request.ControllerType.Name,
                ex.GetType().Name,
                kind,
                formId,
                AuditHandlerHelpers.CurrentUserKeyOrEmpty(securityAccessor)));
            return false;
        }
    }

    /// <summary>
    /// Writes the access row for a completed action. <paramref name="resultValue"/> is the action's result value, the
    /// only place the generated export file name exists (S1, S7).
    /// </summary>
    public async Task<CaptureOutcome> CompleteAsync(bool success, object? resultValue = null)
    {
        if (_kind is not { } kind)
        {
            return CaptureOutcome.None;
        }

        if (!success)
        {
            WarnIfEditOutcomeUnknown(kind);
            if (kind == AccessEventType.Deleted)
            {
                RecordPartialDelete();
            }

            return CaptureOutcome.None;
        }

        if (kind == AccessEventType.Deleted)
        {
            // Erasure must not depend on the audit row, so it runs first and on its own; it never throws.
            await ScrubDeletedAsync();
        }

        try
        {
            AccessWrite? write = BuildWrite(kind, resultValue);
            if (write is null)
            {
                if (collector.Window is not null)
                {
                    // Expected: an edit with no changed field, or a kind that writes nothing here. Not a failure.
                    return CaptureOutcome.None;
                }

                // The window opened in TryBegin was lost while Forms ran, so the action completes with no row (SEC-02).
                // For an export that means the file would go out untraced, so under fail-closed it is refused.
                RecordFailure(kind, null);
                SafeLog.Run(() => logger.LogError(
                    "FormsAuditTrail lost the capture window before it could record an access event: event {EventType}, user {UserKey}; the request is not recorded.",
                    kind,
                    AuditHandlerHelpers.CurrentUserKeyOrEmpty(securityAccessor)));
                return new CaptureOutcome(FailsClosed(kind, options));
            }

            // The writer counts its own failures in health.
            AccessWriteResult result = await writer.WriteAsync(write);
            return new CaptureOutcome(result.Status == AccessWriteStatus.Failed && FailsClosed(kind, options));
        }
        catch (Exception ex)
        {
            Guid? formId = WindowFormId();
            RecordFailure(kind, formId);
            SafeLog.Run(() => logger.LogError(
                "FormsAuditTrail could not record an access event ({ExceptionType}): event {EventType}, form {FormId}, user {UserKey}; the request is not recorded.",
                ex.GetType().Name,
                kind,
                formId,
                AuditHandlerHelpers.CurrentUserKeyOrEmpty(securityAccessor)));
            return new CaptureOutcome(FailsClosed(kind, options));
        }
    }

    /// <summary>
    /// SEC-02, E5: under the opt-in <c>FailClosedOnExport</c>, an export or download whose row cannot be written is
    /// refused with a 503 rather than handing over data that leaves no trace: before the action when capture cannot
    /// start, after it when the row cannot be written. This rule is the only reason the package ever changes a Forms
    /// response; every other failure fails open (E12). Shared with the adapter, which must decide it when it cannot
    /// even reach the capture.
    /// </summary>
    internal static bool FailsClosed(AccessEventType kind, IOptionsMonitor<FormsAuditTrailOptions> options)
    {
        try
        {
            if (kind is not (AccessEventType.Exported or AccessEventType.ExportDownloaded))
            {
                return false;
            }

            EntryAuditOptions audit = options.CurrentValue.EntryAudit;
            // With capture switched off nothing is audited, so there is no row whose absence could fail anything.
            return audit.Enabled && audit.FailClosedOnExport;
        }
        catch
        {
            // Options that cannot be read cannot say "fail closed"; the default is open.
            return false;
        }
    }

    /// <summary>
    /// SEC-04: the stored values before an edit, read through Forms' <c>IRecordStorage</c> so any decrypting
    /// <c>RecordFetching</c> handler runs on them exactly as it does for Forms (S2, S10). Reading <c>UFRecords.RecordData</c>
    /// ourselves would bypass a site's at-rest encryption: every field would diff as changed and the decrypted after
    /// values would be stored next to ciphertext. Copied, because storage may hand back an instance Forms then mutates.
    /// </summary>
    /// <remarks>
    /// Runs before the action and never stops the edit (E12). With no before values the edit, if Forms accepts it, is
    /// still recorded without values as <c>beforeUnavailable</c>; that row is the evidence, so no case here is counted
    /// as a health failure, which would add an <c>AuditGap</c> for a row that exists. A missing form or record, or
    /// a record of another form, is expected: Forms answers those requests with 404 itself (M0 spike).
    /// </remarks>
    private void CaptureBeforeValues(Guid? formId, Guid? recordId)
    {
        try
        {
            string? missing = null;
            Umbraco.Forms.Core.Persistence.Dtos.Record? record = null;
            if (formId is not { } fid || recordId is not { } rid)
            {
                missing = "no form or record id";
            }
            else if (formService.Get(fid) is not { } form)
            {
                missing = "form not found";
            }
            else if ((record = recordStorage.GetRecordByUniqueId(rid, form)) is null)
            {
                missing = "record not found";
            }
            else if (record.Form != fid)
            {
                // Never diff against another form's entry, whatever storage returns for the pair.
                missing = "record belongs to another form";
            }

            if (missing is null)
            {
                collector.SetBeforeValues(RecordValueSnapshot.From(record!));
                return;
            }

            collector.MarkBeforeUnavailable();
            SafeLog.Run(() => logger.LogWarning(
                "FormsAuditTrail has no values before an edit ({Reason}): event {EventType}, form {FormId}, record {RecordId}.",
                missing,
                AccessEventType.Edited,
                formId,
                recordId));
        }
        catch (Exception ex)
        {
            collector.MarkBeforeUnavailable();
            SafeLog.Run(() => logger.LogError(
                "FormsAuditTrail could not read an entry's values before an edit ({ExceptionType}): event {EventType}, form {FormId}, user {UserKey}, records 1; the edit is recorded without values.",
                ex.GetType().Name,
                AccessEventType.Edited,
                formId,
                AuditHandlerHelpers.CurrentUserKeyOrEmpty(securityAccessor)));
        }
    }

    /// <summary>
    /// Forms reported failure after the handler had already seen the changed values at <c>RecordSaving</c>, so
    /// the save may or may not have reached the database. Nothing is written, because a row would claim an edit that may
    /// not exist; the Warning lets an operator check the entry. Ids only, never values.
    /// </summary>
    private void WarnIfEditOutcomeUnknown(AccessEventType kind)
    {
        try
        {
            if (kind == AccessEventType.Edited && collector.Window is { Changes.Count: > 0 } window)
            {
                SafeLog.Run(() => logger.LogWarning(
                    "FormsAuditTrail edit outcome unknown: the request failed after the save was prepared; nothing recorded. Event {EventType}, form {FormId}, record {RecordId}, user {UserKey}.",
                    kind,
                    window.FormId,
                    window.RecordId,
                    AuditHandlerHelpers.CurrentUserKeyOrEmpty(securityAccessor)));
            }
        }
        catch
        {
            // Diagnostic only; it must never affect the response.
        }
    }

    private void RecordFailure(AccessEventType kind, Guid? formId)
    {
        try
        {
            health.RecordWriteFailure(kind, formId ?? Guid.Empty);
        }
        catch
        {
            // Health is in memory; if even that fails there is nowhere left to report it.
        }
    }

    private Guid? WindowFormId()
    {
        try
        {
            return collector.Window?.FormId;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>S1: <c>CreateRecordController</c> returns 201 with an anonymous <c>{ id }</c>; the new record's id is that property.</summary>
    private static Guid? CreatedRecordId(object? resultValue)
    {
        object? id = resultValue?.GetType().GetProperty("id")?.GetValue(resultValue)
            ?? resultValue?.GetType().GetProperty("Id")?.GetValue(resultValue);
        return id switch
        {
            Guid g when g != Guid.Empty => g,
            string text when Guid.TryParse(text, out Guid parsed) && parsed != Guid.Empty => parsed,
            _ => null,
        };
    }

    private AccessWrite? BuildWrite(AccessEventType kind, object? resultValue)
    {
        if (collector.Window is not { } window)
        {
            return null;
        }

        switch (kind)
        {
            // With no changed field there is nothing to record (E3), unless the before values could not be read: then
            // the edit itself is still evidence and is written without a diff (E12).
            case AccessEventType.Edited when window.Changes.Count == 0 && window.Detail.BeforeUnavailable != true:
                return null;
        }

        EntryAuditOptions audit = options.CurrentValue.EntryAudit;
        EntryAuditDetail raw = window.Detail;
        if (kind == AccessEventType.Exported && resultValue is PostGenerateExportController.GenerateExportResponseModel generated)
        {
            // The download is matched to this row by file name (spec 6.2: the export row carries the file name), so both kinds must carry it.
            raw = raw with { FileName = generated.FileName };
        }

        // Hashed before shaping, so two different searches stay two views when the term itself is not stored.
        // The hash lives only in the deduplicator's memory.
        string dedupeHash = raw.FilterHash();
        if (kind == AccessEventType.MemberSummaryViewed)
        {
            // R37a: the member is the subject of this view, and the dedupe key has no other place for it, so without
            // this a lookup of member B straight after member A would be dropped. Hashed again so a raw identifier
            // (possibly an email) is not held even in memory.
            dedupeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{dedupeHash}|{raw.MemberKey}")));
        }
        EntryAuditDetail detail = EntryAuditDetail.Shape(raw, audit.CaptureSearchTerms);

        Guid userKey;
        string userName;
        bool userUnresolved = !AuditHandlerHelpers.TryGetCurrentUser(securityAccessor, out Guid key, out string name);
        if (!userUnresolved)
        {
            (userKey, userName) = (key, name);
        }
        else
        {
            // SEC-L5: a backoffice request with no resolvable user is Unknown, never System, which is kept for jobs.
            (userKey, userName) = (Guid.Empty, AuditUserNames.Unknown);
            detail = detail with { UserUnresolved = true };
        }

        Guid formId;
        if (window.FormId is { } resolved)
        {
            formId = resolved;
        }
        else if (kind == AccessEventType.MemberSummaryViewed)
        {
            // A member lookup spans forms; Guid.Empty here is its subject, not a failed resolution.
            formId = Guid.Empty;
        }
        else
        {
            // Still written (SEC-02, SEC-L3): a request that defeats resolution must leave a trace, and Guid.Empty rows
            // are shown only to users who can access every form.
            formId = Guid.Empty;
            detail = detail with { FormUnresolved = true };
            SafeLog.Run(() => logger.LogWarning(
                "FormsAuditTrail could not resolve the form for an entry access: event {EventType}, user {UserKey}; recorded with an empty form id.",
                kind,
                userKey));
        }

        Guid? recordId = window.RecordId;
        if (kind == AccessEventType.Created)
        {
            // The response is the S1 source for the new id; the saving notification's record is the fallback.
            Guid? responseId = CreatedRecordId(resultValue);
            if (responseId is { } created && recordId is { } saved && created != saved)
            {
                // The captions came from a different saved record than the one Forms reports creating.
                detail = detail with { FieldCaptions = null };
            }

            recordId = responseId ?? recordId;
        }

        IReadOnlyCollection<Guid> recordIds = [];
        int recordCount = recordId is null ? 0 : 1;
        switch (kind)
        {
            case AccessEventType.ListViewed:
                recordCount = window.FetchedRowCount;
                if (audit.LogListRecordIds && window.RecordIds.Count <= EntryAuditWriter.MaxListRecordIds)
                {
                    recordIds = window.RecordIds.ToArray();
                }
                else if (audit.LogListRecordIds)
                {
                    // SEC-10: one page keeps at most 500 child rows; the row says it is incomplete.
                    recordIds = window.RecordIds.Take(EntryAuditWriter.MaxListRecordIds).ToArray();
                    detail = detail with { RecordIdsCaptured = false };
                }
                else
                {
                    // D7: a count only, and the row says the ids were not kept.
                    detail = detail with { RecordIdsCaptured = false };
                }

                break;

            case AccessEventType.Exported:
                recordCount = window.FetchedRowCount;
                recordIds = window.RecordIds.ToArray();
                if (recordIds.Count == 0)
                {
                    // E5: an exporter that does not go through Forms' searcher (S7: uploaded files in disk structure)
                    // publishes no rows, so an empty list means "not known", not "nothing exported".
                    detail = detail with { RecordIdsCaptured = false };
                }

                break;

            case AccessEventType.ExportDownloaded:
                recordCount = 0;
                break;

            case AccessEventType.Deleted:
                IReadOnlyList<Guid> deleted = DeletedIds(window);
                if (deleted.Count == 0)
                {
                    // Forms returned 2xx but published no deletion, so nothing is claimed as deleted. The row
                    // still records the attempt and how many entries it asked for; reconciliation covers a deletion
                    // that happened without the notification.
                    recordId = null;
                    recordCount = 0;
                    detail = detail with { RecordIdsCaptured = false, RequestedCount = RequestedCount(window) };
                }
                else if (window.Detail.Bulk == true)
                {
                    // P8: one row per request with the ids as child rows, however many entries went; entry history
                    // finds it through the child row. If the children fail the writer flags recordIdsCaptured=false.
                    recordId = null;
                    recordIds = deleted;
                    recordCount = deleted.Count;
                }

                // A single delete Forms confirmed keeps its route id and a count of 1, the defaults above.
                break;
        }

        return new AccessWrite(kind, formId, null, recordId, recordCount, userKey, userName, detail, recordIds, window.Changes.ToArray())
        {
            DedupeFilterHash = dedupeHash,
            // Every unresolved user shares Guid.Empty, so a dedupe key would hide a second person behind the first.
            SkipDedupe = userUnresolved,
        };
    }

    /// <summary>
    /// Shapes a delete's detail and reads the ids a bulk request names. Forms' single-delete route carries the id in the
    /// route; the Delete record-set action carries them in its body (S5), found by type so a Forms rename breaks the build.
    /// </summary>
    private static (EntryAuditDetail Detail, IReadOnlyList<Guid> RequestedIds) ReadDelete(CaptureRequest request, EntryAuditDetail detail)
    {
        if (request.ControllerType != typeof(ExecuteActionController))
        {
            return (detail with { DeleteKind = "single" }, []);
        }

        Guid[] ids = request.Arguments.Values.OfType<RecordActionExecution>().FirstOrDefault()?.RecordKeys?.ToArray() ?? [];
        return (detail with { DeleteKind = "bulk", Bulk = true }, ids);
    }

    /// <summary>
    /// The entries this delete request removed: only ids Forms published in <c>RecordDeleting</c> during the request,
    /// for the request's form. The request's own ids are never trusted, because a key Forms could not find, or one
    /// from another form, publishes nothing; claiming it would put a false deletion in the log and scrub a live entry's
    /// edit history. A single delete is its route id, and only when Forms published it.
    /// </summary>
    /// <remarks>
    /// <c>RecordDeletingNotification</c> is cancelable. Our handler notes the ids when it runs; if a handler that runs
    /// after it cancels the delete and Forms still answers 2xx, these ids are claimed and scrubbed although the entries
    /// remain. Nothing here can see that cancel. Accepted: it needs a site handler that vetoes deletes, the user had
    /// Forms' delete permission for these entries, and the loss is their stored edit values, not the entries.
    /// </remarks>
    private IReadOnlyList<Guid> DeletedIds(CaptureWindow window)
    {
        Guid[] seen = window.DeletedRecords
            .Where(d => window.FormId is not { } formId || d.FormId == formId)
            .Select(d => d.RecordId)
            .Distinct()
            .ToArray();

        if (window.Detail.Bulk != true)
        {
            return window.RecordId is { } single && seen.Contains(single) ? [single] : [];
        }

        return seen;
    }

    private int RequestedCount(CaptureWindow window) =>
        window.Detail.Bulk == true ? _requestedDeleteIds.Distinct().Count() : window.RecordId is null ? 0 : 1;

    /// <summary>
    /// A delete that failed after Forms began deleting (a bulk action that throws partway) may have removed entries
    /// with no Deleted row to show it. That is recorded as a failed Deleted event, so the next stored write leaves an
    /// <c>AuditGap</c> row. Nothing is scrubbed in the request: which entries went is unknown, and reconciliation scrubs
    /// whatever is really gone.
    /// </summary>
    private void RecordPartialDelete()
    {
        try
        {
            if (collector.Window is not { Kind: AccessEventType.Deleted, DeletedRecords.Count: > 0 } window)
            {
                return;
            }

            health.RecordWriteFailure(AccessEventType.Deleted, window.FormId ?? Guid.Empty);
            SafeLog.Run(() => logger.LogError(
                "FormsAuditTrail saw Forms start deleting entries in a request that then failed: event {EventType}, form {FormId}, user {UserKey}, records {RecordCount}; no Deleted row is written and the daily reconciliation will scrub what was removed.",
                AccessEventType.Deleted,
                window.FormId,
                AuditHandlerHelpers.TryGetCurrentUser(securityAccessor, out Guid userKey, out _) ? userKey : Guid.Empty,
                window.DeletedRecords.Select(d => d.RecordId).Distinct().Count()));
        }
        catch
        {
            // Health is in-memory; if even that fails there is nowhere left to report it.
        }
    }

    /// <summary>
    /// The in-request erasure scrub (spec 7.3, both S5 branches): once Forms has returned 2xx its delete scope has
    /// completed, so the deleted entries' stored edit values can be nulled now rather than by the next reconciliation.
    /// Limited to the request's form, so ids naming another form's records cannot erase that form's edit history.
    /// </summary>
    private async Task ScrubDeletedAsync()
    {
        try
        {
            if (collector.Window is not { Kind: AccessEventType.Deleted } window)
            {
                return;
            }

            IReadOnlyList<Guid> ids = DeletedIds(window);
            Guid userKey = AuditHandlerHelpers.TryGetCurrentUser(securityAccessor, out Guid key, out _) ? key : Guid.Empty;
            await scrubber.ScrubAsync(window.FormId, ids, userKey);
        }
        catch (Exception ex)
        {
            SafeLog.Run(() => logger.LogError("FormsAuditTrail could not scrub deleted entries' edit values ({ExceptionType}); the daily reconciliation will.", ex.GetType().Name));
        }
    }

    private static bool IsDeleteAction(CaptureRequest request) =>
        request.RouteValues.TryGetValue("actionId", out object? value)
        && (value is Guid g ? g : Guid.TryParse(value?.ToString(), out Guid parsed) ? parsed : Guid.Empty) == DeleteRecordSetAction;
}
