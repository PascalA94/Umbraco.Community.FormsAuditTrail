using Umbraco.Community.FormsAuditTrail.Models;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// What one filtered Forms request has learned so far. Opened by the action filter, filled by notification handlers
/// while Forms runs, read by the filter when the action completes and handed to the writer.
/// </summary>
internal sealed class CaptureWindow
{
    public required AccessEventType Kind { get; init; }
    public Guid? FormId { get; set; }
    public Guid? RecordId { get; set; }
    public required EntryAuditDetail Detail { get; set; }

    /// <summary>Distinct ids of the records Forms fetched. Always kept; the filter decides at write time whether to store them (D7).</summary>
    public List<Guid> RecordIds { get; } = [];

    /// <summary>Every row Forms fetched, including a repeat fetch of one record, so the count reflects what was read.</summary>
    public int FetchedRowCount { get; set; }

    public List<EntryFieldChange> Changes { get; } = [];
    public List<(Guid FormId, Guid RecordId)> DeletedRecords { get; } = [];

    /// <summary>SEC-04: the stored values before an edit, read through Forms' own path by the filter before <c>next()</c>.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<object?>>? BeforeValues { get; set; }
}
