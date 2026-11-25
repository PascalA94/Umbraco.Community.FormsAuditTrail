using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.Authorization;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class AccessLogAuthorizationHandlerTests
{
    private static readonly Guid UserKey = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly FormsAuditTrailOptions _options = new();
    private readonly IOptionsMonitor<FormsAuditTrailOptions> _monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
    private readonly AccessLogAuthorizationHandler _sut;

    public AccessLogAuthorizationHandlerTests()
    {
        // The real post-configure step, so the "admin" default is the one production applies.
        new FormsAuditTrailOptionsSetup(NullLogger<FormsAuditTrailOptionsSetup>.Instance).PostConfigure(null, _options);
        _monitor.CurrentValue.Returns(_ => _options);
        _sut = new AccessLogAuthorizationHandler(_users, _monitor, NullLogger<AccessLogAuthorizationHandler>.Instance);
    }

    private static ClaimsPrincipal PrincipalWithKey(Guid? key)
    {
        var identity = new ClaimsIdentity("test");
        if (key is { } k)
        {
            identity.AddClaim(new Claim("sub", k.ToString()));
        }

        return new ClaimsPrincipal(identity);
    }

    private IUser UserInGroups(params string[] aliases)
    {
        // Built before the Returns call: NSubstitute cannot create substitutes inside a Returns argument.
        var groups = new List<IReadOnlyUserGroup>();
        foreach (var alias in aliases)
        {
            IReadOnlyUserGroup group = Substitute.For<IReadOnlyUserGroup>();
            group.Alias.Returns(alias);
            groups.Add(group);
        }

        IUser user = Substitute.For<IUser>();
        user.IsApproved.Returns(true);
        user.IsLockedOut.Returns(false);
        user.Groups.Returns(groups);
        _users.GetAsync(UserKey).Returns(user);
        return user;
    }

    private async Task<bool> Authorise(ClaimsPrincipal? principal)
    {
        var requirement = new AccessLogRequirement();
        var context = new AuthorizationHandlerContext([requirement], principal ?? new ClaimsPrincipal(), null);
        await _sut.HandleAsync(context);
        return context.HasSucceeded;
    }

    [Fact]
    public async Task Handler_AdminAliasDefault_Succeeds() // @M4-T15
    {
        UserInGroups("admin");

        Assert.True(await Authorise(PrincipalWithKey(UserKey)));
    }

    [Fact]
    public async Task Handler_AliasCaseInsensitive_Succeeds()
    {
        UserInGroups("Admin");

        Assert.True(await Authorise(PrincipalWithKey(UserKey)));
    }

    [Fact]
    public async Task Handler_NotInGroup_Fails()
    {
        UserInGroups("editor", "writer");

        Assert.False(await Authorise(PrincipalWithKey(UserKey)));
    }

    [Fact]
    public async Task Handler_NoCurrentUser_Fails()
    {
        // Key present, but no user behind it (deleted since the cookie was issued).
        _users.GetAsync(UserKey).Returns((IUser?)null);

        Assert.False(await Authorise(PrincipalWithKey(UserKey)));
        Assert.False(await Authorise(null));
    }

    [Fact]
    public async Task Handler_PrincipalWithoutUserKeyClaim_FailsClosed() // T21, SEC-L5
    {
        UserInGroups("admin");

        Assert.False(await Authorise(PrincipalWithKey(null)));
        await _users.DidNotReceive().GetAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handler_NonGuidKeyClaim_FailsClosed()
    {
        UserInGroups("admin");
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim("sub", "not-a-guid"));

        Assert.False(await Authorise(new ClaimsPrincipal(identity)));
    }

    [Fact]
    public async Task Handler_LockedOutOrUnapprovedUser_Fails()
    {
        IUser user = UserInGroups("admin");
        user.IsLockedOut.Returns(true);

        Assert.False(await Authorise(PrincipalWithKey(UserKey)));
    }

    [Fact]
    public void Handler_DoesNotUseBackOfficeSecurityAccessor()
    {
        // P2: the principal on the authorisation context is the request's own, so no ambient accessor is needed.
        ParameterInfo[] parameters = typeof(AccessLogAuthorizationHandler).GetConstructors().Single().GetParameters();

        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(IBackOfficeSecurityAccessor));
    }

    [Fact]
    public async Task Handler_ConfigChangedAtRuntime_UsesNewList()
    {
        UserInGroups("editors");
        Assert.False(await Authorise(PrincipalWithKey(UserKey)));

        _options.EntryAudit.AccessLogUserGroups = ["editors"];

        Assert.True(await Authorise(PrincipalWithKey(UserKey)));
    }

    [Fact]
    public async Task Handler_UnauthenticatedPrincipalWithValidSub_Fails() // @M4-T15 fix1
    {
        UserInGroups("admin");
        // No authentication type, so IsAuthenticated is false even though the claim is well formed.
        var identity = new ClaimsIdentity([new Claim("sub", UserKey.ToString())]);

        Assert.False(await Authorise(new ClaimsPrincipal(identity)));
        await _users.DidNotReceive().GetAsync(Arg.Any<Guid>());
    }

    [Fact]
    public async Task Handler_UnapprovedUser_Fails()
    {
        IUser user = UserInGroups("admin");
        user.IsApproved.Returns(false);

        Assert.False(await Authorise(PrincipalWithKey(UserKey)));
    }

    [Fact]
    public async Task Handler_UserServiceThrows_FailsClosedWithoutThrowing()
    {
        _users.GetAsync(UserKey).Returns<IUser?>(_ => throw new InvalidOperationException("database down"));

        Assert.False(await Authorise(PrincipalWithKey(UserKey)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Handler_EmptyOrWhitespaceConfiguredList_MeansNobody(string alias)
    {
        UserInGroups("admin", "");
        // A list holding only a blank alias is not "empty", so the admin fallback does not apply.
        _options.EntryAudit.AccessLogUserGroups = [alias];

        Assert.False(await Authorise(PrincipalWithKey(UserKey)));
    }

    private static IUserGroup GroupWithAlias(string alias)
    {
        IUserGroup group = Substitute.For<IUserGroup>();
        group.Alias.Returns(alias);
        return group;
    }

    private static IUserGroupService GroupService(params string[] aliases)
    {
        var groups = aliases.Select(GroupWithAlias).ToList();
        var service = Substitute.For<IUserGroupService>();
        service.GetAllAsync(Arg.Any<int>(), Arg.Any<int>()).Returns(new Umbraco.Cms.Core.Models.PagedModel<IUserGroup>(groups.Count, groups));
        return service;
    }

    [Fact]
    public async Task AliasCheck_UnknownAlias_LogsWarning() // T21
    {
        // Case differs from the configured alias on purpose: the comparison ignores case.
        IUserGroupService groups = GroupService("Admin");
        _options.EntryAudit.AccessLogUserGroups = ["admin", "ghost"];
        var logger = new ListLogger<AccessLogGroupAliasCheck>();
        var check = new AccessLogGroupAliasCheck(_monitor, groups, logger);

        await check.HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("ghost", warning.Message);
        // Bounded: an unbounded take would load every group on a site with very many (Task 16 fix1).
        await groups.Received(1).GetAllAsync(0, 1000);
    }

    [Fact]
    public async Task AliasCheck_RepeatedChangeWithSameAliases_WarnsOnce_ThenWarnsForNewSet()
    {
        IUserGroupService groups = GroupService("admin");
        _options.EntryAudit.AccessLogUserGroups = ["ghost"];
        var logger = new ListLogger<AccessLogGroupAliasCheck>();
        var check = new AccessLogGroupAliasCheck(_monitor, groups, logger);

        await check.CheckAsync(CancellationToken.None);
        await check.CheckAsync(CancellationToken.None);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);

        _options.EntryAudit.AccessLogUserGroups = ["ghost", "phantom"];
        await check.CheckAsync(CancellationToken.None);

        Assert.Equal(3, logger.Entries.Count(e => e.Level == LogLevel.Warning));
        Assert.Contains(logger.Entries, e => e.Message.Contains("phantom"));
    }

    [Fact]
    public async Task AliasCheck_OptionsChangeCallback_RechecksTheNewList()
    {
        IUserGroupService groups = GroupService("admin");
        Action<FormsAuditTrailOptions, string?>? onChange = null;
        _monitor.OnChange(Arg.Do<Action<FormsAuditTrailOptions, string?>>(a => onChange = a));
        _options.EntryAudit.AccessLogUserGroups = ["admin"];
        var logger = new ListLogger<AccessLogGroupAliasCheck>();
        var check = new AccessLogGroupAliasCheck(_monitor, groups, logger);
        await check.HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);
        Assert.Empty(logger.Entries);

        _options.EntryAudit.AccessLogUserGroups = ["ghost"];
        Assert.NotNull(onChange);
        onChange!(_options, null);

        // The callback is fire-and-forget; wait briefly for it.
        for (var i = 0; i < 50 && !logger.Entries.Any(e => e.Level == LogLevel.Warning); i++)
        {
            await Task.Delay(20);
        }

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("ghost"));
    }

    [Fact]
    public async Task AliasCheck_GroupServiceThrows_LogsAndDoesNotCrash()
    {
        var groups = Substitute.For<IUserGroupService>();
        groups.GetAllAsync(Arg.Any<int>(), Arg.Any<int>()).Returns<Umbraco.Cms.Core.Models.PagedModel<IUserGroup>>(_ => throw new InvalidOperationException("boom"));
        _options.EntryAudit.AccessLogUserGroups = ["admin"];
        var logger = new ListLogger<AccessLogGroupAliasCheck>();
        var check = new AccessLogGroupAliasCheck(_monitor, groups, logger);

        await check.HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);

        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("InvalidOperationException"));
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
