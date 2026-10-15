using Microsoft.EntityFrameworkCore;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.Persistence;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

internal static class AuditHandlerHelpers
{
    /// <summary>
    /// The AfterSnapshot of the form's most recent audit entry, or null when none exists.
    /// It reflects the last known state of the form including workflows, which Forms
    /// persists before the saving or deleting notification fires.
    /// </summary>
    public static Task<string?> GetLastAfterSnapshotAsync(
        this FormsAuditDbContext db, Guid formId, CancellationToken cancellationToken) =>
        db.AuditEntries
            .Where(e => e.FormId == formId && e.AfterSnapshot != null)
            .OrderByDescending(e => e.Id)
            .Select(e => e.AfterSnapshot)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// The current backoffice user, or "System" with an empty key when there is none
    /// (an Umbraco Deploy transfer, a code-based change, etc.).
    /// </summary>
    public static (Guid Key, string Name) GetCurrentUser(IBackOfficeSecurityAccessor accessor)
    {
        IUser? user = accessor.BackOfficeSecurity?.CurrentUser;
        return user != null ? (user.Key, user.Name ?? "System") : (Guid.Empty, "System");
    }
}
