using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Security;
using Umbraco.Community.FormsAuditTrail.BackgroundJobs;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Community.FormsAuditTrail.Services;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

/// <summary>
/// Writes the Deleted audit entry after the Forms delete scope has completed, so the audit
/// connection does not contend with it (see <see cref="FormDeletingHandler"/>). Then, as separate steps
/// so neither can cost the definition entry, it scrubs the stored edit values of the form's entries that
/// Forms no longer holds and writes one entry-access <c>Deleted</c> row with <c>deleteKind: "form"</c>
/// (design 7.x, Task 14, R63).
/// </summary>
/// <remarks>
/// Forms 18.2.2 and 18.3.1 publish this only for a permanent delete (an IL check found it reached from
/// <c>PermanentDelete</c> and the folder cascade, never from <c>Trash</c>). Forms publishes no per-record
/// <c>RecordDeleted</c> on this path (S5), so the form's candidates are checked against <c>UFRecords</c>
/// rather than trusted: values are scrubbed only for records that are really gone.
/// </remarks>
public class FormDeletedHandler : INotificationAsyncHandler<FormDeletedNotification>
{
    private readonly IFormSnapshotService _snapshotService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FormDeletedHandler> _logger;
    private readonly IEntryAuditWriter _writer;
    private readonly IEntryAuditCollectorAccessor _collectorAccessor;
    private readonly IOptionsMonitor<FormsAuditTrailOptions> _options;
    private readonly TimeProvider _time;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IBackOfficeSecurityAccessor _securityAccessor;

    public FormDeletedHandler(
        IFormSnapshotService snapshotService,
        IServiceScopeFactory scopeFactory,
        ILogger<FormDeletedHandler> logger,
        IEntryAuditWriter writer,
        IEntryAuditCollectorAccessor collectorAccessor,
        IOptionsMonitor<FormsAuditTrailOptions> options,
        TimeProvider time,
        IHttpContextAccessor httpContextAccessor,
        IBackOfficeSecurityAccessor securityAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
        _securityAccessor = securityAccessor;
        _snapshotService = snapshotService;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _writer = writer;
        _collectorAccessor = collectorAccessor;
        _options = options;
        _time = time;
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
                var userName = n as string ?? AuditUserNames.System;
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

            await ScrubFormAsync(form, notification);
            await WriteAccessRowAsync(form, notification);
        }
    }

    /// <summary>
    /// The erasure scrub for the deleted form's entries, through the same path as the daily reconciliation limited to
    /// this form (R63): only values of records Forms no longer holds are scrubbed, checked by <c>UniqueId</c> alone
    /// (SEC-04), with the same probe self-test, so a reader that cannot match ids scrubs nothing. Not gated on entry
    /// capture being on: values stored while it was on are still personal data once their entries are gone. Never
    /// throws; a failure is left to the daily reconciliation.
    /// </summary>
    private async Task ScrubFormAsync(Form form, FormDeletedNotification notification)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            FormsAuditDbContext db = scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();
            IFormsRecordReader reader = scope.ServiceProvider.GetRequiredService<IFormsRecordReader>();
            // CancellationToken.None: an aborted request must not cancel an erasure.
            await AuditRetentionJob.ReconcileErasuresAsync(db, reader, _time.GetUtcNow().UtcDateTime, ct: CancellationToken.None, formId: form.Id);
        }
        catch (ImplausibleReconciliationException)
        {
            _logger.LogError(
                "FormsAuditTrail did not scrub deleted form {FormId}: the entries table holds a record its reader cannot find, so missing records prove nothing. Nothing was scrubbed; the daily reconciliation will retry.",
                form.Id);
        }
        catch (Exception ex)
        {
            // Exception type only: a database error can quote the values it was updating.
            _logger.LogError(
                "FormsAuditTrail could not scrub the stored edit values of deleted form {FormId} ({ExceptionType}): user {UserKey}, event {EventType}; the daily reconciliation will scrub them.",
                form.Id,
                ex.GetType().Name,
                ResolveUser(form, notification).Key,
                AccessEventType.Deleted);
        }
    }

    /// <summary>
    /// One entry-access row for the form's entries going with it. <c>RecordCount</c> is what Forms published in
    /// <c>RecordDeleting</c> during this request (0 when it published none, or outside a request). The writer never
    /// throws and records its own failures in health.
    /// </summary>
    private async Task WriteAccessRowAsync(Form form, FormDeletedNotification notification)
    {
        try
        {
            if (!_options.CurrentValue.EntryAudit.Enabled)
            {
                return;
            }

            (Guid userKey, string userName, bool unresolved) = ResolveUser(form, notification);
            int recordCount = _collectorAccessor.Current?.DeletedRecordCount(form.Id) ?? 0;
            await _writer.WriteAsync(new AccessWrite(
                AccessEventType.Deleted,
                form.Id,
                // The form is gone, so the writer's lookup would only find "(deleted form)"; the name is still known here.
                form.Name,
                null,
                recordCount,
                userKey,
                userName,
                new EntryAuditDetail { DeleteKind = "form", UserUnresolved = unresolved ? true : null },
                [],
                []));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "FormsAuditTrail could not record the entry delete for form {FormId} ({ExceptionType}).",
                form.Id,
                ex.GetType().Name);
        }
    }

    /// <summary>
    /// Who deleted the form, for the entry-access row (R63, SEC-L5). The user saved while deleting comes first; then the
    /// current backoffice user, resolved per call (P2); then, inside a request, "Unknown" flagged <c>userUnresolved</c>.
    /// "System" is kept for deletes with no HTTP context at all (code, jobs), so a request is never passed off as one.
    /// </summary>
    private (Guid Key, string Name, bool Unresolved) ResolveUser(Form form, FormDeletedNotification notification)
    {
        notification.State.TryGetValue($"{FormDeletingHandler.UserKeyStateKeyPrefix}{form.Id}", out var k);
        notification.State.TryGetValue($"{FormDeletingHandler.UserNameStateKeyPrefix}{form.Id}", out var n);
        // The deleting handler stores the empty key with "System" when it saw no user, so an empty key means none.
        if (k is Guid saved && saved != Guid.Empty && n is string savedName)
        {
            return (saved, savedName, false);
        }

        if (_httpContextAccessor.HttpContext is null)
        {
            return (Guid.Empty, AuditUserNames.System, false);
        }

        return AuditHandlerHelpers.TryGetCurrentUser(_securityAccessor, out Guid key, out string name)
            ? (key, name, false)
            : (Guid.Empty, AuditUserNames.Unknown, true);
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
