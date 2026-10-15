using Microsoft.EntityFrameworkCore;

namespace Umbraco.Community.FormsAuditTrail.Persistence;

internal static class FormsAuditDbContextExtensions
{
    /// <summary>
    /// Returns the AfterSnapshot of the form's most recent audit entry that has one, or null when the
    /// form has no such entry. This is the last-known state of the form, including its workflows.
    /// </summary>
    internal static Task<string?> GetLastAfterSnapshotAsync(
        this FormsAuditDbContext db,
        Guid formId,
        CancellationToken cancellationToken) =>
        db.AuditEntries
            .Where(e => e.FormId == formId && e.AfterSnapshot != null)
            .OrderByDescending(e => e.Id)
            .Select(e => e.AfterSnapshot)
            .FirstOrDefaultAsync(cancellationToken);
}
