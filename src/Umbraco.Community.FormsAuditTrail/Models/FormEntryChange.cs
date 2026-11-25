namespace Umbraco.Community.FormsAuditTrail.Models;

/// <summary>One field-level change from an entry edit.</summary>
public class FormEntryChange
{
    public long Id { get; set; }
    public long AccessId { get; set; }
    public Guid FieldId { get; set; }
    public string FieldCaption { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public bool Masked { get; set; }

    /// <summary>True when the new value is empty or whitespace (SEC-03).</summary>
    public bool Cleared { get; set; }

    public DateTime? ValuesScrubbedAt { get; set; }

    public FormAccessEntry? Access { get; set; }
}
