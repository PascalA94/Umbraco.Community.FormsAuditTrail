using System.Net;
using System.Security.Claims;
using System.Security.Principal;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Security.Authorization;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.Authorization;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Controllers;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Security;
using Umbraco.Forms.Core.Services;
#if !UMBRACO_17
using Umbraco.Forms.Web.Authorization.SectionAccess;
#endif
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// Merge-fix item 21 (ruling R75): a caller the access-log policy denies must get 403, as Forms' and Umbraco's
/// Management API endpoints answer, not a 302 to the login page. The host registers both backoffice schemes under their
/// real names: the token scheme Forms and the Management API authenticate with (here a stand-in that authenticates from
/// a header and forbids with 403, as OpenIddict validation does) and the backoffice cookie, whose forbid redirects. The
/// controller sits behind Forms' SectionAccessForms as each Forms line builds it (18: scheme and Forms' real section
/// handler; 17: scheme and the allowedapp claim) and the
/// package's real access-log policy, so a redirect here means the policy named a scheme whose forbid redirects.
/// </summary>
public sealed class AccessLogPolicyForbidTests : IAsyncLifetime
{
    // OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme, as Forms and Umbraco add it to their policies.
    private const string TokenScheme = "OpenIddict.Validation.AspNetCore";
    private const string UserHeader = "X-Test-User";
    // The claim Forms 17's SectionAccessForms requires; Umbraco puts one on the token per allowed section. Issued on both
    // lines: Forms 18's section handler ignores it.
    private const string AllowedAppClaim = "http://umbraco.org/2015/02/identity/claims/backoffice/allowedapp";
    private const string LoginPath = "/umbraco/login";

    private static readonly Guid AdminKey = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EditorKey = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid WriterKey = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static readonly Dictionary<Guid, string[]> SectionsByUser = new()
    {
        [AdminKey] = ["content", "forms"],
        [EditorKey] = ["content", "forms"],
        [WriterKey] = ["content"],
    };

    private readonly TestDatabase _db = new();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAuthorizationHelper _authorizationHelper = Substitute.For<IAuthorizationHelper>();
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Built before Returns: NSubstitute cannot configure one substitute inside another's Returns call.
        var byKey = new Dictionary<Guid, IUser>
        {
            [AdminKey] = User("admin", SectionsByUser[AdminKey]),
            [EditorKey] = User("editor", SectionsByUser[EditorKey]),
            [WriterKey] = User("writer", SectionsByUser[WriterKey]),
        };
        foreach ((Guid key, IUser user) in byKey)
        {
            _users.GetAsync(key).Returns(user);
        }

        _users.GetAsync(Arg.Any<IEnumerable<Guid>>()).Returns(Enumerable.Empty<IUser>());
        _authorizationHelper.TryGetUmbracoUser(Arg.Any<IPrincipal>(), out Arg.Any<IUser?>())
            .Returns(call =>
            {
                string? sub = ((ClaimsPrincipal)call[0]).FindFirstValue("sub");
                IUser? user = Guid.TryParse(sub, out Guid key) && byKey.TryGetValue(key, out IUser? found) ? found : null;
                call[1] = user;
                return user is not null;
            });

        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddControllers().AddApplicationPart(typeof(FormsAuditAccessApiController).Assembly);
                    services.AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, HeaderTokenHandler>(TokenScheme, _ => { })
                        // Umbraco's backoffice cookie sends an unauthorised or forbidden browser request to the login page.
                        .AddCookie(global::Umbraco.Cms.Core.Constants.Security.BackOfficeAuthenticationType, o =>
                        {
                            o.LoginPath = LoginPath;
                            o.AccessDeniedPath = LoginPath;
                        });
                    services.AddAuthorization(o =>
                    {
                        o.AddPolicy("SectionAccessForms", p =>
                        {
                            p.AuthenticationSchemes.Add(TokenScheme);
#if UMBRACO_17
                            p.RequireClaim(AllowedAppClaim, "forms");
#else
                            p.Requirements.Add(new SectionAccessRequirement("forms"));
#endif
                        });
                        o.AddPolicy(AccessLogPolicy.Name, AccessLogPolicy.Configure);
                    });
                    services.AddSingleton(_authorizationHelper);
#if !UMBRACO_17
                    services.AddSingleton<IAuthorizationHandler, SectionAccessHandler>();
#endif

                    var options = new FormsAuditTrailOptions();
                    new FormsAuditTrailOptionsSetup(NullLogger<FormsAuditTrailOptionsSetup>.Instance).PostConfigure(null, options);
                    IOptionsMonitor<FormsAuditTrailOptions> monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
                    monitor.CurrentValue.Returns(options);
                    services.AddSingleton(monitor);
                    services.AddSingleton(_users);
                    services.AddSingleton<IAuthorizationHandler, AccessLogAuthorizationHandler>();

                    services.AddSingleton<FormsAuditDbContext>(_db.Context);
                    services.AddSingleton(Substitute.For<IFormsSecurity>());
                    services.AddSingleton(Substitute.For<IFormService>());
                    services.AddSingleton(Substitute.For<IRecordStorage>());
                    services.AddSingleton(Substitute.For<IFormFieldInfoProvider>());
                    services.AddSingleton(Substitute.For<ISensitiveDataPermission>());
                    services.AddSingleton(Substitute.For<IEntryAuditWriter>());
                    services.AddSingleton(Substitute.For<IBackOfficeSecurityAccessor>());
                    services.AddSingleton<IUploadPathResolver, UploadPathResolver>();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(e => e.MapControllers());
                }))
            .StartAsync();
        // The test client never follows redirects, so a 302 to the login page surfaces as itself.
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _db.Dispose();
    }

    private static IUser User(string groupAlias, string[] sections)
    {
        IReadOnlyUserGroup group = Substitute.For<IReadOnlyUserGroup>();
        group.Alias.Returns(groupAlias);
        IUser user = Substitute.For<IUser>();
        user.IsApproved.Returns(true);
        user.IsLockedOut.Returns(false);
        user.Groups.Returns([group]);
        user.AllowedSections.Returns(sections);
        return user;
    }

    private Task<HttpResponseMessage> Get(string path, Guid userKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(UserHeader, userKey.ToString());
        return _client.SendAsync(request);
    }

    public static TheoryData<string> AccessRoutes() => new()
    {
        "/umbraco/formsaudit/api/v1/access",
        "/umbraco/formsaudit/api/v1/access/export",
        "/umbraco/formsaudit/api/v1/access/1",
        "/umbraco/formsaudit/api/v1/access/records/44444444-4444-4444-4444-444444444444",
    };

    [Theory]
    [MemberData(nameof(AccessRoutes))]
    public async Task AccessRoutes_FormsUserOutsideTheAccessLogGroups_Gets403NotALoginRedirect(string path) // merge-fix 21, R75
    {
        using HttpResponseMessage response = await Get(path, EditorKey);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [MemberData(nameof(AccessRoutes))]
    public async Task AccessRoutes_UserWithoutTheFormsSection_Gets403NotALoginRedirect(string path) // merge-fix 21, R75
    {
        using HttpResponseMessage response = await Get(path, WriterKey);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [MemberData(nameof(AccessRoutes))]
    public async Task AccessRoutes_Unauthenticated_Gets401NotALoginRedirect(string path)
    {
        // No test-user header: the token scheme finds no credentials, so the challenge answers 401.
        using HttpResponseMessage response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task AccessList_AccessLogReader_Gets200() // merge-fix 21, R75
    {
        using HttpResponseMessage response = await Get("/umbraco/formsaudit/api/v1/access", AdminKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Stands in for OpenIddict validation: authenticates a request carrying the header as that user ("sub"), and keeps
    /// the base handler's challenge (401) and forbid (403), which is how OpenIddict answers an API call.
    /// </summary>
    private sealed class HeaderTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var value))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new("sub", value.ToString()) };
            if (Guid.TryParse(value.ToString(), out Guid key) && SectionsByUser.TryGetValue(key, out string[]? sections))
            {
                claims.AddRange(sections.Select(section => new Claim(AllowedAppClaim, section)));
            }

            var identity = new ClaimsIdentity(claims, TokenScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TokenScheme)));
        }
    }
}
