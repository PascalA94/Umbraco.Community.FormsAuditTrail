using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.Controllers.Dtos;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class ReadTimeMaskerTests
{
    private static readonly Guid Field = Guid.Parse("f0000000-0000-0000-0000-000000000001");
    private const string Masked = ReadTimeMasker.MaskedValue;

    private static EntryChangeDto Change(bool masked = false, string? oldValue = "old", string? newValue = "new") =>
        new() { FieldId = Field, FieldCaption = "Email", OldValue = oldValue, NewValue = newValue, Masked = masked };

    private static Dictionary<Guid, FieldInfo> Fields(bool sensitive) => new() { [Field] = new FieldInfo("Email", sensitive) };

    private static readonly Dictionary<Guid, FieldInfo> NoFields = new();

    [Fact]
    public void Masker_NormalFieldStillOnForm_Clear() // @M4-T16
    {
        EntryChangeDto result = ReadTimeMasker.Apply(Change(), Fields(sensitive: false), formLoaded: true, viewerCanSeeSensitive: false);

        Assert.Equal("old", result.OldValue);
        Assert.Equal("new", result.NewValue);
        Assert.False(result.Masked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Masker_MaskedAtWriteTime_AlwaysMasked(bool viewerCanSee) // @M4-T16 SEC-12
    {
        // A value masked at write time was never stored, so no permission can lift it.
        EntryChangeDto result = ReadTimeMasker.Apply(Change(masked: true), Fields(false), formLoaded: true, viewerCanSee);

        Assert.Equal(Masked, result.OldValue);
        Assert.Equal(Masked, result.NewValue);
        Assert.True(result.Masked);
    }

    [Fact]
    public void Masker_FieldFlaggedSensitiveAfterEdit_Masked() // @M4-T16 T17
    {
        EntryChangeDto result = ReadTimeMasker.Apply(Change(), Fields(sensitive: true), formLoaded: true, viewerCanSeeSensitive: false);

        Assert.Equal(Masked, result.OldValue);
        Assert.Equal(Masked, result.NewValue);
        Assert.True(result.Masked);
    }

    [Fact]
    public void Masker_FieldDeleted_Masked() // @M4-T16 T17
    {
        EntryChangeDto result = ReadTimeMasker.Apply(Change(), NoFields, formLoaded: true, viewerCanSeeSensitive: false);

        Assert.Equal(Masked, result.OldValue);
        Assert.Equal(Masked, result.NewValue);
    }

    [Fact]
    public void Masker_FormNotLoaded_Masked() // @M4-T16 T17
    {
        // A stale dictionary with the field in it must not count when the caller says the form did not load.
        EntryChangeDto result = ReadTimeMasker.Apply(Change(), Fields(false), formLoaded: false, viewerCanSeeSensitive: false);

        Assert.Equal(Masked, result.OldValue);
        Assert.Equal(Masked, result.NewValue);
    }

    [Fact]
    public void Masker_PermittedViewer_SeesClearUnderAll() // @M4-T16 T17
    {
        EntryChangeDto result = ReadTimeMasker.Apply(Change(), Fields(sensitive: true), formLoaded: true, viewerCanSeeSensitive: true);

        Assert.Equal("old", result.OldValue);
        Assert.Equal("new", result.NewValue);
        Assert.False(result.Masked);
    }

    [Fact]
    public void Masker_PermittedViewer_FieldDeletedOrFormGone_SeesClear() // @M4-T16 T17, S4 found a public check: no fallback
    {
        Assert.Equal("new", ReadTimeMasker.Apply(Change(), NoFields, formLoaded: true, viewerCanSeeSensitive: true).NewValue);
        Assert.Equal("new", ReadTimeMasker.Apply(Change(), NoFields, formLoaded: false, viewerCanSeeSensitive: true).NewValue);
    }

    [Fact]
    public void Masker_NullValuesStayNull() // @M4-T16
    {
        // Null means scrubbed, cleared or never stored (StoreEditValues=None); showing "[masked]" would claim a value exists.
        EntryChangeDto result = ReadTimeMasker.Apply(Change(oldValue: "old", newValue: null), Fields(sensitive: true), formLoaded: true, viewerCanSeeSensitive: false);

        Assert.Equal(Masked, result.OldValue);
        Assert.Null(result.NewValue);
        Assert.True(result.Masked);
    }

    [Fact]
    public void Masker_DoesNotMutateInput() // @M4-T16
    {
        EntryChangeDto input = Change();

        ReadTimeMasker.Apply(input, NoFields, formLoaded: false, viewerCanSeeSensitive: false);

        Assert.Equal("new", input.NewValue);
    }

    // ---- SensitiveDataPermission ----

    private static IBackOfficeSecurityAccessor AccessorFor(IUser? user)
    {
        IBackOfficeSecurity security = Substitute.For<IBackOfficeSecurity>();
        security.CurrentUser.Returns(user);
        IBackOfficeSecurityAccessor accessor = Substitute.For<IBackOfficeSecurityAccessor>();
        accessor.BackOfficeSecurity.Returns(security);
        return accessor;
    }

    private static IUser UserInGroups(params Guid[] groupKeys)
    {
        var groups = new List<IReadOnlyUserGroup>();
        foreach (Guid key in groupKeys)
        {
            IReadOnlyUserGroup group = Substitute.For<IReadOnlyUserGroup>();
            group.Key.Returns(key);
            groups.Add(group);
        }

        IUser user = Substitute.For<IUser>();
        user.Groups.Returns(groups);
        return user;
    }

    [Fact]
    public void Permission_UserInSensitiveDataGroup_True() // @M4-T16 S4
    {
        var sut = new SensitiveDataPermission(AccessorFor(UserInGroups(Guid.NewGuid(), Constants.Security.SensitiveDataGroupKey)), NullLogger<SensitiveDataPermission>.Instance);

        Assert.True(sut.CurrentUserCanViewSensitiveData());
    }

    [Fact]
    public void Permission_UserNotInGroup_False() // @M4-T16 S4
    {
        var sut = new SensitiveDataPermission(AccessorFor(UserInGroups(Guid.NewGuid())), NullLogger<SensitiveDataPermission>.Instance);

        Assert.False(sut.CurrentUserCanViewSensitiveData());
    }

    [Fact]
    public void Permission_NoUserOrNoSecurity_FailsClosed() // @M4-T16 SEC-12
    {
        Assert.False(new SensitiveDataPermission(AccessorFor(null), NullLogger<SensitiveDataPermission>.Instance).CurrentUserCanViewSensitiveData());

        IBackOfficeSecurityAccessor none = Substitute.For<IBackOfficeSecurityAccessor>();
        none.BackOfficeSecurity.Returns((IBackOfficeSecurity?)null);
        Assert.False(new SensitiveDataPermission(none, NullLogger<SensitiveDataPermission>.Instance).CurrentUserCanViewSensitiveData());
    }

    [Fact]
    public void Permission_LookupThrows_FailsClosed() // @M4-T16 SEC-12
    {
        IBackOfficeSecurity security = Substitute.For<IBackOfficeSecurity>();
        security.CurrentUser.Returns(_ => throw new InvalidOperationException("boom"));
        IBackOfficeSecurityAccessor accessor = Substitute.For<IBackOfficeSecurityAccessor>();
        accessor.BackOfficeSecurity.Returns(security);

        var logger = new ListLogger<SensitiveDataPermission>();

        Assert.False(new SensitiveDataPermission(accessor, logger).CurrentUserCanViewSensitiveData());
        // Logged at Warning with the exception type only: a message could carry user details.
        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(nameof(InvalidOperationException), message);
        Assert.DoesNotContain("boom", message);
    }
}
