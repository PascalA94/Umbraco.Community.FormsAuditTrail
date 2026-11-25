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
/// (spec 7.3, form permanent delete).
/// </summary>
/// <remarks>
/// Forms publishes this only for a permanent delete (an IL check on 18.2.2 and 18.3.1 found it reached from
/// <c>PermanentDelete</c> and the folder cascade, never from <c>Trash</c>; 17.x is checked by the E2E delete specs). Forms publishes no per-record
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

            await ScrubFormAsync(form, notification);
            await WriteAccessRowAsync(form, notification);
        }
    }

    /// <summary>
    /// The erasure scrub for the deleted form's entries, through the same path as the daily reconciliation limited to
    /// this form: only values of records Forms no longer holds are scrubbed, checked by <c>UniqueId</c> alone
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
    /// Who deleted the form, for the entry-access row (SEC-L5). The user saved while deleting comes first; then the
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

        // Throw-safe: this is also called from a catch block and must not cost the access row. A lookup that
        // fails is an unresolvable user, recorded as Unknown, never System.
        try
        {
            return AuditHandlerHelpers.TryGetCurrentUser(_securityAccessor, out Guid key, out string name)
                ? (key, name, false)
                : (Guid.Empty, AuditUserNames.Unknown, true);
        }
        catch
        {
            return (Guid.Empty, AuditUserNames.Unknown, true);
        }
    }
}
