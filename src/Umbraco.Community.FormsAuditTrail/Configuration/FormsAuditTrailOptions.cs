namespace Umbraco.Community.FormsAuditTrail.Configuration;

/// <summary>
/// Bound from the "FormsAuditTrail" configuration section:
/// <code>
/// { "FormsAuditTrail": { "RetentionDays": 365, "EnableCsvExport": false } }
/// </code>
/// </summary>
public class FormsAuditTrailOptions
{
    public const string SectionName = "FormsAuditTrail";

    /// <summary>
    /// Number of days to keep audit entries. Entries older than this are deleted by a daily
    /// background job. Zero (the default) keeps entries forever.
    /// </summary>
    public int RetentionDays { get; set; }

    /// <summary>
    /// Allows the CSV export endpoint and dashboard button to be switched off entirely
    /// for hosts that don't want audit data leaving the backoffice. Enabled by default.
    /// </summary>
    public bool EnableCsvExport { get; set; } = true;
}
