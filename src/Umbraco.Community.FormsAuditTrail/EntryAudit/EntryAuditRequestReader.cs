using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Umbraco.Community.FormsAuditTrail.Models;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// What the capture core needs from one Forms request, independent of how it was observed. The action filter builds it
/// from <c>ActionExecutingContext</c>; a middleware adapter could build it from the endpoint and request, which is why
/// it holds plain route values and query rather than MVC types.
/// </summary>
internal sealed record CaptureRequest(
    Type ControllerType,
    IReadOnlyDictionary<string, object?> RouteValues,
    IQueryCollection Query,
    IReadOnlyDictionary<string, object?> Arguments);

/// <summary>
/// Reads the form id, record id and filter context from a Forms Management API request, using the parameter sources
/// spike S1 recorded on Forms 18.2.2 (docs/spikes.md S1). Returns the raw detail: the capture core hashes it for dedupe
/// before shaping it for storage (R33), so a search term never leaves memory unless capture is switched on.
/// </summary>
internal static class EntryAuditRequestReader
{
    public static (Guid? FormId, Guid? RecordId, EntryAuditDetail Detail) Read(CaptureRequest request, AccessEventType kind)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Most controllers carry formId in the route; the export pair carry it in the query, and ExportController binds
        // it as a string, so every source is parsed rather than cast.
        Guid? formId = GuidFrom(request.RouteValues, "formId") ?? GuidFrom(request.Query, "formId") ?? GuidFrom(request.Arguments, "formId");
        Guid? recordId = GuidFrom(request.RouteValues, "recordId");

        EntryAuditDetail detail = kind switch
        {
            AccessEventType.ListViewed => ListDetail(request.Query),
            AccessEventType.Exported => ListDetail(request.Query) with { ExportType = Text(request.Query, "exportType") },
            AccessEventType.ExportDownloaded => new EntryAuditDetail { FileName = Text(request.Query, "fileName") },
            AccessEventType.WorkflowRetried => new EntryAuditDetail { WorkflowId = GuidFrom(request.RouteValues, "workflowId") },
            AccessEventType.MemberSummaryViewed => new EntryAuditDetail { MemberKey = request.RouteValues.TryGetValue("memberKey", out object? member) ? member?.ToString() : null },
            _ => new EntryAuditDetail(),
        };

        return (formId, recordId, detail);
    }

    /// <summary>
    /// The <c>RecordFilter</c> query keys the backoffice sends (S1). The list and the export share the shape. A page
    /// number is derived from skip and take because that is what a reader of the log means by "page".
    /// </summary>
    private static EntryAuditDetail ListDetail(IQueryCollection query)
    {
        string? term = Text(query, "filter");
        StringValues states = query.TryGetValue("states", out StringValues s) ? s : StringValues.Empty;
        var filter = new EntryAuditFilter(
            From: Date(query, "startDate"),
            To: Date(query, "endDate"),
            State: states.Count == 0 ? null : string.Join(",", states.Where(v => !string.IsNullOrEmpty(v))),
            SortBy: Text(query, "sortBy"),
            SortDirection: Text(query, "sortOrder"),
            SearchTermUsed: string.IsNullOrWhiteSpace(term) ? null : true,
            SearchTerm: string.IsNullOrWhiteSpace(term) ? null : term);

        int? skip = Int(query, "skip");
        int? take = Int(query, "take");
        bool hasFilter = filter != new EntryAuditFilter(null, null, null, null, null, null, null);

        return new EntryAuditDetail
        {
            Filter = hasFilter ? filter : null,
            PageSize = take,
            Page = take is > 0 ? (skip ?? 0) / take.Value + 1 : null,
        };
    }

    private static Guid? GuidFrom(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out object? value) ? AsGuid(value) : null;

    private static Guid? GuidFrom(IQueryCollection query, string key) => AsGuid(Text(query, key));

    private static Guid? AsGuid(object? value) => value switch
    {
        Guid g => g,
        string text when Guid.TryParse(text, out Guid parsed) => parsed,
        _ => null,
    };

    private static string? Text(IQueryCollection query, string key) =>
        query.TryGetValue(key, out StringValues values) && !StringValues.IsNullOrEmpty(values) ? values[0] : null;

    private static int? Int(IQueryCollection query, string key) =>
        int.TryParse(Text(query, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    /// <summary>Forms binds an absent date as <see cref="DateTime.MinValue"/> (S1), so that and anything unparseable count as unset.</summary>
    private static DateTime? Date(IQueryCollection query, string key)
    {
        if (!DateTime.TryParse(Text(query, key), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime value))
        {
            return null;
        }

        return value == DateTime.MinValue ? null : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
