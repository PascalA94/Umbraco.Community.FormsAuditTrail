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
/// Writes the Deleted audit entry once the Forms delete scope has completed, so the write is not
/// blocked by locks held by the outer scope (SQLite "database table is locked").
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
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                FormsAuditDbContext db = scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();

                // Prefer the last known state from the audit trail: it includes workflows, which
                // Forms has already deleted by the time the form is handed to us. A failed read
                // must not drop the entry, so fall through to the Deleting snapshot.
                string? beforeJson = null;
                try
                {
                    beforeJson = await db.GetLastAfterSnapshotAsync(form.Id, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read the last audit snapshot for deleted form {FormId}; using the captured snapshot", form.Id);
                }

                if (string.IsNullOrWhiteSpace(beforeJson)
                    && notification.State.TryGetValue($"{FormDeletingHandler.BeforeSnapshotStateKeyPrefix}{form.Id}", out var captured))
                {
                    beforeJson = captured as string;
                }

                // Last tier. If this fails or yields nothing, still record the deletion with an empty
                // JSON object and log an Error: an audit trail should not lose the fact of a deletion.
                if (string.IsNullOrWhiteSpace(beforeJson))
                {
                    try
                    {
                        beforeJson = _snapshotService.Serialize(form);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to snapshot deleted form {FormId}", form.Id);
                    }
                }

                if (string.IsNullOrWhiteSpace(beforeJson))
                {
                    _logger.LogError("No before-snapshot is available for deleted form {FormId}; recording the deletion with an empty snapshot", form.Id);
                    beforeJson = "{}";
                }

                Guid userKey = Guid.Empty;
                var userName = "System";
                if (notification.State.TryGetValue($"{FormDeletingHandler.UserKeyStateKeyPrefix}{form.Id}", out var key)
                    && key is Guid guid
                    && notification.State.TryGetValue($"{FormDeletingHandler.UserNameStateKeyPrefix}{form.Id}", out var name)
                    && name is string s)
                {
                    userKey = guid;
                    userName = s;
                }
                else
                {
                    _logger.LogDebug("No user captured for deleted form {FormId}; recording System", form.Id);
                }

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
}
