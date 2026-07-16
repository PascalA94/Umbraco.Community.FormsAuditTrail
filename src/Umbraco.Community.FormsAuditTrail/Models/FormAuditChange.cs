namespace Umbraco.Community.FormsAuditTrail.Models;

public class FormAuditChange
{
    public int Id { get; set; }
    public int AuditEntryId { get; set; }
    public int ChangeType { get; set; }
    public string PropertyPath { get; set; } = string.Empty;
    public string FriendlyDescription { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Category { get; set; }

    public FormAuditEntry? AuditEntry { get; set; }
}
