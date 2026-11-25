namespace Umbraco.Community.FormsAuditTrail.Models;

/// <summary>Small key-value store that survives restarts and is shared across load-balanced servers.</summary>
public class AuditStateEntry
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
