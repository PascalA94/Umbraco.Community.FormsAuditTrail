using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services;
using FormsRecord = Umbraco.Forms.Core.Persistence.Dtos.Record;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;

namespace Umbraco.Community.FormsAuditTrail.DemoSite;

/// <summary>
/// Test-only helpers for the load check: bulk-seeds access rows (the CSV cap is 100,000, which no UI path produces in
/// reasonable time) and reports the process's memory so the suite can show that a large export streams instead of
/// buffering. Same gate as <see cref="E2EDiagnosticsController"/>: dropped from the controller list unless
/// <c>E2E:Diagnostics</c> is true on a Development host, 404 at request time otherwise, administrators only. It lives in
/// the demo site, which is never packed, and returns counts and numbers, never stored values.
/// </summary>
[ApiController]
[Route("e2e/load")]
[Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
public sealed class E2ELoadController(
    FormsAuditDbContext db,
    IFormService forms,
    IRecordStorage records,
    IScopeProvider scopeProvider,
    IWebHostEnvironment env,
    IConfiguration config) : ControllerBase, IActionFilter
{
    internal const string SeedFormName = "E2E load seed";
    internal const string SeedUserName = "E2E load seed";

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!E2EGate.IsEnabled(env, config, E2EGate.DiagnosticsKey))
        {
            context.Result = NotFound();
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
    }

    /// <summary>
    /// Removes one entry through Forms' storage layer, which deletes its child rows (field values) as well and publishes
    /// no notification: an erasure the package is not told about. A raw <c>DELETE FROM UFRecords</c> cannot do this on a
    /// database that enforces the child tables' foreign keys.
    /// </summary>
    [HttpPost("erase-record")]
    public IActionResult EraseRecord([FromQuery] Guid formId, [FromQuery] Guid recordId)
    {
        Form? form = forms.Get(formId);
        if (form is null) { return NotFound("No such form."); }

        using IScope scope = scopeProvider.CreateScope(autoComplete: true);
        FormsRecord? record = records.GetRecordByUniqueId(recordId, form);
        if (record is null) { return NotFound("No such entry."); }

        records.DeleteRecord(record, form);
        return Ok(new { deleted = 1 });
    }

    /// <summary>Inserts <c>count</c> ListViewed rows, newest first in time, in batches so no single statement is huge.</summary>
    [HttpPost("seed-access")]
    public async Task<IActionResult> SeedAccess([FromQuery] int count, [FromQuery] Guid formId, CancellationToken ct)
    {
        if (count is < 1 or > 500_000) { return BadRequest("count must be between 1 and 500000."); }

        // Guid.Empty is the package's own "no form" marker for real rows, so seeded rows never use it.
        if (formId == Guid.Empty) { return BadRequest("formId must be a form id, not an empty Guid."); }

        const int batch = 5_000;
        var watch = Stopwatch.StartNew();
        bool tracking = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            DateTime start = DateTime.UtcNow.AddDays(-2);
            for (int done = 0; done < count; done += batch)
            {
                int n = Math.Min(batch, count - done);
                var rows = new List<FormAccessEntry>(n);
                for (int i = 0; i < n; i++)
                {
                    rows.Add(new FormAccessEntry
                    {
                        Timestamp = start.AddMilliseconds(done + i),
                        EventType = (int)AccessEventType.ListViewed,
                        FormId = formId,
                        FormName = SeedFormName,
                        RecordCount = 100,
                        UserKey = Guid.Empty,
                        UserName = SeedUserName,
                        Detail = "{\"v\":1}",
                    });
                }

                db.AccessEntries.AddRange(rows);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = tracking;
        }

        return Ok(new { inserted = count, milliseconds = watch.ElapsedMilliseconds });
    }

    [HttpDelete("seed-access")]
    public async Task<IActionResult> DeleteSeededAccess(CancellationToken ct)
    {
        int deleted = await db.AccessEntries.Where(e => e.FormName == SeedFormName && e.UserName == SeedUserName && e.UserKey == Guid.Empty).ExecuteDeleteAsync(ct);
        return Ok(new { deleted });
    }

    /// <summary>One memory sample. <c>gc=true</c> forces a blocking collection first, for a clean baseline.</summary>
    [HttpGet("memory")]
    public IActionResult Memory([FromQuery] bool gc = false)
    {
        if (gc)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        using Process self = Process.GetCurrentProcess();
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        return Ok(new
        {
            workingSetBytes = self.WorkingSet64,
            managedHeapBytes = GC.GetTotalMemory(false),
            committedBytes = info.TotalCommittedBytes,
            gen2Collections = GC.CollectionCount(2),
        });
    }
}

/// <summary>Registers the feature provider that hides <see cref="E2ELoadController"/> unless the gate is open.</summary>
public sealed class E2ELoadComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        bool open = E2EGate.IsEnabled(builder, E2EGate.DiagnosticsKey);
        builder.Services.AddMvcCore().ConfigureApplicationPartManager(m => m.FeatureProviders.Add(new E2ELoadControllerGate(open)));
    }
}

internal sealed class E2ELoadControllerGate(bool open) : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        if (open)
        {
            return;
        }

        foreach (var type in feature.Controllers.Where(t => t.AsType() == typeof(E2ELoadController)).ToList())
        {
            feature.Controllers.Remove(type);
        }
    }
}
