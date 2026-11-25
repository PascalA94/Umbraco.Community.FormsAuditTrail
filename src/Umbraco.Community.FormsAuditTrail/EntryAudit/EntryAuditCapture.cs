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
using FormsConstants = Umbraco.Forms.Core.Constants;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>What the adapter must do once the capture is complete. Only an export can ever ask for a 503.</summary>
public sealed record CaptureOutcome(bool ReplaceWith503)
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
public sealed class EntryAuditCapture(
    EntryAuditCollector collector,
    IEntryAuditWriter writer,
    IOptionsMonitor<FormsAuditTrailOptions> options,
    IBackOfficeSecurityAccessor securityAccessor,
    IRecordStorage recordStorage,
    IFormService formService,
    EntryAuditHealth health,
    ILogger<EntryAuditCapture> logger)
{
    private static readonly Guid DeleteRecordSetAction = Guid.Parse(FormsConstants.RecordSetActionTypes.DeleteRecordSet);

    private AccessEventType? _kind;

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
                CurrentUserKeyOrEmpty()));
            return false;
        }
    }

    /// <summary>
    /// Writes the access row for a completed action. <paramref name="resultValue"/> is the action's result value, the
    /// only place the generated export file name exists (S1, S7).
    /// </summary>
    public async Task<CaptureOutcome> CompleteAsync(bool success, object? resultValue = null)
    {
        if (_kind is not { } kind || !success)
        {
            return CaptureOutcome.None;
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
                    CurrentUserKeyOrEmpty()));
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
                CurrentUserKeyOrEmpty()));
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
    /// Runs before the action. A failure must not stop the edit (E12): it is logged, counted in health, and the edit is
    /// still recorded, without values, as <c>beforeUnavailable</c>.
    /// </remarks>
    private void CaptureBeforeValues(Guid? formId, Guid? recordId)
    {
        try
        {
            if (formId is not { } fid || recordId is not { } rid)
            {
                throw new InvalidOperationException("The edit request names no form or record.");
            }

            Umbraco.Forms.Core.Models.Form form = formService.Get(fid) ?? throw new InvalidOperationException("Form not found.");
            Umbraco.Forms.Core.Persistence.Dtos.Record record = recordStorage.GetRecordByUniqueId(rid, form)
                ?? throw new InvalidOperationException("Record not found.");
            collector.SetBeforeValues(RecordValueSnapshot.From(record));
        }
        catch (Exception ex)
        {
            collector.MarkBeforeUnavailable();
            RecordFailure(AccessEventType.Edited, formId);
            SafeLog.Run(() => logger.LogError(
                "FormsAuditTrail could not read an entry's values before an edit ({ExceptionType}): event {EventType}, form {FormId}, user {UserKey}, records 1; the edit is recorded without values.",
                ex.GetType().Name,
                AccessEventType.Edited,
                formId,
                CurrentUserKeyOrEmpty()));
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

    /// <summary>For failure log lines only: the user may be the very thing that failed, and the log must still be written.</summary>
    private Guid CurrentUserKeyOrEmpty()
    {
        try
        {
            return AuditHandlerHelpers.TryGetCurrentUser(securityAccessor, out Guid key, out _) ? key : Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
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
            // Deleted rows and their in-request scrub arrive with Task 14 (S5 fallback).
            case AccessEventType.Deleted:
                return null;
        }

        EntryAuditOptions audit = options.CurrentValue.EntryAudit;
        EntryAuditDetail raw = window.Detail;
        if (kind == AccessEventType.Exported && resultValue is PostGenerateExportController.GenerateExportResponseModel generated)
        {
            // The download is matched to this row by file name (Task 11b), so both kinds must carry it.
            raw = raw with { FileName = generated.FileName };
        }

        // R33: hashed before shaping, so two different searches stay two views when the term itself is not stored.
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
            recordId = CreatedRecordId(resultValue) ?? recordId;
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
        }

        return new AccessWrite(kind, formId, null, recordId, recordCount, userKey, userName, detail, recordIds, window.Changes.ToArray())
        {
            DedupeFilterHash = dedupeHash,
            // Every unresolved user shares Guid.Empty, so a dedupe key would hide a second person behind the first.
            SkipDedupe = userUnresolved,
        };
    }

    private static bool IsDeleteAction(CaptureRequest request) =>
        request.RouteValues.TryGetValue("actionId", out object? value)
        && (value is Guid g ? g : Guid.TryParse(value?.ToString(), out Guid parsed) ? parsed : Guid.Empty) == DeleteRecordSetAction;
}
