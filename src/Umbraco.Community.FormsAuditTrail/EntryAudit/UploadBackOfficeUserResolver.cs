using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>The backoffice user behind an upload request.</summary>
public sealed record UploadUser(Guid Key, string Name, bool SessionExpired = false);

/// <summary>
/// Resolves the user for a request at <c>PrePipeline</c>, where <c>HttpContext.User</c> is not authenticated yet (S11).
/// It never throws: a missing, malformed or expired cookie is an unresolved user, and the response is never touched.
/// </summary>
public interface IUploadUserResolver
{
    UploadUser? Resolve(HttpContext context);
}

/// <summary>
/// Reads the <c>UmbracoBackOffice</c> scheme's cookie and unprotects it with the scheme's own <c>TicketDataFormat</c>,
/// exactly as <c>ProtectFormUploadRequestsMiddleware</c> does (S11), then loads the user by username as Forms does.
/// </summary>
/// <remarks>
/// Singleton. <c>IUserService</c> is taken from the request services at call time rather than injected, so this never
/// holds a service whose lifetime it does not control (P2). The ticket's expiry is reported, not enforced, because the
/// data format only decrypts and the audit records what happened rather than deciding access.
/// </remarks>
public sealed class UploadBackOfficeUserResolver(
    IOptionsMonitor<CookieAuthenticationOptions> cookieOptions,
    TimeProvider time) : IUploadUserResolver
{
    public UploadUser? Resolve(HttpContext context)
    {
        try
        {
            CookieAuthenticationOptions options = cookieOptions.Get(Constants.Security.BackOfficeAuthenticationType);
            string? cookieName = options.Cookie.Name;
            if (string.IsNullOrEmpty(cookieName) || options.TicketDataFormat is null)
            {
                return null;
            }

            // The cookie manager rather than Request.Cookies, so a chunked cookie is reassembled as the handler would.
            string? raw = options.CookieManager?.GetRequestCookie(context, cookieName) ?? context.Request.Cookies[cookieName];
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            var ticket = options.TicketDataFormat.Unprotect(raw);
            if (ticket is null)
            {
                return null;
            }

            // The ticket decrypted, so it is genuine; an expired one still says who it was issued to. Forms served the
            // file, so the row keeps the identity and carries the expiry as evidence. A null expiry is not expired.
            bool expired = ticket.Properties.ExpiresUtc is { } expires && expires <= time.GetUtcNow();

            string? username = ticket.Principal.FindFirstValue(ClaimTypes.Name);
            if (string.IsNullOrEmpty(username))
            {
                return null;
            }

            var users = context.RequestServices.GetRequiredService<IUserService>();
            var user = users.GetByUsername(username);
            if (user is null)
            {
                return null;
            }

            string name = !string.IsNullOrEmpty(user.Name) ? user.Name : user.Username;
            return new UploadUser(user.Key, name, expired);
        }
        catch
        {
            // Unprotect throws on a tampered or foreign cookie; nothing here may reach the response.
            return null;
        }
    }
}
