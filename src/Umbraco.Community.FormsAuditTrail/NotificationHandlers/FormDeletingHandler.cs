using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.Services;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

/// <summary>
/// Captures the before-state and acting user into the notification state. It never writes to the
/// audit database: Forms 18.2 deletes workflows and security rows inside the outer scope before this
/// notification fires, and a second connection writing on SQLite then fails with "table is locked".
/// <see cref="FormDeletedHandler"/> writes the entry once that scope has completed.
/// </summary>
public class FormDeletingHandler : INotificationHandler<FormDeletingNotification>
{
    internal const string BeforeSnapshotStateKeyPrefix = "FormsAuditTrail_DeleteBeforeSnapshot_";
    internal const string UserKeyStateKeyPrefix = "FormsAuditTrail_DeleteUserKey_";
    internal const string UserNameStateKeyPrefix = "FormsAuditTrail_DeleteUserName_";

    private readonly IFormSnapshotService _snapshotService;
    private readonly IBackOfficeSecurityAccessor _backOfficeSecurityAccessor;
    private readonly ILogger<FormDeletingHandler> _logger;

    public FormDeletingHandler(
        IFormSnapshotService snapshotService,
        IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
        ILogger<FormDeletingHandler> logger)
    {
        _snapshotService = snapshotService;
        _backOfficeSecurityAccessor = backOfficeSecurityAccessor;
        _logger = logger;
    }

    public void Handle(FormDeletingNotification notification)
    {
        foreach (Form form in notification.DeletedEntities)
        {
            try
            {
                (Guid userKey, string userName) = AuditHandlerHelpers.GetCurrentUser(_backOfficeSecurityAccessor);
                notification.State[$"{UserKeyStateKeyPrefix}{form.Id}"] = userKey;
                notification.State[$"{UserNameStateKeyPrefix}{form.Id}"] = userName;

                // Fallback only: workflows are already gone by now, so FormDeletedHandler prefers
                // the last audit entry's AfterSnapshot.
                notification.State[$"{BeforeSnapshotStateKeyPrefix}{form.Id}"] = _snapshotService.Serialize(form);
            }
            catch (Exception ex)
            {
                // Never block a delete due to an audit failure
                _logger.LogWarning(ex, "Failed to capture before-delete audit state for form {FormId}", form.Id);
            }
        }
    }
}
