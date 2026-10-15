using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Community.FormsAuditTrail.Services;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services.Notifications;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class FormDeleteHandlerTests : IDisposable
{
    private const string FallbackJson = "{\"Name\":\"fallback\",\"Workflows\":[]}";
    private const string PriorJson = "{\"Name\":\"prior\",\"Workflows\":[{\"Name\":\"Send email\"}]}";

    private readonly TestDatabase _db = new();
    private readonly ServiceProvider _services;
    private readonly IFormSnapshotService _snapshotService = Substitute.For<IFormSnapshotService>();
    private readonly IBackOfficeSecurityAccessor _securityAccessor = Substitute.For<IBackOfficeSecurityAccessor>();
    private readonly IBackOfficeSecurity _security = Substitute.For<IBackOfficeSecurity>();
    private readonly Form _form = new() { Id = Guid.Parse("55555555-5555-5555-5555-555555555555"), Name = "Contact form" };

    public FormDeleteHandlerTests()
    {
        // A singleton instance is not disposed with the scope, so the in-memory database survives the handler.
        _services = new ServiceCollection()
            .AddSingleton<FormsAuditDbContext>(_db.Context)
            .BuildServiceProvider();
        _snapshotService.Serialize(Arg.Any<Form>()).Returns(FallbackJson);
        _securityAccessor.BackOfficeSecurity.Returns(_security);
    }

    private FormDeletingHandler Deleting() =>
        new(_snapshotService, _securityAccessor, NullLogger<FormDeletingHandler>.Instance);

    private FormDeletedHandler Deleted() =>
        new(_snapshotService, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<FormDeletedHandler>.Instance);

    private (FormDeletingNotification Deleting, FormDeletedNotification Deleted) Notifications()
    {
        var deleting = new FormDeletingNotification(_form, new EventMessages());
        var deleted = new FormDeletedNotification(_form, new EventMessages());
        deleted.WithStateFrom(deleting);
        return (deleting, deleted);
    }

    private void SetUser(Guid key, string name)
    {
        IUser user = Substitute.For<IUser>();
        user.Key.Returns(key);
        user.Name.Returns(name);
        _security.CurrentUser.Returns(user);
    }

    [Fact]
    public void Deleting_DoesNotWriteToAuditTable()
    {
        (FormDeletingNotification deleting, _) = Notifications();

        Deleting().Handle(deleting);

        Assert.Empty(_db.Context.AuditEntries);
    }

    [Fact]
    public async Task Deleted_WritesOneDeletedEntryUsingPriorAfterSnapshotWithWorkflows()
    {
        var userKey = Guid.NewGuid();
        SetUser(userKey, "Pascal");
        _db.AddEntry(_form.Id, "Contact form", DateTime.UtcNow.AddMinutes(-5), afterSnapshot: PriorJson);
        (FormDeletingNotification deleting, FormDeletedNotification deleted) = Notifications();

        Deleting().Handle(deleting);
        await Deleted().HandleAsync(deleted, CancellationToken.None);

        FormAuditEntry entry = Assert.Single(_db.Context.AuditEntries.Where(e => e.EventType == (int)AuditEventType.Deleted));
        Assert.Equal(PriorJson, entry.BeforeSnapshot);
        Assert.Null(entry.AfterSnapshot);
        Assert.Equal(_form.Id, entry.FormId);
        Assert.Equal("Contact form", entry.FormName);
        Assert.Equal(userKey, entry.UserKey);
        Assert.Equal("Pascal", entry.UserName);
        Assert.Contains("Form deleted", entry.ChangeSummaryJson);
    }

    [Fact]
    public async Task Deleted_FallsBackToNotificationSnapshotWhenNoPriorEntry()
    {
        (FormDeletingNotification deleting, FormDeletedNotification deleted) = Notifications();

        Deleting().Handle(deleting);
        await Deleted().HandleAsync(deleted, CancellationToken.None);

        FormAuditEntry entry = Assert.Single(_db.Context.AuditEntries);
        Assert.Equal((int)AuditEventType.Deleted, entry.EventType);
        Assert.Equal(FallbackJson, entry.BeforeSnapshot);
    }

    [Fact]
    public async Task Deleted_RecordsSystemUserWhenNoBackofficeUser()
    {
        _security.CurrentUser.Returns((IUser?)null);
        (FormDeletingNotification deleting, FormDeletedNotification deleted) = Notifications();

        Deleting().Handle(deleting);
        await Deleted().HandleAsync(deleted, CancellationToken.None);

        FormAuditEntry entry = Assert.Single(_db.Context.AuditEntries);
        Assert.Equal("System", entry.UserName);
        Assert.Equal(Guid.Empty, entry.UserKey);
    }

    [Fact]
    public async Task Deleted_RecordsEachFormWithItsOwnState()
    {
        var other = new Form { Id = Guid.NewGuid(), Name = "Other form" };
        var deleting = new FormDeletingNotification(new[] { _form, other }, new EventMessages());
        var deleted = new FormDeletedNotification(new[] { _form, other }, new EventMessages());
        deleted.WithStateFrom(deleting);
        _snapshotService.Serialize(_form).Returns("{\"Name\":\"one\"}");
        _snapshotService.Serialize(other).Returns("{\"Name\":\"two\"}");

        Deleting().Handle(deleting);
        await Deleted().HandleAsync(deleted, CancellationToken.None);

        Assert.Equal(2, _db.Context.AuditEntries.Count());
        Assert.Equal("{\"Name\":\"one\"}", _db.Context.AuditEntries.Single(e => e.FormId == _form.Id).BeforeSnapshot);
        Assert.Equal("{\"Name\":\"two\"}", _db.Context.AuditEntries.Single(e => e.FormId == other.Id).BeforeSnapshot);
    }

    [Fact]
    public async Task Deleted_DoesNotThrowAndWritesNothingWhenScopeFails()
    {
        IServiceScopeFactory factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(_ => throw new InvalidOperationException("boom"));
        var sut = new FormDeletedHandler(_snapshotService, factory, NullLogger<FormDeletedHandler>.Instance);
        (_, FormDeletedNotification deleted) = Notifications();

        await sut.HandleAsync(deleted, CancellationToken.None);

        Assert.Empty(_db.Context.AuditEntries);
    }

    [Fact]
    public async Task Deleted_RecordsSystemWhenStateIsMissing()
    {
        var deleted = new FormDeletedNotification(_form, new EventMessages());

        await Deleted().HandleAsync(deleted, CancellationToken.None);

        FormAuditEntry entry = Assert.Single(_db.Context.AuditEntries);
        Assert.Equal("System", entry.UserName);
        Assert.Equal(Guid.Empty, entry.UserKey);
        Assert.Equal(FallbackJson, entry.BeforeSnapshot);
    }

    [Fact]
    public async Task Deleted_PrefersSnapshotCapturedWhileDeletingOverFreshSnapshot()
    {
        (FormDeletingNotification deleting, FormDeletedNotification deleted) = Notifications();
        _snapshotService.Serialize(_form).Returns("{\"Name\":\"captured\"}");
        Deleting().Handle(deleting);
        _snapshotService.Serialize(_form).Returns("{\"Name\":\"fresh\"}");

        await Deleted().HandleAsync(deleted, CancellationToken.None);

        Assert.Equal("{\"Name\":\"captured\"}", Assert.Single(_db.Context.AuditEntries).BeforeSnapshot);
    }

    [Fact]
    public async Task Deleted_IgnoresPriorEntryWithNullAfterSnapshot()
    {
        _db.AddEntry(_form.Id, "Contact form", DateTime.UtcNow.AddMinutes(-5), afterSnapshot: null);
        (FormDeletingNotification deleting, FormDeletedNotification deleted) = Notifications();
        Deleting().Handle(deleting);

        await Deleted().HandleAsync(deleted, CancellationToken.None);

        FormAuditEntry entry = _db.Context.AuditEntries.Single(e => e.EventType == (int)AuditEventType.Deleted);
        Assert.Equal(FallbackJson, entry.BeforeSnapshot);
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }
}
