using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Forms.Core.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class EntryAuditWriterSqlServerTests
{
    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Write_OverLengthStrings_TruncatesAndWrites_OnSqlServer() // T4, Review Focus 6
    {
        using var database = SqlServerTestDatabase.Create();
        await using (SqlServerFormsAuditDbContext migrate = database.CreateContext())
        {
            await migrate.Database.MigrateAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped<FormsAuditDbContext>(_ => database.CreateContext());
        services.AddScoped(_ => Substitute.For<IFormService>());
        using ServiceProvider provider = services.BuildServiceProvider();
        IOptionsMonitor<FormsAuditTrailOptions> options = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        options.CurrentValue.Returns(new FormsAuditTrailOptions());
        using var dedupe = new EntryAuditDeduplicator(options, TimeProvider.System);
        var health = new EntryAuditHealth(Substitute.For<IAuditStateStore>(), TimeProvider.System);
        var writer = new EntryAuditWriter(provider.GetRequiredService<IServiceScopeFactory>(), dedupe, health, options, TimeProvider.System, NullLogger<EntryAuditWriter>.Instance);
        string longText = new('y', 600);

        AccessWriteResult result = await writer.WriteAsync(new AccessWrite(
            AccessEventType.Edited, Guid.NewGuid(), longText, Guid.NewGuid(), 1, Guid.NewGuid(), longText, null, [],
            [new EntryFieldChange(Guid.NewGuid(), longText, "a", "b", false, false)]));

        Assert.Equal(AccessWriteStatus.Written, result.Status);
        await using SqlServerFormsAuditDbContext read = database.CreateContext();
        FormAccessEntry row = await read.AccessEntries.Include(a => a.Changes).SingleAsync();
        Assert.Equal(AccessColumnLimits.FormName, row.FormName.Length);
        Assert.Equal(AccessColumnLimits.UserName, row.UserName.Length);
        Assert.Equal(AccessColumnLimits.FieldCaption, row.Changes.Single().FieldCaption.Length);
    }

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task Write_Export50k_StoresAll_OnSqlServer() // P8
    {
        using var database = SqlServerTestDatabase.Create();
        await using (SqlServerFormsAuditDbContext migrate = database.CreateContext())
        {
            await migrate.Database.MigrateAsync();
        }

        var services = new ServiceCollection();
        services.AddScoped<FormsAuditDbContext>(_ => database.CreateContext());
        services.AddScoped(_ => Substitute.For<IFormService>());
        using ServiceProvider provider = services.BuildServiceProvider();
        IOptionsMonitor<FormsAuditTrailOptions> options = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        options.CurrentValue.Returns(new FormsAuditTrailOptions());
        using var dedupe = new EntryAuditDeduplicator(options, TimeProvider.System);
        var health = new EntryAuditHealth(Substitute.For<IAuditStateStore>(), TimeProvider.System);
        var writer = new EntryAuditWriter(provider.GetRequiredService<IServiceScopeFactory>(), dedupe, health, options, TimeProvider.System, NullLogger<EntryAuditWriter>.Instance);
        Guid[] ids = Enumerable.Range(0, 50_000).Select(_ => Guid.NewGuid()).ToArray();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        AccessWriteResult result = await writer.WriteAsync(new AccessWrite(
            AccessEventType.Exported, Guid.NewGuid(), "Form", null, ids.Length, Guid.NewGuid(), "Editor",
            new EntryAuditDetail { FileName = "big.csv" }, ids, []));
        clock.Stop();

        Assert.Equal(AccessWriteStatus.Written, result.Status);
        Assert.True(result.RecordIdsCaptured);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"took {clock.Elapsed}");
        await using SqlServerFormsAuditDbContext read = database.CreateContext();
        Assert.Equal(50_000, await read.AccessRecords.CountAsync(r => r.AccessId == result.AccessId));
        Assert.NotNull((await read.AccessEntries.SingleAsync(a => a.Id == result.AccessId)).CorrelationId);
    }
}
