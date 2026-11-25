using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Umbraco.Cms.Core.Events;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Forms.Core.Services.Notifications;
using Umbraco.Forms.Web.Api.ManagementApi.Export;
using Umbraco.Forms.Web.Api.ManagementApi.Member;
using Umbraco.Forms.Web.Api.ManagementApi.Record;
using Xunit;
using FormsConstants = Umbraco.Forms.Core.Constants;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>Task 12: the capture core through its MVC adapter (spec 7.1 request flow, 7.3 per-event table).</summary>
public sealed class EntryAuditActionFilterTests : IDisposable
{
    private static readonly Guid RecordId = Guid.NewGuid();
    private static readonly Guid WorkflowId = Guid.NewGuid();

    private readonly CaptureHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static Dictionary<string, object?> FormRoute(Guid? record = null)
    {
        var route = new Dictionary<string, object?> { ["formId"] = CaptureHarness.FormId.ToString() };
        if (record is { } id)
        {
            route["recordId"] = id.ToString();
        }

        return route;
    }

    private static Guid[] Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();

    private static IActionResult ExportResult(string fileName) =>
        new OkObjectResult(new PostGenerateExportController.GenerateExportResponseModel { FormId = CaptureHarness.FormId.ToString(), FileName = fileName });

    [Fact]
    public async Task Filter_UnrelatedController_CallsNextOnlyAndTouchesNothing() // @M2-T12
    {
        bool ran = false;
        ActionExecutedContext executed = await _h.RunAsync(typeof(GetMetadataController), FormRoute(), duringAction: () => ran = true);

        Assert.True(ran);
        Assert.IsType<OkObjectResult>(executed.Result);
        Assert.Empty(_h.Writes);
        Assert.Null(Collector().Window);
    }

    [Fact]
    public async Task Filter_Disabled_DoesNoWork() // @M2-T12 E10
    {
        _h.Options.EntryAudit.Enabled = false;

        await _h.RunAsync(typeof(GetCollectionController), FormRoute(), duringAction: () => _h.FetchRows(Ids(2)));

        Assert.Null(Collector().Window);
        await _h.WriterSub.DidNotReceiveWithAnyArgs().WriteAsync(default!);
    }

    [Fact]
    public async Task Filter_ListViewed_WritesOneRowWithRowCountFromHandler() // @M2-T12
    {
        await _h.RunAsync(typeof(GetCollectionController), FormRoute(), duringAction: () => _h.FetchRows(Ids(3)));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(AccessEventType.ListViewed, write.EventType);
        Assert.Equal(CaptureHarness.FormId, write.FormId);
        Assert.Equal(3, write.RecordCount);
        Assert.Equal(CaptureHarness.UserKey, write.UserKey);
        Assert.Equal("Editor", write.UserName);
        // D7: ids are not kept by default, and the row says so.
        Assert.Empty(write.RecordIds);
        Assert.False(write.Detail!.RecordIdsCaptured);
    }

    [Fact]
    public async Task Filter_LogListViewsFalse_WritesNoListRow() // @M2-T12
    {
        _h.Options.EntryAudit.LogListViews = false;

        await _h.RunAsync(typeof(GetCollectionController), FormRoute(), duringAction: () => _h.FetchRows(Ids(3)));

        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task Filter_DetailViewed_WritesRecordIdFromRoute() // @M2-T12
    {
        await _h.RunAsync(typeof(GetAuditTrailController), FormRoute(RecordId));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(AccessEventType.DetailViewed, write.EventType);
        Assert.Equal(RecordId, write.RecordId);
        Assert.Equal(CaptureHarness.FormId, write.FormId);
        Assert.Equal(1, write.RecordCount);
    }

    [Fact]
    public async Task Filter_DetailViewed_ResolvesFormIdFromRecordFetching() // @M2-T12
    {
        Guid otherForm = Guid.NewGuid();
        await _h.RunAsync(
            typeof(GetAuditTrailController),
            new Dictionary<string, object?> { ["recordId"] = RecordId.ToString() },
            duringAction: () => _h.FetchRecord(RecordId, otherForm));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(otherForm, write.FormId);
        Assert.Null(write.Detail!.FormUnresolved);
    }

    [Fact]
    public async Task Filter_UnresolvedFormId_WritesGuidEmptyRowWithFormUnresolvedAndLogsWarning() // @M2-T12 T7
    {
        await _h.RunAsync(typeof(GetAuditTrailController), new Dictionary<string, object?> { ["recordId"] = RecordId.ToString() });

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(Guid.Empty, write.FormId);
        Assert.True(write.Detail!.FormUnresolved);
        Assert.Contains(_h.CaptureLog.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Filter_NoResolvableUser_RecordsUnknownWithFlag_NotSystem() // @M2-T12 SEC-L5
    {
        _h.ClearUser();

        await _h.RunAsync(typeof(GetAuditTrailController), FormRoute(RecordId));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(Guid.Empty, write.UserKey);
        Assert.Equal("Unknown", write.UserName);
        Assert.True(write.Detail!.UserUnresolved);
        // @M2-FIX A2: one Guid.Empty key would hide different unresolved users behind the first view.
        Assert.True(write.SkipDedupe);
    }

    [Fact]
    public async Task Filter_ResolvedUser_DoesNotSkipDedupe() // @M2-FIX A2
    {
        await _h.RunAsync(typeof(GetAuditTrailController), FormRoute(RecordId));

        Assert.False(Assert.Single(_h.Writes).SkipDedupe);
    }

    [Fact]
    public async Task Filter_UserNamedSystem_IsNotTreatedAsUnresolved() // @M2-T12 P19
    {
        Guid key = Guid.NewGuid();
        _h.SetUser(key, "System");

        await _h.RunAsync(typeof(GetAuditTrailController), FormRoute(RecordId));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(key, write.UserKey);
        Assert.Equal("System", write.UserName);
        Assert.Null(write.Detail!.UserUnresolved);
    }

    [Fact]
    public async Task Filter_Exported_StoresFileNameExportTypeAndIds() // @M2-T12
    {
        Guid[] ids = Ids(4);
        string exportType = Guid.NewGuid().ToString();

        await _h.RunAsync(
            typeof(PostGenerateExportController),
            query: new Dictionary<string, string> { ["formId"] = CaptureHarness.FormId.ToString(), ["exportType"] = exportType },
            result: () => ExportResult("export-1.csv"),
            duringAction: () => _h.FetchRows(ids));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(AccessEventType.Exported, write.EventType);
        Assert.Equal(CaptureHarness.FormId, write.FormId);
        Assert.Equal("export-1.csv", write.Detail!.FileName);
        Assert.Equal(exportType, write.Detail.ExportType);
        Assert.Equal(ids.Order(), write.RecordIds.Order());
        Assert.Equal(4, write.RecordCount);
        Assert.Null(write.Detail.RecordIdsCaptured);
    }

    [Fact]
    public async Task Filter_ExportWithNoFetchedRows_SetsRecordIdsCapturedFalse() // @M2-T12 E5
    {
        await _h.RunAsync(
            typeof(PostGenerateExportController),
            query: new Dictionary<string, string> { ["formId"] = CaptureHarness.FormId.ToString(), ["exportType"] = "x" },
            result: () => ExportResult("files.zip"));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Empty(write.RecordIds);
        Assert.False(write.Detail!.RecordIdsCaptured);
    }

    [Fact]
    public async Task Filter_Download_WritesExportDownloaded() // @M2-T12
    {
        await _h.RunAsync(
            typeof(ExportController),
            query: new Dictionary<string, string> { ["formId"] = CaptureHarness.FormId.ToString(), ["fileName"] = "export-1.csv" },
            result: () => new PhysicalFileResult("/tmp/none.csv", "application/octet-stream"));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(AccessEventType.ExportDownloaded, write.EventType);
        // ExportController.formId is a string (S1); it still resolves.
        Assert.Equal(CaptureHarness.FormId, write.FormId);
        Assert.Equal("export-1.csv", write.Detail!.FileName);
    }

    [Fact]
    public async Task Filter_ExportThenDownload_RealWriter_SharesCorrelationIdAndFileName() // @M2-T12 R36(1)
    {
        using var h = new CaptureHarness(realWriter: true);

        await h.RunAsync(
            typeof(PostGenerateExportController),
            query: new Dictionary<string, string> { ["formId"] = CaptureHarness.FormId.ToString(), ["exportType"] = "x" },
            result: () => ExportResult("export-7.csv"),
            duringAction: () => h.FetchRows(Ids(2)));
        await h.RunAsync(
            typeof(ExportController),
            query: new Dictionary<string, string> { ["formId"] = CaptureHarness.FormId.ToString(), ["fileName"] = "export-7.csv" },
            result: () => new PhysicalFileResult("/tmp/none.csv", "application/octet-stream"));

        using var db = h.OpenDb();
        var rows = await db.AccessEntries.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal([(int)AccessEventType.Exported, (int)AccessEventType.ExportDownloaded], rows.Select(r => r.EventType));
        Assert.All(rows, r => Assert.Contains("\"fileName\":\"export-7.csv\"", r.Detail));
        Assert.NotNull(rows[0].CorrelationId);
        Assert.Equal(rows[0].CorrelationId, rows[1].CorrelationId);
    }

    [Fact]
    public async Task Filter_ListViewed_LogListRecordIds_CapsAt500_SetsRecordIdsCapturedFalse() // @M2-T12 SEC-10
    {
        _h.Options.EntryAudit.LogListRecordIds = true;
        Guid[] ids = Ids(501);

        await _h.RunAsync(typeof(GetCollectionController), FormRoute(), duringAction: () => _h.FetchRows(ids));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(500, write.RecordIds.Count);
        Assert.Equal(501, write.RecordCount);
        Assert.False(write.Detail!.RecordIdsCaptured);
    }

    [Fact]
    public async Task Filter_ListViewed_LogListRecordIds_Under500_KeepsAllIds() // @M2-T12
    {
        _h.Options.EntryAudit.LogListRecordIds = true;
        Guid[] ids = Ids(500);

        await _h.RunAsync(typeof(GetCollectionController), FormRoute(), duringAction: () => _h.FetchRows(ids));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(500, write.RecordIds.Count);
        Assert.Null(write.Detail!.RecordIdsCaptured);
    }

    [Fact]
    public async Task Filter_RetryWorkflow_WritesWorkflowRetriedWithWorkflowIdNoValues() // @M2-T12 T3
    {
        Dictionary<string, object?> route = FormRoute(RecordId);
        route["workflowId"] = WorkflowId.ToString();

        await _h.RunAsync(typeof(RetryWorkflowController), route);

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(AccessEventType.WorkflowRetried, write.EventType);
        Assert.Equal(RecordId, write.RecordId);
        Assert.Equal(WorkflowId, write.Detail!.WorkflowId);
        Assert.Empty(write.Changes);
        Assert.Null(write.Detail.Filter);
    }

    [Fact]
    public async Task Filter_RetryWorkflow_NeverDeduplicated() // @M2-T12
    {
        using var h = new CaptureHarness(realWriter: true);
        Dictionary<string, object?> route = FormRoute(RecordId);
        route["workflowId"] = WorkflowId.ToString();

        await h.RunAsync(typeof(RetryWorkflowController), route);
        await h.RunAsync(typeof(RetryWorkflowController), route);

        using var db = h.OpenDb();
        Assert.Equal(2, await db.AccessEntries.CountAsync(a => a.EventType == (int)AccessEventType.WorkflowRetried));
    }

    [Fact]
    public async Task Filter_TwoDifferentSearchesCaptureOff_WritesTwoRows() // @M2-T12 R33(1)
    {
        using var h = new CaptureHarness(realWriter: true);

        await h.RunAsync(typeof(GetCollectionController), FormRoute(), query: new Dictionary<string, string> { ["filter"] = "alice" });
        await h.RunAsync(typeof(GetCollectionController), FormRoute(), query: new Dictionary<string, string> { ["filter"] = "bob" });
        // The same search again inside the window is still a duplicate.
        await h.RunAsync(typeof(GetCollectionController), FormRoute(), query: new Dictionary<string, string> { ["filter"] = "bob" });

        using var db = h.OpenDb();
        var details = await db.AccessEntries.Select(a => a.Detail).ToListAsync();
        Assert.Equal(2, details.Count);
        Assert.All(details, d =>
        {
            Assert.DoesNotContain("alice", d);
            Assert.DoesNotContain("bob", d);
            Assert.Contains("\"searchTermUsed\":true", d);
        });
    }

    [Fact]
    public async Task Filter_MemberSummary_WritesGuidEmptyRow_MemberKeyOnlyWhenKey() // @M2-T12 E16
    {
        string memberKey = Guid.NewGuid().ToString();
        await _h.RunAsync(typeof(GetMemberFormSummariesController), new Dictionary<string, object?> { ["memberKey"] = memberKey });
        await _h.RunAsync(typeof(GetMemberFormSummariesController), new Dictionary<string, object?> { ["memberKey"] = "someone@example.test" });

        AccessWrite[] writes = _h.Writes.ToArray();
        Assert.Equal(2, writes.Length);
        Assert.All(writes, w =>
        {
            Assert.Equal(AccessEventType.MemberSummaryViewed, w.EventType);
            Assert.Equal(Guid.Empty, w.FormId);
            // Not a failed resolution: a member lookup is not about one form.
            Assert.Null(w.Detail!.FormUnresolved);
        });
        Assert.Equal(memberKey, writes[0].Detail!.MemberKey);
        Assert.Null(writes[1].Detail!.MemberKey);
        Assert.True(writes[1].Detail!.MemberIdentifierUsed);
        Assert.DoesNotContain("someone@example.test", writes[1].Detail!.ToJson(EntryAuditDetail.DefaultMaxBytes));
    }

    [Theory]
    [InlineData(typeof(PostGenerateExportController))]
    [InlineData(typeof(ExportController))]
    public async Task Filter_FailClosedOnExport_WriterFailed_Returns503_NoFileName(Type controller) // @M2-T12 T9
    {
        _h.Options.EntryAudit.FailClosedOnExport = true;
        _h.WriterResult = new AccessWriteResult(AccessWriteStatus.Failed, null, false);

        ActionExecutedContext executed = await RunExport(controller);

        StatusCodeResult status = Assert.IsType<StatusCodeResult>(executed.Result);
        Assert.Equal(503, status.StatusCode);
    }

    [Theory]
    [InlineData(typeof(PostGenerateExportController))]
    [InlineData(typeof(ExportController))]
    public async Task Filter_FailClosed_BuilderThrows_Returns503ForExports(Type controller) // @M2-T12 P16
    {
        _h.Options.EntryAudit.FailClosedOnExport = true;
        // Resolving the user is part of building the write; a throw there must still fail closed.
        _h.Security.BackOfficeSecurity.Throws(new InvalidOperationException("boom"));

        ActionExecutedContext executed = await RunExport(controller);

        Assert.Equal(503, Assert.IsType<StatusCodeResult>(executed.Result).StatusCode);
        await _h.WriterSub.DidNotReceiveWithAnyArgs().WriteAsync(default!);
        Assert.Contains(_h.CaptureLog.Entries, e => e.Level == LogLevel.Error);
        await AssertOneFailureThenGapRow(); // @M2-FIX A1 SEC-02
    }

    [Fact]
    public async Task Filter_BuilderThrows_FailClosedOff_ResponseUnchanged() // @M2-T12 E12
    {
        _h.Security.BackOfficeSecurity.Throws(new InvalidOperationException("boom"));

        ActionExecutedContext executed = await RunExport(typeof(PostGenerateExportController));

        Assert.IsType<OkObjectResult>(executed.Result);
        await AssertOneFailureThenGapRow(); // @M2-FIX A1 SEC-02
    }

    [Fact]
    public async Task Filter_WriterThrows_ErrorLogNamesUserAndFormButNoMessage() // @M2-FIX A1
    {
        _h.WriterSub.WriteAsync(Arg.Any<AccessWrite>()).ThrowsAsync(new InvalidOperationException("secret value"));

        await RunExport(typeof(PostGenerateExportController));

        (LogLevel _, string message) = Assert.Single(_h.CaptureLog.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(CaptureHarness.UserKey.ToString(), message);
        Assert.Contains(CaptureHarness.FormId.ToString(), message);
        Assert.Contains(nameof(InvalidOperationException), message);
        Assert.DoesNotContain("secret value", message);
        Assert.Equal(1, _h.Health.WriteFailuresSinceStart);
    }

    [Fact]
    public async Task Filter_FailClosedOnExport_Off_WriterFailed_ResponseUnchanged() // @M2-T12
    {
        _h.WriterResult = new AccessWriteResult(AccessWriteStatus.Failed, null, false);

        ActionExecutedContext executed = await RunExport(typeof(PostGenerateExportController));

        Assert.IsType<OkObjectResult>(executed.Result);
    }

    [Fact]
    public async Task Filter_FailClosedOnExport_DoesNotAffectEditOrList() // @M2-T12
    {
        _h.Options.EntryAudit.FailClosedOnExport = true;
        _h.WriterResult = new AccessWriteResult(AccessWriteStatus.Failed, null, false);

        ActionExecutedContext list = await _h.RunAsync(typeof(GetCollectionController), FormRoute(), duringAction: () => _h.FetchRows(Ids(1)));
        ActionExecutedContext edit = await _h.RunAsync(typeof(UpdateRecordController), FormRoute(RecordId), result: () => new OkResult());

        Assert.IsType<OkObjectResult>(list.Result);
        Assert.IsType<OkResult>(edit.Result);
    }

    [Fact]
    public async Task Filter_EditedWithNoChanges_WritesNothing() // @M2-T12 E3
    {
        await _h.RunAsync(typeof(UpdateRecordController), FormRoute(RecordId), result: () => new OkResult());

        Assert.Empty(_h.Writes);
        // An expected empty write is not a failure (@M2-FIX A1).
        Assert.Equal(0, _h.Health.WriteFailuresSinceStart);
    }

    [Fact]
    public async Task Filter_ExecuteAction_NonDeleteBulkAction_WritesNothing() // @M2-T12 P16
    {
        foreach (string action in new[] { FormsConstants.RecordSetActionTypes.ApproveRecordSet, FormsConstants.RecordSetActionTypes.RejectRecordSet })
        {
            Dictionary<string, object?> route = FormRoute();
            route["actionId"] = action;
            await _h.RunAsync(typeof(ExecuteActionController), route, result: () => new OkResult(), duringAction: () => _h.FetchRecord(RecordId, CaptureHarness.FormId));
            Assert.Null(Collector().Window);
        }

        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task Filter_ExecuteAction_Delete_OpensDeletedWindowAndWritesDeleted() // @M2-T12 @M3-T14
    {
        Dictionary<string, object?> route = FormRoute();
        route["actionId"] = FormsConstants.RecordSetActionTypes.DeleteRecordSet.ToLowerInvariant();

        await _h.RunAsync(typeof(ExecuteActionController), route, result: () => new OkResult());

        Assert.Equal(AccessEventType.Deleted, Collector().Window?.Kind);
        Assert.Equal(AccessEventType.Deleted, Assert.Single(_h.Writes).EventType);
        Assert.Equal(0, _h.Health.WriteFailuresSinceStart); // @M2-FIX A1
    }

    [Theory]
    [InlineData(typeof(PostGenerateExportController))]
    [InlineData(typeof(ExportController))]
    public async Task Filter_ReaderThrows_ExportKind_FailClosedOn_Returns503AndActionNeverRuns(Type controller) // @M2-T12 R37(2) SEC-02
    {
        _h.Options.EntryAudit.FailClosedOnExport = true;

        // No form id in the route or query sends the reader to the action arguments, which throw here.
        await _h.RunAsync(controller, arguments: new ThrowingArguments());

        Assert.Equal(503, Assert.IsType<StatusCodeResult>(_h.LastExecuting!.Result).StatusCode);
        Assert.Equal(0, _h.NextCalls);
        Assert.Empty(_h.Writes);
        Assert.Contains(_h.CaptureLog.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(CaptureHarness.UserKey.ToString()));
        await AssertOneFailureThenGapRow(); // @M2-FIX A1 SEC-02
    }

    [Fact]
    public async Task Filter_ReaderThrows_FailClosedOff_ActionRunsUnchanged() // @M2-T12 R37(2) E12
    {
        ActionExecutedContext executed = await _h.RunAsync(typeof(ExportController), arguments: new ThrowingArguments(), result: () => new OkObjectResult(new object()));

        Assert.Null(_h.LastExecuting!.Result);
        Assert.Equal(1, _h.NextCalls);
        Assert.IsType<OkObjectResult>(executed.Result);
        Assert.Empty(_h.Writes);
        await AssertOneFailureThenGapRow(); // @M2-FIX A1 SEC-02
    }

    [Fact]
    public async Task Filter_ReaderThrows_ListView_FailClosedOn_ActionRunsUnchanged() // @M2-T12 R37(2)
    {
        _h.Options.EntryAudit.FailClosedOnExport = true;

        await _h.RunAsync(typeof(GetCollectionController), arguments: new ThrowingArguments());

        Assert.Null(_h.LastExecuting!.Result);
        Assert.Equal(1, _h.NextCalls);
        await AssertOneFailureThenGapRow(); // @M2-FIX A1 SEC-02
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Filter_CaptureUnavailable_ExportKind_RefusedOnlyWhenFailClosed(bool failClosed) // @M2-T12 R37(2)
    {
        using var h = new CaptureHarness(captureUnavailable: true);
        h.Options.EntryAudit.FailClosedOnExport = failClosed;

        await h.RunAsync(typeof(PostGenerateExportController), query: new Dictionary<string, string> { ["formId"] = CaptureHarness.FormId.ToString() }, result: () => ExportResult("e.csv"));

        if (failClosed)
        {
            Assert.Equal(503, Assert.IsType<StatusCodeResult>(h.LastExecuting!.Result).StatusCode);
            Assert.Equal(0, h.NextCalls);
        }
        else
        {
            Assert.Null(h.LastExecuting!.Result);
            Assert.Equal(1, h.NextCalls);
        }

        Assert.Contains(h.FilterLog.Entries, e => e.Level == LogLevel.Error
            && e.Message.Contains(CaptureHarness.UserKey.ToString())
            && e.Message.Contains(CaptureHarness.FormId.ToString()));
        await AssertOneFailureThenGapRow(h); // @M2-FIX A1 SEC-02
    }

    [Fact]
    public async Task Filter_CaptureUnavailable_Disabled_FailClosedOn_ActionRuns() // @M2-T12 R37(2)
    {
        using var h = new CaptureHarness(captureUnavailable: true);
        h.Options.EntryAudit.FailClosedOnExport = true;
        h.Options.EntryAudit.Enabled = false;

        await h.RunAsync(typeof(ExportController), query: new Dictionary<string, string> { ["formId"] = CaptureHarness.FormId.ToString(), ["fileName"] = "e.csv" });

        Assert.Null(h.LastExecuting!.Result);
        Assert.Equal(1, h.NextCalls);
        // Nothing is audited while capture is off, so nothing was lost (@M2-FIX A1).
        Assert.Equal(0, h.Health.WriteFailuresSinceStart);
    }

    [Fact]
    public async Task Filter_NullWrite_ExportKind_FailClosedOn_Returns503() // @M2-T12 R37(2)
    {
        _h.Options.EntryAudit.FailClosedOnExport = true;

        // The window is lost while Forms runs, so there is nothing to build a row from.
        ActionExecutedContext executed = await _h.RunAsync(
            typeof(ExportController),
            query: new Dictionary<string, string> { ["formId"] = CaptureHarness.FormId.ToString(), ["fileName"] = "e.csv" },
            duringAction: () => typeof(EntryAuditCollector).GetProperty("Window", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(Collector(), null));

        Assert.Equal(503, Assert.IsType<StatusCodeResult>(executed.Result).StatusCode);
        Assert.Empty(_h.Writes);
        await AssertOneFailureThenGapRow(); // @M2-FIX A1 SEC-02
    }

    [Fact]
    public async Task Filter_MemberSummary_RealWriter_DifferentMembersAreDifferentViews() // @M2-T12 R37(1) R37(8)
    {
        using var h = new CaptureHarness(realWriter: true);
        string memberA = Guid.NewGuid().ToString();
        string memberB = Guid.NewGuid().ToString();

        foreach (string member in new[] { memberA, memberB, memberA })
        {
            await h.RunAsync(typeof(GetMemberFormSummariesController), new Dictionary<string, object?> { ["memberKey"] = member });
        }

        using var db = h.OpenDb();
        var rows = await db.AccessEntries.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(memberA, rows[0].Detail);
        Assert.Contains(memberB, rows[1].Detail);
        Assert.All(rows, r => Assert.Equal(EntryAuditWriter.MemberLookupFormName, r.FormName));
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(404, false)]
    [InlineData(500, false)]
    [InlineData(null, true)]
    public async Task Filter_Non2xxOrException_WritesNothing(int? status, bool throws) // @M2-T12
    {
        await _h.RunAsync(
            typeof(GetAuditTrailController),
            FormRoute(RecordId),
            result: () => new ObjectResult(null) { StatusCode = status },
            exception: throws ? new InvalidOperationException("forms failed") : null);

        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task Filter_WriterFailed_ResponseUnchanged() // @M2-T12 E12
    {
        _h.WriterResult = new AccessWriteResult(AccessWriteStatus.Failed, null, false);

        ActionExecutedContext executed = await _h.RunAsync(typeof(GetAuditTrailController), FormRoute(RecordId));

        Assert.IsType<OkObjectResult>(executed.Result);
        Assert.Single(_h.Writes);
    }

    [Fact]
    public async Task Filter_WriterThrows_ResponseUnchanged() // @M2-T12 E12
    {
        _h.WriterSub.WriteAsync(Arg.Any<AccessWrite>()).ThrowsAsync(new InvalidOperationException("writer bug"));

        ActionExecutedContext executed = await _h.RunAsync(typeof(GetAuditTrailController), FormRoute(RecordId));

        Assert.IsType<OkObjectResult>(executed.Result);
    }

    [Fact]
    public async Task Filter_RequestAbortedCancelled_RealWriter_RowStillWritten() // @M2-T12 T6 (P7)
    {
        using var h = new CaptureHarness(realWriter: true);
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        await h.RunAsync(typeof(GetAuditTrailController), FormRoute(RecordId), configure: http => http.RequestAborted = aborted.Token);

        using var db = h.OpenDb();
        Assert.Equal(1, await db.AccessEntries.CountAsync(a => a.EventType == (int)AccessEventType.DetailViewed && a.RecordId == RecordId));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(200, true)]
    [InlineData(201, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    [InlineData(404, false)]
    public void IsSuccess_ObjectResultStatus(int? status, bool expected) // @M2-T12
    {
        Assert.Equal(expected, EntryAuditActionFilter.IsSuccess(Executed(new ObjectResult(null) { StatusCode = status })));
    }

    [Fact]
    public void IsSuccess_FileResultAndStatusCodeResult() // @M2-T12
    {
        Assert.True(EntryAuditActionFilter.IsSuccess(Executed(new PhysicalFileResult("/x", "text/plain"))));
        Assert.True(EntryAuditActionFilter.IsSuccess(Executed(new OkResult())));
        Assert.False(EntryAuditActionFilter.IsSuccess(Executed(new NotFoundResult())));
        Assert.False(EntryAuditActionFilter.IsSuccess(Executed(new EmptyResult())));
        ActionExecutedContext failed = Executed(new OkResult());
        failed.Exception = new InvalidOperationException();
        Assert.False(EntryAuditActionFilter.IsSuccess(failed));
    }

    private static ActionExecutedContext Executed(IActionResult result) =>
        new(new Microsoft.AspNetCore.Mvc.ActionContext(
                new Microsoft.AspNetCore.Http.DefaultHttpContext(),
                new Microsoft.AspNetCore.Routing.RouteData(),
                new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()), [], new object())
        { Result = result };

    // Task 13b: edits through Forms' read path (SEC-04) and backoffice creates (P4).

    private static readonly Guid NameField = Guid.NewGuid();
    private static readonly Guid EmailField = Guid.NewGuid();

    private static Umbraco.Forms.Core.Persistence.Dtos.Record StoredWith(Guid recordId, int id, params (Guid Field, object[] Values)[] fields)
    {
        var record = new Umbraco.Forms.Core.Persistence.Dtos.Record { Id = id, UniqueId = recordId, Form = CaptureHarness.FormId };
        foreach ((Guid field, object[] values) in fields)
        {
            record.RecordFields[Guid.NewGuid()] = new Umbraco.Forms.Core.Persistence.Dtos.RecordField { FieldId = field, Values = [.. values] };
        }

        return record;
    }

    private void KnowFields() =>
        _h.FieldInfo.GetFields(CaptureHarness.FormId).Returns(new Dictionary<Guid, FieldInfo>
        {
            [NameField] = new("Name", false),
            [EmailField] = new("Email", true),
        });

    private Task<ActionExecutedContext> RunEdit(Func<IActionResult?>? result = null, params (Guid Field, object[] Values)[] after) =>
        _h.RunAsync(typeof(UpdateRecordController), FormRoute(RecordId), result: result ?? (() => new OkResult()), duringAction: () => _h.SaveRecord(StoredWith(RecordId, 7, after)));

    [Fact]
    public async Task Filter_Edited_SnapshotTakenBeforeNext() // @M3-T13b SEC-04
    {
        var order = new List<string>();
        _h.RecordStorage.GetRecordByUniqueId(RecordId, Arg.Any<Umbraco.Forms.Core.Models.Form>()).Returns(_ =>
        {
            order.Add("storage");
            return StoredWith(RecordId, 7, (NameField, ["old"]));
        });

        await _h.RunAsync(typeof(UpdateRecordController), FormRoute(RecordId), result: () => new OkResult(), duringAction: () => order.Add("next"));

        Assert.Equal(["storage", "next"], order);
    }

    [Fact]
    public async Task Filter_Edited_StorageReturnsClearText_DiffComparesClearTextOnly() // @M3-T13b T11 SEC-04
    {
        KnowFields();
        // The stored bytes are "encrypted" (reversed); Forms' read path, standing in for a decrypting handler, hands back
        // the clear text. The diff must compare clear text to clear text, so only the edited field changes.
        static string Decrypt(string stored) => new(stored.Reverse().ToArray());
        _h.StoredRecord = StoredWith(RecordId, 7, (NameField, [Decrypt("ecilA")]), (EmailField, [Decrypt("tset.elpmaxe@a")]));
        _h.Options.EntryAudit.StoreEditValues = StoreEditValuesMode.All;

        await RunEdit(after: [(NameField, ["Alicia"]), (EmailField, ["a@example.test"])]);

        AccessWrite write = Assert.Single(_h.Writes);
        EntryFieldChange change = Assert.Single(write.Changes);
        Assert.Equal(NameField, change.FieldId);
        Assert.Equal("Alice", change.OldValue);
        Assert.Equal("Alicia", change.NewValue);
        Assert.DoesNotContain(write.Changes, c => c.OldValue is "ecilA" or "tset.elpmaxe@a" || c.NewValue is "ecilA" or "tset.elpmaxe@a");
    }

    [Fact]
    public async Task Filter_Edited_StorageThrows_FormsActionRuns_EditedRowWithBeforeUnavailable() // @M3-T13b E12
    {
        _h.RecordStorage.GetRecordByUniqueId(Arg.Any<Guid>(), Arg.Any<Umbraco.Forms.Core.Models.Form>())
            .Returns(_ => throw new InvalidOperationException("SENTINEL-STORAGE"));

        ActionExecutedContext executed = await RunEdit(after: [(NameField, ["new"])]);

        Assert.Equal(1, _h.NextCalls);
        Assert.IsType<OkResult>(executed.Result);
        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(AccessEventType.Edited, write.EventType);
        Assert.Equal(RecordId, write.RecordId);
        Assert.Empty(write.Changes);
        Assert.True(write.Detail!.BeforeUnavailable);
        Assert.Contains(_h.CaptureLog.Entries, e => e.Level == LogLevel.Error);
        Assert.DoesNotContain(_h.CaptureLog.Entries, e => e.Message.Contains("SENTINEL-STORAGE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Filter_Edited_RecordNotFound_Forms404_NoHealthFailure_NoGap() // @M3-T13b R54(1)
    {
        // Spike R54(0): Forms answers 404 itself for an unknown record or a record of another form.
        _h.RecordStorage.GetRecordByUniqueId(Arg.Any<Guid>(), Arg.Any<Umbraco.Forms.Core.Models.Form>()).Returns((Umbraco.Forms.Core.Persistence.Dtos.Record?)null);

        await _h.RunAsync(typeof(UpdateRecordController), FormRoute(RecordId), result: () => new NotFoundObjectResult(new object()));

        Assert.Empty(_h.Writes);
        Assert.Equal(0, _h.Health.WriteFailuresSinceStart);
        Assert.Null(_h.Health.TakePendingGap());
        Assert.Contains(_h.CaptureLog.Entries, e => e.Level == LogLevel.Warning);
        Assert.DoesNotContain(_h.CaptureLog.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Filter_Edited_ReadThrows_2xx_NoAuditGapForWrittenRow() // @M3-T13b R54(1)
    {
        _h.RecordStorage.GetRecordByUniqueId(Arg.Any<Guid>(), Arg.Any<Umbraco.Forms.Core.Models.Form>())
            .Returns(_ => throw new InvalidOperationException("storage down"));

        await RunEdit(after: [(NameField, ["new"])]);

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.True(write.Detail!.BeforeUnavailable);
        Assert.Equal(0, _h.Health.WriteFailuresSinceStart);
        Assert.Null(_h.Health.TakePendingGap());
        Assert.Contains(_h.CaptureLog.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Filter_Edited_StoredRecordOtherForm_NoValuesStored() // @M3-T13b R54(2)
    {
        KnowFields();
        Umbraco.Forms.Core.Persistence.Dtos.Record other = StoredWith(RecordId, 7, (NameField, ["SENTINEL-OTHER-FORM"]));
        other.Form = Guid.NewGuid();
        _h.StoredRecord = other;
        _h.Options.EntryAudit.StoreEditValues = StoreEditValuesMode.All;

        await RunEdit(after: [(NameField, ["new"])]);

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Empty(write.Changes);
        Assert.True(write.Detail!.BeforeUnavailable);
        Assert.DoesNotContain("SENTINEL-OTHER-FORM", System.Text.Json.JsonSerializer.Serialize(write), StringComparison.Ordinal);
        Assert.Equal(0, _h.Health.WriteFailuresSinceStart);
    }

    [Fact]
    public async Task Filter_Edited_ThroughFormsReadPath_ForeignFetchHandlerTransformsBeforeValues() // @M3-T13b R54(4) T11 SEC-04
    {
        KnowFields();
        _h.Options.EntryAudit.StoreEditValues = StoreEditValuesMode.All;
        // At rest the values are "encrypted" (reversed). The storage plays Forms' read path (S2, S10): it loads the raw
        // record and publishes RecordFetchingNotification to every registered handler, a foreign decrypting one included,
        // before returning it. Only a read through that path can see clear text.
        var foreign = new ReversingFetchHandler();
        INotificationHandler<RecordFetchingNotification>[] registered = [_h.RecordHandler, foreign];
        _h.RecordStorage.GetRecordByUniqueId(RecordId, Arg.Any<Umbraco.Forms.Core.Models.Form>()).Returns(_ =>
        {
            Umbraco.Forms.Core.Persistence.Dtos.Record raw = StoredWith(RecordId, 7, (NameField, ["ecilA"]), (EmailField, ["tset.elpmaxe@a"]));
            var notification = new RecordFetchingNotification(raw, new Umbraco.Cms.Core.Events.EventMessages());
            foreach (INotificationHandler<RecordFetchingNotification> handler in registered)
            {
                handler.Handle(notification);
            }

            return raw;
        });

        await RunEdit(after: [(NameField, ["Alicia"]), (EmailField, ["a@example.test"])]);

        Assert.Equal(1, foreign.Calls);
        AccessWrite write = Assert.Single(_h.Writes);
        EntryFieldChange change = Assert.Single(write.Changes);
        Assert.Equal(("Alice", "Alicia"), (change.OldValue, change.NewValue));
    }

    [Fact]
    public async Task Filter_Edited_Non2xxWithChanges_LogsOutcomeUnknownWarning() // @M3-T13b R54(6)
    {
        _h.StoredRecord = StoredWith(RecordId, 7, (NameField, ["SENTINEL-OLD"]));

        await RunEdit(result: () => new StatusCodeResult(500), after: [(NameField, ["SENTINEL-NEW"])]);

        Assert.Empty(_h.Writes);
        (LogLevel level, string message) = Assert.Single(_h.CaptureLog.Entries, e => e.Message.Contains("edit outcome unknown", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains(RecordId.ToString(), message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SENTINEL", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filter_Edited_Non2xxNoChanges_NoOutcomeWarning() // @M3-T13b R54(6)
    {
        await RunEdit(result: () => new BadRequestResult(), after: []);

        Assert.DoesNotContain(_h.CaptureLog.Entries, e => e.Message.Contains("edit outcome unknown", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Filter_Create_ResponseIdDiffersFromSavedRecord_DropsCaptions() // @M3-T13b R54(3)
    {
        KnowFields();
        Guid responseId = Guid.NewGuid();

        await _h.RunAsync(
            typeof(CreateRecordController),
            FormRoute(),
            result: () => new ObjectResult(new { id = responseId }) { StatusCode = 201 },
            duringAction: () => _h.SaveRecord(StoredWith(Guid.NewGuid(), 0, (NameField, ["x"]))));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(responseId, write.RecordId);
        Assert.Null(write.Detail!.FieldCaptions);
    }

    [Fact]
    public async Task Filter_Create_SavedRecordOfOtherForm_NoCaptions() // @M3-T13b R54(2)(3)
    {
        KnowFields();
        Guid newId = Guid.NewGuid();
        Umbraco.Forms.Core.Persistence.Dtos.Record saved = StoredWith(newId, 0, (NameField, ["x"]));
        saved.Form = Guid.NewGuid();

        await _h.RunAsync(
            typeof(CreateRecordController),
            FormRoute(),
            result: () => new ObjectResult(new { id = newId }) { StatusCode = 201 },
            duringAction: () => _h.SaveRecord(saved));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(CaptureHarness.FormId, write.FormId);
        Assert.Null(write.Detail!.FieldCaptions);
    }

    /// <summary>Stands in for another package's decrypting read hook: it reverses every string it is handed.</summary>
    private sealed class ReversingFetchHandler : INotificationHandler<RecordFetchingNotification>
    {
        public int Calls { get; private set; }

        public void Handle(RecordFetchingNotification notification)
        {
            Calls++;
            foreach (Umbraco.Forms.Core.Persistence.Dtos.RecordField field in notification.Record.RecordFields.Values)
            {
                field.Values = field.Values.Select(v => v is string s ? (object)new string(s.Reverse().ToArray()) : v).ToList();
            }
        }
    }

    [Fact]
    public async Task Filter_Edited_FormNotFound_EditedRowWithBeforeUnavailable() // @M3-T13b E12
    {
        _h.FormService.Get(Arg.Any<Guid>()).Returns((Umbraco.Forms.Core.Models.Form?)null);

        await RunEdit(after: [(NameField, ["new"])]);

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.True(write.Detail!.BeforeUnavailable);
    }

    [Fact]
    public async Task Filter_Edited_WithChanges_WritesOneEditedRow() // @M3-T13b
    {
        KnowFields();
        _h.StoredRecord = StoredWith(RecordId, 7, (NameField, ["old"]));

        await RunEdit(after: [(NameField, ["new"])]);

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(AccessEventType.Edited, write.EventType);
        Assert.Equal(CaptureHarness.FormId, write.FormId);
        Assert.Equal(RecordId, write.RecordId);
        Assert.Equal(CaptureHarness.UserKey, write.UserKey);
        EntryFieldChange change = Assert.Single(write.Changes);
        Assert.Equal(("Name", "old", "new", false, false), (change.FieldCaption, change.OldValue, change.NewValue, change.Masked, change.Cleared));
        Assert.Null(write.Detail!.BeforeUnavailable);
    }

    [Fact]
    public async Task Filter_Edited_NoChanges_WritesNothing() // @M3-T13b E3
    {
        _h.StoredRecord = StoredWith(RecordId, 7, (NameField, ["same"]));

        await RunEdit(after: [(NameField, ["same"])]);

        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task Filter_Edited_Non2xx_WritesNothing() // @M3-T13b
    {
        _h.StoredRecord = StoredWith(RecordId, 7, (NameField, ["old"]));

        await RunEdit(result: () => new BadRequestResult(), after: [(NameField, ["new"])]);

        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task Filter_Edited_MaskSensitive_NoClearSensitiveValueReachesWriter() // @M3-T13b SEC-12
    {
        KnowFields();
        _h.StoredRecord = StoredWith(RecordId, 7, (EmailField, ["SENTINEL-OLD@example.test"]), (NameField, ["n"]));

        await RunEdit(after: [(EmailField, ["SENTINEL-NEW@example.test"]), (NameField, ["n"])]);

        AccessWrite write = Assert.Single(_h.Writes);
        EntryFieldChange change = Assert.Single(write.Changes);
        Assert.Equal(("Email", "[masked]", "[masked]", true), (change.FieldCaption, change.OldValue, change.NewValue, change.Masked));
        string everything = System.Text.Json.JsonSerializer.Serialize(write) + write.Detail!.ToJson();
        Assert.DoesNotContain("SENTINEL", everything, StringComparison.Ordinal);
    }

    private Task<ActionExecutedContext> RunCreate(Guid newId, Func<IActionResult?>? result = null) =>
        _h.RunAsync(
            typeof(CreateRecordController),
            FormRoute(),
            result: result ?? (() => new ObjectResult(new { id = newId }) { StatusCode = 201 }),
            duringAction: () => _h.SaveRecord(StoredWith(newId, 0, (NameField, ["SENTINEL-CREATED"]), (EmailField, []))));

    [Fact]
    public async Task Filter_Create_2xx_WritesCreatedWithRecordIdAndCaptionsOnly() // @M3-T13b P4 T3
    {
        KnowFields();
        Guid newId = Guid.NewGuid();

        await RunCreate(newId);

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(AccessEventType.Created, write.EventType);
        Assert.Equal(CaptureHarness.FormId, write.FormId);
        Assert.Equal(newId, write.RecordId);
        Assert.Equal(1, write.RecordCount);
        Assert.Equal(["Name"], write.Detail!.FieldCaptions);
        Assert.Empty(write.Changes);
        Assert.Empty(write.RecordIds);
    }

    [Fact]
    public async Task Filter_Create_DetailContainsNoFieldValues() // @M3-T13b P4
    {
        KnowFields();

        await RunCreate(Guid.NewGuid());

        AccessWrite write = Assert.Single(_h.Writes);
        string everything = System.Text.Json.JsonSerializer.Serialize(write) + write.Detail!.ToJson();
        Assert.DoesNotContain("SENTINEL-CREATED", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filter_Create_Non2xx_WritesNothing() // @M3-T13b P4
    {
        await RunCreate(Guid.NewGuid(), result: () => new BadRequestObjectResult(new object()));

        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task Filter_Create_IdFromResult_WhenNoSavingNotification() // @M3-T13b S1
    {
        Guid newId = Guid.NewGuid();

        await _h.RunAsync(typeof(CreateRecordController), FormRoute(), result: () => new ObjectResult(new { id = newId }) { StatusCode = 201 });

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(newId, write.RecordId);
        Assert.Null(write.Detail!.FieldCaptions);
    }

    [Fact]
    public async Task Filter_Create_DoesNotReadStorage() // @M3-T13b
    {
        await RunCreate(Guid.NewGuid());

        Assert.Empty(_h.RecordStorage.ReceivedCalls());
    }

    // @M2-FIX B10 (L4): an export or download whose result type is not one the filter knows cannot be shown to have
    // succeeded or failed, so it is treated as a failed audit rather than silently recording nothing.

    [Theory]
    [InlineData(typeof(PostGenerateExportController))]
    [InlineData(typeof(ExportController))]
    public async Task Filter_ExportUnknownResultType_RecordsHealthFailure_ResponseUnchanged(Type controller) // @M2-FIX B10
    {
        ActionExecutedContext executed = await RunExportWith(controller, () => new EmptyResult());

        Assert.IsType<EmptyResult>(executed.Result);
        Assert.Empty(_h.Writes);
        Assert.Contains(_h.FilterLog.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(nameof(EmptyResult)));
        await AssertOneFailureThenGapRow();
    }

    [Theory]
    [InlineData(typeof(PostGenerateExportController))]
    [InlineData(typeof(ExportController))]
    public async Task Filter_ExportUnknownResultType_FailClosedOn_Returns503(Type controller) // @M2-FIX B10
    {
        _h.Options.EntryAudit.FailClosedOnExport = true;

        ActionExecutedContext executed = await RunExportWith(controller, () => new EmptyResult());

        Assert.Equal(503, Assert.IsType<StatusCodeResult>(executed.Result).StatusCode);
        Assert.Equal(1, _h.Health.WriteFailuresSinceStart);
    }

    [Fact]
    public void IsUnrecognisedResult_NoResultAndNoException_IsUnrecognised() // @M2-FIX B10
    {
        Assert.True(EntryAuditActionFilter.IsUnrecognisedResult(Executed(null!)));
        ActionExecutedContext failed = Executed(null!);
        failed.Exception = new InvalidOperationException();
        // An unhandled exception is a known failure: Forms did not serve the file.
        Assert.False(EntryAuditActionFilter.IsUnrecognisedResult(failed));
    }

    [Fact]
    public async Task Filter_ExportNotFound_IsAKnownFailure_NoHealthFailure() // @M2-FIX B10
    {
        _h.Options.EntryAudit.FailClosedOnExport = true;

        ActionExecutedContext executed = await RunExportWith(typeof(ExportController), () => new NotFoundObjectResult(null));

        Assert.IsType<NotFoundObjectResult>(executed.Result);
        Assert.Equal(0, _h.Health.WriteFailuresSinceStart);
    }

    [Fact]
    public async Task Filter_ListUnknownResultType_KeepsCurrentBehaviour() // @M2-FIX B10
    {
        _h.Options.EntryAudit.FailClosedOnExport = true;

        ActionExecutedContext executed = await _h.RunAsync(typeof(GetCollectionController), FormRoute(), result: () => new EmptyResult());

        Assert.IsType<EmptyResult>(executed.Result);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, _h.Health.WriteFailuresSinceStart);
    }

    [Fact]
    public void IsSuccess_FormsExportResultTypes_AreRecognisedSuccesses() // @M2-FIX B10 S1
    {
        // The types FormsContractTests pins for Forms' export (OkObjectResult) and download (PhysicalFileResult).
        Assert.True(EntryAuditActionFilter.IsSuccess(Executed(new OkObjectResult(new object()))));
        Assert.True(EntryAuditActionFilter.IsSuccess(Executed(new PhysicalFileResult("/x", "application/octet-stream"))));
        Assert.False(EntryAuditActionFilter.IsUnrecognisedResult(Executed(new OkObjectResult(new object()))));
        Assert.False(EntryAuditActionFilter.IsUnrecognisedResult(Executed(new PhysicalFileResult("/x", "application/octet-stream"))));
        Assert.True(EntryAuditActionFilter.IsUnrecognisedResult(Executed(new EmptyResult())));
    }

    // merge-fix 12: an authentication or authorisation denial, or any result that states a non-2xx status, is Forms (or
    // ASP.NET Core) refusing the request. Nothing was served, so it is not a failed audit, never a 503 and never a gap.
    public static TheoryData<Type, string, bool> DenialCases()
    {
        var data = new TheoryData<Type, string, bool>();
        foreach (Type controller in new[] { typeof(PostGenerateExportController), typeof(ExportController) })
        {
            foreach (string denial in new[] { "forbid", "challenge", "unauthorized", "signout", "json401", "content403" })
            {
                data.Add(controller, denial, false);
                data.Add(controller, denial, true);
            }
        }

        return data;
    }

    private static IActionResult Denial(string kind) => kind switch
    {
        "forbid" => new ForbidResult(),
        "challenge" => new ChallengeResult(),
        "unauthorized" => new UnauthorizedResult(),
        "signout" => new SignOutResult(),
        "json401" => new JsonResult(null) { StatusCode = 401 },
        "content403" => new ContentResult { StatusCode = 403 },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(DenialCases))]
    public async Task Filter_ExportDenied_IsNotAnAuditFailure_ResponseUnchanged(Type controller, string denial, bool failClosed) // merge-fix 12
    {
        _h.Options.EntryAudit.FailClosedOnExport = failClosed;
        IActionResult result = Denial(denial);

        ActionExecutedContext executed = await RunExportWith(controller, () => result);

        Assert.Same(result, executed.Result);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, _h.Health.WriteFailuresSinceStart);
        Assert.Null(_h.Health.TakePendingGap());
    }

    [Fact]
    public void IsUnrecognisedResult_UnknownTypeThatCouldBeASuccess_StaysUnrecognised() // merge-fix 12
    {
        Assert.True(EntryAuditActionFilter.IsUnrecognisedResult(Executed(new ContentResult())));
        Assert.True(EntryAuditActionFilter.IsUnrecognisedResult(Executed(new ContentResult { StatusCode = 200 })));
        Assert.True(EntryAuditActionFilter.IsUnrecognisedResult(Executed(new JsonResult(null))));
        Assert.False(EntryAuditActionFilter.IsUnrecognisedResult(Executed(new ForbidResult())));
        Assert.False(EntryAuditActionFilter.IsUnrecognisedResult(Executed(new ContentResult { StatusCode = 302 })));
    }

    private Task<ActionExecutedContext> RunExportWith(Type controller, Func<IActionResult?> result) =>
        _h.RunAsync(
            controller,
            query: new Dictionary<string, string> { ["formId"] = CaptureHarness.FormId.ToString(), ["fileName"] = "e.csv", ["exportType"] = "x" },
            result: result);

    private async Task AssertOneFailureThenGapRow(CaptureHarness? harness = null)
    {
        CaptureHarness h = harness ?? _h;
        Assert.Equal(1, h.Health.WriteFailuresSinceStart);
        IReadOnlyList<string?> gaps = await h.GapRowsAfterNextSuccessfulWriteAsync();
        Assert.Contains("\"count\":1", Assert.Single(gaps));
    }

    private Task<ActionExecutedContext> RunExport(Type controller) =>
        controller == typeof(ExportController)
            ? _h.RunAsync(controller, query: new Dictionary<string, string> { ["formId"] = CaptureHarness.FormId.ToString(), ["fileName"] = "e.csv" }, result: () => new OkObjectResult(new object()))
            : _h.RunAsync(controller, query: new Dictionary<string, string> { ["formId"] = CaptureHarness.FormId.ToString(), ["exportType"] = "x" }, result: () => ExportResult("e.csv"));

    private EntryAuditCollector Collector() =>
        (EntryAuditCollector)_h.LastScope!.ServiceProvider.GetService(typeof(EntryAuditCollector))!;

    /// <summary>Action arguments whose lookup fails, standing in for any fault while the request is read.</summary>
    private sealed class ThrowingArguments : Dictionary<string, object?>, IDictionary<string, object?>
    {
        bool IDictionary<string, object?>.TryGetValue(string key, out object? value) => throw new InvalidOperationException("arguments unreadable");
    }
}
