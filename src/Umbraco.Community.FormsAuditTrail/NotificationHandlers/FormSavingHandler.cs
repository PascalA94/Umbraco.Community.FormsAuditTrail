using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Community.FormsAuditTrail.Services;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

public class FormSavingHandler : INotificationHandler<FormSavingNotification>
{
    internal const string IsNewStateKeyPrefix = "FormsAuditTrail_IsNew_";
    internal const string BeforeSnapshotStateKeyPrefix = "FormsAuditTrail_BeforeSnapshot_";

    private readonly IFormSnapshotService _snapshotService;
    private readonly IFormService _formService;
    private readonly ILogger<FormSavingHandler> _logger;

    public FormSavingHandler(
        IFormSnapshotService snapshotService,
        IFormService formService,
        ILogger<FormSavingHandler> logger)
    {
        _snapshotService = snapshotService;
        _formService = formService;
        _logger = logger;
    }

    public void Handle(FormSavingNotification notification)
    {
        foreach (Form form in notification.SavedEntities)
        {
            try
            {
                // Load from the DB to get the true before-state; null means the form doesn't exist yet (new)
                Form? currentForm = _formService.Get(form.Id);
                var isNew = currentForm == null;
                notification.State[$"{IsNewStateKeyPrefix}{form.Id}"] = isNew;

                if (!isNew)
                {
                    var beforeJson = _snapshotService.Serialize(currentForm!);
                    notification.State[$"{BeforeSnapshotStateKeyPrefix}{form.Id}"] = beforeJson;
                }
            }
            catch (Exception ex)
            {
                // Never block a save due to an audit failure
                _logger.LogWarning(ex, "Failed to capture before-save audit snapshot for form {FormId}", form.Id);
            }
        }
    }
}
