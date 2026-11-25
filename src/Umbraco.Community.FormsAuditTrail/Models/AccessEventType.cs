namespace Umbraco.Community.FormsAuditTrail.Models;

/// <summary>
/// Kinds of entry access and administrative events. Separate from <see cref="AuditEventType"/> so the definition
/// audit's 0 to 2 are untouched. Values are stored in the database: never reuse or renumber one once shipped.
/// </summary>
public enum AccessEventType
{
    ListViewed = 10,
    DetailViewed = 11,
    Edited = 12,
    Deleted = 13,
    Exported = 14,
    ExportDownloaded = 15,
    AuditExported = 16,
    AuditViewed = 17,
    FileViewed = 18,
    AuditSearched = 19,
    WorkflowRetried = 20,
    Created = 21,
    MemberSummaryViewed = 22,
    ConfigChanged = 23,
    AuditGap = 24,
}
