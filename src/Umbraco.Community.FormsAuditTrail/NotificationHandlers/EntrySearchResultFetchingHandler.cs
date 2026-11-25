using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Forms.Core.Searchers;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

/// <summary>
/// Counts the rows Forms' searcher returns for a list or an export, once per row (spec 7.3). The notification carries no
/// user and cannot tell a list from an export, so it only adds to a window the action filter opened; with no window
/// (front-end, workflows, other packages' searches) it returns at once.
/// </summary>
/// <remarks>
/// It runs inside Forms' search loop, about a hundred times per list page, so it does as little as possible and never
/// throws: a failure here would break the backoffice entries list, not just the audit.
/// </remarks>
internal sealed class EntrySearchResultFetchingHandler(
    IEntryAuditCollectorAccessor accessor,
    ILogger<EntrySearchResultFetchingHandler> logger) : INotificationHandler<EntrySearchResultFetchingNotification>
{
    public void Handle(EntrySearchResultFetchingNotification notification)
    {
        try
        {
            if (accessor.Current is not { Window: not null } collector)
            {
                return;
            }

            EntrySearchResult row = notification.EntrySearchResult;
            collector.AddFetchedRecord(row.UniqueId);
            // An export request whose form id did not parse can still learn it from the rows it returned.
            if (Guid.TryParse(row.Form, out Guid formId))
            {
                collector.SetFormIdIfMissing(formId);
            }
        }
        catch (Exception ex)
        {
            SafeLog.Run(() => logger.LogError("FormsAuditTrail could not count a fetched entry ({ExceptionType}).", ex.GetType().Name));
        }
    }
}
