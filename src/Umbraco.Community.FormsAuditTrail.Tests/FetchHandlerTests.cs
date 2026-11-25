using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Umbraco.Cms.Core.Events;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Record = Umbraco.Forms.Core.Persistence.Dtos.Record;
using Umbraco.Forms.Core.Searchers;
using Umbraco.Forms.Core.Services.Notifications;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>The handlers run inside Forms' own code path, so their first job is to cost nothing and never throw.</summary>
public class FetchHandlerTests
{
    private static EntrySearchResultFetchingNotification Row(Guid id, string? form = null) =>
        new(new EntrySearchResult { UniqueId = id, Form = form! }, new EventMessages());

    private static RecordFetchingNotification Rec(Guid id, Guid form) =>
        new(new Record { UniqueId = id, Form = form }, new EventMessages());

    [Fact]
    public void FetchHandler_NoWindow_ReturnsImmediately() // @M2-T12
    {
        var collector = new EntryAuditCollector();
        IEntryAuditCollectorAccessor accessor = Substitute.For<IEntryAuditCollectorAccessor>();
        accessor.Current.Returns(collector);

        new EntrySearchResultFetchingHandler(accessor, NullLogger<EntrySearchResultFetchingHandler>.Instance).Handle(Row(Guid.NewGuid()));
        new RecordFetchingHandler(accessor, NullLogger<RecordFetchingHandler>.Instance).Handle(Rec(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Null(collector.Window);
    }

    [Fact]
    public void FetchHandler_FrontEndSubmissionPath_NoCollector_NoThrow() // @M2-T12
    {
        IEntryAuditCollectorAccessor accessor = Substitute.For<IEntryAuditCollectorAccessor>();
        accessor.Current.Returns((EntryAuditCollector?)null);

        new EntrySearchResultFetchingHandler(accessor, NullLogger<EntrySearchResultFetchingHandler>.Instance).Handle(Row(Guid.NewGuid()));
        new RecordFetchingHandler(accessor, NullLogger<RecordFetchingHandler>.Instance).Handle(Rec(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void FetchHandler_AccessorThrows_NoThrow() // @M2-T12 E12
    {
        IEntryAuditCollectorAccessor accessor = Substitute.For<IEntryAuditCollectorAccessor>();
        accessor.Current.Throws(new ObjectDisposedException("scope"));

        new EntrySearchResultFetchingHandler(accessor, NullLogger<EntrySearchResultFetchingHandler>.Instance).Handle(Row(Guid.NewGuid()));
        new RecordFetchingHandler(accessor, NullLogger<RecordFetchingHandler>.Instance).Handle(Rec(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public void FetchHandler_OpenWindow_AddsRowsAndFillsMissingFormId() // @M2-T12
    {
        var collector = new EntryAuditCollector();
        collector.Open(AccessEventType.Exported, null, null, new EntryAuditDetail());
        IEntryAuditCollectorAccessor accessor = Substitute.For<IEntryAuditCollectorAccessor>();
        accessor.Current.Returns(collector);
        Guid form = Guid.NewGuid();
        Guid id = Guid.NewGuid();
        var handler = new EntrySearchResultFetchingHandler(accessor, NullLogger<EntrySearchResultFetchingHandler>.Instance);

        handler.Handle(Row(id, form.ToString()));
        handler.Handle(Row(id, "not-a-guid"));

        Assert.Equal(2, collector.Window!.FetchedRowCount);
        Assert.Equal([id], collector.Window.RecordIds);
        Assert.Equal(form, collector.Window.FormId);
    }

    [Fact]
    public void RecordFetchingHandler_DoesNotOverrideFormIdFromRoute() // @M2-T12
    {
        var collector = new EntryAuditCollector();
        Guid route = Guid.NewGuid();
        collector.Open(AccessEventType.DetailViewed, route, null, new EntryAuditDetail());
        IEntryAuditCollectorAccessor accessor = Substitute.For<IEntryAuditCollectorAccessor>();
        accessor.Current.Returns(collector);

        new RecordFetchingHandler(accessor, NullLogger<RecordFetchingHandler>.Instance).Handle(Rec(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(route, collector.Window!.FormId);
        Assert.Equal(0, collector.Window.FetchedRowCount);
    }
}
