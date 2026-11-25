using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Forms.Core.Searchers;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>Tells the dashboard whether another package may be transforming entry values (for example encrypting them).</summary>
public interface IForeignValueHandlerDetector
{
    internal bool HasForeignValueHandlers { get; }
}

/// <summary>
/// Decides once at <see cref="UmbracoApplicationStartedNotification"/> by enumerating the final service collection
/// (spike S10). A compose-time scan is unreliable because composer order is not guaranteed, and enumerating
/// descriptors, unlike resolving handlers, instantiates nothing. Sees notification handlers only; a decorated
/// <c>IRecordStorage</c> or database-level encryption is not detected, which the README notice covers.
/// </summary>
internal sealed class ForeignValueHandlerDetector(
    IEnumerable<ServiceDescriptor> services,
    IOptionsMonitor<FormsAuditTrailOptions> options,
    ILogger<ForeignValueHandlerDetector> logger)
    : IForeignValueHandlerDetector, INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private static readonly Type[] WatchedServiceTypes =
    [
        typeof(INotificationHandler<RecordFetchingNotification>),
        typeof(INotificationAsyncHandler<RecordFetchingNotification>),
        typeof(INotificationHandler<EntrySearchResultFetchingNotification>),
        typeof(INotificationAsyncHandler<EntrySearchResultFetchingNotification>),
    ];

    private volatile bool _hasForeign;

    public bool HasForeignValueHandlers => _hasForeign;

    public Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            _hasForeign = services.Any(d => WatchedServiceTypes.Contains(d.ServiceType) && !IsOurs(d));

            if (_hasForeign && options.CurrentValue.EntryAudit.StoreEditValues != StoreEditValuesMode.None)
            {
                logger.LogWarning(
                    "Another package handles Umbraco Forms entry fetching and may transform entry values (for example by encrypting them). " +
                    "Stored edit values can then differ from what the dashboard shows editors. Set FormsAuditTrail:EntryAudit:StoreEditValues to None to avoid storing them.");
            }
        }
        catch (Exception ex)
        {
            // Detection must never stop the site starting, but unknown is not the same as none: assuming foreign
            // keeps the dashboard from claiming stored values are trustworthy when the check could not run.
            _hasForeign = true;
            logger.LogWarning(ex, "Could not check for other Forms entry value handlers; assuming one is present.");
        }

        return Task.CompletedTask;
    }

    // A factory with no declared type cannot be proven ours, so it counts as foreign rather than risk under-reporting.
    private static bool IsOurs(ServiceDescriptor d)
    {
        object? instance = d.IsKeyedService ? d.KeyedImplementationInstance : d.ImplementationInstance;
        Type? type = d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType;
        Type? known = type ?? instance?.GetType();
        return known is not null && known.Assembly == typeof(ForeignValueHandlerDetector).Assembly;
    }
}
