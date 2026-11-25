using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.Authorization;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Controllers;
using Umbraco.Community.FormsAuditTrail.Controllers.Dtos;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Forms.Core.Security;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public sealed class ConfigChangeRecorderTests : IDisposable
{
    private readonly FormsAuditTrailOptions _options = new();
    private readonly IOptionsMonitor<FormsAuditTrailOptions> _monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
    private readonly FakeStateStore _state = new();
    private readonly IEntryAuditWriter _writer = Substitute.For<IEntryAuditWriter>();
    private readonly List<AccessWrite> _writes = [];
    private readonly ListLogger<ConfigChangeRecorder> _logger = new();
    private readonly EntryAuditHealth _health;
    private readonly TestDatabase _db = new();
    private readonly SettableTime _time = new();
    private readonly IRuntimeState _runtime = Substitute.For<IRuntimeState>();
    private Action<FormsAuditTrailOptions, string?>? _onChange;
    private ConfigChangeRecorder? _sut;

    public ConfigChangeRecorderTests()
    {
        _runtime.Level.Returns(RuntimeLevel.Run);
        _monitor.CurrentValue.Returns(_options);
        _monitor.OnChange(Arg.Do<Action<FormsAuditTrailOptions, string?>>(a => _onChange = a)).Returns(Substitute.For<IDisposable>());
        _health = new EntryAuditHealth(_state, _time);
        _writer.WriteAsync(Arg.Any<AccessWrite>()).Returns(call =>
        {
            _writes.Add(call.Arg<AccessWrite>());
            return new AccessWriteResult(AccessWriteStatus.Written, _writes.Count, false);
        });
    }

    private ConfigChangeRecorder Recorder() =>
        _sut ??= new ConfigChangeRecorder(_monitor, _state, _writer, _health, _runtime, _time, _logger);

    private async Task StartAsync() =>
        await Recorder().HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);

    private async Task FireChangeAsync()
    {
        Assert.NotNull(_onChange);
        _onChange!(_options, null);
        await Recorder().IdleAsync();
    }

    private static Dictionary<string, ConfigValueChange> ConfigOf(AccessWrite write) => write.Detail!.Config!.ToDictionary(k => k.Key, k => k.Value);

    [Fact]
    public async Task Start_NoStoredSnapshot_WritesConfigChangedWithStartingValues() // @M4-T16a
    {
        await StartAsync();

        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(AccessEventType.ConfigChanged, write.EventType);
        Assert.Equal(Guid.Empty, write.FormId);
        Assert.Equal(Guid.Empty, write.UserKey);
        Assert.Equal("System", write.UserName);
        Dictionary<string, ConfigValueChange> config = ConfigOf(write);
        Assert.Equal(new ConfigValueChange(null, "true"), config["enabled"]);
        Assert.Equal(new ConfigValueChange(null, "365"), config["retentionDays"]);
        Assert.Contains(ConfigChangeRecorder.SnapshotKey, _state.Keys);
        Assert.Equal(Environment.MachineName, write.Detail!.Server);
    }

    [Fact]
    public async Task Start_SameAsStored_WritesNothing() // @M4-T16a
    {
        await StartAsync();
        _writes.Clear();

        var second = new ConfigChangeRecorder(_monitor, _state, _writer, _health, _runtime, _time, _logger);
        await second.HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);

        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Start_RetentionChangedBetweenRestarts_WritesOldAndNew() // T14
    {
        await StartAsync();
        _writes.Clear();
        _options.EntryAudit.RetentionDays = 30;

        var second = new ConfigChangeRecorder(_monitor, _state, _writer, _health, _runtime, _time, _logger);
        await second.HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);

        Dictionary<string, ConfigValueChange> config = ConfigOf(Assert.Single(_writes));
        KeyValuePair<string, ConfigValueChange> only = Assert.Single(config);
        Assert.Equal("retentionDays", only.Key);
        Assert.Equal(new ConfigValueChange("365", "30"), only.Value);
    }

    [Fact]
    public async Task OnChange_EnabledFalse_WritesRowEvenThoughDisabled() // T14, E17
    {
        await StartAsync();
        _writes.Clear();
        _options.EntryAudit.Enabled = false;

        await FireChangeAsync();

        Dictionary<string, ConfigValueChange> config = ConfigOf(Assert.Single(_writes));
        Assert.Equal(new ConfigValueChange("true", "false"), config["enabled"]);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task OnChange_StoreEditValuesChanged_WritesRow() // T14
    {
        await StartAsync();
        _writes.Clear();
        _options.EntryAudit.StoreEditValues = StoreEditValuesMode.None;

        await FireChangeAsync();

        Dictionary<string, ConfigValueChange> config = ConfigOf(Assert.Single(_writes));
        Assert.Equal(new ConfigValueChange(nameof(StoreEditValuesMode.MaskSensitive), nameof(StoreEditValuesMode.None)), config["storeEditValues"]);
    }

    [Fact]
    public async Task OnChange_TopLevelCsvSwitchWhileNestedUnset_WritesConfigChangedRow() // @M4-FIX item 17
    {
        await StartAsync();
        _writes.Clear();

        _options.EnableCsvExport = false;
        await FireChangeAsync();

        ConfigValueChange change = ConfigOf(Assert.Single(_writes))["enableCsvExport"];
        Assert.Equal("true", change.Old);
        Assert.Equal("false", change.New);
    }

    [Fact]
    public async Task OnChange_FiredTwiceForOneEdit_WritesOneRow()
    {
        // Relies on the second comparison finding the snapshot already advanced; the callback itself is not deduplicated.
        await StartAsync();
        _writes.Clear();
        _options.EntryAudit.DeduplicateSeconds = 5;

        Assert.NotNull(_onChange);
        _onChange!(_options, null);
        _onChange!(_options, null);
        await Recorder().IdleAsync();

        Assert.Single(_writes);
    }

    [Fact]
    public async Task OnChange_WriterFailed_LoggedNotThrown()
    {
        await StartAsync();
        _writes.Clear();
        _writer.WriteAsync(Arg.Any<AccessWrite>()).ThrowsAsync(new InvalidOperationException("db down"));
        _options.EntryAudit.Enabled = false;

        await FireChangeAsync();

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(1, _health.WriteFailuresSinceStart);

        // The snapshot was not advanced, so the next change retries the whole difference.
        _writer.WriteAsync(Arg.Any<AccessWrite>()).Returns(c =>
        {
            _writes.Add(c.Arg<AccessWrite>());
            return new AccessWriteResult(AccessWriteStatus.Written, 1, false);
        });
        await FireChangeAsync();
        Assert.Contains("enabled", ConfigOf(Assert.Single(_writes)).Keys);
    }

    [Fact]
    public async Task OnChange_WriterReportsFailedStatus_SnapshotNotAdvanced()
    {
        await StartAsync();
        _writes.Clear();
        _writer.WriteAsync(Arg.Any<AccessWrite>()).Returns(new AccessWriteResult(AccessWriteStatus.Failed, null, false));
        _options.EntryAudit.Enabled = false;
        await FireChangeAsync();

        _writer.WriteAsync(Arg.Any<AccessWrite>()).Returns(c =>
        {
            _writes.Add(c.Arg<AccessWrite>());
            return new AccessWriteResult(AccessWriteStatus.Written, 1, false);
        });
        await FireChangeAsync();

        Assert.Single(_writes);
    }

    [Fact]
    public async Task OnChange_TwoDifferentChangesInQuickSuccession_AreNeverDeduplicated()
    {
        await StartAsync();
        _writes.Clear();

        _options.EntryAudit.Enabled = false;
        await FireChangeAsync();
        _options.EntryAudit.Enabled = true;
        await FireChangeAsync();
        _options.EntryAudit.Enabled = false;
        await FireChangeAsync();

        Assert.Equal(3, _writes.Count);
        // The writer's dedupe hashes the detail's filter, which a config row lacks, so each write carries its own key.
        Assert.Equal(3, _writes.Select(w => w.DedupeFilterHash).Distinct().Count());
        Assert.All(_writes, w => Assert.False(string.IsNullOrEmpty(w.DedupeFilterHash)));
    }

    [Fact]
    public async Task Detail_KeysAreCamelCaseNamesOfRecordedOptionsOnly()
    {
        _options.EntryAudit.AccessLogUserGroups = ["admin", "auditors"];
        await StartAsync();

        AccessWrite write = Assert.Single(_writes);
        // Keys are camelCase in the detail, so compare against the converted names of the recorded options.
        string[] allowed = EntryAuditOptions.RecordedKeys.Select(System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName).ToArray();
        Assert.All(write.Detail!.Config!.Keys, k => Assert.Contains(k, allowed));
        Assert.Equal("admin,auditors", write.Detail.Config["accessLogUserGroups"].New);
    }

    [Fact]
    public async Task Start_StateStoreThrows_LoggedNotThrown()
    {
        _state.FailReads = true;

        await StartAsync();

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromOptionChanges()
    {
        IDisposable subscription = Substitute.For<IDisposable>();
        _monitor.OnChange(Arg.Any<Action<FormsAuditTrailOptions, string?>>()).Returns(subscription);
        await StartAsync();

        Recorder().Dispose();

        subscription.Received(1).Dispose();
    }

    [Fact]
    public void ComputeOffPeriods_PairsOffAndOn_OpenPeriodHasNullTo()
    {
        DateTime t = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        FormAccessEntry[] rows =
        [
            Row(t.AddHours(3), "true", "false"),
            Row(t.AddHours(1), "false", "true"),
            Row(t, "true", "false"),
            Row(t.AddHours(2), "365", "30", key: "retentionDays"),
            Row(t.AddHours(5), "true", "false"),
        ];

        IReadOnlyList<OffPeriodDto> periods = FormsAuditApiController.ComputeOffPeriods(rows);

        Assert.Equal(2, periods.Count);
        Assert.Equal(new OffPeriodDto(t, t.AddHours(1)), periods[0]);
        Assert.Equal(new OffPeriodDto(t.AddHours(3), null), periods[1]);
    }

    [Fact]
    public void ComputeOffPeriods_RowsWithoutEnabledOrBadDetail_AreIgnored()
    {
        DateTime t = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        FormAccessEntry[] rows =
        [
            new() { Timestamp = t, EventType = (int)AccessEventType.ConfigChanged, Detail = null },
            new() { Timestamp = t.AddMinutes(1), EventType = (int)AccessEventType.ConfigChanged, Detail = "not json" },
            Row(t.AddMinutes(2), "true", "true"),
        ];

        Assert.Empty(FormsAuditApiController.ComputeOffPeriods(rows));
    }

    [Fact]
    public async Task GetConfig_ReturnsOffPeriodsForAccessLogUserOnly()
    {
        DateTime now = DateTime.UtcNow;
        _db.AddAccess(AccessEventType.ConfigChanged, Guid.Empty, now.AddDays(-2), detail: DetailJson("true", "false"));
        _db.AddAccess(AccessEventType.ConfigChanged, Guid.Empty, now.AddDays(-1), detail: DetailJson("false", "true"));
        // Outside the 365-day retention window, so it must not appear.
        _db.AddAccess(AccessEventType.ConfigChanged, Guid.Empty, now.AddDays(-800), detail: DetailJson("true", "false"));
        _options.EntryAudit.RetentionDays = 365;
        IAuthorizationService authorization = Substitute.For<IAuthorizationService>();
        FormsAuditApiController controller = new(
            _db.Context, Substitute.For<IFormsSecurity>(), _monitor, authorization, _health, TimeProvider.System, Substitute.For<IForeignValueHandlerDetector>(),
            new ListLogger<FormsAuditApiController>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), AccessLogPolicy.Name).Returns(AuthorizationResult.Failed());
        var other = (OkObjectResult)(await controller.GetConfig()).Result!;
        Assert.Null(((AuditConfigDto)other.Value!).OffPeriods);

        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), AccessLogPolicy.Name).Returns(AuthorizationResult.Success());
        var reader = (OkObjectResult)(await controller.GetConfig()).Result!;
        OffPeriodDto period = Assert.Single(((AuditConfigDto)reader.Value!).OffPeriods!);
        Assert.NotNull(period.ToUtc);
        Assert.True(period.ToUtc > period.FromUtc);
    }

    private void WriterFails() => _writer.WriteAsync(Arg.Any<AccessWrite>()).ThrowsAsync(new InvalidOperationException("db down"));

    private void WriterWorks() => _writer.WriteAsync(Arg.Any<AccessWrite>()).Returns(c =>
    {
        _writes.Add(c.Arg<AccessWrite>());
        return new AccessWriteResult(AccessWriteStatus.Written, _writes.Count, false);
    });

    [Fact]
    public async Task Start_RuntimeNotRun_DoesNothingAndRecordsNoFailure()
    {
        _runtime.Level.Returns(RuntimeLevel.Install);

        await StartAsync();

        Assert.Empty(_writes);
        Assert.Equal(0, _health.WriteFailuresSinceStart);
    }

    [Fact]
    public async Task NotReady_RuntimeBelowRun_WarnsOncePerHourNotEveryRetry() // @M3-M4-FIX item 12
    {
        _runtime.Level.Returns(RuntimeLevel.Upgrade);
        int Warnings() => _logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("not recording"));

        await StartAsync();
        Assert.Equal(1, Warnings());

        // Retries every five minutes for 55 minutes stay quiet.
        for (int i = 0; i < 11; i++)
        {
            _time.Now = _time.Now.Add(ConfigChangeRecorder.RetryInterval);
            await Recorder().CompareAsync(CancellationToken.None);
        }

        Assert.Equal(1, Warnings());

        _time.Now = _time.Now.Add(ConfigChangeRecorder.RetryInterval);
        await Recorder().CompareAsync(CancellationToken.None);
        Assert.Equal(2, Warnings());
    }

    [Fact]
    public async Task NotReady_MigrationsPending_StaysAtOneWarningBecauseHealthReportsIt() // @M3-M4-FIX item 12
    {
        _health.MigrationsPending = true;
        await StartAsync();

        _time.Now = _time.Now.AddHours(3);
        await Recorder().CompareAsync(CancellationToken.None);

        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not recording"));
    }

    [Fact]
    public async Task Start_BeforeMigration_NoHealthFailure_ThenRecordsOnceMigrated() // R53 item 1
    {
        _health.MigrationsPending = true;
        _state.FailReads = true;

        await StartAsync();

        Assert.Empty(_writes);
        Assert.Equal(0, _health.WriteFailuresSinceStart);
        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Error);

        _health.MigrationsPending = false;
        _state.FailReads = false;
        await FireChangeAsync();

        Assert.Single(_writes);
    }

    [Fact]
    public async Task OnChange_WriteFailsThenFlipBack_WritesBothRowsWithOriginalTimestamps() // R53 item 2
    {
        await StartAsync();
        _writes.Clear();
        DateTime off = _time.Now.UtcDateTime.AddMinutes(10);
        DateTime on = off.AddMinutes(20);

        WriterFails();
        _time.Now = off;
        _options.EntryAudit.Enabled = false;
        await FireChangeAsync();
        Assert.Equal(1, Recorder().PendingCount);

        WriterWorks();
        _time.Now = on;
        _options.EntryAudit.Enabled = true;
        await FireChangeAsync();

        Assert.Equal(2, _writes.Count);
        Assert.Equal(off, _writes[0].OccurredUtc);
        Assert.Equal(new ConfigValueChange("true", "false"), ConfigOf(_writes[0])["enabled"]);
        Assert.Equal(on, _writes[1].OccurredUtc);
        Assert.Equal(new ConfigValueChange("false", "true"), ConfigOf(_writes[1])["enabled"]);
        Assert.Equal(0, Recorder().PendingCount);
    }

    [Fact]
    public async Task OnChange_SnapshotSaveFails_NoHealthFailure_SnapshotRetriedNextTime() // R53 item 8
    {
        await StartAsync();
        _writes.Clear();
        _state.FailWrites = true;
        _options.EntryAudit.Enabled = false;

        await FireChangeAsync();

        Assert.Single(_writes);
        Assert.Equal(0, _health.WriteFailuresSinceStart);
        _state.FailWrites = false;
        int before = _state.SetCalls;
        await FireChangeAsync();

        Assert.Single(_writes);
        Assert.Equal(before + 1, _state.SetCalls);
    }

    [Fact]
    public async Task OnChange_RetentionAndGroupsChanged_WritesBothKeys() // R53 item 8
    {
        await StartAsync();
        _writes.Clear();
        _options.EntryAudit.RetentionDays = 7;
        _options.EntryAudit.AccessLogUserGroups = ["admin"];

        await FireChangeAsync();

        Dictionary<string, ConfigValueChange> config = ConfigOf(Assert.Single(_writes));
        Assert.Equal(new ConfigValueChange("365", "7"), config["retentionDays"]);
        Assert.Equal(new ConfigValueChange("", "admin"), config["accessLogUserGroups"]);
    }

    [Fact]
    public async Task Detail_VeryLongGroupList_KeepsConfigAndOffPeriodIsStillFound() // R53 item 3
    {
        await StartAsync();
        _writes.Clear();
        _options.EntryAudit.AccessLogUserGroups = Enumerable.Range(0, 2000).Select(i => "group-number-" + i).ToList();
        _options.EntryAudit.Enabled = false;

        await FireChangeAsync();

        AccessWrite write = Assert.Single(_writes);
        string json = write.Detail!.ToJson(EntryAuditDetail.DefaultMaxBytes);
        Assert.Contains("\"config\"", json);
        Assert.StartsWith("2000 items, sha256:", write.Detail.Config!["accessLogUserGroups"].New);
        var row = new FormAccessEntry { Timestamp = _time.Now.UtcDateTime, EventType = (int)AccessEventType.ConfigChanged, Detail = json };
        Assert.Single(FormsAuditApiController.ComputeOffPeriods([row]));
    }

    [Fact]
    public void ComputeOffPeriods_OffNowWithNoOpenSpanInWindow_AddsStartUnknownSpan() // R53 item 5
    {
        DateTime window = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        IReadOnlyList<OffPeriodDto> periods = FormsAuditApiController.ComputeOffPeriods([], enabledNow: false, windowStartUtc: window);

        Assert.Equal(new OffPeriodDto(window, null, true), Assert.Single(periods));
        Assert.Empty(FormsAuditApiController.ComputeOffPeriods([], enabledNow: true, windowStartUtc: window));
    }

    [Fact]
    public async Task GetConfig_AuditOffAndOffRowOlderThanWindow_ReportsStartUnknown() // R53 item 5
    {
        _db.AddAccess(AccessEventType.ConfigChanged, Guid.Empty, DateTime.UtcNow.AddDays(-800), detail: DetailJson("true", "false"));
        _options.EntryAudit.Enabled = false;
        _options.EntryAudit.RetentionDays = 30;
        IAuthorizationService authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), AccessLogPolicy.Name).Returns(AuthorizationResult.Success());
        FormsAuditApiController controller = new(
            _db.Context, Substitute.For<IFormsSecurity>(), _monitor, authorization, _health, TimeProvider.System, Substitute.For<IForeignValueHandlerDetector>(),
            new ListLogger<FormsAuditApiController>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var ok = (OkObjectResult)(await controller.GetConfig()).Result!;

        OffPeriodDto period = Assert.Single(((AuditConfigDto)ok.Value!).OffPeriods!);
        Assert.True(period.StartUnknown);
        Assert.Null(period.ToUtc);
    }

    [Fact]
    public async Task Snapshot_IsShared_AndRowNamesTheServer() // R58 item 2
    {
        var first = new ConfigChangeRecorder(_monitor, _state, _writer, _health, _runtime, _time, _logger, "WEB-1");
        await first.HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);
        first.Dispose();
        Assert.Equal("WEB-1", Assert.Single(_writes).Detail!.Server);
        Assert.Equal([ConfigChangeRecorder.SnapshotKey], _state.Keys);

        // A different machine name (a container restarted under a new name) finds the same snapshot and writes nothing.
        _writes.Clear();
        var second = new ConfigChangeRecorder(_monitor, _state, _writer, _health, _runtime, _time, _logger, "random-container-id");
        await second.HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);
        second.Dispose();
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Drain_DeferredToTimer_SavesSnapshot_SoARestartWritesNoDuplicate() // R58 item 1
    {
        await StartAsync();
        _writes.Clear();
        WriterFails();
        _options.EntryAudit.Enabled = false;
        await FireChangeAsync();
        Assert.Equal(1, Recorder().PendingCount);

        // The timer's job: a comparison with no further change.
        WriterWorks();
        await Recorder().CompareAsync(CancellationToken.None);
        Assert.Single(_writes);
        Assert.Equal(0, Recorder().PendingCount);

        _writes.Clear();
        var restarted = new ConfigChangeRecorder(_monitor, _state, _writer, _health, _runtime, _time, _logger);
        await restarted.HandleAsync(new UmbracoApplicationStartedNotification(false), CancellationToken.None);
        restarted.Dispose();

        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Dispose_WithPendingChanges_LogsErrorWithCountAndTransitions() // R58 item 3
    {
        await StartAsync();
        WriterFails();
        _options.EntryAudit.Enabled = false;
        await FireChangeAsync();

        Recorder().Dispose();

        (LogLevel Level, string Message) entry = Assert.Single(_logger.Entries, e => e.Message.Contains("shutting down"));
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("1 configuration changes", entry.Message);
        Assert.Contains("enabled:true->false", entry.Message);
    }

    [Fact]
    public async Task NotReady_WarnsOnceThenQuietUntilItSucceeds() // R58 item 4
    {
        _health.MigrationsPending = true;
        await StartAsync();
        await Recorder().CompareAsync(CancellationToken.None);
        await Recorder().CompareAsync(CancellationToken.None);
        Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not recording"));

        _health.MigrationsPending = false;
        await Recorder().CompareAsync(CancellationToken.None);
        _health.MigrationsPending = true;
        await Recorder().CompareAsync(CancellationToken.None);

        Assert.Equal(2, _logger.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("not recording")));
    }

    [Fact]
    public void ComputeOffPeriods_OffNowButNewestEnabledRowIsOn_OpensOneUnknownSpanAtThatRow() // R60 item 1
    {
        DateTime t = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);
        FormAccessEntry[] rows = [Row(t, "true", "false"), Row(t.AddHours(1), "false", "true"), Row(t.AddHours(2), "true", "false"), Row(t.AddHours(3), "false", "true")];

        IReadOnlyList<OffPeriodDto> periods = FormsAuditApiController.ComputeOffPeriods(rows, enabledNow: false, windowStartUtc: t.AddDays(-30));

        Assert.Equal(
            [new OffPeriodDto(t, t.AddHours(1)), new OffPeriodDto(t.AddHours(2), t.AddHours(3)), new OffPeriodDto(t.AddHours(3), null, true)],
            periods);
        for (int i = 1; i < periods.Count; i++)
        {
            Assert.True(periods[i].FromUtc >= periods[i - 1].ToUtc, "Periods must not overlap.");
        }
    }

    [Fact]
    public async Task Dispose_Twice_LogsPendingErrorOnce() // R60 item 2
    {
        await StartAsync();
        WriterFails();
        _options.EntryAudit.Enabled = false;
        await FireChangeAsync();

        Recorder().Dispose();
        Recorder().Dispose();

        Assert.Single(_logger.Entries, e => e.Message.Contains("shutting down"));
    }

    [Fact]
    public async Task Writer_HonoursOccurredUtcOnlyForConfigChanged_AndClampsToNow() // R58 item 6
    {
        var time = new SettableTime();
        DateTime now = time.Now.UtcDateTime;
        using var host = new WriterHost(time);

        await host.Writer.WriteAsync(Access(AccessEventType.ConfigChanged, now.AddHours(-5)));
        await host.Writer.WriteAsync(Access(AccessEventType.ConfigChanged, now.AddHours(5)));
        await host.Writer.WriteAsync(Access(AccessEventType.ListViewed, now.AddHours(-5)));

        List<FormAccessEntry> rows = host.Db.Context.AccessEntries.OrderBy(e => e.Id).ToList();
        Assert.Equal(now.AddHours(-5), rows[0].Timestamp);
        Assert.Equal(now, rows[1].Timestamp);
        Assert.Equal(now, rows[2].Timestamp);
    }

    private static AccessWrite Access(AccessEventType type, DateTime occurred) =>
        new(type, Guid.Empty, null, null, 0, Guid.Empty, "System", null, [], []) { OccurredUtc = occurred, DedupeFilterHash = Guid.NewGuid().ToString("N") };

    private sealed class WriterHost : IDisposable
    {
        private readonly Microsoft.Extensions.DependencyInjection.ServiceProvider _provider;

        public WriterHost(TimeProvider time)
        {
            Db = new TestDatabase();
            _provider = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
                .AddSingleton<Persistence.FormsAuditDbContext>(Db.Context)
                .AddSingleton(Substitute.For<Umbraco.Forms.Core.Services.IFormService>())
                .BuildServiceProvider();
            var options = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
            options.CurrentValue.Returns(new FormsAuditTrailOptions());
            var state = new FakeStateStore();
            Writer = new EntryAuditWriter(
                _provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
                new EntryAuditDeduplicator(options, TimeProvider.System),
                new EntryAuditHealth(state, time),
                options,
                time,
                new ListLogger<EntryAuditWriter>());
        }

        public TestDatabase Db { get; }

        public EntryAuditWriter Writer { get; }

        public void Dispose()
        {
            _provider.Dispose();
            Db.Dispose();
        }
    }

    [Fact]
    public async Task Dispose_WhileIdle_DoesNotThrowOnLaterChange()
    {
        await StartAsync();
        Recorder().Dispose();

        await Recorder().CompareAsync(CancellationToken.None);
    }

    private static FormAccessEntry Row(DateTime at, string? oldValue, string? newValue, string key = "enabled") => new()
    {
        Timestamp = at,
        EventType = (int)AccessEventType.ConfigChanged,
        Detail = DetailJson(oldValue, newValue, key),
    };

    private static string DetailJson(string? oldValue, string? newValue, string key = "enabled") =>
        JsonSerializer.Serialize(new { v = 1, config = new Dictionary<string, object> { [key] = new { old = oldValue, @new = newValue } } });

    public void Dispose()
    {
        _sut?.Dispose();
        _db.Dispose();
    }

    private sealed class SettableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeStateStore : IAuditStateStore
    {
        private readonly Dictionary<string, string> _values = [];

        public bool FailReads { get; set; }

        public bool FailWrites { get; set; }

        public int SetCalls { get; private set; }

        public IReadOnlyCollection<string> Keys => _values.Keys;

        public Task<string?> GetAsync(string key, CancellationToken ct)
        {
            if (FailReads)
            {
                throw new InvalidOperationException("state unavailable");
            }

            return Task.FromResult(_values.TryGetValue(key, out string? v) ? v : null);
        }

        public Task SetAsync(string key, string json, CancellationToken ct)
        {
            SetCalls++;
            if (FailWrites)
            {
                throw new InvalidOperationException("state write failed");
            }

            _values[key] = json;
            return Task.CompletedTask;
        }
    }
}
