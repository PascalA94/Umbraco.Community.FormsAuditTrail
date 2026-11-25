using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Community.FormsAuditTrail.Authorization;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Controllers.Dtos;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Community.FormsAuditTrail.Services;
using Umbraco.Forms.Core.Security;

namespace Umbraco.Community.FormsAuditTrail.Controllers;

[ApiController]
[Route("umbraco/formsaudit/api/v1")]
[Authorize(Policy = SectionAccessFormsPolicy)]
public class FormsAuditApiController : ControllerBase
{
    // The policy name registered by Umbraco Forms for backoffice users with access to the Forms
    // section. Forms' own Umbraco.Forms.Web.Authorization.AuthorizationPolicies class is internal,
    // so the name is referenced here directly.
    private const string SectionAccessFormsPolicy = "SectionAccessForms";

    private readonly FormsAuditDbContext _db;
    private readonly IFormsSecurity _formsSecurity;
    private readonly IOptionsMonitor<FormsAuditTrailOptions> _options;
    private readonly IAuthorizationService _authorization;
    private readonly EntryAuditHealth _health;
    private readonly TimeProvider _time;
    private readonly IForeignValueHandlerDetector _foreignHandlers;
    private readonly ILogger<FormsAuditApiController> _logger;

    public FormsAuditApiController(
        FormsAuditDbContext db,
        IFormsSecurity formsSecurity,
        IOptionsMonitor<FormsAuditTrailOptions> options,
        IAuthorizationService authorization,
        EntryAuditHealth health,
        TimeProvider time,
        IForeignValueHandlerDetector foreignHandlers,
        ILogger<FormsAuditApiController> logger)
    {
        _db = db;
        _formsSecurity = formsSecurity;
        _options = options;
        _authorization = authorization;
        _health = health;
        _time = time;
        _foreignHandlers = foreignHandlers;
        _logger = logger;
    }

    [HttpGet("config")]
    public async Task<ActionResult<AuditConfigDto>> GetConfig(CancellationToken cancellationToken = default)
    {
        FormsAuditTrailOptions options = _options.CurrentValue;
        var dto = new AuditConfigDto
        {
            CsvExportEnabled = options.EnableCsvExport,
            EntryAuditEnabled = options.EntryAudit.Enabled,
            AccessRetentionDays = options.EntryAudit.RetentionDays,
            EditValueRetentionDays = options.EntryAudit.EditValueRetentionDays,
            EntryAuditCsvEnabled = options.EntryAuditCsvEnabled,
        };

        // A failing user lookup must not take the definition-audit config down with it; the caller just loses the access-log extras.
        try
        {
            AuthorizationResult access = await _authorization.AuthorizeAsync(User, AccessLogPolicy.Name);
            dto.CanReadAccessLog = access.Succeeded;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not evaluate the access-log policy for the config request ({ExceptionType}).", ex.GetType().Name);
            dto.CanReadAccessLog = false;
        }

        if (!dto.CanReadAccessLog)
        {
            return Ok(dto);
        }

        EntryAuditHealthReport report = await _health.GetReportAsync(_time.GetUtcNow().UtcDateTime, cancellationToken);
        dto.EntryAuditHealthy = report.Healthy;
        dto.HealthReasons = report.Reasons.ToList();
        dto.LastWriteFailureUtc = report.LastWriteFailureUtc;
        dto.WriteFailuresSinceStart = report.WriteFailuresSinceStart;
        dto.LastJobRunUtc = report.LastJobRunUtc;
        dto.StoreEditValues = options.EntryAudit.StoreEditValues;
        dto.ForeignValueHandlers = options.EntryAudit.StoreEditValues == StoreEditValuesMode.None
            ? null
            : _foreignHandlers.HasForeignValueHandlers;
        dto.OffPeriods = await ReadOffPeriodsAsync(options, cancellationToken);
        return Ok(dto);
    }

    /// <summary>
    /// Turns <c>ConfigChanged</c> rows into the spans during which entry auditing was switched off (E17), oldest first.
    /// A span opens when <c>Enabled</c> becomes false (including a first recorded value of false, since auditing was
    /// then off from the start) and closes when it becomes true; an unclosed span has no end. Rows without a readable
    /// <c>Enabled</c> change are skipped, so a malformed detail cannot hide the others.
    /// </summary>
    internal static IReadOnlyList<OffPeriodDto> ComputeOffPeriods(IEnumerable<FormAccessEntry> configChangedRows) =>
        ComputeOffPeriods(configChangedRows, enabledNow: true, windowStartUtc: null);

    /// <summary>
    /// As above, plus the case the stored rows cannot show: auditing is off now, no open span was found, and the row that
    /// switched it off is older than the retention window. A span starting at <paramref name="windowStartUtc"/> is then
    /// added and flagged <c>StartUnknown</c>, so a long switch-off is never reported as "never off".
    /// </summary>
    internal static IReadOnlyList<OffPeriodDto> ComputeOffPeriods(IEnumerable<FormAccessEntry> configChangedRows, bool enabledNow, DateTime? windowStartUtc)
    {
        var periods = new List<OffPeriodDto>();
        DateTime? openFrom = null;
        DateTime? lastOnRow = null;
        bool sawEnabledChange = false;
        foreach (FormAccessEntry row in configChangedRows.OrderBy(r => r.Timestamp).ThenBy(r => r.Id))
        {
            string? newValue = ReadEnabledChange(row.Detail);
            if (newValue is null)
            {
                continue;
            }

            sawEnabledChange = true;
            lastOnRow = newValue == "true" ? row.Timestamp : null;

            if (newValue == "false" && openFrom is null)
            {
                openFrom = row.Timestamp;
            }
            else if (newValue == "true" && openFrom is { } from)
            {
                periods.Add(new OffPeriodDto(from, row.Timestamp));
                openFrom = null;
            }
        }

        if (openFrom is { } stillOff)
        {
            periods.Add(new OffPeriodDto(stillOff, null));
        }
        else if (!enabledNow)
        {
            // Off now but no span left open by the rows. With no Enabled row in the window the switch-off is older than
            // the window, so its start is unknown. Otherwise the newest Enabled row says on while the state says off (a
            // queued or lost write): the span can start no earlier than that row, which closed any earlier span, so it
            // opens there and is flagged unknown. It never starts at a row that already ended a period.
            if (lastOnRow is { } disagreeing)
            {
                periods.Add(new OffPeriodDto(disagreeing, null, StartUnknown: true));
            }
            else if (!sawEnabledChange && windowStartUtc is { } windowStart)
            {
                periods.Add(new OffPeriodDto(windowStart, null, StartUnknown: true));
            }
        }

        return periods;
    }

    private static string? ReadEnabledChange(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(detail);
            if (!document.RootElement.TryGetProperty("config", out JsonElement config) || config.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // Keys are the option property names; matched case-insensitively so a future casing change cannot blind this.
            foreach (JsonProperty property in config.EnumerateObject())
            {
                if (string.Equals(property.Name, nameof(EntryAuditOptions.Enabled), StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Object
                    && property.Value.TryGetProperty("new", out JsonElement newValue)
                    && newValue.ValueKind == JsonValueKind.String)
                {
                    return newValue.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Null (not checked) rather than an empty list when the lookup fails: an empty list would tell the dashboard that
    /// auditing was never switched off, which is the claim this exists to prove.
    /// </summary>
    private async Task<List<OffPeriodDto>?> ReadOffPeriodsAsync(FormsAuditTrailOptions options, CancellationToken cancellationToken)
    {
        try
        {
            IQueryable<FormAccessEntry> rows = _db.AccessEntries.AsNoTracking()
                .Where(e => e.EventType == (int)AccessEventType.ConfigChanged);
            DateTime? windowStart = null;
            if (options.EntryAudit.RetentionDays > 0)
            {
                DateTime cutoff = _time.GetUtcNow().UtcDateTime.AddDays(-options.EntryAudit.RetentionDays);
                windowStart = cutoff;
                rows = rows.Where(e => e.Timestamp >= cutoff);
            }

            List<FormAccessEntry> configRows = await rows.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).ToListAsync(cancellationToken);
            return ComputeOffPeriods(configRows, options.EntryAudit.Enabled, windowStart).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Could not read the entry audit off periods ({ExceptionType}).", ex.GetType().Name);
            return null;
        }
    }

    [HttpGet("entries")]
    public async Task<ActionResult<PagedResultDto<AuditEntryListDto>>> GetEntries(
        [FromQuery] Guid? formId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? userKey = null,
        [FromQuery] int? eventType = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        if (formId.HasValue && !_formsSecurity.HasAccessToForm(formId.Value))
        {
            return Forbid();
        }

        IQueryable<FormAuditEntry> query = ApplyFilters(_db.AuditEntries, formId, userKey, eventType, fromDate, toDate);
        if (!formId.HasValue)
        {
            query = await RestrictToAccessibleFormsAsync(query, cancellationToken);
        }

        var total = await query.CountAsync(cancellationToken);
        List<AuditEntryListDto> items = await ProjectToListDtos(
            query.Skip((page - 1) * pageSize).Take(pageSize),
            cancellationToken);

        return Ok(new PagedResultDto<AuditEntryListDto>
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
        });
    }

    [HttpGet("entries/{id:int}")]
    public async Task<ActionResult<AuditEntryDetailDto>> GetEntry(int id, CancellationToken cancellationToken)
    {
        FormAuditEntry? entry = await _db.AuditEntries
            .AsNoTracking()
            .Include(e => e.Changes)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

        // NotFound (not Forbid) for inaccessible forms, so entry ids can't be enumerated
        if (entry is null || !_formsSecurity.HasAccessToForm(entry.FormId))
        {
            return NotFound();
        }

        return Ok(MapToDetail(entry));
    }

    [HttpGet("forms")]
    public async Task<ActionResult<List<FormSummaryDto>>> GetForms(CancellationToken cancellationToken)
    {
        List<FormSummaryDto> forms = await _db.AuditEntries
            .GroupBy(e => new { e.FormId, e.FormName })
            .Select(g => new FormSummaryDto
            {
                FormId = g.Key.FormId,
                FormName = g.Key.FormName,
            })
            .OrderBy(f => f.FormName)
            .ToListAsync(cancellationToken);

        return Ok(forms.Where(f => _formsSecurity.HasAccessToForm(f.FormId)).ToList());
    }

    [HttpGet("users")]
    public async Task<ActionResult<List<UserSummaryDto>>> GetUsers(CancellationToken cancellationToken)
    {
        IQueryable<FormAuditEntry> query = await RestrictToAccessibleFormsAsync(_db.AuditEntries, cancellationToken);

        List<UserSummaryDto> users = await query
            .GroupBy(e => new { e.UserKey, e.UserName })
            .Select(g => new UserSummaryDto
            {
                UserKey = g.Key.UserKey,
                UserName = g.Key.UserName,
            })
            .OrderBy(u => u.UserName)
            .ToListAsync(cancellationToken);

        return Ok(users);
    }

    [HttpGet("entries/export")]
    public async Task<IActionResult> ExportCsv(
        [FromQuery] Guid? formId,
        [FromQuery] Guid? userKey,
        [FromQuery] int? eventType,
        [FromQuery] DateTime? fromDate,
        [FromQuery] DateTime? toDate,
        CancellationToken cancellationToken = default)
    {
        // 404 rather than 403: when export is switched off the endpoint effectively doesn't exist
        if (!_options.CurrentValue.EnableCsvExport)
        {
            return NotFound();
        }

        if (formId.HasValue && !_formsSecurity.HasAccessToForm(formId.Value))
        {
            return Forbid();
        }

        IQueryable<FormAuditEntry> query = ApplyFilters(_db.AuditEntries, formId, userKey, eventType, fromDate, toDate);
        if (!formId.HasValue)
        {
            query = await RestrictToAccessibleFormsAsync(query, cancellationToken);
        }

        List<AuditEntryListDto> items = await ProjectToListDtos(query, cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("Id,Timestamp,FormId,FormName,UserName,Event,Summary");
        foreach (AuditEntryListDto item in items)
        {
            csv.AppendLine(
                $"{item.Id},{item.Timestamp:O},{item.FormId},{CsvFormatter.Escape(item.FormName)},{CsvFormatter.Escape(item.UserName)},{item.EventType},{CsvFormatter.Escape(item.ChangeSummary)}");
        }

        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"forms-audit-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    /// <summary>
    /// Umbraco Forms supports restricting users to specific forms; the audit trail honours that by
    /// only exposing entries for forms the current user can access. The distinct form id list is
    /// small (one per audited form), so checking each id in memory is cheap.
    /// </summary>
    private async Task<IQueryable<FormAuditEntry>> RestrictToAccessibleFormsAsync(
        IQueryable<FormAuditEntry> query,
        CancellationToken cancellationToken)
    {
        List<Guid> allFormIds = await _db.AuditEntries
            .Select(e => e.FormId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var accessible = allFormIds.Where(_formsSecurity.HasAccessToForm).ToList();

        // Skip the WHERE ... IN clause when nothing is restricted
        return accessible.Count == allFormIds.Count
            ? query
            : query.Where(e => accessible.Contains(e.FormId));
    }

    private static IQueryable<FormAuditEntry> ApplyFilters(
        IQueryable<FormAuditEntry> query,
        Guid? formId,
        Guid? userKey,
        int? eventType,
        DateTime? fromDate,
        DateTime? toDate)
    {
        if (formId.HasValue)
        {
            query = query.Where(e => e.FormId == formId.Value);
        }

        if (userKey.HasValue)
        {
            query = query.Where(e => e.UserKey == userKey.Value);
        }

        if (eventType.HasValue)
        {
            query = query.Where(e => e.EventType == eventType.Value);
        }

        if (fromDate.HasValue)
        {
            query = query.Where(e => e.Timestamp >= fromDate.Value);
        }

        if (toDate.HasValue)
        {
            // A date-only upper bound means "up to and including that day"
            DateTime to = toDate.Value.TimeOfDay == TimeSpan.Zero ? toDate.Value.AddDays(1) : toDate.Value;
            query = query.Where(e => e.Timestamp < to);
        }

        return query;
    }

    private static async Task<List<AuditEntryListDto>> ProjectToListDtos(
        IQueryable<FormAuditEntry> query,
        CancellationToken cancellationToken)
    {
        // Project server-side to avoid pulling the (large) snapshot columns,
        // then finish the mapping in memory, because enum names and JSON parsing don't translate to SQL.
        var rows = await query
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Select(e => new { e.Id, e.FormId, e.FormName, e.UserName, e.UserKey, e.Timestamp, e.EventType, e.ChangeSummaryJson })
            .ToListAsync(cancellationToken);

        return rows
            .Select(e => new AuditEntryListDto
            {
                Id = e.Id,
                FormId = e.FormId,
                FormName = e.FormName,
                UserName = e.UserName,
                UserKey = e.UserKey,
                Timestamp = e.Timestamp,
                EventType = ((AuditEventType)e.EventType).ToString(),
                ChangeSummary = GetSummaryFromJson(e.ChangeSummaryJson),
            })
            .ToList();
    }

    private static AuditEntryDetailDto MapToDetail(FormAuditEntry entry) => new()
    {
        Id = entry.Id,
        FormId = entry.FormId,
        FormName = entry.FormName,
        UserName = entry.UserName,
        UserKey = entry.UserKey,
        Timestamp = entry.Timestamp,
        EventType = ((AuditEventType)entry.EventType).ToString(),
        ChangeSummary = GetSummaryFromJson(entry.ChangeSummaryJson),
        BeforeSnapshot = entry.BeforeSnapshot,
        AfterSnapshot = entry.AfterSnapshot,
        Changes = entry.Changes.Select(c => new AuditChangeDto
        {
            ChangeType = ((ChangeType)c.ChangeType).ToString(),
            PropertyPath = c.PropertyPath,
            FriendlyDescription = c.FriendlyDescription,
            OldValue = c.OldValue,
            NewValue = c.NewValue,
            Category = c.Category,
        }).ToList(),
    };

    private static string? GetSummaryFromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("Summary", out JsonElement s) ? s.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
