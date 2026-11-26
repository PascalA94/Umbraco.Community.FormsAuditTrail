using Microsoft.AspNetCore.Authorization;
using OpenIddict.Validation.AspNetCore;

namespace Umbraco.Community.FormsAuditTrail.Authorization;

/// <summary>Policy name for reading the access log, on top of the Forms section policy. The rules live in <see cref="AccessLogAuthorizationHandler"/>.</summary>
internal static class AccessLogPolicy
{
    public const string Name = "FormsAuditTrail.AccessLog";

#if UMBRACO_17
    // The literal Forms 17 uses in SectionAccessForms; Umbraco's constant for it is obsolete from 17 and removed in 18.
    private const string AllowedApplicationClaimType = "http://umbraco.org/2015/02/identity/claims/backoffice/allowedapp";
#endif

    /// <summary>
    /// Builds the policy. It stands alone: Forms' SectionAccessForms is not inherited, so the scheme, the
    /// authenticated-user check and the Forms section requirement are repeated here. "forms" is the section alias that
    /// policy checks.
    /// </summary>
    /// <remarks>
    /// The scheme is the Management API's token scheme, the one Forms' SectionAccessForms and Umbraco's own policies name
    /// (Forms 17.6 and 18.2, Umbraco 17.8 and 18.3), so a denied caller gets 403 from its forbid. The section check differs
    /// by line: Forms 18 uses a section requirement and handler, while Forms 17's policy is the scheme plus an
    /// <c>allowedapp</c> claim of "forms" (the handler does not exist on 17), so each line repeats its own Forms check.
    /// The backoffice cookie scheme must not be added: its forbid redirects to the login page, and because the
    /// authorisation middleware forbids with every scheme the combined endpoint policy names, one cookie entry turns the
    /// 403 into a 302.
    /// </remarks>
    internal static void Configure(AuthorizationPolicyBuilder policy) => policy
        .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
#if UMBRACO_17
        .RequireClaim(AllowedApplicationClaimType, "forms")
        .AddRequirements(new AccessLogRequirement());
#else
        .AddRequirements(new global::Umbraco.Forms.Web.Authorization.SectionAccess.SectionAccessRequirement("forms"), new AccessLogRequirement());
#endif
}
