using Microsoft.EntityFrameworkCore;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.Models;
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
    /// <remarks>
    /// Kept apart from <see cref="TryGetCurrentUser"/> so definition-audit attribution never changes: a user with no
    /// display name is still recorded as before (a null name gives "System", an empty one is kept as it is).
    /// </remarks>
    public static (Guid Key, string Name) GetCurrentUser(IBackOfficeSecurityAccessor accessor)
    {
        IUser? user = accessor.BackOfficeSecurity?.CurrentUser;
        return user != null ? (user.Key, user.Name ?? AuditUserNames.System) : (Guid.Empty, AuditUserNames.System);
    }

    /// <summary>
    /// The current backoffice user, resolved per call and never cached (P2). Returns false when no user is signed in, so
    /// a caller decides what "no user" means from this flag, never by comparing names: a real user can be called
    /// "System" (P19, SEC-L5).
    /// </summary>
    /// <remarks>
    /// Adapted from UserActivity <c>Identity/UserIdentityResolver.cs</c>: a found user keeps its own name and a missing
    /// one never borrows another identity's. For access rows a user with no display name falls back to the username, so
    /// the row still names someone; "System" is the last resort, and the key, not the name, is the evidence.
    /// </remarks>
    public static bool TryGetCurrentUser(IBackOfficeSecurityAccessor accessor, out Guid key, out string name)
    {
        IUser? user = accessor.BackOfficeSecurity?.CurrentUser;
        if (user is null)
        {
            key = Guid.Empty;
            name = string.Empty;
            return false;
        }

        key = user.Key;
        name = !string.IsNullOrEmpty(user.Name) ? user.Name : !string.IsNullOrEmpty(user.Username) ? user.Username : AuditUserNames.System;
        return true;
    }

    /// <summary>
    /// For failure log lines only: the user may be the very thing that failed, and the log line must still be written,
    /// so any exception gives <see cref="Guid.Empty"/> rather than escaping (E12, SEC-02).
    /// </summary>
    public static Guid CurrentUserKeyOrEmpty(IBackOfficeSecurityAccessor accessor)
    {
        try
        {
            return TryGetCurrentUser(accessor, out Guid key, out _) ? key : Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
        }
    }
}
