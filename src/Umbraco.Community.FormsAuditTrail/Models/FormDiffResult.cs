namespace Umbraco.Community.FormsAuditTrail.Models;

public class FormDiffResult
{
    public List<FormChange> Changes { get; set; } = new();
    public string Summary { get; set; } = string.Empty;
}

public class FormChange
{
    public ChangeType ChangeType { get; set; }
    public string PropertyPath { get; set; } = string.Empty;
    public string FriendlyDescription { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string Category { get; set; } = string.Empty;
}
