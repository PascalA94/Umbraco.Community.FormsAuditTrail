using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Community.FormsAuditTrail.Services;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

public class FormDeletingHandler : INotificationHandler<FormDeletingNotification>
{
    private readonly IFormSnapshotService _snapshotService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBackOfficeSecurityAccessor _backOfficeSecurityAccessor;
    private readonly ILogger<FormDeletingHandler> _logger;

    public FormDeletingHandler(
        IFormSnapshotService snapshotService,
        IServiceScopeFactory scopeFactory,
        IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
        ILogger<FormDeletingHandler> logger)
    {
        _snapshotService = snapshotService;
        _scopeFactory = scopeFactory;
        _backOfficeSecurityAccessor = backOfficeSecurityAccessor;
        _logger = logger;
    }

    public void Handle(FormDeletingNotification notification)
    {
        foreach (Form form in notification.DeletedEntities)
        {
            try
            {
                var beforeJson = _snapshotService.Serialize(form);

                // No backoffice user means the delete came from a background process —
                // an Umbraco Deploy transfer, a code-based delete, etc.
                Guid userKey = Guid.Empty;
                var userName = "System";
                IBackOfficeSecurity? security = _backOfficeSecurityAccessor.BackOfficeSecurity;
                if (security?.CurrentUser != null)
                {
                    userKey = security.CurrentUser.Key;
                    userName = security.CurrentUser.Name ?? "System";
                }

                using IServiceScope scope = _scopeFactory.CreateScope();
                FormsAuditDbContext db = scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();
                db.AuditEntries.Add(new FormAuditEntry
                {
                    FormId = form.Id,
                    FormName = form.Name,
                    UserKey = userKey,
                    UserName = userName,
                    Timestamp = DateTime.UtcNow,
                    EventType = (int)AuditEventType.Deleted,
                    BeforeSnapshot = beforeJson,
                    AfterSnapshot = null,
                    ChangeSummaryJson = "{\"Summary\":\"Form deleted\",\"Changes\":[]}",
                });
                db.SaveChanges();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record deletion audit entry for form {FormId}", form.Id);
            }
        }
    }
}
