using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class ErasureScrubberSqlServerTests
{
    private static readonly Guid FormA = Guid.NewGuid();
    private static readonly Guid FormB = Guid.NewGuid();
    private static readonly Guid FormC = Guid.NewGuid();
    private static readonly Guid Deleted1 = Guid.NewGuid();
    private static readonly Guid Deleted2 = Guid.NewGuid();
    private static readonly Guid Kept = Guid.NewGuid();

    // The scrub predicates join EntryChanges to AccessEntries and filter on a nullable form id; SQLite proves the logic,
    // this proves SQL Server translates and runs the same ExecuteUpdate statements (merge-fix 14, R63).
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Scrub_FormRestrictedRecordsThenReconciliationThenWholeForm_OnSqlServer() // @M3-T14 R63
    {
        using var database = SqlServerTestDatabase.Create();
        await using (SqlServerFormsAuditDbContext seed = database.CreateContext())
        {
            await seed.Database.MigrateAsync();
            seed.AccessEntries.AddRange(
                Edit(FormA, Deleted1),
                Edit(FormA, Deleted2),
                Edit(FormA, Kept),
                // The same record id under another form: a delete request naming FormA must not erase it.
                Edit(FormB, Deleted1),
                Edit(FormC, Guid.NewGuid()),
                Edit(FormC, Guid.NewGuid()));
            await seed.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped<FormsAuditDbContext>(_ => database.CreateContext());
        using ServiceProvider provider = services.BuildServiceProvider();
        var scrubber = new DeletedRecordScrubber(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<DeletedRecordScrubber>.Instance);

        // 1. The in-request scrub (Task 14 path) is restricted to the request's form.
        Assert.True(await scrubber.ScrubAsync(FormA, [Deleted1, Deleted2], Guid.NewGuid()));
        Assert.Equal(
            [(FormA, Deleted1, true), (FormA, Deleted2, true), (FormA, Kept, false), (FormB, Deleted1, false)],
            await StateAsync(database, FormA, FormB));

        // 2. Reconciliation has checked the record is gone, so it passes no form and catches the other form's copy.
        await using (SqlServerFormsAuditDbContext db = database.CreateContext())
        {
            Assert.Equal(1, await ErasureScrubber.ScrubRecordsAsync(db, [Deleted1], DateTime.UtcNow, CancellationToken.None));
        }

        Assert.Equal(
            [(FormA, Deleted1, true), (FormA, Deleted2, true), (FormA, Kept, false), (FormB, Deleted1, true)],
            await StateAsync(database, FormA, FormB));

        // 3. A permanent form delete scrubs that form only.
        await using (SqlServerFormsAuditDbContext db = database.CreateContext())
        {
            Assert.Equal(2, await ErasureScrubber.ScrubFormAsync(db, FormC, DateTime.UtcNow, CancellationToken.None));
        }

        await using SqlServerFormsAuditDbContext read = database.CreateContext();
        List<FormEntryChange> all = await read.EntryChanges.Include(c => c.Access).ToListAsync();
        Assert.All(all.Where(c => c.Access!.FormId == FormC), c => Assert.True(c.ValuesScrubbedAt is not null && c.OldValue is null && c.NewValue is null));
        FormEntryChange kept = Assert.Single(all, c => c.Access!.RecordId == Kept);
        Assert.Equal(("old", "new", null), (kept.OldValue, kept.NewValue, kept.ValuesScrubbedAt));
    }

    private static FormAccessEntry Edit(Guid formId, Guid recordId) => new()
    {
        Timestamp = DateTime.UtcNow,
        EventType = (int)AccessEventType.Edited,
        FormId = formId,
        FormName = "Form",
        RecordId = recordId,
        RecordCount = 1,
        UserKey = Guid.NewGuid(),
        UserName = "Editor",
        Changes = [new FormEntryChange { FieldId = Guid.NewGuid(), FieldCaption = "Name", OldValue = "old", NewValue = "new" }],
    };

    private static async Task<List<(Guid FormId, Guid RecordId, bool Scrubbed)>> StateAsync(SqlServerTestDatabase database, params Guid[] forms)
    {
        await using SqlServerFormsAuditDbContext read = database.CreateContext();
        var rows = await read.EntryChanges
            .Where(c => forms.Contains(c.Access!.FormId))
            .Select(c => new { c.Access!.FormId, RecordId = c.Access.RecordId!.Value, c.ValuesScrubbedAt, c.OldValue, c.NewValue })
            .ToListAsync();

        // A scrubbed row has no values left; a kept row still has both.
        Assert.All(rows, r => Assert.Equal(r.ValuesScrubbedAt is null, r.OldValue is not null && r.NewValue is not null));
        int Order(Guid f, Guid r) => Array.IndexOf(forms, f) * 10 + (r == Deleted1 ? 0 : r == Deleted2 ? 1 : 2);
        return rows
            .OrderBy(r => Order(r.FormId, r.RecordId))
            .Select(r => (r.FormId, r.RecordId, r.ValuesScrubbedAt is not null))
            .ToList();
    }
}
