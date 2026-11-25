using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Forms.Web.Api.ManagementApi.Export;
using Umbraco.Forms.Web.Api.ManagementApi.Member;
using Umbraco.Forms.Web.Api.ManagementApi.Record;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>Parameter sources per controller as spike S1 recorded them.</summary>
public class EntryAuditRequestReaderTests
{
    private static readonly Guid FormId = Guid.NewGuid();
    private static readonly Guid RecordId = Guid.NewGuid();

    private static CaptureRequest Request(Type controller, Dictionary<string, object?>? route = null, Dictionary<string, StringValues>? query = null) =>
        new(controller, route ?? [], new QueryCollection(query ?? []), new Dictionary<string, object?>());

    [Fact]
    public void Reader_ListViewed_DropsSearchTermByDefault() // @M2-T12
    {
        CaptureRequest request = Request(
            typeof(GetCollectionController),
            new() { ["formId"] = FormId.ToString() },
            new() { ["filter"] = "jane@example.test", ["skip"] = "40", ["take"] = "20", ["states"] = new StringValues(["Approved", "Submitted"]), ["sortBy"] = "created", ["sortOrder"] = "descending", ["startDate"] = "2026-11-01T00:00:00Z" });

        (Guid? formId, Guid? recordId, EntryAuditDetail raw) = EntryAuditRequestReader.Read(request, AccessEventType.ListViewed);
        // The reader returns the raw detail so the dedupe hash can tell two searches apart (R33); shaping drops the term.
        EntryAuditDetail stored = EntryAuditDetail.Shape(raw, new EntryAuditOptions().CaptureSearchTerms);

        Assert.Equal(FormId, formId);
        Assert.Null(recordId);
        Assert.Equal("jane@example.test", raw.Filter!.SearchTerm);
        Assert.Null(stored.Filter!.SearchTerm);
        Assert.True(stored.Filter.SearchTermUsed);
        Assert.DoesNotContain("jane", stored.ToJson(EntryAuditDetail.DefaultMaxBytes));
        Assert.Equal(3, stored.Page);
        Assert.Equal(20, stored.PageSize);
        Assert.Equal("Approved,Submitted", stored.Filter.State);
        Assert.Equal("created", stored.Filter.SortBy);
        Assert.Equal("descending", stored.Filter.SortDirection);
        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), stored.Filter.From);
        Assert.Null(stored.Filter.To);
    }

    [Fact]
    public void Reader_ListViewed_NoFilter_HasNoFilterObject() // @M2-T12
    {
        (_, _, EntryAuditDetail raw) = EntryAuditRequestReader.Read(Request(typeof(GetCollectionController), new() { ["formId"] = FormId }), AccessEventType.ListViewed);

        Assert.Null(raw.Filter);
    }

    [Fact]
    public void Reader_ListViewed_MinValueDate_IsTreatedAsAbsent() // @M2-T12
    {
        (_, _, EntryAuditDetail raw) = EntryAuditRequestReader.Read(
            Request(typeof(GetCollectionController), query: new() { ["endDate"] = DateTime.MinValue.ToString("O"), ["sortBy"] = "x" }),
            AccessEventType.ListViewed);

        Assert.Null(raw.Filter!.To);
    }

    [Fact]
    public void Reader_Export_FormIdAsQueryString_AndFileName() // @M2-T12 S1
    {
        (Guid? formId, _, EntryAuditDetail detail) = EntryAuditRequestReader.Read(
            Request(typeof(ExportController), query: new() { ["formId"] = FormId.ToString(), ["fileName"] = "a.csv" }),
            AccessEventType.ExportDownloaded);

        Assert.Equal(FormId, formId);
        Assert.Equal("a.csv", detail.FileName);
    }

    [Fact]
    public void Reader_Export_UnparseableFormId_IsNull() // @M2-T12
    {
        (Guid? formId, _, _) = EntryAuditRequestReader.Read(
            Request(typeof(ExportController), query: new() { ["formId"] = "not-a-guid" }),
            AccessEventType.ExportDownloaded);

        Assert.Null(formId);
    }

    [Fact]
    public void Reader_GenerateExport_ReadsExportTypeAndFilter() // @M2-T12
    {
        (Guid? formId, _, EntryAuditDetail detail) = EntryAuditRequestReader.Read(
            Request(typeof(PostGenerateExportController), query: new() { ["formId"] = FormId.ToString(), ["exportType"] = "csv-id", ["filter"] = "x" }),
            AccessEventType.Exported);

        Assert.Equal(FormId, formId);
        Assert.Equal("csv-id", detail.ExportType);
        Assert.Equal("x", detail.Filter!.SearchTerm);
    }

    [Fact]
    public void Reader_RetryWorkflow_ReadsRecordAndWorkflowFromRoute() // @M2-T12
    {
        Guid workflow = Guid.NewGuid();
        (Guid? formId, Guid? recordId, EntryAuditDetail detail) = EntryAuditRequestReader.Read(
            Request(typeof(RetryWorkflowController), new() { ["formId"] = FormId, ["recordId"] = RecordId.ToString(), ["workflowId"] = workflow.ToString() }),
            AccessEventType.WorkflowRetried);

        Assert.Equal(FormId, formId);
        Assert.Equal(RecordId, recordId);
        Assert.Equal(workflow, detail.WorkflowId);
    }

    [Fact]
    public void Reader_MemberSummary_ReadsMemberKeyFromRoute() // @M2-T12 E16
    {
        (Guid? formId, _, EntryAuditDetail detail) = EntryAuditRequestReader.Read(
            Request(typeof(GetMemberFormSummariesController), new() { ["memberKey"] = "1234" }),
            AccessEventType.MemberSummaryViewed);

        Assert.Null(formId);
        Assert.Equal("1234", detail.MemberKey);
    }
}
