using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Events;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.NotificationHandlers;
using Umbraco.Forms.Core.Searchers;
using Umbraco.Forms.Core.Services.Notifications;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class ForeignValueHandlerDetectorTests
{
    private sealed class ForeignSyncHandler : INotificationHandler<RecordFetchingNotification>
    {
        public void Handle(RecordFetchingNotification notification) { }
    }

    private sealed class ForeignAsyncHandler : INotificationAsyncHandler<EntrySearchResultFetchingNotification>
    {
        public Task HandleAsync(EntrySearchResultFetchingNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CapturingLogger : ILogger<ForeignValueHandlerDetector>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    // The detector decides "foreign" by assembly, so a type from another assembly stands in for a third-party package.
    private static ServiceDescriptor Foreign<TService, TImpl>() where TService : class where TImpl : class, TService =>
        ServiceDescriptor.Transient(typeof(TService), typeof(TImpl));

    private static (ForeignValueHandlerDetector Detector, CapturingLogger Log) Build(
        IEnumerable<ServiceDescriptor> descriptors, StoreEditValuesMode mode = StoreEditValuesMode.MaskSensitive)
    {
        var log = new CapturingLogger();
        IOptionsMonitor<FormsAuditTrailOptions> monitor = NSubstitute.Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        monitor.CurrentValue.Returns(new FormsAuditTrailOptions { EntryAudit = { StoreEditValues = mode } });
        return (new ForeignValueHandlerDetector(descriptors, monitor, log), log);
    }

    [Fact]
    public async Task Detector_ForeignHandlerRegistered_True_AndWarningLogged() // @M3-T13 T11
    {
        var (detector, log) = Build([Foreign<INotificationHandler<RecordFetchingNotification>, ForeignSyncHandler>()]);

        Assert.False(detector.HasForeignValueHandlers); // nothing is decided before the application has started
        await detector.HandleAsync(null!, CancellationToken.None);

        Assert.True(detector.HasForeignValueHandlers);
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Detector_ForeignAsyncHandlerOnSearchResults_True() // @M3-T13 S10
    {
        var (detector, _) = Build([Foreign<INotificationAsyncHandler<EntrySearchResultFetchingNotification>, ForeignAsyncHandler>()]);

        await detector.HandleAsync(null!, CancellationToken.None);

        Assert.True(detector.HasForeignValueHandlers);
    }

    [Fact]
    public async Task Detector_OnlyOurHandlers_False() // @M3-T13 S10: excludes the package's own by assembly, both notifications
    {
        var (detector, log) = Build(
        [
            ServiceDescriptor.Transient<INotificationHandler<RecordFetchingNotification>, RecordFetchingHandler>(),
            ServiceDescriptor.Transient<INotificationHandler<EntrySearchResultFetchingNotification>, EntrySearchResultFetchingHandler>(),
        ]);

        await detector.HandleAsync(null!, CancellationToken.None);

        Assert.False(detector.HasForeignValueHandlers);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Detector_ForeignHandlerButStoreEditValuesNone_TrueWithoutWarning() // @M3-T13
    {
        var (detector, log) = Build([Foreign<INotificationHandler<RecordFetchingNotification>, ForeignSyncHandler>()], StoreEditValuesMode.None);

        await detector.HandleAsync(null!, CancellationToken.None);

        Assert.True(detector.HasForeignValueHandlers);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Detector_UnrelatedForeignHandler_False() // @M3-T13
    {
        var (detector, _) = Build([ServiceDescriptor.Transient<INotificationHandler<Umbraco.Cms.Core.Notifications.UmbracoApplicationStartedNotification>, ForeignStartedHandler>()]);

        await detector.HandleAsync(null!, CancellationToken.None);

        Assert.False(detector.HasForeignValueHandlers);
    }

    [Fact]
    public async Task Detector_FactoryDescriptorWithNoType_AssumedForeign() // @M3-T13 R44
    {
        var (detector, _) = Build([ServiceDescriptor.Transient<INotificationHandler<RecordFetchingNotification>>(_ => new ForeignSyncHandler())]);

        await detector.HandleAsync(null!, CancellationToken.None);

        Assert.True(detector.HasForeignValueHandlers);
    }

    [Fact]
    public async Task Detector_InstanceDescriptorFromAnotherAssembly_True() // @M3-T13 R44
    {
        var (detector, _) = Build([ServiceDescriptor.Singleton<INotificationHandler<RecordFetchingNotification>>(new ForeignSyncHandler())]);

        await detector.HandleAsync(null!, CancellationToken.None);

        Assert.True(detector.HasForeignValueHandlers);
    }

    [Fact]
    public async Task Detector_KeyedDescriptorFromAnotherAssembly_True() // @M3-T13 R44
    {
        var (detector, _) = Build([ServiceDescriptor.KeyedTransient<INotificationHandler<RecordFetchingNotification>, ForeignSyncHandler>("k")]);

        await detector.HandleAsync(null!, CancellationToken.None);

        Assert.True(detector.HasForeignValueHandlers);
    }

    [Fact]
    public async Task Detector_EnumerationThrows_AssumesForeignAndWarns() // @M3-T13 R44
    {
        var (detector, log) = Build(new ThrowingEnumerable());

        await detector.HandleAsync(null!, CancellationToken.None);

        Assert.True(detector.HasForeignValueHandlers);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning);
    }

    private sealed class ThrowingEnumerable : IEnumerable<ServiceDescriptor>
    {
        public IEnumerator<ServiceDescriptor> GetEnumerator() => throw new InvalidOperationException("boom");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ForeignStartedHandler : INotificationHandler<Umbraco.Cms.Core.Notifications.UmbracoApplicationStartedNotification>
    {
        public void Handle(Umbraco.Cms.Core.Notifications.UmbracoApplicationStartedNotification notification) { }
    }
}
