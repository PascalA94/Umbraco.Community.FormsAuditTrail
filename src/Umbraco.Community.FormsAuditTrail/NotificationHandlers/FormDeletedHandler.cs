using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Events;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Community.FormsAuditTrail.Services;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

/// <summary>
/// Writes the Deleted audit entry after the Forms delete scope has completed, so the audit
/// connection does not contend with it (see <see cref="FormDeletingHandler"/>).
/// </summary>
public class FormDeletedHandler : INotificationAsyncHandler<FormDeletedNotification>
{
    private readonly IFormSnapshotService _snapshotService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FormDeletedHandler> _logger;

    public FormDeletedHandler(
        IFormSnapshotService snapshotService,
        IServiceScopeFactory scopeFactory,
        ILogger<FormDeletedHandler> logger)
    {
        _snapshotService = snapshotService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task HandleAsync(FormDeletedNotification notification, CancellationToken cancellationToken)
    {
        foreach (Form form in notification.DeletedEntities)
        {
            try
            {
                var hasUserKey = notification.State.TryGetValue($"{FormDeletingHandler.UserKeyStateKeyPrefix}{form.Id}", out var k);
                var hasUserName = notification.State.TryGetValue($"{FormDeletingHandler.UserNameStateKeyPrefix}{form.Id}", out var n);
                var userKey = k is Guid g ? g : Guid.Empty;
                var userName = n as string ?? "System";
                if (!hasUserKey || !hasUserName)
                {
                    _logger.LogDebug("No captured user for deleted form {FormId}; recording as System", form.Id);
                }

                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                FormsAuditDbContext db = scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();

                var beforeJson = await ResolveBeforeSnapshotAsync(db, form, notification, cancellationToken);

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
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record deletion audit entry for form {FormId}", form.Id);
            }
        }
    }

    /// <summary>
    /// Before-state chain: the last audit entry's AfterSnapshot (includes workflows), then the snapshot
    /// captured while deleting, then a fresh snapshot of the notification entity. If every tier fails the
    /// entry is still recorded with an empty JSON object, which the API, diff view and CSV export handle.
    /// </summary>
    private async Task<string> ResolveBeforeSnapshotAsync(
        FormsAuditDbContext db, Form form, FormDeletedNotification notification, CancellationToken cancellationToken)
    {
        try
        {
            var last = await db.GetLastAfterSnapshotAsync(form.Id, cancellationToken);
            if (last != null)
            {
                return last;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read the previous audit snapshot for deleted form {FormId}", form.Id);
        }

        if (notification.State.TryGetValue($"{FormDeletingHandler.BeforeSnapshotStateKeyPrefix}{form.Id}", out var captured)
            && captured is string capturedJson)
        {
            return capturedJson;
        }

        try
        {
            return _snapshotService.Serialize(form);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No before-state available for deleted form {FormId}; recording an empty snapshot", form.Id);
            return "{}";
        }
    }
}
