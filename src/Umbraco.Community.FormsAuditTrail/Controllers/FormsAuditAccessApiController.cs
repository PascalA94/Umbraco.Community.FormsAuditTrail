using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.Authorization;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Controllers.Dtos;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Community.FormsAuditTrail.Services;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Security;
using Umbraco.Forms.Core.Services;

namespace Umbraco.Community.FormsAuditTrail.Controllers;

/// <summary>The filters of the access-log list, shared with the CSV export so the two cannot drift.</summary>
internal sealed record AccessFilter(
    Guid? FormId = null,
    Guid? UserKey = null,
    int? EventType = null,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    Guid? RecordId = null);

/// <summary>
/// Which forms the current user may see rows for, computed once per request. <see cref="AccessibleFormIds"/> is null
/// when nothing is restricted, so the list query carries no <c>IN</c> clause.
/// </summary>
internal sealed record AccessRowScope(bool AllForms, IReadOnlyList<Guid>? AccessibleFormIds);

/// <summary>
/// Reads the entry access log (design 7.5). Both policies sit at class level: the access-log policy narrows the Forms
/// section policy and never stands in for it.
/// </summary>
/// <remarks>
/// The <c>AuditSearched</c> and <c>AuditViewed</c> writes are adapted from UserActivity <c>Security/ViewLogger.cs</c>:
/// reading the log is itself logged, a repeat inside the dedupe window is not written again, and a failed write never
/// fails the read. The throttle half (UserActivity <c>Security/ViewThrottle.cs</c>) already lives in
/// <see cref="EntryAuditDeduplicator"/>, which the writer applies.
/// </remarks>
[ApiController]
[Route("umbraco/formsaudit/api/v1/access")]
[Authorize(Policy = SectionAccessFormsPolicy)]
[Authorize(Policy = AccessLogPolicy.Name)]
public class FormsAuditAccessApiController(
    FormsAuditDbContext db,
    IFormsSecurity formsSecurity,
    IFormService formService,
    IRecordStorage recordStorage,
    IFormFieldInfoProvider fieldInfo,
    ISensitiveDataPermission sensitiveData,
    IEntryAuditWriter writer,
    IBackOfficeSecurityAccessor securityAccessor,
    IUserService userService,
    IUploadPathResolver uploadPaths,
    IOptionsMonitor<FormsAuditTrailOptions> options,
    ILogger<FormsAuditAccessApiController> logger) : ControllerBase
{
    // Forms' own AuthorizationPolicies class is internal, so the name is repeated here as in FormsAuditApiController.
    private const string SectionAccessFormsPolicy = "SectionAccessForms";
    private const int MaxPageSize = 100;

    /// <summary>
    /// Set on a record history that was cut short, to <c>history</c>, <c>fileViews</c> or both. A header rather than a
    /// field so the history stays the plain list the dashboard already reads.
    /// </summary>
    public const string TruncatedHeader = "X-FormsAuditTrail-Truncated";

    private bool? _canAccessEveryForm;

    /// <summary>Most rows one record history returns, newest first.</summary>
    internal int HistoryLimit { get; set; } = 1000;

    /// <summary>Most <c>FileViewed</c> rows read to match one record's uploads, newest first.</summary>
    internal int FileViewScanLimit { get; set; } = 50_000;

    [HttpGet("")]
    public async Task<ActionResult<PagedResultDto<AccessEntryListDto>>> GetAccess(
        [FromQuery] Guid? formId = null,
        [FromQuery] Guid? userKey = null,
        [FromQuery] int? eventType = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] Guid? recordId = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        NoStore();
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        if (formId is { } requested && !await CanAccessFormAsync(requested))
        {
            return Forbid();
        }

        var filter = new AccessFilter(formId, userKey, eventType, fromDate, toDate, recordId);
        AccessRowScope scope = await GetRowScopeAsync(cancellationToken);
        IQueryable<FormAccessEntry> query = VisibleRows(scope, filter);

        int total = await query.CountAsync(cancellationToken);
        List<AccessEntryListDto> items = await ToListDtosAsync(query, cancellationToken, (page - 1) * pageSize, pageSize);

        // SEC-06: a userKey or recordId filter is the "what did this colleague do / who saw this entry" query, so it is
        // logged. Other filters are browsing and are not.
        if (userKey.HasValue || recordId.HasValue)
        {
            var detail = new EntryAuditDetail
            {
                Filter = new EntryAuditFilter(fromDate, toDate, null, null, null, null, null),
                Page = page,
                PageSize = pageSize,
                SubjectUserKey = userKey,
                EventType = eventType,
            };

            // The row's form must be a fact about the record, never the caller's filter: a search row carrying a form the
            // caller can open would otherwise be a row they wrote that places the record there (fix1).
            Guid searchedForm = formId is { } f && f != Guid.Empty && (recordId is not { } r || await RecordSeenOnFormAsync(r, f, cancellationToken))
                ? f
                : Guid.Empty;
            await LogReadAsync(AccessEventType.AuditSearched, searchedForm, recordId, detail, SearchDedupeHash(filter, page, pageSize));
        }

        return Ok(new PagedResultDto<AccessEntryListDto> { Items = items, Total = total, Page = page, PageSize = pageSize });
    }

    /// <summary>
    /// Streams the rows the current user may see as CSV (SEC-10). The audit row is written before the first byte, so an
    /// export that is cancelled half-way still leaves a trace. Under the opt-in <c>FailClosedOnExport</c> a row that
    /// cannot be written refuses the export with a 503 before any byte is sent (E5); by default a failed
    /// write is logged and the export proceeds (E12). At most one export per user runs at a time (429 otherwise).
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> ExportAccess(
        [FromQuery] Guid? formId = null,
        [FromQuery] Guid? userKey = null,
        [FromQuery] int? eventType = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] Guid? recordId = null,
        CancellationToken cancellationToken = default)
    {
        // 404 rather than 403, as on the definition export: a switched-off export effectively does not exist (SEC-L4).
        if (!options.CurrentValue.EntryAuditCsvEnabled)
        {
            return NotFound();
        }

        if (formId is { } requested && !await CanAccessFormAsync(requested))
        {
            return Forbid();
        }

        Guid exporter = AuditHandlerHelpers.TryGetCurrentUser(securityAccessor, out Guid exporterKey, out _) ? exporterKey : Guid.Empty;
        if (!AccessExportGate.TryEnter(exporter))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        try
        {
            var filter = new AccessFilter(formId, userKey, eventType, fromDate, toDate, recordId);
            AccessRowScope scope = await GetRowScopeAsync(cancellationToken);
            IQueryable<FormAccessEntry> query = VisibleRows(scope, filter);

            int total = await query.CountAsync(cancellationToken);
            string fileName = string.Create(CultureInfo.InvariantCulture, $"access-log-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
            var detail = new EntryAuditDetail
            {
                ExportType = "accessLogCsv",
                FileName = fileName,
                Filter = new EntryAuditFilter(fromDate, toDate, null, null, null, null, null),
                // SEC-06: an export filtered to a colleague or an event type is the same question as the logged search,
                // so the row records who and what was asked about, as the AuditSearched row does.
                SubjectUserKey = userKey,
                EventType = eventType,
                Truncated = total > AccessCsvWriter.DefaultMaxRows ? true : null,
            };

            // Same rule as the search row: the stamped form is a fact about the record, never the caller's filter.
            Guid exportedForm = formId is { } f && f != Guid.Empty && (recordId is not { } r || await RecordSeenOnFormAsync(r, f, cancellationToken))
                ? f
                : Guid.Empty;
            bool recorded = await WriteExportAuditAsync(exportedForm, recordId, detail, Math.Min(total, AccessCsvWriter.DefaultMaxRows));
            if (!recorded && options.CurrentValue.EntryAudit.FailClosedOnExport)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            Response.ContentType = "text/csv; charset=utf-8";
            Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            NoStore();
            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            try
            {
                await AccessCsvWriter.WriteAsync(
                    Response.Body, StreamExportRows(query, AccessCsvWriter.DefaultMaxRows + 1, cancellationToken), AccessCsvWriter.DefaultMaxRows, cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException && cancellationToken.IsCancellationRequested)
            {
                // The client went away mid-download (Kestrel reports it as either); the headers are already sent, so
                // there is nothing to answer with.
                logger.LogDebug(ex, "FormsAuditTrail access-log export was abandoned by the client.");
            }

            return new EmptyResult();
        }
        finally
        {
            AccessExportGate.Exit(exporter);
        }
    }

    /// <summary>
    /// Reads the export in keyset-paginated batches, each its own short query, so no database reader or transaction is
    /// held open while a slow client drains the response. Ordered by (Timestamp, Id) descending; the next batch starts
    /// strictly after the last row seen, so rows written meanwhile can neither repeat nor shift the window.
    /// </summary>
    private static async IAsyncEnumerable<AccessEntryListDto> StreamExportRows(
        IQueryable<FormAccessEntry> query, int take, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int batchSize = 1_000;
        IQueryable<FormAccessEntry> ordered = query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id);
        (DateTime Timestamp, long Id)? last = null;
        int remaining = take;

        while (remaining > 0)
        {
            IQueryable<FormAccessEntry> page = ordered;
            if (last is { } after)
            {
                DateTime lastTimestamp = after.Timestamp;
                long lastId = after.Id;
                page = page.Where(e => e.Timestamp < lastTimestamp || (e.Timestamp == lastTimestamp && e.Id < lastId));
            }

            int size = Math.Min(batchSize, remaining);
            var batch = await page
                .Take(size)
                .Select(e => new { e.Id, e.Timestamp, e.EventType, e.FormId, e.FormName, e.RecordId, e.RecordCount, e.UserKey, e.UserName, e.Detail, e.CorrelationId })
                .ToListAsync(cancellationToken);

            foreach (var e in batch)
            {
                yield return MapRow<AccessEntryListDto>(e.Id, e.Timestamp, e.EventType, e.FormId, e.FormName, e.RecordId, e.RecordCount, e.UserKey, e.UserName, e.Detail, e.CorrelationId);
            }

            if (batch.Count < size)
            {
                yield break;
            }

            last = (batch[^1].Timestamp, batch[^1].Id);
            remaining -= batch.Count;
        }
    }

    /// <summary>
    /// Writes the <c>AuditExported</c> row. Kept apart from the search and view logging because an export has a record
    /// count and no dedupe window: two exports a minute apart are two exports. Never throws (E12); returns false when
    /// the row could not be written, so the caller can apply <c>FailClosedOnExport</c>. Nothing to write counts as success.
    /// </summary>
    private async Task<bool> WriteExportAuditAsync(Guid formId, Guid? recordId, EntryAuditDetail detail, int rowCount)
    {
        try
        {
            if (!options.CurrentValue.EntryAudit.Enabled)
            {
                return true;
            }

            if (!AuditHandlerHelpers.TryGetCurrentUser(securityAccessor, out Guid userKey, out string userName))
            {
                // SEC-L5: a backoffice request with no resolvable user is Unknown, never System.
                (userKey, userName) = (Guid.Empty, AuditUserNames.Unknown);
                detail = detail with { UserUnresolved = true };
            }

            var write = new AccessWrite(AccessEventType.AuditExported, formId, null, recordId, rowCount, userKey, userName, detail, [], []);
            AccessWriteResult result = await writer.WriteAsync(write);
            if (result.Status == AccessWriteStatus.Failed)
            {
                logger.LogError(
                    "FormsAuditTrail could not record an access-log export: user {UserKey}, form {FormId}, record count {RecordCount}.",
                    userKey, formId, rowCount);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FormsAuditTrail could not record an access-log export: form {FormId}.", formId);
            return false;
        }
    }

    [HttpGet("{id:long}")]
    public async Task<ActionResult<AccessEntryDetailDto>> GetAccessEntry(long id, CancellationToken cancellationToken = default)
    {
        NoStore();

        FormAccessEntry? entry = await db.AccessEntries
            .AsNoTracking()
            .Include(e => e.Changes)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        // 404, not 403, for an inaccessible form, so ids cannot be enumerated.
        if (entry is null || !await CanAccessFormAsync(entry.FormId))
        {
            return NotFound();
        }

        int childCount = await db.AccessRecords.CountAsync(r => r.AccessId == id, cancellationToken);
        long? linked = null;
        if (entry.CorrelationId is { } correlation)
        {
            // Same form only: an export and its download always share the form, and this keeps the link inside what the
            // per-form check above already allowed.
            linked = await db.AccessEntries
                .Where(e => e.CorrelationId == correlation && e.Id != id && e.FormId == entry.FormId)
                .OrderByDescending(e => e.Id)
                .Select(e => (long?)e.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        AccessEntryDetailDto dto = MapRow<AccessEntryDetailDto>(
            entry.Id, entry.Timestamp, entry.EventType, entry.FormId, entry.FormName, entry.RecordId, entry.RecordCount,
            entry.UserKey, entry.UserName, entry.Detail, entry.CorrelationId);
        dto.ChildRecordCount = childCount;
        dto.LinkedAccessId = linked;
        dto.Changes = MaskChanges(entry);
        await ApplyCurrentNamesAsync([dto]);

        // SEC-06: opening an edit whose values are still stored is a read of personal data, so it is logged.
        if (entry.EventType == (int)AccessEventType.Edited && entry.Changes.Any(c => c.ValuesScrubbedAt == null))
        {
            await LogReadAsync(AccessEventType.AuditViewed, entry.FormId, entry.RecordId, null, null);
        }

        return Ok(dto);
    }

    [HttpGet("records/{recordId:guid}")]
    public async Task<ActionResult<List<AccessEntryListDto>>> GetRecordHistory(Guid recordId, CancellationToken cancellationToken = default)
    {
        NoStore();

        // Ids and form ids only, before any visibility rule, to decide the 404 before anything is returned.
        var direct = await db.AccessEntries
            .Where(e => e.RecordId == recordId || e.Records.Any(r => r.RecordId == recordId))
            .Select(e => new { e.Id, e.FormId, e.Timestamp, e.CorrelationId, e.EventType })
            .ToListAsync(cancellationToken);

        // The gate reads only events on the record itself. Audit rows are written by whoever reads the log, so they could
        // otherwise place the record on a form of the caller's choosing (fix1).
        var primary = direct.Where(e => IsRecordEvent(e.EventType)).ToList();
        List<Guid> primaryForms = primary.Select(e => e.FormId).Distinct().ToList();

        // One answer for "never seen" and "not yours" (Review Focus 3), so a record id cannot be probed. Every form the
        // record was seen on must be accessible; a record seen only through Guid.Empty rows falls to the all-forms rule.
        if (primary.Count == 0 || !await CanAccessEveryAsync(primaryForms))
        {
            return NotFound();
        }

        Guid formId = primary.Where(e => e.FormId != Guid.Empty).OrderByDescending(e => e.Timestamp).Select(e => e.FormId).FirstOrDefault();
        List<Guid> correlations = primary.Where(e => e.CorrelationId.HasValue).Select(e => e.CorrelationId!.Value).Distinct().ToList();
        (List<long> fileViews, bool fileViewsTruncated) = formId == Guid.Empty
            ? ([], false)
            : await MatchFileViewsAsync(recordId, formId, cancellationToken);
        List<long> directIds = direct.Select(e => e.Id).ToList();

        // A download carries no record ids; it belongs to this history through its export's correlation id, and only on
        // a form the record itself was seen on.
        AccessRowScope scope = await GetRowScopeAsync(cancellationToken);
        IQueryable<FormAccessEntry> query = VisibleRows(scope, new AccessFilter())
            .Where(e => directIds.Contains(e.Id)
                || (e.CorrelationId != null && correlations.Contains(e.CorrelationId.Value) && primaryForms.Contains(e.FormId))
                || fileViews.Contains(e.Id));
        List<AccessEntryListDto> history = await ToListDtosAsync(query, cancellationToken, 0, HistoryLimit + 1);

        if (history.Count == 0)
        {
            return NotFound();
        }

        var truncated = new List<string>();
        if (history.Count > HistoryLimit)
        {
            history.RemoveRange(HistoryLimit, history.Count - HistoryLimit);
            truncated.Add("history");
        }

        if (fileViewsTruncated)
        {
            truncated.Add("fileViews");
        }

        if (truncated.Count > 0)
        {
            Response.Headers[TruncatedHeader] = string.Join(", ", truncated);
        }

        await LogReadAsync(AccessEventType.AuditViewed, formId, recordId, null, null);
        return Ok(history);
    }

    [HttpGet("forms")]
    public async Task<ActionResult<List<FormSummaryDto>>> GetForms(CancellationToken cancellationToken = default)
    {
        NoStore();
        AccessRowScope scope = await GetRowScopeAsync(cancellationToken);
        List<FormSummaryDto> forms = await VisibleRows(scope, new AccessFilter())
            .GroupBy(e => new { e.FormId, e.FormName })
            .Select(g => new FormSummaryDto { FormId = g.Key.FormId, FormName = g.Key.FormName })
            .OrderBy(f => f.FormName)
            .ToListAsync(cancellationToken);
        return Ok(forms);
    }

    [HttpGet("users")]
    public async Task<ActionResult<List<AccessUserSummaryDto>>> GetUsers(CancellationToken cancellationToken = default)
    {
        NoStore();
        AccessRowScope scope = await GetRowScopeAsync(cancellationToken);
        var rows = await VisibleRows(scope, new AccessFilter())
            .GroupBy(e => new { e.UserKey, e.UserName })
            .Select(g => new { g.Key.UserKey, g.Key.UserName, Latest = g.Max(e => e.Timestamp) })
            .ToListAsync(cancellationToken);

        // One entry per key, under the most recently stored name: the key is the identity, the name is presentation.
        List<AccessUserSummaryDto> users = rows
            .GroupBy(r => r.UserKey)
            .Select(g => g.OrderByDescending(r => r.Latest).First())
            .Select(r => new AccessUserSummaryDto { UserKey = r.UserKey, UserName = r.UserName })
            .OrderBy(u => u.UserName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Dictionary<Guid, string> current = await CurrentNamesAsync(users.Select(u => u.UserKey));
        foreach (AccessUserSummaryDto user in users)
        {
            user.CurrentUserName = DifferentName(current, user.UserKey, user.UserName);
        }

        return Ok(users);
    }

    /// <summary>
    /// True when every form that exists, trashed ones included, passes <see cref="IFormsSecurity.HasAccessToForm(Guid)"/>;
    /// only such a user sees <c>Guid.Empty</c> rows (SEC-L3) and the rows of forms that no longer exist. Computed once per
    /// request; a failed lookup answers false, which fails closed.
    /// </summary>
    internal async Task<bool> CanAccessEveryFormAsync()
    {
        await LoadFormSetsAsync();
        return _canAccessEveryForm!.Value;
    }

    private HashSet<Guid>? _liveForms;

    private Task LoadFormSetsAsync()
    {
        if (_canAccessEveryForm is not null)
        {
            return Task.CompletedTask;
        }

        // Separate lookups so one failure does not discard the other. A failed live list leaves _liveForms null, which
        // means no form can be proven live: every non-empty form id then follows the all-forms rule, which itself fails
        // closed. Falling back to the per-id answer would let Forms' answer for a deleted id decide visibility.
        HashSet<Guid>? live = null;
        try
        {
            live = formService.GetSlim().Select(f => f.Id).ToHashSet();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FormsAuditTrail could not list the forms; rows of forms that cannot be proven to exist are hidden from restricted users for this request.");
        }

        HashSet<Guid>? trashed = null;
        try
        {
            trashed = formService.GetAllTrashedKeys().ToHashSet();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FormsAuditTrail could not list the trashed forms; they are treated as not live for this request.");
        }

        if (live is not null)
        {
            // GetSlim is not documented to exclude trashed forms, so they are removed here rather than assumed gone.
            if (trashed is not null)
            {
                live.ExceptWith(trashed);
            }

            // A trashed form still has rows, so it counts towards all-forms access; with no trashed list, all-forms access fails closed.
            _canAccessEveryForm = trashed is not null && live.Concat(trashed).All(formsSecurity.HasAccessToForm);
            _liveForms = live;
        }
        else
        {
            _canAccessEveryForm = false;
            _liveForms = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A row's form is checked by id only while the form exists. Once it is deleted or trashed, what Forms' security says
    /// about its id is not something we can rely on, so those rows follow the all-forms rule, exactly like
    /// <c>Guid.Empty</c> rows.
    /// </summary>
    private async Task<bool> CanSeeFormRowsAsync(Guid formId)
    {
        await LoadFormSetsAsync();
        if (formId == Guid.Empty)
        {
            return _canAccessEveryForm!.Value;
        }

        return _liveForms is not null && _liveForms.Contains(formId) ? formsSecurity.HasAccessToForm(formId) : _canAccessEveryForm!.Value;
    }

    internal async Task<AccessRowScope> GetRowScopeAsync(CancellationToken cancellationToken)
    {
        bool allForms = await CanAccessEveryFormAsync();
        List<Guid> logged = await db.AccessEntries
            .Where(e => e.FormId != Guid.Empty)
            .Select(e => e.FormId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var accessible = new List<Guid>();
        foreach (Guid id in logged)
        {
            if (await CanSeeFormRowsAsync(id))
            {
                accessible.Add(id);
            }
        }

        return new AccessRowScope(allForms, allForms && accessible.Count == logged.Count ? null : accessible);
    }

    /// <summary>The rows the current user may see, with the list filters applied. Reused by the CSV export so the list and the file cannot drift.</summary>
    internal IQueryable<FormAccessEntry> VisibleRows(AccessRowScope scope, AccessFilter filter)
    {
        IQueryable<FormAccessEntry> query = db.AccessEntries.AsNoTracking();

        if (scope.AccessibleFormIds is { } ids)
        {
            query = scope.AllForms
                ? query.Where(e => e.FormId == Guid.Empty || ids.Contains(e.FormId))
                : query.Where(e => ids.Contains(e.FormId));
        }

        if (filter.FormId is { } formId)
        {
            query = query.Where(e => e.FormId == formId);
        }

        if (filter.UserKey is { } userKey)
        {
            query = query.Where(e => e.UserKey == userKey);
        }

        if (filter.EventType is { } eventType)
        {
            query = query.Where(e => e.EventType == eventType);
        }

        if (filter.FromDate is { } from)
        {
            query = query.Where(e => e.Timestamp >= from);
        }

        if (filter.ToDate is { } toDate)
        {
            // A date-only upper bound means "up to and including that day", as on the definition audit. On the last
            // representable day there is no next day to compare with, and no row can be later, so no bound applies.
            if (toDate.TimeOfDay != TimeSpan.Zero)
            {
                query = query.Where(e => e.Timestamp < toDate);
            }
            else if (toDate.Date < DateTime.MaxValue.Date)
            {
                DateTime to = toDate.AddDays(1);
                query = query.Where(e => e.Timestamp < to);
            }
        }

        if (filter.RecordId is { } recordId)
        {
            // Design 7.5: the row's own record id or a child row (bulk deletes, exports).
            query = query.Where(e => e.RecordId == recordId || e.Records.Any(r => r.RecordId == recordId));
        }

        return query;
    }

    private async Task<bool> CanAccessFormAsync(Guid formId) =>
        await CanSeeFormRowsAsync(formId);

    private async Task<bool> CanAccessEveryAsync(IEnumerable<Guid> formIds)
    {
        foreach (Guid formId in formIds)
        {
            if (!await CanAccessFormAsync(formId))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Events that happen to a record. The <c>Audit*</c> events record someone reading the log, so they never say which
    /// form a record is on.
    /// </summary>
    private static bool IsRecordEvent(int eventType) => !AuditEventTypes.Contains(eventType);

    /// <summary>
    /// The one list of <c>Audit*</c> events. <see cref="IsRecordEvent"/> and the EF predicate in
    /// <see cref="RecordSeenOnFormAsync"/> both read it, so a new <c>Audit*</c> event cannot be added to one and missed
    /// by the other (which would let it vouch for a record on a form it never touched).
    /// </summary>
    private static readonly int[] AuditEventTypes =
    [
        (int)AccessEventType.AuditViewed,
        (int)AccessEventType.AuditSearched,
        (int)AccessEventType.AuditExported,
    ];

    private Task<bool> RecordSeenOnFormAsync(Guid recordId, Guid formId, CancellationToken cancellationToken) =>
        db.AccessEntries.AnyAsync(
            e => e.FormId == formId
                && !AuditEventTypes.Contains(e.EventType)
                && (e.RecordId == recordId || e.Records.Any(r => r.RecordId == recordId)),
            cancellationToken);

    private List<EntryChangeDto> MaskChanges(FormAccessEntry entry)
    {
        if (entry.Changes.Count == 0)
        {
            return [];
        }

        IReadOnlyDictionary<Guid, FieldInfo> fields;
        bool viewerCanSee;
        try
        {
            fields = entry.FormId == Guid.Empty ? new Dictionary<Guid, FieldInfo>() : fieldInfo.GetFields(entry.FormId);
            viewerCanSee = sensitiveData.CurrentUserCanViewSensitiveData();
        }
        catch (Exception ex)
        {
            // SEC-12 fails closed: without the field list or the permission, every stored value is masked.
            logger.LogWarning(ex, "FormsAuditTrail could not read the fields of form {FormId}; edit values are masked.", entry.FormId);
            fields = new Dictionary<Guid, FieldInfo>();
            viewerCanSee = false;
        }

        // The provider returns an empty set when the form cannot be loaded, which looks the same as a form with no
        // fields; either way no change matches a current field, so treating both as not loaded masks the same values.
        bool formLoaded = fields.Count > 0;

        return entry.Changes
            .OrderBy(c => c.Id)
            .Select(c => ReadTimeMasker.Apply(
                new EntryChangeDto
                {
                    FieldId = c.FieldId,
                    FieldCaption = c.FieldCaption,
                    OldValue = c.OldValue,
                    NewValue = c.NewValue,
                    Masked = c.Masked,
                    Cleared = c.Cleared,
                    ValuesScrubbedAt = c.ValuesScrubbedAt,
                },
                fields,
                formLoaded,
                viewerCanSee))
            .ToList();
    }

    /// <summary>
    /// <c>FileViewed</c> rows carry a path hash, not a record id (E13). They are matched by hashing the record's current
    /// values, which works only while the record exists; after erasure the link is gone, which is intended. The rows are
    /// streamed newest first, and only a row whose detail contains one of the record's hashes is parsed.
    /// </summary>
    private async Task<(List<long> Ids, bool Truncated)> MatchFileViewsAsync(Guid recordId, Guid formId, CancellationToken cancellationToken)
    {
        HashSet<string> hashes;
        try
        {
            Form? form = formService.Get(formId);
            var record = form is null ? null : recordStorage.GetRecordByUniqueId(recordId, form);
            if (record is null)
            {
                return ([], false);
            }

            hashes = UploadHashes(record.RecordFields.Values
                .SelectMany(f => f.Values ?? [])
                .Select(v => v?.ToString())
                .OfType<string>());
        }
        catch (Exception ex)
        {
            // The history is still worth returning without its file opens.
            logger.LogWarning(ex, "FormsAuditTrail could not read record {RecordId} to match its file views.", recordId);
            return ([], false);
        }

        if (hashes.Count == 0)
        {
            return ([], false);
        }

        var matched = new List<long>();
        int scanned = 0;
        await foreach (var row in db.AccessEntries
            .Where(e => e.FormId == formId && e.EventType == (int)AccessEventType.FileViewed && e.Detail != null)
            .OrderByDescending(e => e.Id)
            .Select(e => new { e.Id, e.Detail })
            .AsAsyncEnumerable()
            .WithCancellation(cancellationToken))
        {
            if (++scanned > FileViewScanLimit)
            {
                return (matched, true);
            }

            if (hashes.Any(h => row.Detail!.Contains(h, StringComparison.OrdinalIgnoreCase))
                && ReadDetailString(row.Detail, "filePathHash") is { } hash
                && hashes.Contains(hash))
            {
                matched.Add(row.Id);
            }
        }

        return (matched, false);
    }

    /// <summary>
    /// The path hashes of a record's upload values, computed once. Each candidate is confirmed through
    /// <see cref="FileViewedMatcher"/>, so what counts as an upload path stays defined in one place.
    /// </summary>
    private HashSet<string> UploadHashes(IEnumerable<string> values)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in values)
        {
            int cut = value.IndexOfAny(['?', '#']);
            string pathText = cut >= 0 ? value[..cut] : value;
            if (!pathText.StartsWith('/'))
            {
                continue;
            }

            string hash = uploadPaths.HashPath(new PathString(pathText));
            if (FileViewedMatcher.Matches(hash, [value], uploadPaths))
            {
                hashes.Add(hash);
            }
        }

        return hashes;
    }

    private async Task<List<AccessEntryListDto>> ToListDtosAsync(
        IQueryable<FormAccessEntry> query, CancellationToken cancellationToken, int skip = 0, int? take = null)
    {
        IQueryable<FormAccessEntry> ordered = query.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id).Skip(skip);
        if (take is { } count)
        {
            ordered = ordered.Take(count);
        }

        // Projected server-side so the change rows and child ids are never loaded for a list.
        var rows = await ordered
            .Select(e => new { e.Id, e.Timestamp, e.EventType, e.FormId, e.FormName, e.RecordId, e.RecordCount, e.UserKey, e.UserName, e.Detail, e.CorrelationId })
            .ToListAsync(cancellationToken);

        List<AccessEntryListDto> items = rows
            .Select(e => MapRow<AccessEntryListDto>(e.Id, e.Timestamp, e.EventType, e.FormId, e.FormName, e.RecordId, e.RecordCount, e.UserKey, e.UserName, e.Detail, e.CorrelationId))
            .ToList();
        await ApplyCurrentNamesAsync(items);
        return items;
    }

    private static T MapRow<T>(
        long id, DateTime timestamp, int eventType, Guid formId, string formName, Guid? recordId, int recordCount,
        Guid userKey, string userName, string? detail, Guid? correlationId)
        where T : AccessEntryListDto, new() => new()
    {
        Id = id,
        // SQLite loses the kind; the context's converter restores it, and this keeps a projection honest as well.
        Timestamp = DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
        EventType = ((AccessEventType)eventType).ToString(),
        FormId = formId,
        FormName = formName,
        RecordId = recordId,
        RecordCount = recordCount,
        RecordIdsCaptured = ReadDetailBool(detail, "recordIdsCaptured"),
        UserKey = userKey,
        UserName = userName,
        Detail = detail,
        CorrelationId = correlationId,
    };

    private async Task ApplyCurrentNamesAsync(IReadOnlyCollection<AccessEntryListDto> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        Dictionary<Guid, string> current = await CurrentNamesAsync(items.Select(i => i.UserKey));
        foreach (AccessEntryListDto item in items)
        {
            item.CurrentUserName = DifferentName(current, item.UserKey, item.UserName);
        }
    }

    /// <summary>Current names by key (SEC-L2). Presentation only, so a failed lookup leaves the stored names alone.</summary>
    private async Task<Dictionary<Guid, string>> CurrentNamesAsync(IEnumerable<Guid> keys)
    {
        // Guid.Empty is System or Unknown, which no user can be.
        Guid[] distinct = keys.Where(k => k != Guid.Empty).Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return [];
        }

        try
        {
            IEnumerable<IUser> users = await userService.GetAsync(distinct) ?? [];
            return users
                .Where(u => !string.IsNullOrEmpty(u.Name))
                .GroupBy(u => u.Key)
                .ToDictionary(g => g.Key, g => g.First().Name!);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "FormsAuditTrail could not look up current user names; stored names are shown.");
            return [];
        }
    }

    private static string? DifferentName(Dictionary<Guid, string> current, Guid key, string stored) =>
        current.TryGetValue(key, out string? name) && !string.Equals(name, stored, StringComparison.Ordinal) ? name : null;

    /// <summary>
    /// Writes an <c>AuditSearched</c> or <c>AuditViewed</c> row. Never throws: reading the log must not fail because
    /// logging the read did (E12). Nothing is written while entry auditing is switched off (E10).
    /// </summary>
    private async Task LogReadAsync(AccessEventType type, Guid formId, Guid? recordId, EntryAuditDetail? detail, string? dedupeHash)
    {
        try
        {
            if (!options.CurrentValue.EntryAudit.Enabled)
            {
                return;
            }

            Guid userKey;
            string userName;
            if (AuditHandlerHelpers.TryGetCurrentUser(securityAccessor, out Guid key, out string name))
            {
                (userKey, userName) = (key, name);
            }
            else
            {
                // SEC-L5: a backoffice request with no resolvable user is Unknown, never System.
                (userKey, userName) = (Guid.Empty, AuditUserNames.Unknown);
                detail = (detail ?? new EntryAuditDetail()) with { UserUnresolved = true };
            }

            var write = new AccessWrite(type, formId, null, recordId, recordId is null ? 0 : 1, userKey, userName, detail, [], [])
            {
                DedupeFilterHash = dedupeHash,
            };
            AccessWriteResult result = await writer.WriteAsync(write);
            if (result.Status == AccessWriteStatus.Failed)
            {
                logger.LogError(
                    "FormsAuditTrail could not record an access-log read: user {UserKey}, event {EventType}, form {FormId}, record count {RecordCount}.",
                    userKey, type, formId, write.RecordCount);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FormsAuditTrail could not record an access-log read: event {EventType}, form {FormId}.", type, formId);
        }
    }

    /// <summary>
    /// The dedupe key for a search covers every filter, because the stored detail has no slot for the user key filter
    /// and two searches for different colleagues must stay two rows. Held in memory only, never stored.
    /// </summary>
    private static string SearchDedupeHash(AccessFilter filter, int page, int pageSize)
    {
        string text = string.Join('|',
            filter.FormId?.ToString("N"),
            filter.UserKey?.ToString("N"),
            filter.EventType?.ToString(CultureInfo.InvariantCulture),
            filter.FromDate?.ToString("O", CultureInfo.InvariantCulture),
            filter.ToDate?.ToString("O", CultureInfo.InvariantCulture),
            filter.RecordId?.ToString("N"),
            page.ToString(CultureInfo.InvariantCulture),
            pageSize.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";

    private static bool? ReadDetailBool(string? json, string property) =>
        ReadDetail(json, property) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    private static string? ReadDetailString(string? json, string property) =>
        ReadDetail(json, property) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static JsonElement? ReadDetail(string? json, string property)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(property, out JsonElement value)
                ? value.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
