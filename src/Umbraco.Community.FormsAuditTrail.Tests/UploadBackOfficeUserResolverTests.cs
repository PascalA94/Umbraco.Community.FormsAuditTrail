using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>The cookie read the way Forms reads it (S11). Cookie values are synthetic and never printed.</summary>
public class UploadBackOfficeUserResolverTests
{
    private const string CookieName = "UMB_UCONTEXT";
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserKey = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(Now);
    private readonly TicketDataFormat _format = new(new EphemeralDataProtectionProvider().CreateProtector("test"));
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly UploadBackOfficeUserResolver _resolver;

    public UploadBackOfficeUserResolverTests()
    {
        IUser user = Substitute.For<IUser>();
        user.Key.Returns(UserKey);
        user.Name.Returns("Pat Admin");
        user.Username.Returns("pat@example.test");
        // NSubstitute would otherwise auto-substitute a user for any other name.
        _userService.GetByUsername(Arg.Any<string>()).Returns((IUser?)null);
        _userService.GetByUsername("pat@example.test").Returns(user);

        var options = new CookieAuthenticationOptions { TicketDataFormat = _format, CookieManager = new ChunkingCookieManager() };
        options.Cookie.Name = CookieName;
        IOptionsMonitor<CookieAuthenticationOptions> monitor = Substitute.For<IOptionsMonitor<CookieAuthenticationOptions>>();
        monitor.Get("UmbracoBackOffice").Returns(options);
        _resolver = new UploadBackOfficeUserResolver(monitor, _time);
    }

    private string Protect(string? username, DateTimeOffset? expires = null)
    {
        var identity = new ClaimsIdentity("UmbracoBackOffice");
        if (username is not null)
        {
            identity.AddClaim(new Claim(ClaimTypes.Name, username));
        }

        return _format.Protect(new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = expires ?? Now.AddHours(1) },
            "UmbracoBackOffice"));
    }

    private DefaultHttpContext Request(string? cookie)
    {
        var services = new ServiceCollection().AddSingleton(_userService).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = $"{CookieName}={cookie}";
        }

        return context;
    }

    [Fact]
    public void Resolve_ValidCookie_ReturnsUserKeyAndName() // @M2-T12a
    {
        UploadUser? user = _resolver.Resolve(Request(Protect("pat@example.test")));

        Assert.Equal(new UploadUser(UserKey, "Pat Admin"), user);
    }

    [Fact]
    public void Resolve_NoCookie_IsUnresolved() // @M2-T12a
    {
        Assert.Null(_resolver.Resolve(Request(null)));
    }

    [Fact]
    public void Resolve_GarbageCookie_IsUnresolvedAndDoesNotThrow() // @M2-T12a
    {
        Assert.Null(_resolver.Resolve(Request("not-a-protected-ticket")));
    }

    [Fact]
    public void Resolve_CookieProtectedByAnotherKey_IsUnresolved() // @M2-T12a
    {
        var foreign = new TicketDataFormat(new EphemeralDataProtectionProvider().CreateProtector("other"));
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "pat@example.test")], "x")), "x");

        Assert.Null(_resolver.Resolve(Request(foreign.Protect(ticket))));
    }

    [Fact]
    public void Resolve_ExpiredCookie_KeepsTheUserAndFlagsExpiry() // @M2-T12a fix1 item 2
    {
        Assert.Equal(new UploadUser(UserKey, "Pat Admin", true), _resolver.Resolve(Request(Protect("pat@example.test", Now.AddMinutes(-1)))));
    }

    [Fact]
    public void Resolve_NullExpiry_IsNotExpired() // @M2-T12a fix1 item 2
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "pat@example.test")], "UmbracoBackOffice");
        string cookie = _format.Protect(new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties(), "UmbracoBackOffice"));

        Assert.Equal(new UploadUser(UserKey, "Pat Admin", false), _resolver.Resolve(Request(cookie)));
    }

    [Fact]
    public void Resolve_ChunkedCookie_IsReassembled() // @M2-T12a fix1 item 6
    {
        string cookie = Protect("pat@example.test");
        int half = cookie.Length / 2;
        DefaultHttpContext context = Request(null);
        // ChunkingCookieManager's wire format: the main cookie says "chunks-N", then name C1..CN hold the pieces.
        context.Request.Headers.Cookie = new Microsoft.Extensions.Primitives.StringValues(
            $"{CookieName}=chunks-2; {CookieName}C1={cookie[..half]}; {CookieName}C2={cookie[half..]}");

        Assert.Equal(new UploadUser(UserKey, "Pat Admin"), _resolver.Resolve(context));
    }

    [Fact]
    public void Resolve_NoNameClaimOrUnknownUser_IsUnresolved() // @M2-T12a
    {
        Assert.Null(_resolver.Resolve(Request(Protect(null))));
        Assert.Null(_resolver.Resolve(Request(Protect("gone@example.test"))));
    }

    [Fact]
    public void Resolve_UserServiceThrows_IsUnresolved() // @M2-T12a
    {
        _userService.GetByUsername(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("db down"));

        Assert.Null(_resolver.Resolve(Request(Protect("pat@example.test"))));
    }
}
