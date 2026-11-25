using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class EntryAuditCollectorTests
{
    private static readonly Guid FormA = Guid.NewGuid();
    private static readonly Guid FormB = Guid.NewGuid();

    [Fact]
    public void Collector_NoWindow_MutatorsAddNothing()
    {
        var collector = new EntryAuditCollector();

        collector.SetBeforeValues(new Dictionary<Guid, IReadOnlyList<object?>>());
        collector.AddFetchedRecord(Guid.NewGuid());
        collector.SetFormIdIfMissing(FormA);
        collector.SetEdit(FormA, Guid.NewGuid(), [new EntryFieldChange(Guid.NewGuid(), "Name", "a", "b", false, false)]);
        collector.SetCreated(FormA, Guid.NewGuid(), ["Name"]);
        collector.AddDeletedRecord(FormA, Guid.NewGuid());

        Assert.Null(collector.Window);
        Assert.False(collector.IsOpen(AccessEventType.ListViewed));
    }

    [Fact]
    public void Collector_Open_ExposesWindowAndKind()
    {
        var collector = new EntryAuditCollector();
        var detail = new EntryAuditDetail { Page = 2 };

        collector.Open(AccessEventType.ListViewed, FormA, null, detail);

        Assert.True(collector.IsOpen(AccessEventType.ListViewed));
        Assert.False(collector.IsOpen(AccessEventType.Edited));
        Assert.Equal(FormA, collector.Window!.FormId);
        Assert.Same(detail, collector.Window.Detail);
    }

    [Fact]
    public void Collector_AddFetchedRecord_CountsRowsAndKeepsIds()
    {
        var collector = new EntryAuditCollector();
        collector.Open(AccessEventType.ListViewed, FormA, null, new EntryAuditDetail());
        Guid one = Guid.NewGuid(), two = Guid.NewGuid();

        collector.AddFetchedRecord(one);
        collector.AddFetchedRecord(two);
        collector.AddFetchedRecord(one);

        Assert.Equal(3, collector.Window!.FetchedRowCount);
        Assert.Equal([one, two], collector.Window.RecordIds);
    }

    [Fact]
    public void Collector_SetFormIdIfMissing_DoesNotOverwrite()
    {
        var collector = new EntryAuditCollector();
        collector.Open(AccessEventType.ExportDownloaded, null, null, new EntryAuditDetail());

        collector.SetFormIdIfMissing(FormA);
        collector.SetFormIdIfMissing(FormB);

        Assert.Equal(FormA, collector.Window!.FormId);
    }

    [Fact]
    public void Collector_SetEdit_StoresChangesAndIds()
    {
        var collector = new EntryAuditCollector();
        collector.Open(AccessEventType.Edited, FormA, null, new EntryAuditDetail());
        Guid record = Guid.NewGuid();
        var change = new EntryFieldChange(Guid.NewGuid(), "Email", "a", "b", true, false);

        collector.SetEdit(FormA, record, [change]);

        Assert.Equal(record, collector.Window!.RecordId);
        Assert.Equal([change], collector.Window.Changes);
    }

    [Fact]
    public void Collector_AddDeletedRecord_KeepsFormAndRecord()
    {
        var collector = new EntryAuditCollector();
        collector.Open(AccessEventType.Deleted, FormA, null, new EntryAuditDetail());
        Guid record = Guid.NewGuid();

        collector.AddDeletedRecord(FormA, record);

        Assert.Equal([(FormA, record)], collector.Window!.DeletedRecords);
    }

    [Fact]
    public void Collector_SetBeforeValues_IsKeptForTheFilter()
    {
        var collector = new EntryAuditCollector();
        collector.Open(AccessEventType.Edited, FormA, null, new EntryAuditDetail());
        var values = new Dictionary<Guid, IReadOnlyList<object?>> { [Guid.NewGuid()] = ["x"] };

        collector.SetBeforeValues(values);

        Assert.Same(values, collector.Window!.BeforeValues);
    }

    [Fact]
    public void Collector_SetCreated_StoresIdAndCaptionsOnly() // P4
    {
        var collector = new EntryAuditCollector();
        collector.Open(AccessEventType.Created, FormA, null, new EntryAuditDetail());
        Guid record = Guid.NewGuid();

        collector.SetCreated(FormA, record, ["Name", "Email"]);

        Assert.Equal(record, collector.Window!.RecordId);
        Assert.Equal(["Name", "Email"], collector.Window.Detail.FieldCaptions);
        Assert.Empty(collector.Window.Changes);
        Assert.Null(collector.Window.BeforeValues);
    }
}
