using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.Models;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

// On v17 the snapshot lookup lives in Persistence/FormsAuditDbContextExtensions (GetLastAfterSnapshotAsync), so this
// class carries only the user resolution the entry audit needs.
internal static class AuditHandlerHelpers
{
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
}
