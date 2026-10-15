using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models.Membership;
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
    private const string PriorAfterSnapshot = "{\"Name\":\"Contact\",\"Workflows\":[{\"Name\":\"Send email\"}]}";
    private const string FallbackSnapshot = "{\"Name\":\"Contact\",\"Workflows\":[]}";

    private readonly TestDatabase _db = new();
    private readonly IFormSnapshotService _snapshotService = Substitute.For<IFormSnapshotService>();
    private readonly IBackOfficeSecurityAccessor _securityAccessor = Substitute.For<IBackOfficeSecurityAccessor>();
    private readonly ServiceProvider _services;
    private readonly Form _form = new() { Id = Guid.NewGuid(), Name = "Contact" };

    public FormDeleteHandlerTests()
    {
        _snapshotService.Serialize(Arg.Any<Form>()).Returns(FallbackSnapshot);
        var collection = new ServiceCollection();
        // Instance registration: the container does not dispose it, TestDatabase owns the context
        collection.AddSingleton<FormsAuditDbContext>(_db.Context);
        _services = collection.BuildServiceProvider();
    }

    private FormDeletingHandler CreateDeletingHandler() =>
        new(_snapshotService, _securityAccessor, NullLogger<FormDeletingHandler>.Instance);

    private FormDeletedHandler CreateDeletedHandler() =>
        new(_snapshotService, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<FormDeletedHandler>.Instance);

    private void SignIn(Guid key, string name)
    {
        IUser user = Substitute.For<IUser>();
        user.Key.Returns(key);
        user.Name.Returns(name);
        IBackOfficeSecurity security = Substitute.For<IBackOfficeSecurity>();
        security.CurrentUser.Returns(user);
        _securityAccessor.BackOfficeSecurity.Returns(security);
    }

    /// <summary>Runs Deleting then Deleted, carrying state across as Forms does.</summary>
    private async Task DeleteAsync()
    {
        var deleting = new FormDeletingNotification(_form, new EventMessages());
        CreateDeletingHandler().Handle(deleting);
        var deleted = new FormDeletedNotification(_form, new EventMessages());
        foreach (KeyValuePair<string, object?> item in deleting.State)
        {
            deleted.State[item.Key] = item.Value;
        }

        await CreateDeletedHandler().HandleAsync(deleted, CancellationToken.None);
    }

    [Fact]
    public async Task Deleting_DoesNotWriteAnAuditEntry()
    {
        var deleting = new FormDeletingNotification(_form, new EventMessages());

        CreateDeletingHandler().Handle(deleting);

        Assert.Equal(0, await _db.Context.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task Deleted_WritesOneDeletedEntryUsingPriorAfterSnapshotWithWorkflows()
    {
        _db.AddEntry(_form.Id, "Contact", DateTime.UtcNow.AddDays(-2), afterSnapshot: "{\"old\":true}");
        _db.AddEntry(_form.Id, "Contact", DateTime.UtcNow.AddDays(-1), afterSnapshot: PriorAfterSnapshot);
        var userKey = Guid.NewGuid();
        SignIn(userKey, "Editor One");

        await DeleteAsync();

        FormAuditEntry entry = await _db.Context.AuditEntries.SingleAsync(e => e.EventType == (int)AuditEventType.Deleted);
        Assert.Equal(_form.Id, entry.FormId);
        Assert.Equal("Contact", entry.FormName);
        Assert.Equal(PriorAfterSnapshot, entry.BeforeSnapshot);
        Assert.Contains("Send email", entry.BeforeSnapshot);
        Assert.Null(entry.AfterSnapshot);
        Assert.Equal(userKey, entry.UserKey);
        Assert.Equal("Editor One", entry.UserName);
        Assert.Equal(1, await _db.Context.AuditEntries.CountAsync(e => e.EventType == (int)AuditEventType.Deleted));
    }

    [Fact]
    public async Task Deleted_WithNoPriorEntry_FallsBackToSnapshotOfTheForm()
    {
        await DeleteAsync();

        FormAuditEntry entry = await _db.Context.AuditEntries.SingleAsync();
        Assert.Equal((int)AuditEventType.Deleted, entry.EventType);
        Assert.Equal(FallbackSnapshot, entry.BeforeSnapshot);
    }

    [Fact]
    public async Task Deleted_WithNoBackofficeUser_RecordsSystemUser()
    {
        _securityAccessor.BackOfficeSecurity.Returns((IBackOfficeSecurity?)null);

        await DeleteAsync();

        FormAuditEntry entry = await _db.Context.AuditEntries.SingleAsync();
        Assert.Equal("System", entry.UserName);
        Assert.Equal(Guid.Empty, entry.UserKey);
    }

    [Fact]
    public async Task Deleted_WithoutDeletingState_StillRecordsEntryAsSystem()
    {
        var deleted = new FormDeletedNotification(_form, new EventMessages());

        await CreateDeletedHandler().HandleAsync(deleted, CancellationToken.None);

        FormAuditEntry entry = await _db.Context.AuditEntries.SingleAsync();
        Assert.Equal("System", entry.UserName);
        Assert.Equal(FallbackSnapshot, entry.BeforeSnapshot);
    }

    [Fact]
    public async Task Deleted_IgnoresPriorEntriesOfOtherForms()
    {
        _db.AddEntry(Guid.NewGuid(), "Other", DateTime.UtcNow.AddDays(-1), afterSnapshot: "{\"other\":true}");

        await DeleteAsync();

        FormAuditEntry entry = await _db.Context.AuditEntries.SingleAsync(e => e.FormId == _form.Id);
        Assert.Equal(FallbackSnapshot, entry.BeforeSnapshot);
    }

    [Fact]
    public async Task Deleted_UsesDeletingSnapshotWhenItDiffersFromFreshSnapshot()
    {
        var deleted = new FormDeletedNotification(_form, new EventMessages());
        deleted.State[$"{FormDeletingHandler.BeforeSnapshotStateKeyPrefix}{_form.Id}"] = "{\"captured\":true}";
        _snapshotService.Serialize(Arg.Any<Form>()).Returns("{\"fresh\":true}");

        await CreateDeletedHandler().HandleAsync(deleted, CancellationToken.None);

        Assert.Equal("{\"captured\":true}", (await _db.Context.AuditEntries.SingleAsync()).BeforeSnapshot);
    }

    [Fact]
    public async Task Deleted_WithMultipleForms_WritesOneEntryPerFormWithItsOwnUser()
    {
        var second = new Form { Id = Guid.NewGuid(), Name = "Second" };
        _db.AddEntry(_form.Id, "Contact", DateTime.UtcNow.AddDays(-1), afterSnapshot: PriorAfterSnapshot);
        SignIn(Guid.NewGuid(), "Editor One");
        var deleting = new FormDeletingNotification(new[] { _form, second }, new EventMessages());
        CreateDeletingHandler().Handle(deleting);
        var deleted = new FormDeletedNotification(new[] { _form, second }, new EventMessages());
        foreach (KeyValuePair<string, object?> item in deleting.State)
        {
            deleted.State[item.Key] = item.Value;
        }

        await CreateDeletedHandler().HandleAsync(deleted, CancellationToken.None);

        List<FormAuditEntry> entries = await _db.Context.AuditEntries
            .Where(e => e.EventType == (int)AuditEventType.Deleted).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(PriorAfterSnapshot, entries.Single(e => e.FormId == _form.Id).BeforeSnapshot);
        Assert.Equal(FallbackSnapshot, entries.Single(e => e.FormId == second.Id).BeforeSnapshot);
        Assert.All(entries, e => Assert.Equal("Editor One", e.UserName));
    }

    [Fact]
    public async Task Deleted_WhenScopeResolutionThrows_DoesNotRethrowAndWritesNothing()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(_ => throw new InvalidOperationException("boom"));
        var handler = new FormDeletedHandler(_snapshotService, scopeFactory, NullLogger<FormDeletedHandler>.Instance);

        await handler.HandleAsync(new FormDeletedNotification(_form, new EventMessages()), CancellationToken.None);

        Assert.Equal(0, await _db.Context.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task Deleted_WhenNoSnapshotIsAvailable_StillRecordsDeletionWithEmptyJsonObject()
    {
        _snapshotService.Serialize(Arg.Any<Form>()).Returns(_ => throw new InvalidOperationException("boom"));

        await CreateDeletedHandler().HandleAsync(new FormDeletedNotification(_form, new EventMessages()), CancellationToken.None);

        FormAuditEntry entry = await _db.Context.AuditEntries.SingleAsync();
        Assert.Equal((int)AuditEventType.Deleted, entry.EventType);
        Assert.Equal("{}", entry.BeforeSnapshot);
    }

    [Fact]
    public async Task Deleted_IgnoresPriorEntryWithNullAfterSnapshot()
    {
        _db.AddEntry(_form.Id, "Contact", DateTime.UtcNow.AddDays(-2), afterSnapshot: PriorAfterSnapshot);
        _db.AddEntry(_form.Id, "Contact", DateTime.UtcNow.AddDays(-1), afterSnapshot: null);

        await DeleteAsync();

        FormAuditEntry entry = await _db.Context.AuditEntries.SingleAsync(e => e.EventType == (int)AuditEventType.Deleted);
        Assert.Equal(PriorAfterSnapshot, entry.BeforeSnapshot);
    }

    [Fact]
    public void Deleting_WhenSecurityAccessorThrows_DoesNotThrowAndStillCapturesSnapshot()
    {
        _securityAccessor.BackOfficeSecurity.Returns(_ => throw new InvalidOperationException("no context"));
        var deleting = new FormDeletingNotification(_form, new EventMessages());

        CreateDeletingHandler().Handle(deleting);

        Assert.Equal(FallbackSnapshot, deleting.State[$"{FormDeletingHandler.BeforeSnapshotStateKeyPrefix}{_form.Id}"]);
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }
}
