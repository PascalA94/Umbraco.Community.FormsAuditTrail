using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.Services;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

/// <summary>
/// Captures the before-state and acting user of a form delete into the notification state. It must not
/// touch the database: Forms 17.6+ deletes workflows, versions and security rows inside the outer scope
/// before this notification fires, and a second EF connection write fails on SQLite with "database table
/// is locked". The audit row is written by <see cref="FormDeletedHandler"/> after the scope completes.
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
        // The acting user is only available on the request thread, so capture it now.
        // No backoffice user means a background process: an Umbraco Deploy transfer, a code-based delete, etc.
        Guid userKey = Guid.Empty;
        var userName = "System";
        try
        {
            IBackOfficeSecurity? security = _backOfficeSecurityAccessor.BackOfficeSecurity;
            if (security?.CurrentUser != null)
            {
                userKey = security.CurrentUser.Key;
                userName = security.CurrentUser.Name ?? "System";
            }
        }
        catch (Exception ex)
        {
            // Never block a delete due to an audit failure
            _logger.LogWarning(ex, "Failed to resolve the current user for the delete audit entry");
        }

        foreach (Form form in notification.DeletedEntities)
        {
            notification.State[$"{UserKeyStateKeyPrefix}{form.Id}"] = userKey;
            notification.State[$"{UserNameStateKeyPrefix}{form.Id}"] = userName;

            try
            {
                // Fallback only: the Deleted handler prefers the last audit entry's AfterSnapshot,
                // because workflows are already gone from the form at this point.
                notification.State[$"{BeforeSnapshotStateKeyPrefix}{form.Id}"] = _snapshotService.Serialize(form);
            }
            catch (Exception ex)
            {
                // Never block a delete due to an audit failure
                _logger.LogWarning(ex, "Failed to capture before-delete audit snapshot for form {FormId}", form.Id);
            }
        }
    }
}
