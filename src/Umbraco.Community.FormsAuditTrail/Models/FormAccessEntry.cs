namespace Umbraco.Community.FormsAuditTrail.Models;

/// <summary>One access to form entries (a view, export, edit or delete) or an administrative event.</summary>
public class FormAccessEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public int EventType { get; set; }
    public Guid FormId { get; set; }
    public string FormName { get; set; } = string.Empty;
    public Guid? RecordId { get; set; }
    public int RecordCount { get; set; }
    public Guid UserKey { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public Guid? CorrelationId { get; set; }

    public ICollection<FormAccessRecord> Records { get; set; } = new List<FormAccessRecord>();
    public ICollection<FormEntryChange> Changes { get; set; } = new List<FormEntryChange>();
}
