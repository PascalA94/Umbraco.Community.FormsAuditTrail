using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Forms.Core.Persistence.Dtos;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.NotificationHandlers;

/// <summary>
/// Turns the record Forms is about to save into what the capture core writes after the action (spec 7.3 handler steps
/// 1 to 5): a diff for a backoffice edit, the captions of the submitted fields for a backoffice create (P4). At
/// <c>RecordSaving</c> the notification holds the after values and the before values were copied by the filter, through
/// Forms' own read path, before the action ran (SEC-04). So this handler reads no database and writes nothing: a write
/// here would run inside Forms' save scope, which locks SQLite (S3, S5).
/// </summary>
/// <remarks>
/// Front-end submissions also publish this notification but never open a window, so they are ignored (S1). The field
/// provider is scoped and reached through the request, never injected, so this handler holds nothing scoped (P2). It
/// never throws, because it runs inside Forms' save.
/// </remarks>
public sealed class RecordSavingHandler(
    IEntryAuditCollectorAccessor accessor,
    IHttpContextAccessor httpContextAccessor,
    IOptionsMonitor<FormsAuditTrailOptions> options,
    ILogger<RecordSavingHandler> logger) : INotificationHandler<RecordSavingNotification>
{
    public void Handle(RecordSavingNotification notification)
    {
        try
        {
            if (accessor.Current is not { Window: { Kind: AccessEventType.Edited or AccessEventType.Created } window } collector)
            {
                return;
            }

            foreach (Record record in notification.SavedEntities)
            {
                if (window.Kind == AccessEventType.Edited)
                {
                    HandleEdit(collector, window, record);
                }
                else
                {
                    HandleCreate(collector, record);
                }
            }
        }
        catch (Exception ex)
        {
            // Only the type: an exception message here could carry the entry values being saved.
            SafeLog.Run(() => logger.LogError("FormsAuditTrail could not capture an entry save ({ExceptionType}).", ex.GetType().Name));
        }
    }

    private void HandleEdit(EntryAuditCollector collector, CaptureWindow window, Record record)
    {
        // Insert path: Forms inserts with Id 0, which in an edit request can only be some other record being created.
        if (record.Id <= 0)
        {
            return;
        }

        // The window is for the record in the route; anything else Forms saves in the request is not this edit.
        if (window.RecordId is { } expected && record.UniqueId != expected)
        {
            return;
        }

        // Null when the filter's read failed. The core then writes the edit with beforeUnavailable rather than a diff
        // that would show every field as changed.
        if (window.BeforeValues is not { } before)
        {
            return;
        }

        EntryAuditOptions audit = options.CurrentValue.EntryAudit;
        IReadOnlyList<EntryFieldChange> changes = EntryEditDiffer.Diff(
            before,
            RecordValueSnapshot.From(record),
            Fields(record.Form),
            audit.StoreEditValues,
            audit.KeepClearedValues);
        collector.SetEdit(record.Form, record.UniqueId, changes);
    }

    private void HandleCreate(EntryAuditCollector collector, Record record)
    {
        IReadOnlyDictionary<Guid, FieldInfo> fields = Fields(record.Form);
        // P4: which fields were filled in is useful and harmless; what was typed into them is personal data and is
        // never handed on, so only the caption leaves this method.
        string[] captions = record.RecordFields.Values
            .Where(f => f.Values?.Any(v => !string.IsNullOrWhiteSpace(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))) == true)
            .Select(f => fields.TryGetValue(f.FieldId, out FieldInfo? info) ? info.Caption : f.FieldId.ToString())
            .ToArray();
        collector.SetCreated(record.Form, record.UniqueId, captions);
    }

    private IReadOnlyDictionary<Guid, FieldInfo> Fields(Guid formId) =>
        httpContextAccessor.HttpContext?.RequestServices.GetService<IFormFieldInfoProvider>()?.GetFields(formId)
        // No provider means no captions or sensitivity: the differ then masks every field under MaskSensitive.
        ?? new Dictionary<Guid, FieldInfo>();
}
