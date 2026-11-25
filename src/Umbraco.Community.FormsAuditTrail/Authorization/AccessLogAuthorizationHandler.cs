using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Extensions;

namespace Umbraco.Community.FormsAuditTrail.Authorization;

/// <summary>
/// Succeeds when the caller is in a group whose alias is listed in <c>EntryAudit:AccessLogUserGroups</c>, compared
/// ignoring case. Every other outcome (no key claim, an unknown, locked or unapproved user) fails closed (SEC-L5).
/// </summary>
/// <remarks>
/// Registered as a singleton, never scoped: a scoped <see cref="IAuthorizationHandler"/> crashes start-up under scope
/// validation because core's singleton authorisers consume <c>IAuthorizationService</c> (UserActivity spike S6). The
/// user comes from the context's own principal, not <c>IBackOfficeSecurityAccessor</c>, so nothing ambient is held (P2).
/// Adapted from UserActivity <c>Security/UserActivityAuthorizationHandler.cs</c>.
/// </remarks>
internal sealed class AccessLogAuthorizationHandler(
    IUserService userService,
    IOptionsMonitor<FormsAuditTrailOptions> options,
    ILogger<AccessLogAuthorizationHandler> logger)
    : AuthorizationHandler<AccessLogRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AccessLogRequirement requirement)
    {
        // The policy authenticates first, but the handler does not rely on that: a principal that is not authenticated
        // can carry any claim, so "sub" is only read from an authenticated identity.
        if (context.User.Identity is not { IsAuthenticated: true } identity)
        {
            return;
        }

        // The claim is "sub" on both lines (S1); a malformed value is treated as no key rather than thrown on.
        if (!Guid.TryParse(identity.GetUserKey()?.ToString(), out Guid key) || key == Guid.Empty)
        {
            return;
        }

        IUser? user;
        try
        {
            user = await userService.GetAsync(key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail closed, and say why: a 403 with no trace would look like a permissions mistake. Type only, no user data.
            logger.LogWarning("Access-log authorisation failed closed because the user lookup threw {ExceptionType}.", ex.GetType().Name);
            return;
        }

        if (user is null || !user.IsApproved || user.IsLockedOut)
        {
            return;
        }

        // Read per call so a changed configuration applies without a restart.
        var allowed = new HashSet<string>(
            options.CurrentValue.EntryAudit.AccessLogUserGroups.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()),
            StringComparer.OrdinalIgnoreCase);

        if (user.Groups.Any(g => allowed.Contains(g.Alias)))
        {
            context.Succeed(requirement);
        }
    }
}
