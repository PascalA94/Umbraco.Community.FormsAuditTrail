using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Cms.Core.Events;
using Umbraco.Community.FormsAuditTrail.BackgroundJobs;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Forms.Core.Services.Notifications;
using Umbraco.Forms.Web.Api.ManagementApi.Record;
using Umbraco.Forms.Web.Models.Backoffice;
using Xunit;
using FormsConstants = Umbraco.Forms.Core.Constants;
using Record = Umbraco.Forms.Core.Persistence.Dtos.Record;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// Task 14 on the S5 fallback branch: record deletes through the capture core, the in-request erasure scrub, and the
/// <c>RecordDeleting</c> handler that only ever touches the collector (SQLite locks on a write inside Forms' delete scope).
/// </summary>
public sealed class RecordDeleteHandlerTests : IDisposable
{
    private static readonly Guid RecordId = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);

    private readonly CaptureHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static Dictionary<string, object?> SingleRoute(Guid recordId) => new()
    {
        ["formId"] = CaptureHarness.FormId.ToString(),
        ["recordId"] = recordId.ToString(),
    };

    private static Dictionary<string, object?> BulkRoute() => new()
    {
        ["formId"] = CaptureHarness.FormId.ToString(),
        ["actionId"] = FormsConstants.RecordSetActionTypes.DeleteRecordSet.ToLowerInvariant(),
    };

    // S5: Execute(Guid formId, Guid actionId, RecordActionExecution model); the ids are model.RecordKeys.
    private static Dictionary<string, object?> BulkArguments(IEnumerable<Guid> ids) => new()
    {
        ["formId"] = CaptureHarness.FormId,
        ["model"] = new RecordActionExecution { RecordKeys = ids.ToArray() },
    };

    private static Guid[] Ids(int count) => Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();

    private static FormEntryChange Change() => new()
    {
        FieldId = Guid.NewGuid(),
        FieldCaption = "Email",
        OldValue = "old@example.test",
        NewValue = "new@example.test",
    };

    private static void SeedEdits(CaptureHarness h, Guid formId, IEnumerable<Guid> recordIds)
    {
        using SqliteFormsAuditDbContext db = h.OpenDb();
        foreach (Guid recordId in recordIds)
        {
            db.AccessEntries.Add(new FormAccessEntry
            {
                Timestamp = Now.AddDays(-1),
                EventType = (int)AccessEventType.Edited,
                FormId = formId,
                FormName = "Contact",
                RecordId = recordId,
                RecordCount = 1,
                UserKey = Guid.NewGuid(),
                UserName = "Editor",
                Changes = { Change() },
            });
        }

        db.SaveChanges();
    }

    private static List<FormEntryChange> Changes(CaptureHarness h, Guid recordId)
    {
        using SqliteFormsAuditDbContext db = h.OpenDb();
        return db.EntryChanges.AsNoTracking().Where(c => c.Access!.RecordId == recordId).ToList();
    }

    // ---- RecordDeletingHandler ----

    [Fact]
    public void Deleting_InWindow_CollectsIdsAndNeverWrites() // @M3-T14 mirrors Deleting_DoesNotWriteToAuditTable
    {
        var collector = new EntryAuditCollector();
        collector.Open(AccessEventType.Deleted, CaptureHarness.FormId, null, new EntryAuditDetail());
        IEntryAuditCollectorAccessor accessor = Substitute.For<IEntryAuditCollectorAccessor>();
        accessor.Current.Returns(collector);
        var handler = new RecordDeletingHandler(accessor, NullLogger<RecordDeletingHandler>.Instance);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        handler.Handle(new RecordDeletingNotification(
            new[] { new Record { UniqueId = a, Form = CaptureHarness.FormId }, new Record { UniqueId = b, Form = CaptureHarness.FormId } },
            new EventMessages()));

        Assert.Equal([(CaptureHarness.FormId, a), (CaptureHarness.FormId, b)], collector.Window!.DeletedRecords);
        // S5: a write here runs inside Forms' open delete scope and locks SQLite, so the handler cannot reach a writer,
        // a scope or the audit database at all.
        Type[] parameters = typeof(RecordDeletingHandler).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToArray();
        Assert.DoesNotContain(typeof(IEntryAuditWriter), parameters);
        Assert.DoesNotContain(typeof(IServiceScopeFactory), parameters);
        Assert.DoesNotContain(typeof(FormsAuditDbContext), parameters);
        Assert.DoesNotContain(typeof(DeletedRecordScrubber), parameters);
    }

    [Fact]
    public void Deleting_NoWindow_CountsRecordsForTheFormDeleteOnly() // @M3-T14
    {
        // A permanent form delete is not an audited controller, so there is no window; the form's Deleted row still
        // needs the number of records Forms deleted with it.
        var collector = new EntryAuditCollector();
        IEntryAuditCollectorAccessor accessor = Substitute.For<IEntryAuditCollectorAccessor>();
        accessor.Current.Returns(collector);
        var handler = new RecordDeletingHandler(accessor, NullLogger<RecordDeletingHandler>.Instance);
        Guid other = Guid.NewGuid();

        handler.Handle(new RecordDeletingNotification(Ids(3).Select(id => new Record { UniqueId = id, Form = CaptureHarness.FormId }).ToArray(), new EventMessages()));
        handler.Handle(new RecordDeletingNotification(Ids(2).Select(id => new Record { UniqueId = id, Form = CaptureHarness.FormId }).ToArray(), new EventMessages()));
        handler.Handle(new RecordDeletingNotification(new Record { UniqueId = Guid.NewGuid(), Form = other }, new EventMessages()));

        Assert.Null(collector.Window);
        Assert.Equal(5, collector.DeletedRecordCount(CaptureHarness.FormId));
        Assert.Equal(1, collector.DeletedRecordCount(other));
        Assert.Equal(0, collector.DeletedRecordCount(Guid.NewGuid()));
    }

    [Fact]
    public void Deleting_NoRequest_DoesNothingAndDoesNotThrow() // @M3-T14
    {
        IEntryAuditCollectorAccessor accessor = Substitute.For<IEntryAuditCollectorAccessor>();
        accessor.Current.Returns((EntryAuditCollector?)null);
        var handler = new RecordDeletingHandler(accessor, NullLogger<RecordDeletingHandler>.Instance);

        handler.Handle(new RecordDeletingNotification(new Record { UniqueId = Guid.NewGuid(), Form = CaptureHarness.FormId }, new EventMessages()));
    }

    [Fact]
    public void Deleting_AccessorThrows_DoesNotThrow() // @M3-T14 E12: it runs inside Forms' delete
    {
        IEntryAuditCollectorAccessor accessor = Substitute.For<IEntryAuditCollectorAccessor>();
        accessor.Current.Returns(_ => throw new InvalidOperationException("broken"));
        var handler = new RecordDeletingHandler(accessor, NullLogger<RecordDeletingHandler>.Instance);

        handler.Handle(new RecordDeletingNotification(new Record { UniqueId = Guid.NewGuid(), Form = CaptureHarness.FormId }, new EventMessages()));
    }

    [Fact]
    public async Task Deleted_ScrubFails_LoggedNotThrown() // @M3-T14 E12
    {
        // This harness registers no audit database, so the scrub's scope cannot build a context.
        ActionExecutedContext executed = await _h.RunAsync(typeof(DeleteRecordController), SingleRoute(RecordId), result: () => new OkResult(),
            duringAction: () => _h.DeleteRecords(CaptureHarness.FormId, RecordId));

        Assert.IsType<OkResult>(executed.Result);
        Assert.Equal(AccessEventType.Deleted, Assert.Single(_h.Writes).EventType);
        (LogLevel level, string message) = Assert.Single(_h.ScrubLog.Entries);
        Assert.Equal(LogLevel.Error, level);
        Assert.Contains("scrub", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(CaptureHarness.UserKey.ToString(), message);
        Assert.Contains(CaptureHarness.FormId.ToString(), message);
    }

    [Fact]
    public async Task Deleted_NoWindow_LeftToReconciliation_ScrubbedByNextJobRun() // @M3-T14 S5 fallback, replaces T23
    {
        // A delete outside a backoffice request (code, Forms' scheduled deletion) has no window and no RecordDeleted
        // event to act on (S5, S6), so nothing scrubs it at once; the daily reconciliation does, up to 24 hours later.
        using var db = new TestDatabase();
        Guid erased = Guid.NewGuid();
        db.AddAccess(AccessEventType.Edited, CaptureHarness.FormId, Now.AddDays(-1), recordId: erased, changes: [Change()]);
        IEntryAuditCollectorAccessor accessor = Substitute.For<IEntryAuditCollectorAccessor>();
        accessor.Current.Returns((EntryAuditCollector?)null);
        new RecordDeletingHandler(accessor, NullLogger<RecordDeletingHandler>.Instance)
            .Handle(new RecordDeletingNotification(new Record { UniqueId = erased, Form = CaptureHarness.FormId }, new EventMessages()));

        FormEntryChange untouched = await db.Context.EntryChanges.AsNoTracking().SingleAsync();
        Assert.Equal("old@example.test", untouched.OldValue);
        Assert.Null(untouched.ValuesScrubbedAt);

        IFormsRecordReader reader = Substitute.For<IFormsRecordReader>();
        Guid probe = Guid.NewGuid();
        reader.FindExistingAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlySet<Guid>>(call.Arg<IReadOnlyCollection<Guid>>().Contains(probe) ? new HashSet<Guid> { probe } : new HashSet<Guid>()));
        reader.FindAnyExistingIdAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<Guid?>(probe));

        Assert.Equal(1, await AuditRetentionJob.ReconcileErasuresAsync(db.Context, reader, Now));

        db.Context.ChangeTracker.Clear();
        FormEntryChange scrubbed = await db.Context.EntryChanges.AsNoTracking().SingleAsync();
        Assert.Null(scrubbed.OldValue);
        Assert.Null(scrubbed.NewValue);
        Assert.Equal(Now, scrubbed.ValuesScrubbedAt);
    }

    // ---- Capture core: rows ----

    [Fact]
    public async Task Filter_SingleDelete_WritesOneDeletedRowWithUser() // @M3-T14
    {
        await _h.RunAsync(typeof(DeleteRecordController), SingleRoute(RecordId), result: () => new OkResult(),
            duringAction: () => _h.DeleteRecords(CaptureHarness.FormId, RecordId));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(AccessEventType.Deleted, write.EventType);
        Assert.Equal(CaptureHarness.FormId, write.FormId);
        Assert.Equal(RecordId, write.RecordId);
        Assert.Equal(1, write.RecordCount);
        Assert.Equal(CaptureHarness.UserKey, write.UserKey);
        Assert.Equal("Editor", write.UserName);
        Assert.Equal("single", write.Detail!.DeleteKind);
        Assert.Null(write.Detail.Bulk);
        Assert.Empty(write.RecordIds);
    }

    [Fact]
    public async Task Filter_SingleDelete_NoUser_WritesUnknown() // @M3-T14 SEC-L5
    {
        _h.ClearUser();

        await _h.RunAsync(typeof(DeleteRecordController), SingleRoute(RecordId), result: () => new OkResult());

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(Guid.Empty, write.UserKey);
        Assert.Equal(AuditUserNames.Unknown, write.UserName);
        Assert.True(write.Detail!.UserUnresolved);
    }

    [Fact]
    public async Task Filter_BulkDelete_WritesOneRowWithChildRecordIds() // @M3-T14 P8, replaces Filter_BulkDelete_WritesOneRowPerRecord
    {
        using var h = new CaptureHarness(realWriter: true);
        Guid[] ids = Ids(3);

        await h.RunAsync(typeof(ExecuteActionController), BulkRoute(), arguments: BulkArguments(ids), result: () => new OkResult(),
            duringAction: () => h.DeleteRecords(CaptureHarness.FormId, ids));

        using SqliteFormsAuditDbContext db = h.OpenDb();
        FormAccessEntry row = await db.AccessEntries.Include(a => a.Records).SingleAsync();
        Assert.Equal((int)AccessEventType.Deleted, row.EventType);
        Assert.Null(row.RecordId);
        Assert.Equal(3, row.RecordCount);
        Assert.Equal(CaptureHarness.UserKey, row.UserKey);
        Assert.Equal(ids.Order(), row.Records.Select(r => r.RecordId).Order());
        Assert.Contains("\"bulk\":true", row.Detail);
        Assert.Contains("\"deleteKind\":\"bulk\"", row.Detail);
        Assert.DoesNotContain("recordIdsCaptured", row.Detail);
    }

    [Fact]
    public async Task Filter_BulkDelete_UsesTheIdsFormsDeleted_WhenItPublishedThem() // @M3-T14
    {
        Guid[] requested = Ids(3);

        // S5: the Delete action publishes RecordDeleting twice per record; a key Forms did not find publishes none.
        await _h.RunAsync(typeof(ExecuteActionController), BulkRoute(), arguments: BulkArguments(requested), result: () => new OkResult(),
            duringAction: () =>
            {
                _h.DeleteRecords(CaptureHarness.FormId, requested[0]);
                _h.DeleteRecords(CaptureHarness.FormId, requested[0]);
                _h.DeleteRecords(CaptureHarness.FormId, requested[1]);
                _h.DeleteRecords(CaptureHarness.FormId, requested[1]);
            });

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Equal(new[] { requested[0], requested[1] }.Order(), write.RecordIds.Order());
        Assert.Equal(2, write.RecordCount);
        Assert.Null(write.RecordId);
    }

    [Fact]
    public async Task Filter_BulkDelete_OneKey_IsStillABulkRow() // @M3-T14 the backoffice's single delete is this action (S1)
    {
        await _h.RunAsync(typeof(ExecuteActionController), BulkRoute(), arguments: BulkArguments([RecordId]), result: () => new OkResult(),
            duringAction: () => _h.DeleteRecords(CaptureHarness.FormId, RecordId));

        AccessWrite write = Assert.Single(_h.Writes);
        Assert.Null(write.RecordId);
        Assert.Equal(1, write.RecordCount);
        Assert.Equal([RecordId], write.RecordIds);
        Assert.Equal("bulk", write.Detail!.DeleteKind);
        Assert.True(write.Detail.Bulk);
    }

    [Fact]
    public async Task Filter_BulkDelete_NonDeleteAction_WritesAndScrubsNothing() // @M3-T14 P16
    {
        var route = BulkRoute();
        route["actionId"] = FormsConstants.RecordSetActionTypes.ApproveRecordSet;

        await _h.RunAsync(typeof(ExecuteActionController), route, arguments: BulkArguments(Ids(2)), result: () => new OkResult());

        Assert.Empty(_h.Writes);
        Assert.Empty(_h.ScrubLog.Entries);
    }

    [Theory]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(500, false)]
    [InlineData(0, true)]
    public async Task Filter_Delete_Non2xx_WritesNothingAndScrubsNothing(int status, bool throws) // @M3-T14
    {
        using var h = new CaptureHarness(realWriter: true);
        SeedEdits(h, CaptureHarness.FormId, [RecordId]);

        await h.RunAsync(typeof(DeleteRecordController), SingleRoute(RecordId),
            result: () => new StatusCodeResult(status),
            exception: throws ? new InvalidOperationException("Forms failed") : null,
            duringAction: () => h.DeleteRecords(CaptureHarness.FormId, RecordId));

        using SqliteFormsAuditDbContext db = h.OpenDb();
        Assert.Equal(0, await db.AccessEntries.CountAsync(a => a.EventType == (int)AccessEventType.Deleted));
        FormEntryChange change = Assert.Single(Changes(h, RecordId));
        Assert.Equal("old@example.test", change.OldValue);
        Assert.Null(change.ValuesScrubbedAt);
    }

    // ---- Capture core: in-request scrub ----

    [Fact]
    public async Task Filter_SingleDelete_ScrubsInRequest() // @M3-T14 both S5 branches
    {
        using var h = new CaptureHarness(realWriter: true);
        Guid kept = Guid.NewGuid();
        SeedEdits(h, CaptureHarness.FormId, [RecordId, RecordId, kept]);

        await h.RunAsync(typeof(DeleteRecordController), SingleRoute(RecordId), result: () => new OkResult(),
            duringAction: () => h.DeleteRecords(CaptureHarness.FormId, RecordId));

        // RunAsync returns when the filter does, which is before MVC writes the response.
        List<FormEntryChange> scrubbed = Changes(h, RecordId);
        Assert.Equal(2, scrubbed.Count);
        Assert.All(scrubbed, c =>
        {
            Assert.Null(c.OldValue);
            Assert.Null(c.NewValue);
            Assert.NotNull(c.ValuesScrubbedAt);
            Assert.Equal("Email", c.FieldCaption);
        });
        FormEntryChange untouched = Assert.Single(Changes(h, kept));
        Assert.Equal("old@example.test", untouched.OldValue);
        Assert.Null(untouched.ValuesScrubbedAt);
    }

    [Fact]
    public async Task Filter_BulkDelete_ScrubsInRequest_SetBased() // @M3-T14 both S5 branches, SEC-10
    {
        var counter = new CommandCounter();
        using var h = new CaptureHarness(true, false, counter);
        Guid[] ids = Ids(1200);
        SeedEdits(h, CaptureHarness.FormId, ids);
        counter.Commands.Clear();

        await h.RunAsync(typeof(ExecuteActionController), BulkRoute(), arguments: BulkArguments(ids), result: () => new OkResult(),
            duringAction: () => h.DeleteRecords(CaptureHarness.FormId, ids));

        // 1,200 ids in batches of 500: three statements, never one per record.
        Assert.Equal(3, counter.Commands.Count(c => c.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) && c.Contains("formsAuditTrailEntryChanges")));
        using SqliteFormsAuditDbContext db = h.OpenDb();
        Assert.Equal(1200, await db.EntryChanges.CountAsync(c => c.ValuesScrubbedAt != null && c.OldValue == null && c.NewValue == null));
    }

    [Fact]
    public async Task Filter_Delete_ScrubsOnlyTheRequestFormsEdits() // @M3-T14 a key from another form is not erased by this request
    {
        using var h = new CaptureHarness(realWriter: true);
        Guid otherForm = Guid.NewGuid();
        Guid otherFormRecord = Guid.NewGuid();
        SeedEdits(h, otherForm, [otherFormRecord]);

        // Even if something in the request deleted that record, this request's form does not own it.
        await h.RunAsync(typeof(ExecuteActionController), BulkRoute(), arguments: BulkArguments([otherFormRecord]), result: () => new OkResult(),
            duringAction: () => h.DeleteRecords(otherForm, otherFormRecord));

        FormEntryChange change = Assert.Single(Changes(h, otherFormRecord));
        Assert.Equal("old@example.test", change.OldValue);
        Assert.Null(change.ValuesScrubbedAt);
    }

    [Fact]
    public async Task Filter_Delete_CaptureDisabled_WritesAndScrubsNothing() // @M3-T14 E10: reconciliation still covers it
    {
        using var h = new CaptureHarness(realWriter: true);
        h.Options.EntryAudit.Enabled = false;
        SeedEdits(h, CaptureHarness.FormId, [RecordId]);

        await h.RunAsync(typeof(DeleteRecordController), SingleRoute(RecordId), result: () => new OkResult(),
            duringAction: () => h.DeleteRecords(CaptureHarness.FormId, RecordId));

        Assert.Null(Assert.Single(Changes(h, RecordId)).ValuesScrubbedAt);
    }

    // ---- Fix round 1 (R63) ----

    [Fact]
    public async Task Filter_BulkDelete_FormsDeletedNone_ClaimsAndScrubsNothing() // @M3-T14 R63(1)
    {
        using var h = new CaptureHarness(realWriter: true);
        Guid[] ids = Ids(2);
        SeedEdits(h, CaptureHarness.FormId, ids);

        // Forms returned 200 but published no RecordDeleting: it deleted nothing we can prove.
        await h.RunAsync(typeof(ExecuteActionController), BulkRoute(), arguments: BulkArguments(ids), result: () => new OkResult());

        using SqliteFormsAuditDbContext db = h.OpenDb();
        FormAccessEntry row = await db.AccessEntries.Include(a => a.Records).SingleAsync(a => a.EventType == (int)AccessEventType.Deleted);
        Assert.Equal(0, row.RecordCount);
        Assert.Null(row.RecordId);
        Assert.Empty(row.Records);
        Assert.Contains("\"recordIdsCaptured\":false", row.Detail);
        Assert.Contains("\"requestedCount\":2", row.Detail);
        Assert.All(ids, id => Assert.Null(Assert.Single(Changes(h, id)).ValuesScrubbedAt));
    }

    [Fact]
    public async Task Filter_SingleDelete_FormsDeletedNone_ClaimsAndScrubsNothing() // @M3-T14 R63(1)
    {
        using var h = new CaptureHarness(realWriter: true);
        SeedEdits(h, CaptureHarness.FormId, [RecordId]);

        await h.RunAsync(typeof(DeleteRecordController), SingleRoute(RecordId), result: () => new OkResult());

        using SqliteFormsAuditDbContext db = h.OpenDb();
        FormAccessEntry row = await db.AccessEntries.SingleAsync(a => a.EventType == (int)AccessEventType.Deleted);
        Assert.Equal(0, row.RecordCount);
        Assert.Null(row.RecordId);
        Assert.Contains("\"recordIdsCaptured\":false", row.Detail);
        Assert.Contains("\"requestedCount\":1", row.Detail);
        Assert.Contains("\"deleteKind\":\"single\"", row.Detail);
        Assert.Null(Assert.Single(Changes(h, RecordId)).ValuesScrubbedAt);
    }

    [Fact]
    public async Task Filter_BulkDelete_LargeRandomKeyBody_NoChildRowsAndNoScrub() // @M3-T14 R63(1) SEC-10
    {
        var counter = new CommandCounter();
        using var h = new CaptureHarness(true, false, counter);
        Guid[] keys = Ids(5000);

        await h.RunAsync(typeof(ExecuteActionController), BulkRoute(), arguments: BulkArguments(keys), result: () => new OkResult());

        Assert.Equal(0, counter.Count("UPDATE"));
        using SqliteFormsAuditDbContext db = h.OpenDb();
        Assert.Equal(0, await db.AccessRecords.CountAsync());
        FormAccessEntry row = await db.AccessEntries.SingleAsync();
        Assert.Equal(0, row.RecordCount);
        Assert.Contains("\"requestedCount\":5000", row.Detail);
    }

    [Fact]
    public async Task Filter_BulkDelete_Non2xxAfterFormsStartedDeleting_RecordsHealthFailureAndScrubsNothing() // @M3-T14 R63(3)
    {
        using var h = new CaptureHarness(realWriter: true);
        Guid[] ids = Ids(2);
        SeedEdits(h, CaptureHarness.FormId, ids);

        // The first record went, then Forms failed: some entries may be gone with no Deleted row to show it.
        await h.RunAsync(typeof(ExecuteActionController), BulkRoute(), arguments: BulkArguments(ids),
            exception: new InvalidOperationException("Forms failed partway"),
            duringAction: () => h.DeleteRecords(CaptureHarness.FormId, ids[0]));

        Assert.Equal(1, h.Health.WriteFailuresSinceStart);
        Assert.All(ids, id => Assert.Null(Assert.Single(Changes(h, id)).ValuesScrubbedAt));
        using (SqliteFormsAuditDbContext db = h.OpenDb())
        {
            Assert.Equal(0, await db.AccessEntries.CountAsync(a => a.EventType == (int)AccessEventType.Deleted));
        }

        // The next stored write carries the gap, so the failed delete leaves a durable trace.
        await h.RunAsync(typeof(DeleteRecordController), SingleRoute(Guid.NewGuid()), result: () => new OkResult());
        using SqliteFormsAuditDbContext after = h.OpenDb();
        FormAccessEntry gap = await after.AccessEntries.SingleAsync(a => a.EventType == (int)AccessEventType.AuditGap);
        Assert.Contains("Deleted", gap.Detail);
    }

    [Fact]
    public async Task Filter_Delete_Non2xx_NothingDeleted_RecordsNoHealthFailure() // @M3-T14 R63(3) a refused delete is not a gap
    {
        await _h.RunAsync(typeof(DeleteRecordController), SingleRoute(RecordId), result: () => new StatusCodeResult(403));

        Assert.Equal(0, _h.Health.WriteFailuresSinceStart);
        Assert.Empty(_h.Writes);
    }
}
