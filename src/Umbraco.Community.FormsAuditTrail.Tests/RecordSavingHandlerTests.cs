using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Events;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Umbraco.Forms.Core.Persistence.Dtos;
using Umbraco.Forms.Core.Services.Notifications;
using Xunit;
using Record = Umbraco.Forms.Core.Persistence.Dtos.Record;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// Task 13b: the handler turns the record Forms is about to save into a diff (Edited) or a list of captions (Created)
/// on the request's collector. It never reads the database and never writes (spec 7.3 handler steps 1 to 5).
/// </summary>
public sealed class RecordSavingHandlerTests : IDisposable
{
    private static readonly Guid FormId = Guid.NewGuid();
    private static readonly Guid RecordId = Guid.NewGuid();
    private static readonly Guid Name = Guid.NewGuid();
    private static readonly Guid Email = Guid.NewGuid();

    private readonly FormsAuditTrailOptions _options = new();
    private readonly IFormFieldInfoProvider _fields = Substitute.For<IFormFieldInfoProvider>();
    private readonly EntryAuditCollector _collector = new();
    private readonly ServiceProvider _services;
    private readonly RecordSavingHandler _handler;

    public RecordSavingHandlerTests()
    {
        _fields.GetLoadedFields(FormId).Returns(new Dictionary<Guid, FieldInfo>
        {
            [Name] = new("Name", false),
            [Email] = new("Email", true),
        });

        var collection = new ServiceCollection();
        collection.AddSingleton(_collector);
        collection.AddSingleton(_fields);
        _services = collection.BuildServiceProvider();

        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = _services } };
        IOptionsMonitor<FormsAuditTrailOptions> monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        monitor.CurrentValue.Returns(_options);
        _handler = new RecordSavingHandler(new EntryAuditCollectorAccessor(http), http, monitor, NullLogger<RecordSavingHandler>.Instance);
    }

    public void Dispose() => _services.Dispose();

    private static Record Saved(int id, params (Guid Field, object[] Values)[] fields)
    {
        var record = new Record { Id = id, UniqueId = RecordId, Form = FormId };
        foreach ((Guid field, object[] values) in fields)
        {
            record.RecordFields[Guid.NewGuid()] = new RecordField { FieldId = field, Values = [.. values] };
        }

        return record;
    }

    private void Handle(Record record) => _handler.Handle(new RecordSavingNotification(record, new EventMessages()));

    private void OpenEdited(params (Guid Field, object?[] Values)[] before)
    {
        _collector.Open(AccessEventType.Edited, FormId, RecordId, new EntryAuditDetail());
        _collector.SetBeforeValues(before.ToDictionary(b => b.Field, b => (IReadOnlyList<object?>)b.Values));
    }

    [Fact]
    public void Saving_NoEditedWindow_DoesNothing() // @M3-T13b
    {
        _collector.Open(AccessEventType.DetailViewed, FormId, RecordId, new EntryAuditDetail());

        Handle(Saved(5, (Name, ["new"])));

        Assert.Empty(_collector.Window!.Changes);
        Assert.Null(_collector.Window.Detail.FieldCaptions);
    }

    [Fact]
    public void Saving_InsertPath_IdZero_EditedWindow_DoesNothing() // @M3-T13b
    {
        OpenEdited((Name, ["old"]));

        Handle(Saved(0, (Name, ["new"])));

        Assert.Empty(_collector.Window!.Changes);
    }

    [Fact]
    public void Saving_NoWindow_FrontEndSubmission_WritesNothing() // @M3-T13b P4
    {
        Handle(Saved(0, (Name, ["SENTINEL-FRONTEND"])));

        Assert.Null(_collector.Window);
        _fields.DidNotReceive().GetLoadedFields(Arg.Any<Guid>());
    }

    [Fact]
    public void Saving_CreatedWindow_RecordsCaptionsNotValues() // @M3-T13b P4
    {
        _collector.Open(AccessEventType.Created, FormId, null, new EntryAuditDetail());

        // A create is an insert (Id 0): the insert-path rule applies only to Edited windows.
        Handle(Saved(0, (Name, ["SENTINEL-CREATED"]), (Email, [])));

        CaptureWindow window = _collector.Window!;
        Assert.Equal(RecordId, window.RecordId);
        Assert.Equal(["Name"], window.Detail.FieldCaptions);
        Assert.Empty(window.Changes);
        Assert.DoesNotContain("SENTINEL-CREATED", window.Detail.ToJson());
    }

    [Fact]
    public void Saving_UsesWindowBeforeValues_NeverReadsDatabase() // @M3-T13b SEC-04
    {
        Type[] forbidden =
        [
            typeof(IFormsRecordReader),
            typeof(Umbraco.Forms.Core.Data.Storage.IRecordStorage),
            typeof(Persistence.FormsAuditDbContext),
            typeof(Umbraco.Cms.Core.Scoping.ICoreScopeProvider),
            typeof(Umbraco.Cms.Infrastructure.Scoping.IScopeProvider),
            typeof(Umbraco.Cms.Infrastructure.Scoping.IScopeAccessor),
            typeof(IServiceScopeFactory),
        ];
        Type[] parameters = typeof(RecordSavingHandler).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.Empty(parameters.Intersect(forbidden));

        OpenEdited((Name, ["old"]), (Email, ["a@example.test"]));
        Handle(Saved(5, (Name, ["new"]), (Email, ["a@example.test"])));

        EntryFieldChange change = Assert.Single(_collector.Window!.Changes);
        Assert.Equal(Name, change.FieldId);
        Assert.Equal("old", change.OldValue);
        Assert.Equal("new", change.NewValue);
    }

    [Fact]
    public void Saving_NullBeforeValues_AddsNothing() // @M3-T13b E12
    {
        _collector.Open(AccessEventType.Edited, FormId, RecordId, new EntryAuditDetail());

        Handle(Saved(5, (Name, ["new"])));

        Assert.Empty(_collector.Window!.Changes);
    }

    [Fact]
    public void Saving_MaskSensitive_EmailMasked() // @M3-T13b
    {
        OpenEdited((Email, ["old@example.test"]));

        Handle(Saved(5, (Email, ["new@example.test"])));

        EntryFieldChange change = Assert.Single(_collector.Window!.Changes);
        Assert.True(change.Masked);
        Assert.Equal(EntryEditDiffer.MaskedValue, change.OldValue);
        Assert.Equal(EntryEditDiffer.MaskedValue, change.NewValue);
    }

    [Fact]
    public void Saving_Edited_FormNotLoaded_MaskSensitive_MasksBothValues() // @M3-T13b SQLite create hang
    {
        // The filter could not read the form, so no field is known to be non-sensitive: fail closed.
        _fields.GetLoadedFields(FormId).Returns(new Dictionary<Guid, FieldInfo>());
        _options.EntryAudit.StoreEditValues = StoreEditValuesMode.MaskSensitive;
        OpenEdited((Name, ["old"]));

        Handle(Saved(5, (Name, ["new"])));

        EntryFieldChange change = Assert.Single(_collector.Window!.Changes);
        Assert.Equal(EntryEditDiffer.MaskedValue, change.OldValue);
        Assert.Equal(EntryEditDiffer.MaskedValue, change.NewValue);
    }

    [Fact]
    public void Saving_DifferentRecordThanWindow_DoesNothing() // @M3-T13b
    {
        OpenEdited((Name, ["old"]));
        Record other = Saved(9, (Name, ["new"]));
        other.UniqueId = Guid.NewGuid();

        Handle(other);

        Assert.Empty(_collector.Window!.Changes);
    }

    [Fact]
    public void Saving_RecordOfOtherForm_DoesNothing() // @M3-T13b R54(2)
    {
        OpenEdited((Name, ["old"]));
        Record edit = Saved(5, (Name, ["new"]));
        edit.Form = Guid.NewGuid();

        Handle(edit);

        Assert.Empty(_collector.Window!.Changes);

        _collector.Open(AccessEventType.Created, FormId, null, new EntryAuditDetail());
        Record created = Saved(0, (Name, ["x"]));
        created.Form = Guid.NewGuid();

        Handle(created);

        Assert.Null(_collector.Window!.Detail.FieldCaptions);
        Assert.Null(_collector.Window.RecordId);
    }

    [Fact]
    public void Saving_StoresDiffOnCollector_DoesNotWrite() // @M3-T13b
    {
        Type[] parameters = typeof(RecordSavingHandler).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(IEntryAuditWriter), parameters);

        IEntryAuditWriter writer = Substitute.For<IEntryAuditWriter>();
        using var services = new ServiceCollection().AddSingleton(_collector).AddSingleton(_fields).AddSingleton(writer).BuildServiceProvider();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { RequestServices = services } };
        IOptionsMonitor<FormsAuditTrailOptions> monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        monitor.CurrentValue.Returns(_options);
        var handler = new RecordSavingHandler(new EntryAuditCollectorAccessor(http), http, monitor, NullLogger<RecordSavingHandler>.Instance);

        OpenEdited((Name, ["old"]));
        handler.Handle(new RecordSavingNotification(Saved(5, (Name, ["new"])), new EventMessages()));

        Assert.Single(_collector.Window!.Changes);
        Assert.Empty(writer.ReceivedCalls());
    }

    [Fact]
    public void Saving_Created_AsksOnlyForFieldsAlreadyLoaded_NeverReadsTheForm() // @M3-T13b SQLite create hang
    {
        _collector.Open(AccessEventType.Created, FormId, null, new EntryAuditDetail());

        Handle(Saved(0, (Name, ["x"])));

        // GetFields would go to IFormService.Get inside Forms' save scope, where a read opens the SQLite snapshot that
        // a concurrent commit then turns into an unrecoverable 'database is locked' on Forms' INSERT.
        _fields.DidNotReceive().GetFields(Arg.Any<Guid>());
        _fields.Received().GetLoadedFields(FormId);
    }

    [Fact]
    public void Saving_Edited_AsksOnlyForFieldsAlreadyLoaded_NeverReadsTheForm() // @M3-T13b SQLite create hang
    {
        OpenEdited((Name, ["old"]));

        Handle(Saved(5, (Name, ["new"])));

        _fields.DidNotReceive().GetFields(Arg.Any<Guid>());
        _fields.Received().GetLoadedFields(FormId);
    }

    [Fact]
    public void Saving_FieldProviderThrows_NoThrow() // @M3-T13b E12
    {
        _fields.GetLoadedFields(Arg.Any<Guid>()).Returns(_ => throw new InvalidOperationException("forms down"));
        OpenEdited((Name, ["old"]));

        Handle(Saved(5, (Name, ["new"])));
    }
}
