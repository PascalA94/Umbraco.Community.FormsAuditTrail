using Microsoft.AspNetCore.Authorization;
using OpenIddict.Validation.AspNetCore;

namespace Umbraco.Community.FormsAuditTrail.Authorization;

/// <summary>Policy name for reading the access log, on top of the Forms section policy. The rules live in <see cref="AccessLogAuthorizationHandler"/>.</summary>
internal static class AccessLogPolicy
{
    public const string Name = "FormsAuditTrail.AccessLog";

    // The literal Forms 17 uses in SectionAccessForms; Umbraco's constant for it is obsolete from 17 and removed in 18.
    private const string AllowedApplicationClaimType = "http://umbraco.org/2015/02/identity/claims/backoffice/allowedapp";

    /// <summary>
    /// Builds the policy. It stands alone: Forms' SectionAccessForms is not inherited, so the scheme, the
    /// authenticated-user check and the Forms section requirement are repeated here.
    /// </summary>
    /// <remarks>
    /// The scheme is the Management API's token scheme, the one Forms' SectionAccessForms and Umbraco's own policies name
    /// (Forms 17.6, Umbraco 17.8), so a denied caller gets 403 from its forbid. On Forms 17 that policy is the OpenIddict
    /// scheme plus an <c>allowedapp</c> claim of "forms" (Forms 18 moved the check into a section handler, which does not
    /// exist on 17), so the same claim is required here. The backoffice cookie scheme must not be added: its forbid redirects to the login page, and because the authorisation middleware forbids with every scheme the
    /// combined endpoint policy names, one cookie entry turns the 403 into a 302.
    /// </remarks>
    internal static void Configure(AuthorizationPolicyBuilder policy) => policy
        .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireClaim(AllowedApplicationClaimType, "forms")
        .AddRequirements(new AccessLogRequirement());
}
