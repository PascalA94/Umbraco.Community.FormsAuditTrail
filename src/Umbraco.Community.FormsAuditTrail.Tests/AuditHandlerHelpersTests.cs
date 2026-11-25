using NSubstitute;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>Definition-audit attribution must not move when access rows gained their own user resolution (R37(6)).</summary>
public class AuditHandlerHelpersTests
{
    private static IBackOfficeSecurityAccessor Accessor(IUser? user)
    {
        IBackOfficeSecurity security = Substitute.For<IBackOfficeSecurity>();
        security.CurrentUser.Returns(user);
        IBackOfficeSecurityAccessor accessor = Substitute.For<IBackOfficeSecurityAccessor>();
        accessor.BackOfficeSecurity.Returns(security);
        return accessor;
    }

    private static IUser User(Guid key, string? name, string username)
    {
        IUser user = Substitute.For<IUser>();
        user.Key.Returns(key);
        user.Name.Returns(name);
        user.Username.Returns(username);
        return user;
    }

    [Theory]
    [InlineData(null, "System")]
    [InlineData("", "")]
    [InlineData("Editor", "Editor")]
    public void GetCurrentUser_KeepsTheDefinitionAuditNameFallback(string? name, string expected) // @M2-T12 R37(6)
    {
        Guid key = Guid.NewGuid();

        (Guid resolvedKey, string resolvedName) = AuditHandlerHelpers.GetCurrentUser(Accessor(User(key, name, "editor@example.test")));

        Assert.Equal(key, resolvedKey);
        Assert.Equal(expected, resolvedName);
    }

    [Fact]
    public void GetCurrentUser_NoUser_IsSystemWithEmptyKey() // @M2-T12
    {
        Assert.Equal((Guid.Empty, "System"), AuditHandlerHelpers.GetCurrentUser(Accessor(null)));
    }

    [Fact]
    public void TryGetCurrentUser_EmptyName_FallsBackToUsername() // @M2-T12 P19
    {
        Assert.True(AuditHandlerHelpers.TryGetCurrentUser(Accessor(User(Guid.NewGuid(), "", "editor@example.test")), out _, out string name));
        Assert.Equal("editor@example.test", name);
    }

    [Fact]
    public void TryGetCurrentUser_NoUser_ReturnsFalse() // @M2-T12 SEC-L5
    {
        Assert.False(AuditHandlerHelpers.TryGetCurrentUser(Accessor(null), out Guid key, out _));
        Assert.Equal(Guid.Empty, key);
    }
    [Fact]
    public void CurrentUserKeyOrEmpty_SignedInUser_ReturnsTheKey() // @FINAL-FIX item 4
    {
        Guid key = Guid.NewGuid();

        Assert.Equal(key, AuditHandlerHelpers.CurrentUserKeyOrEmpty(Accessor(User(key, "Editor", "editor@example.test"))));
    }

    [Fact]
    public void CurrentUserKeyOrEmpty_NoUser_ReturnsEmpty() // @FINAL-FIX item 4
    {
        Assert.Equal(Guid.Empty, AuditHandlerHelpers.CurrentUserKeyOrEmpty(Accessor(null)));
    }

    [Fact]
    public void CurrentUserKeyOrEmpty_AccessorThrows_ReturnsEmptyInsteadOfThrowing() // @FINAL-FIX item 4
    {
        // The failure log line must still be written when resolving the user is what failed.
        IBackOfficeSecurityAccessor accessor = Substitute.For<IBackOfficeSecurityAccessor>();
        accessor.BackOfficeSecurity.Returns(_ => throw new InvalidOperationException("no scope"));

        Assert.Equal(Guid.Empty, AuditHandlerHelpers.CurrentUserKeyOrEmpty(accessor));
    }
}
