namespace Umbraco.Community.FormsAuditTrail.Models;

public class FormAuditEntry
{
    public int Id { get; set; }
    public Guid FormId { get; set; }
    public string FormName { get; set; } = string.Empty;
    public Guid UserKey { get; set; }
    public string UserName { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public int EventType { get; set; }
    public string BeforeSnapshot { get; set; } = string.Empty;
    public string? AfterSnapshot { get; set; }
    public string? ChangeSummaryJson { get; set; }

    public ICollection<FormAuditChange> Changes { get; set; } = new List<FormAuditChange>();
}
