using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Forms' own "view sensitive data" rule: CMS <c>UserExtensions.HasAccessToSensitiveData</c>, membership of the built-in
/// Sensitive data group (spike S4). Every failure, including no resolvable user, answers false (SEC-12).
/// </summary>
/// <remarks>
/// The user comes from <see cref="IBackOfficeSecurityAccessor"/>, the source Forms' own masking call sites use, so the
/// log never shows more than Forms would. It is read per call and never held (P2). Scoped, as the brief registers it.
/// </remarks>
public sealed class SensitiveDataPermission(IBackOfficeSecurityAccessor securityAccessor, ILogger<SensitiveDataPermission> logger)
    : ISensitiveDataPermission
{
    public bool CurrentUserCanViewSensitiveData()
    {
        try
        {
            IUser? user = securityAccessor.BackOfficeSecurity?.CurrentUser;
            return user is not null && user.HasAccessToSensitiveData();
        }
        catch (Exception ex)
        {
            // The type only: the message or stack could carry details of the user being resolved.
            logger.LogWarning("FormsAuditTrail could not check the sensitive data permission ({ExceptionType}); values stay masked.", ex.GetType().Name);
            return false;
        }
    }
}
