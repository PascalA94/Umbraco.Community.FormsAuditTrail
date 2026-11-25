namespace Umbraco.Community.FormsAuditTrail.Models;

/// <summary>
/// The stored names for rows with no real user. They differ on purpose (SEC-L5): System is a job, a code change or a
/// Deploy transfer, while Unknown is a backoffice request whose user could not be resolved, which is evidence in itself.
/// The values are stored in existing rows, so they must not change.
/// </summary>
internal static class AuditUserNames
{
    public const string System = "System";
    public const string Unknown = "Unknown";
}
