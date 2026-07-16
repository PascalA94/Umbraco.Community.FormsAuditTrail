using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

public class FormSavedHandler : INotificationAsyncHandler<FormSavedNotification>
{
    private readonly IFormSnapshotService _snapshotService;
    private readonly IFormDiffService _diffService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IBackOfficeSecurityAccessor _backOfficeSecurityAccessor;
    private readonly ILogger<FormSavedHandler> _logger;

    public FormSavedHandler(
        IFormSnapshotService snapshotService,
        IFormDiffService diffService,
        IServiceScopeFactory scopeFactory,
        IBackOfficeSecurityAccessor backOfficeSecurityAccessor,
        ILogger<FormSavedHandler> logger)
    {
        _snapshotService = snapshotService;
        _diffService = diffService;
        _scopeFactory = scopeFactory;
        _backOfficeSecurityAccessor = backOfficeSecurityAccessor;
        _logger = logger;
    }

    public async Task HandleAsync(FormSavedNotification notification, CancellationToken cancellationToken)
    {
        foreach (Form form in notification.SavedEntities)
        {
            try
            {
                var afterJson = _snapshotService.Serialize(form);
                var isNew = notification.State.TryGetValue($"{FormSavingHandler.IsNewStateKeyPrefix}{form.Id}", out var isNewObj)
                    && isNewObj is true;
                var notificationBeforeJson = notification.State.TryGetValue($"{FormSavingHandler.BeforeSnapshotStateKeyPrefix}{form.Id}", out var bj)
                    ? bj as string
                    : null;
                AuditEventType eventType = isNew ? AuditEventType.Created : AuditEventType.Saved;

                // Use the last audit entry's AfterSnapshot as the before-state for diffs.
                // This is more reliable than the notification state because Umbraco Forms saves
                // workflows to the DB before firing FormSavingNotification, so the notification
                // state misses workflow adds/removes. The previous AfterSnapshot reflects the
                // true last-known state of the form including workflows.
                await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
                FormsAuditDbContext db = scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();

                string? beforeJson = notificationBeforeJson;
                if (!isNew)
                {
                    var lastSnapshot = await db.AuditEntries
                        .Where(e => e.FormId == form.Id && e.AfterSnapshot != null)
                        .OrderByDescending(e => e.Id)
                        .Select(e => e.AfterSnapshot)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (lastSnapshot != null)
                    {
                        beforeJson = lastSnapshot;
                    }
                }

                FormDiffResult? diff = null;
                if (isNew)
                {
                    diff = new FormDiffResult { Summary = "New form created" };
                }
                else if (beforeJson != null)
                {
                    diff = _diffService.ComputeDiff(beforeJson, afterJson);
                }

                // Skip entries with no detectable changes (e.g. workflow property edits which are
                // committed to the DB before the notification fires, so before == after)
                if (diff != null && diff.Changes.Count == 0 && eventType == AuditEventType.Saved)
                {
                    continue;
                }

                // No backoffice user means the save came from a background process —
                // an Umbraco Deploy transfer, a code-based save, etc.
                Guid userKey = Guid.Empty;
                var userName = "System";
                IBackOfficeSecurity? security = _backOfficeSecurityAccessor.BackOfficeSecurity;
                if (security?.CurrentUser != null)
                {
                    userKey = security.CurrentUser.Key;
                    userName = security.CurrentUser.Name ?? "System";
                }

                var entry = new FormAuditEntry
                {
                    FormId = form.Id,
                    FormName = form.Name,
                    UserKey = userKey,
                    UserName = userName,
                    Timestamp = DateTime.UtcNow,
                    EventType = (int)eventType,
                    BeforeSnapshot = beforeJson ?? string.Empty,
                    AfterSnapshot = afterJson,
                    ChangeSummaryJson = diff != null ? JsonSerializer.Serialize(diff) : null,
                };

                if (diff != null)
                {
                    foreach (FormChange change in diff.Changes)
                    {
                        entry.Changes.Add(new FormAuditChange
                        {
                            ChangeType = (int)change.ChangeType,
                            PropertyPath = change.PropertyPath,
                            FriendlyDescription = change.FriendlyDescription,
                            OldValue = change.OldValue,
                            NewValue = change.NewValue,
                            Category = change.Category,
                        });
                    }
                }

                db.AuditEntries.Add(entry);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to record audit entry for form {FormId}", form.Id);
            }
        }
    }
}
