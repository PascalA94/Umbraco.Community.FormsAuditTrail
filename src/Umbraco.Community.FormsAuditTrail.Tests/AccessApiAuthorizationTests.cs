using System.Net;
using System.Security.Claims;
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
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.Authorization;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Controllers;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Security;
using Umbraco.Forms.Core.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// P10: the real controller behind the real access-log policy and handler, over HTTP. The Forms section policy is a
/// stand-in that passes any authenticated user, so a 403 here can only come from the access-log policy.
/// </summary>
public sealed class AccessApiAuthorizationTests : IAsyncLifetime
{
    private const string TestScheme = "Test";
    private const string UserHeader = "X-Test-User";
    private const string NoKey = "no-key";
    private const string AccessUrl = "/umbraco/formsaudit/api/v1/access";

    private static readonly Guid AdminKey = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid EditorKey = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly TestDatabase _db = new();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Built before Returns: NSubstitute cannot configure one substitute inside another's Returns call.
        IUser admin = User("admin");
        IUser editor = User("editor");
        _users.GetAsync(AdminKey).Returns(admin);
        _users.GetAsync(EditorKey).Returns(editor);
        _users.GetAsync(Arg.Any<IEnumerable<Guid>>()).Returns(Enumerable.Empty<IUser>());

        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddControllers().AddApplicationPart(typeof(FormsAuditAccessApiController).Assembly);
                    services.AddAuthentication(TestScheme).AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(TestScheme, _ => { });
                    services.AddAuthorization(o =>
                    {
                        o.AddPolicy("SectionAccessForms", p => p.AddAuthenticationSchemes(TestScheme).RequireAuthenticatedUser());
                        o.AddPolicy(AccessLogPolicy.Name, p => p.AddAuthenticationSchemes(TestScheme).AddRequirements(new AccessLogRequirement()));
                    });

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
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
        _db.Dispose();
    }

    private static IUser User(string groupAlias)
    {
        IReadOnlyUserGroup group = Substitute.For<IReadOnlyUserGroup>();
        group.Alias.Returns(groupAlias);
        IUser user = Substitute.For<IUser>();
        user.IsApproved.Returns(true);
        user.IsLockedOut.Returns(false);
        user.Groups.Returns([group]);
        return user;
    }

    private Task<HttpResponseMessage> Get(string? userHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, AccessUrl);
        if (userHeader is not null)
        {
            request.Headers.Add(UserHeader, userHeader);
        }

        return _client.SendAsync(request);
    }

    [Fact]
    public async Task Get_Access_NonMemberOfAccessLogGroup_Returns403() // @M4-T16 P10
    {
        using HttpResponseMessage response = await Get(EditorKey.ToString());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_Access_AdminGroupMember_Returns200() // @M4-T16 P10
    {
        using HttpResponseMessage response = await Get(AdminKey.ToString());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Get_Access_NoUserKeyClaim_Returns403() // @M4-T16 T21, fails closed
    {
        // Authenticated, so the section stand-in passes, but with no key claim to resolve a user from.
        using HttpResponseMessage response = await Get(NoKey);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_Access_Anonymous_Returns401() // @M4-T16
    {
        using HttpResponseMessage response = await Get(null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Authenticates any request carrying the header; any value but <see cref="NoKey"/> becomes the "sub" claim.</summary>
    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var value))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(TestScheme);
            if (value.ToString() != NoKey)
            {
                identity.AddClaim(new Claim("sub", value.ToString()));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), TestScheme)));
        }
    }
}
