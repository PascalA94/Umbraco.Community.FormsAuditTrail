using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.EntityFrameworkCore;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Infrastructure.BackgroundJobs;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Community.FormsAuditTrail.BackgroundJobs;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services;

namespace Umbraco.Community.FormsAuditTrail.DemoSite;

/// <summary>
/// Test-only endpoints that let the Playwright suite look at, and break, the real tables the package works against:
/// a scan of the audit tables for a value that must never be stored, a one-off retention run, and direct changes to
/// <c>UFRecords</c> that Forms itself would never make (a delete with no notification, a table that cannot be read).
/// It lives in the demo site, which is never packed, and is dropped from the application's controller list unless
/// <c>E2E:Diagnostics</c> is true on a Development host (see <see cref="E2EControllerGate"/>). Responses carry table and
/// column names and counts, never stored values.
/// </summary>
[ApiController]
[Route("e2e")]
// Umbraco's own administrator policy: the backoffice scheme plus the Administrators group.
[Authorize(Policy = AuthorizationPolicies.RequireAdminAccess)]
public sealed class E2EDiagnosticsController(
    FormsAuditDbContext db,
    IEnumerable<IRecurringBackgroundJob> jobs,
    IServiceProvider services,
    IWebHostEnvironment env,
    IConfiguration config) : ControllerBase, IActionFilter
{
    // Second gate, at request time: outside Development, or with the flag off, the action never runs.
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

    private const string BrokenSuffix = "_e2e_broken";

    private static readonly string[] AccessTables =
    [
        FormsAuditDbContext.AccessTableName,
        FormsAuditDbContext.AccessRecordsTableName,
        FormsAuditDbContext.EntryChangesTableName,
        FormsAuditDbContext.StateTableName,
    ];

    [HttpGet("scan")]
    public async Task<IActionResult> Scan([FromQuery] string needle, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(needle)) { return BadRequest("needle is required."); }

        DbConnection connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var hits = new List<object>();
            foreach (string table in AccessTables)
            {
                foreach (string column in await TextColumnsAsync(connection, table, ct))
                {
                    await using DbCommand command = connection.CreateCommand();
                    // Table and column names come from the database's own catalogue, never from the request.
                    command.CommandText = $"SELECT COUNT(*) FROM [{table}] WHERE [{column}] LIKE @needle ESCAPE '\\'";
                    DbParameter p = command.CreateParameter();
                    p.ParameterName = "@needle";
                    p.Value = "%" + needle.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                    command.Parameters.Add(p);
                    long count = Convert.ToInt64(await command.ExecuteScalarAsync(ct));
                    if (count > 0)
                    {
                        hits.Add(new { table, column, count });
                    }
                }
            }

            return Ok(hits);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    [HttpPost("run-retention-job")]
    public async Task<IActionResult> RunRetentionJob(CancellationToken ct)
    {
        AuditRetentionJob job = jobs.OfType<AuditRetentionJob>().Single();
        await job.RunJobAsync();
        // The literal is the job's own state key; its constant and the state store are internal to the package, so the
        // row is read through the public context. AsNoTracking so the job's write from its own scope is what comes back.
        string? lastRun = await db.State.AsNoTracking()
            .Where(s => s.Key == "job.lastRun")
            .Select(s => s.Value)
            .SingleOrDefaultAsync(ct);
        return Content(lastRun ?? "null", "application/json");
    }

    [HttpPost("reseed-encrypted")]
    public IActionResult ReseedEncrypted()
    {
        // Only present when the seed flag is on too.
        E2ESeeder? seeder = services.GetService<E2ESeeder>();
        if (seeder is null) { return Conflict("E2E:Seed is not enabled."); }

        return seeder.ResetEncryptedRecord()
            ? NoContent()
            : Conflict("E2E Encrypted does not exist; start the site with E2E:SimulateEncryption=true as well as E2E:Seed.");
    }

    [HttpPost("ufrecords/delete")]
    public IActionResult DeleteUfRecord([FromQuery] Guid recordId)
    {
        // Through Forms' storage rather than SQL on UFRecords: storage removes the record's field rows too, where a bare
        // DELETE fails on SQLite's foreign keys (500) once the entry has values. It stands for an entry removed outside
        // the backoffice, so it bypasses the backoffice controllers the capture filter watches.
        IFormService forms = services.GetRequiredService<IFormService>();
        IRecordStorage storage = services.GetRequiredService<IRecordStorage>();
        using ICoreScope scope = services.GetRequiredService<ICoreScopeProvider>().CreateCoreScope();
        var deleted = 0;
        foreach (FormSlim slim in forms.GetSlim())
        {
            if (forms.Get(slim.Id) is { } form && storage.GetRecordByUniqueId(recordId, form) is { } record)
            {
                storage.DeleteRecord(record, form);
                deleted = 1;
                break;
            }
        }

        scope.Complete();
        return Ok(new { deleted });
    }

    [HttpPost("ufrecords/break")]
    public async Task<IActionResult> BreakUfRecords(CancellationToken ct)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE UFRecords RENAME TO UFRecords{BrokenSuffix}", ct);
        }
        else
        {
            // Needs a principal that is not sysadmin or db_owner, which DENY does not bind; the SQL Server run says so.
            // Named principal through QUOTENAME so the statement is explicit about who is denied. DENY does not bind dbo
            // or a sysadmin login: the run must connect as a plain database user (Task 18b).
            await db.Database.ExecuteSqlRawAsync("DECLARE @u sysname = QUOTENAME(USER_NAME()); EXEC(N'DENY SELECT ON OBJECT::UFRecords TO ' + @u);", ct);
        }

        return NoContent();
    }

    [HttpPost("ufrecords/restore")]
    public async Task<IActionResult> RestoreUfRecords(CancellationToken ct)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE UFRecords{BrokenSuffix} RENAME TO UFRecords", ct);
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync("DECLARE @u sysname = QUOTENAME(USER_NAME()); EXEC(N'REVOKE SELECT ON OBJECT::UFRecords FROM ' + @u);", ct);
        }

        return NoContent();
    }

    private static async Task<List<string>> TextColumnsAsync(DbConnection connection, string table, CancellationToken ct)
    {
        var columns = new List<string>();
        await using DbCommand command = connection.CreateCommand();
        bool sqlite = connection.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        command.CommandText = sqlite
            ? $"SELECT name FROM pragma_table_info('{table}') WHERE UPPER(type) LIKE '%TEXT%' OR UPPER(type) LIKE '%CHAR%'"
            : $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = '{table}' AND DATA_TYPE IN ('nvarchar','varchar','nchar','char','ntext','text')";
        await using DbDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }
}

/// <summary>
/// Removes <see cref="E2EDiagnosticsController"/> from the controller list unless the gate is open. A controller that
/// is not in the list has no route, so a production site answers 404 to <c>/e2e/*</c> rather than 401 or 403 (which
/// would confirm the endpoint exists). Registered always; it is the gate that decides.
/// </summary>
internal sealed class E2EControllerGate(bool open) : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        if (open)
        {
            return;
        }

        foreach (var type in feature.Controllers.Where(t => t.AsType() == typeof(E2EDiagnosticsController)).ToList())
        {
            feature.Controllers.Remove(type);
        }
    }
}
