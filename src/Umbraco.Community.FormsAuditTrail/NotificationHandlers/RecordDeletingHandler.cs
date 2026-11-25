using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Forms.Core.Persistence.Dtos;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

/// <summary>
/// Notes which records Forms is deleting, for the capture core to record and scrub once the request has completed
/// (spec 7.3). It only touches the request's collector: <c>RecordDeleting</c> fires inside Forms' open delete
/// scope, and any audit write there locks SQLite or hangs Forms' request (S5). Holding ids in memory is the most it may do.
/// </summary>
/// <remarks>
/// S5 fallback: Forms publishes no <c>RecordDeleted</c> for a route delete, a permanent form delete or its scheduled
/// deletion, so there is no post-delete handler. A delete outside a backoffice request is scrubbed by the daily
/// reconciliation instead, up to 24 hours later (Pascal, final). Never throws, because it runs inside Forms' delete.
/// </remarks>
internal sealed class RecordDeletingHandler(
    IEntryAuditCollectorAccessor accessor,
    ILogger<RecordDeletingHandler> logger) : INotificationHandler<RecordDeletingNotification>
{
    public void Handle(RecordDeletingNotification notification)
    {
        try
        {
            if (accessor.Current is not { } collector)
            {
                return;
            }

            bool inDeleteWindow = collector.IsOpen(AccessEventType.Deleted);
            foreach (IGrouping<Guid, Record> form in notification.DeletedEntities.GroupBy(r => r.Form))
            {
                collector.CountDeletedRecords(form.Key, form.Count());
                if (!inDeleteWindow)
                {
                    continue;
                }

                foreach (Record record in form)
                {
                    collector.AddDeletedRecord(form.Key, record.UniqueId);
                }
            }
        }
        catch (Exception ex)
        {
            // Only the type: the records being deleted carry their field values.
            try { logger.LogError("FormsAuditTrail could not note an entry delete ({ExceptionType}).", ex.GetType().Name); }
            catch { /* A throwing logger cannot be reported through itself. */ }
        }
    }
}
