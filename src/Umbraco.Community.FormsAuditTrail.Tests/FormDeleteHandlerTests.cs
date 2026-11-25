using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
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
    private static readonly DateTime Now = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);
    private readonly IEntryAuditWriter _writer = Substitute.For<IEntryAuditWriter>();
    private readonly List<AccessWrite> _writes = new();
    private readonly IEntryAuditCollectorAccessor _collectorAccessor = Substitute.For<IEntryAuditCollectorAccessor>();
    private readonly FormsAuditTrailOptions _options = new();
    private readonly IOptionsMonitor<FormsAuditTrailOptions> _monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
    private readonly FixedTime _time = new();
    private readonly IFormsRecordReader _reader = Substitute.For<IFormsRecordReader>();
    private readonly Microsoft.AspNetCore.Http.IHttpContextAccessor _http = new Microsoft.AspNetCore.Http.HttpContextAccessor();

    public FormDeleteHandlerTests()
    {
        // A singleton instance is not disposed with the scope, so the in-memory database survives the handler.
        _services = new ServiceCollection()
            .AddSingleton<FormsAuditDbContext>(_db.Context)
            .AddScoped(_ => _reader)
            .BuildServiceProvider();
        // Default: UFRecords is empty (no probe) and none of the form's records exist any more.
        _reader.FindAnyExistingIdAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<Guid?>(null));
        _reader.FindExistingAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>()));
        _snapshotService.Serialize(Arg.Any<Form>()).Returns(FallbackJson);
        _securityAccessor.BackOfficeSecurity.Returns(_security);
        _monitor.CurrentValue.Returns(_options);
        _writer.WriteAsync(Arg.Any<AccessWrite>()).Returns(call =>
        {
            _writes.Add(call.Arg<AccessWrite>());
            return new AccessWriteResult(AccessWriteStatus.Written, 1, true);
        });
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private FormDeletingHandler Deleting() =>
        new(_snapshotService, _securityAccessor, NullLogger<FormDeletingHandler>.Instance);

    private FormDeletedHandler Deleted(IServiceScopeFactory? scopeFactory = null) =>
        new(_snapshotService, scopeFactory ?? _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<FormDeletedHandler>.Instance,
            _writer, _collectorAccessor, _monitor, _time, _http, _securityAccessor);

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
        var sut = Deleted(factory);
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

    private void SeedEdit(Guid formId, Guid recordId) =>
        _db.AddAccess(AccessEventType.Edited, formId, Now.AddDays(-1), recordId: recordId, changes:
        [
            new FormEntryChange { FieldId = Guid.NewGuid(), FieldCaption = "Email", OldValue = "old@example.test", NewValue = "new@example.test" },
        ]);

    [Fact]
    public async Task Deleted_Form_ScrubsAllEditValuesForThatForm() // @M3-T14
    {
        SeedEdit(_form.Id, Guid.NewGuid());
        SeedEdit(_form.Id, Guid.NewGuid());
        Guid otherForm = Guid.NewGuid();
        SeedEdit(otherForm, Guid.NewGuid());
        (FormDeletingNotification deleting, FormDeletedNotification deleted) = Notifications();
        Deleting().Handle(deleting);

        await Deleted().HandleAsync(deleted, CancellationToken.None);

        _db.Context.ChangeTracker.Clear();
        List<FormEntryChange> rows = await _db.Context.EntryChanges.AsNoTracking().Include(c => c.Access).ToListAsync();
        Assert.All(rows.Where(r => r.Access!.FormId == _form.Id), r =>
        {
            Assert.Null(r.OldValue);
            Assert.Null(r.NewValue);
            Assert.Equal(Now, r.ValuesScrubbedAt);
        });
        FormEntryChange kept = rows.Single(r => r.Access!.FormId == otherForm);
        Assert.Equal("old@example.test", kept.OldValue);
        Assert.Null(kept.ValuesScrubbedAt);
    }

    [Fact]
    public async Task Deleted_Form_WritesDeletedAccessRowWithDeleteKindForm() // @M3-T14
    {
        var userKey = Guid.NewGuid();
        SetUser(userKey, "Pascal");
        var collector = new EntryAuditCollector();
        collector.CountDeletedRecords(_form.Id, 3);
        collector.CountDeletedRecords(_form.Id, 2);
        _collectorAccessor.Current.Returns(collector);
        (FormDeletingNotification deleting, FormDeletedNotification deleted) = Notifications();
        Deleting().Handle(deleting);

        await Deleted().HandleAsync(deleted, CancellationToken.None);

        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(AccessEventType.Deleted, write.EventType);
        Assert.Equal(_form.Id, write.FormId);
        Assert.Equal("Contact form", write.FormName);
        Assert.Null(write.RecordId);
        Assert.Equal(5, write.RecordCount);
        Assert.Empty(write.RecordIds);
        Assert.Equal(userKey, write.UserKey);
        Assert.Equal("Pascal", write.UserName);
        Assert.Equal("form", write.Detail!.DeleteKind);
    }

    [Fact]
    public async Task Deleted_Form_NoRecordsPublished_WritesZeroCount() // @M3-T14
    {
        _collectorAccessor.Current.Returns((EntryAuditCollector?)null);
        (_, FormDeletedNotification deleted) = Notifications();

        await Deleted().HandleAsync(deleted, CancellationToken.None);

        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(0, write.RecordCount);
        Assert.Equal("System", write.UserName);
        Assert.Equal(Guid.Empty, write.UserKey);
    }

    [Fact]
    public async Task Deleted_Form_CaptureDisabled_ScrubsButWritesNoAccessRow() // @M3-T14 erasure does not depend on capture
    {
        _options.EntryAudit.Enabled = false;
        SeedEdit(_form.Id, Guid.NewGuid());
        (_, FormDeletedNotification deleted) = Notifications();

        await Deleted().HandleAsync(deleted, CancellationToken.None);

        Assert.Empty(_writes);
        _db.Context.ChangeTracker.Clear();
        Assert.NotNull((await _db.Context.EntryChanges.AsNoTracking().SingleAsync()).ValuesScrubbedAt);
        Assert.Single(_db.Context.AuditEntries.Where(e => e.EventType == (int)AuditEventType.Deleted));
    }

    [Fact]
    public async Task Deleted_Form_StillWritesDefinitionEntryWhenScrubThrows() // @M3-T14
    {
        var failing = new FailingUpdate();
        using var db = new TestDatabase(failing);
        using ServiceProvider services = new ServiceCollection().AddSingleton<FormsAuditDbContext>(db.Context).AddScoped(_ => _reader).BuildServiceProvider();
        db.AddAccess(AccessEventType.Edited, _form.Id, Now.AddDays(-1), recordId: Guid.NewGuid(), changes:
        [
            new FormEntryChange { FieldId = Guid.NewGuid(), FieldCaption = "Email", OldValue = "old", NewValue = "new" },
        ]);
        failing.Fail = true;
        (FormDeletingNotification deleting, FormDeletedNotification deleted) = Notifications();
        Deleting().Handle(deleting);

        await Deleted(services.GetRequiredService<IServiceScopeFactory>()).HandleAsync(deleted, CancellationToken.None);

        Assert.Single(db.Context.AuditEntries.Where(e => e.EventType == (int)AuditEventType.Deleted));
        // The access row is a separate step, so a failed scrub does not cost it either.
        Assert.Equal("form", Assert.Single(_writes).Detail!.DeleteKind);
    }

    [Fact]
    public async Task Deleted_Form_ScrubsOnlyRecordsNoLongerInForms() // @M3-T14 R63(2)
    {
        Guid gone = Guid.NewGuid();
        Guid present = Guid.NewGuid();
        SeedEdit(_form.Id, gone);
        SeedEdit(_form.Id, present);
        _reader.FindAnyExistingIdAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<Guid?>(present));
        _reader.FindExistingAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlySet<Guid>>(call.Arg<IReadOnlyCollection<Guid>>().Where(id => id == present).ToHashSet()));
        (_, FormDeletedNotification deleted) = Notifications();

        await Deleted().HandleAsync(deleted, CancellationToken.None);

        _db.Context.ChangeTracker.Clear();
        List<FormEntryChange> rows = await _db.Context.EntryChanges.AsNoTracking().Include(c => c.Access).ToListAsync();
        Assert.NotNull(rows.Single(r => r.Access!.RecordId == gone).ValuesScrubbedAt);
        FormEntryChange kept = rows.Single(r => r.Access!.RecordId == present);
        Assert.Equal("old@example.test", kept.OldValue);
        Assert.Null(kept.ValuesScrubbedAt);
    }

    [Fact]
    public async Task Deleted_Form_ProbeNotFound_ScrubsNothing() // @M3-T14 R63(2) SEC-I1
    {
        SeedEdit(_form.Id, Guid.NewGuid());
        // The reader cannot find a record the table holds, so "missing" proves nothing.
        _reader.FindAnyExistingIdAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<Guid?>(Guid.NewGuid()));
        (_, FormDeletedNotification deleted) = Notifications();

        await Deleted().HandleAsync(deleted, CancellationToken.None);

        _db.Context.ChangeTracker.Clear();
        Assert.Null((await _db.Context.EntryChanges.AsNoTracking().SingleAsync()).ValuesScrubbedAt);
        Assert.Single(_writes);
    }

    [Fact]
    public async Task Deleted_Form_NoStateUser_InRequest_UsesCurrentBackofficeUser() // @M3-T14 R63(5)
    {
        var userKey = Guid.NewGuid();
        SetUser(userKey, "Pascal");
        _http.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var deleted = new FormDeletedNotification(_form, new EventMessages());

        await Deleted().HandleAsync(deleted, CancellationToken.None);

        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(userKey, write.UserKey);
        Assert.Equal("Pascal", write.UserName);
        Assert.Null(write.Detail!.UserUnresolved);
    }

    [Fact]
    public async Task Deleted_Form_NoUserAnywhere_InRequest_WritesUnknownNeverSystem() // @M3-T14 R63(5) SEC-L5
    {
        _security.CurrentUser.Returns((IUser?)null);
        _http.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        (FormDeletingNotification deleting, FormDeletedNotification deleted) = Notifications();
        // The deleting handler saw no user either, so the state holds the empty key.
        Deleting().Handle(deleting);

        await Deleted().HandleAsync(deleted, CancellationToken.None);

        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(Guid.Empty, write.UserKey);
        Assert.Equal("Unknown", write.UserName);
        Assert.True(write.Detail!.UserUnresolved);
    }

    [Fact]
    public async Task Deleted_Form_NoUserAndNoHttpContext_WritesSystem() // @M3-T14 R63(5)
    {
        _security.CurrentUser.Returns((IUser?)null);
        var deleted = new FormDeletedNotification(_form, new EventMessages());

        await Deleted().HandleAsync(deleted, CancellationToken.None);

        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(Guid.Empty, write.UserKey);
        Assert.Equal("System", write.UserName);
        Assert.Null(write.Detail!.UserUnresolved);
    }

    /// <summary>Fails the set-based scrub (an UPDATE) and nothing else.</summary>
    private sealed class FailingUpdate : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public bool Fail;

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command,
            Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Fail && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("scrub failed");
            }

            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }
}
