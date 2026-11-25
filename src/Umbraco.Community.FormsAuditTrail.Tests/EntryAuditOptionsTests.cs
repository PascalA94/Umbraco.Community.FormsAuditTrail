using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class EntryAuditOptionsTests
{
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<(LogLevel, string)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static ServiceProvider Build(Dictionary<string, string?> values, CapturingLoggerProvider? logs = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            if (logs is not null)
            {
                b.AddProvider(logs);
            }
        });

        // Same registration the composer uses, so binding, post-configure and validation are the shipped ones.
        FormsAuditTrailOptionsRegistration.Add(services, config);
        return services.BuildServiceProvider();
    }

    private static FormsAuditTrailOptions Resolve(Dictionary<string, string?> values, CapturingLoggerProvider? logs = null) =>
        Build(values, logs).GetRequiredService<IOptions<FormsAuditTrailOptions>>().Value;

    [Fact]
    public void Bind_WithNoEntryAuditSection_AppliesSpecDefaults()
    {
        var o = Resolve([]);

        Assert.True(o.EntryAudit.Enabled);
        Assert.True(o.EntryAudit.LogListViews);
        Assert.False(o.EntryAudit.LogListRecordIds);
        Assert.Equal(60, o.EntryAudit.DeduplicateSeconds);
        Assert.Equal(StoreEditValuesMode.MaskSensitive, o.EntryAudit.StoreEditValues);
        Assert.Equal(365, o.EntryAudit.RetentionDays);
        Assert.Equal(90, o.EntryAudit.EditValueRetentionDays);
        Assert.False(o.EntryAudit.CaptureSearchTerms);
        Assert.Equal(["admin"], o.EntryAudit.AccessLogUserGroups);
        Assert.False(o.EntryAudit.KeepClearedValues);
        Assert.False(o.EntryAudit.FailClosedOnExport);
        Assert.False(o.EntryAudit.EmitLogEvents);
        Assert.False(o.EntryAudit.EmitLogEventRecordIds);
        Assert.Null(o.EntryAudit.EnableCsvExport);
        Assert.Equal(0, o.RetentionDays);
        Assert.True(o.EnableCsvExport);
        Assert.True(o.EntryAuditCsvEnabled);
    }

    [Fact]
    public void Bind_WithConfiguredGroups_DoesNotAppendAdmin()
    {
        var o = Resolve(new() { ["FormsAuditTrail:EntryAudit:AccessLogUserGroups:0"] = "editors" });

        Assert.Equal(["editors"], o.EntryAudit.AccessLogUserGroups);
    }

    [Theory]
    [InlineData("RetentionDays")]
    [InlineData("EditValueRetentionDays")]
    [InlineData("DeduplicateSeconds")]
    public void Validate_RejectsNegative(string key)
    {
        var sp = Build(new() { [$"FormsAuditTrail:EntryAudit:{key}"] = "-1" });
        var options = sp.GetRequiredService<IOptions<FormsAuditTrailOptions>>();

        var ex = Assert.Throws<OptionsValidationException>(() => options.Value);
        Assert.Contains(key, string.Join(' ', ex.Failures));
    }

    [Fact]
    public void Bind_NegativeDefinitionRetentionDays_CoercedToZeroWithWarning()
    {
        var logs = new CapturingLoggerProvider();

        var o = Resolve(new() { ["FormsAuditTrail:RetentionDays"] = "-1" }, logs);

        Assert.Equal(0, o.RetentionDays);
        var warning = Assert.Single(logs.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("FormsAuditTrail:RetentionDays", warning.Message);
    }

    [Theory]
    [InlineData("None", StoreEditValuesMode.None)]
    [InlineData("All", StoreEditValuesMode.All)]
    public void Bind_StoreEditValuesFromString(string value, StoreEditValuesMode expected)
    {
        var o = Resolve(new() { ["FormsAuditTrail:EntryAudit:StoreEditValues"] = value });

        Assert.Equal(expected, o.EntryAudit.StoreEditValues);
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    public void EntryAuditCsvEnabled_FollowsEnableCsvExportUnlessSet(bool definition, bool? entryAudit, bool expected)
    {
        var o = new FormsAuditTrailOptions
        {
            EnableCsvExport = definition,
            EntryAudit = new EntryAuditOptions { EnableCsvExport = entryAudit },
        };

        Assert.Equal(expected, o.EntryAuditCsvEnabled);
    }

    [Fact]
    public void RecordedKeys_CoverEveryEntryAuditProperty()
    {
        var properties = typeof(EntryAuditOptions).GetProperties().Select(p => p.Name).Order().ToList();

        Assert.Equal(properties, EntryAuditOptions.RecordedKeys.Order().ToList());
    }
}
