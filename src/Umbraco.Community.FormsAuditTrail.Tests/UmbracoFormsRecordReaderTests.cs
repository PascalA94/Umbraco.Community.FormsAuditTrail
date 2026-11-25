using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Enums;
using Umbraco.Forms.Core.Models;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// Runs the real reader through a real Umbraco scope against a real <c>UFRecords</c> table, with rows written by
/// Forms' own <c>IRecordStorage</c>. A wrong match here is the dangerous failure: reconciliation reads "not found"
/// as "erased" and scrubs live data, so the reader must be proved against what Forms actually stores (SEC-I1).
/// </summary>
// One real Umbraco runtime at a time: the runtime keeps process-wide state.
[Collection("UmbracoHost")]
public class UmbracoFormsRecordReaderTests
{
    [Fact]
    public async Task FindExisting_PresentFoundAbsentNot_OnSqlite()
    {
        var folder = Path.Combine(Path.GetTempPath(), "formsaudit-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var connection = $"Data Source={Path.Combine(folder, "Umbraco.sqlite.db")};Cache=Private;Foreign Keys=True;Pooling=False";
            await using var host = await UmbracoFormsHost.StartAsync("Microsoft.Data.Sqlite", connection);

            await ExerciseAsync(host, isSqlite: true);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: a lingering handle must not fail a passed test.
            }
        }
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task FindExisting_PresentFoundAbsentNot_OnSqlServer()
    {
        using var database = SqlServerTestDatabase.Create();
        await using var host = await UmbracoFormsHost.StartAsync("Microsoft.Data.SqlClient", database.ConnectionString);

        await ExerciseAsync(host, isSqlite: false);
    }

    private static async Task ExerciseAsync(UmbracoFormsHost host, bool isSqlite)
    {
        using IServiceScope scope = host.App.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        var storage = services.GetRequiredService<IRecordStorage>();
        var scopes = services.GetRequiredService<IScopeProvider>();
        var form = new Form { Id = Guid.NewGuid(), Name = "Reader test" };

        var reader = services.GetRequiredService<IFormsRecordReader>();

        // The probe's empty-table answer, before Forms has written anything.
        Assert.Null(await reader.FindAnyExistingIdAsync(CancellationToken.None));

        Guid[] stored = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        foreach (Guid id in stored)
        {
            storage.InsertRecord(
                new Umbraco.Forms.Core.Persistence.Dtos.Record { Form = form.Id, UniqueId = id, State = FormState.Approved, Created = DateTime.UtcNow, Updated = DateTime.UtcNow },
                form);
        }

        if (isSqlite)
        {
            using IScope inspect = scopes.CreateScope(autoComplete: true);
            List<string> texts = inspect.Database.Fetch<string>("SELECT UniqueId FROM UFRecords");
            // What Forms really writes on SQLite: 36-character upper-case "D" text.
            Assert.Equal(stored.Select(id => id.ToString("D").ToUpperInvariant()).Order(), texts.Order());

            // A row written in lower case by something other than Forms must still be found.
            inspect.Database.Execute("UPDATE UFRecords SET UniqueId = lower(UniqueId) WHERE UniqueId = @0", stored[2].ToString("D").ToUpperInvariant());
        }

        // More absent ids than one batch, so the batching path runs too.
        Guid[] absent = Enumerable.Range(0, UmbracoFormsRecordReader.BatchSize * 2 + 10).Select(_ => Guid.NewGuid()).ToArray();
        // The probe returns a row Forms wrote, and the matching path finds it: the self-test reconciliation relies on.
        Guid? probe = await reader.FindAnyExistingIdAsync(CancellationToken.None);
        Assert.NotNull(probe);
        Assert.Contains(probe.Value, stored);
        Assert.Contains(probe.Value, await reader.FindExistingAsync([probe.Value], CancellationToken.None));

        IReadOnlySet<Guid> found = await reader.FindExistingAsync(stored.Concat(absent).ToArray(), CancellationToken.None);

        Assert.Equal(stored.Order(), found.Order());
        Assert.Empty(await reader.FindExistingAsync(absent, CancellationToken.None));
    }
}
