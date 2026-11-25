using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

/// <summary>
/// Fills in the form id of a window opened without one, from the record Forms loads in the same request (spec 7.1,
/// "Where the form id and record id come from"). A route form id is never overwritten: Forms may load records of
/// other forms in the request, and the route is what the user asked for.
/// </summary>
/// <remarks>
/// Forms also publishes this notification from workflow execution with no request (S1), and from our own before-value
/// read; with no open window it returns at once. It never throws, because it runs in Forms' record read path.
/// </remarks>
public sealed class RecordFetchingHandler(
    IEntryAuditCollectorAccessor accessor,
    ILogger<RecordFetchingHandler> logger) : INotificationHandler<RecordFetchingNotification>
{
    public void Handle(RecordFetchingNotification notification)
    {
        try
        {
            if (accessor.Current is not { Window: not null } collector)
            {
                return;
            }

            collector.SetFormIdIfMissing(notification.Record.Form);
        }
        catch (Exception ex)
        {
            SafeLog.Run(() => logger.LogError("FormsAuditTrail could not read a fetched record's form ({ExceptionType}).", ex.GetType().Name));
        }
    }
}
