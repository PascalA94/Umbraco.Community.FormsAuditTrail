using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Forms.Web.Api.ManagementApi.Record;
using Umbraco.Forms.Web.Models.Backoffice;
using Xunit;
using FormsConstants = Umbraco.Forms.Core.Constants;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// P8, SEC-10: a bulk delete of thousands of entries costs one parent row, a few child batches and a few scrub
/// statements, through the real capture core, the real singleton writer and a SQLite file.
/// </summary>
public sealed class BulkDeleteVolumeTests
{
    [Fact]
    public async Task Filter_BulkDelete5000_OneParentRowWithChildren_WithinBounds() // @M3-T14 P8 SEC-10
    {
        var saves = new SaveProbe();
        var commands = new CommandCounter();
        using var h = new CaptureHarness(true, false, saves, commands);
        Guid[] ids = Enumerable.Range(0, 5000).Select(_ => Guid.NewGuid()).ToArray();
        using (SqliteFormsAuditDbContext seed = h.OpenDb())
        {
            foreach (Guid id in ids)
            {
                seed.AccessEntries.Add(new FormAccessEntry
                {
                    Timestamp = DateTime.UtcNow.AddDays(-1),
                    EventType = (int)AccessEventType.Edited,
                    FormId = CaptureHarness.FormId,
                    FormName = "Contact",
                    RecordId = id,
                    RecordCount = 1,
                    UserKey = Guid.NewGuid(),
                    UserName = "Editor",
                    Changes = { new FormEntryChange { FieldId = Guid.NewGuid(), FieldCaption = "Email", OldValue = "a", NewValue = "b" } },
                });
            }

            await seed.SaveChangesAsync();
        }

        saves.Reset();
        commands.Commands.Clear();
        var route = new Dictionary<string, object?>
        {
            ["formId"] = CaptureHarness.FormId.ToString(),
            ["actionId"] = FormsConstants.RecordSetActionTypes.DeleteRecordSet,
        };
        var arguments = new Dictionary<string, object?> { ["model"] = new RecordActionExecution { RecordKeys = ids } };

        var clock = Stopwatch.StartNew();
        await h.RunAsync(typeof(ExecuteActionController), route, arguments: arguments, result: () => new OkResult(),
            duringAction: () => h.DeleteRecords(CaptureHarness.FormId, ids));
        clock.Stop();

        Assert.Equal(1, saves.ParentSaves);
        // 5,000 child ids at 1,000 per save (Task 11b).
        Assert.Equal(5, saves.ChildSaves);
        // 5,000 ids at 500 per statement (ErasureScrubber), and no other UPDATE: nothing is updated per record.
        // The child rows are five saves, but on SQLite EF sends one INSERT per row inside each save (it does not batch
        // for this provider; SQL Server batches). That is the writer's shape from Task 11b, bounded by the 10 s check.
        Assert.Equal(10, commands.Count("UPDATE"));
        Assert.Equal(0, commands.Count("DELETE"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");

        using SqliteFormsAuditDbContext db = h.OpenDb();
        FormAccessEntry parent = await db.AccessEntries.SingleAsync(a => a.EventType == (int)AccessEventType.Deleted);
        Assert.Equal(5000, parent.RecordCount);
        Assert.Null(parent.RecordId);
        Assert.Equal(5000, await db.AccessRecords.CountAsync(r => r.AccessId == parent.Id));
        Assert.Equal(5000, await db.EntryChanges.CountAsync(c => c.ValuesScrubbedAt != null && c.OldValue == null));
    }

    /// <summary>Counts saves that add a parent access row and saves that add child record rows.</summary>
    private sealed class SaveProbe : SaveChangesInterceptor
    {
        private int _parentSaves;
        private int _childSaves;

        public int ParentSaves => _parentSaves;
        public int ChildSaves => _childSaves;

        public void Reset() => (_parentSaves, _childSaves) = (0, 0);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<FormAccessEntry>().Any(e => e.State == EntityState.Added))
            {
                Interlocked.Increment(ref _parentSaves);
            }

            if (eventData.Context.ChangeTracker.Entries<FormAccessRecord>().Any(e => e.State == EntityState.Added))
            {
                Interlocked.Increment(ref _childSaves);
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
