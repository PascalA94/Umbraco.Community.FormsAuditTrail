using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Forms.Core.Services;
using Umbraco.Forms.Core.Services.Notifications;

namespace Umbraco.Community.FormsAuditTrail.DemoSite;

/// <summary>
/// Test stand-in for an at-rest encryption package (plan P3, spike S10). It lives in the demo site, never in the
/// package: its only job is to prove that the audit reads before values through Forms' read path, so a "decrypting"
/// handler from another assembly runs on our read too, and that the foreign-handler check sees it. Registering it in
/// a real site would corrupt every short-text value it reads, which is why it is off unless asked for twice over.
/// </summary>
/// <remarks>
/// Active only when <c>E2E:SimulateEncryption</c> is true and the host environment is Development. It reverses short-text
/// values on read, standing in for decryption of values stored reversed. Prevalue fields and upload paths are left
/// alone: Forms validates those on save and a one-way stand-in cannot round-trip them (S10 saw 400s when it tried).
/// </remarks>
public sealed class SimulatedEncryptionComposer : IComposer
{
    internal const string SettingKey = "E2E:SimulateEncryption";

    public void Compose(IUmbracoBuilder builder)
    {
        // Host environment, not a configuration key: see E2EGate.
        if (!E2EGate.IsEnabled(builder, SettingKey))
        {
            return;
        }

        builder.AddNotificationHandler<RecordFetchingNotification, SimulatedDecryptionHandler>();
    }
}

/// <summary>The "decrypting" read hook. Demo site only; see <see cref="SimulatedEncryptionComposer"/>.</summary>
public sealed class SimulatedDecryptionHandler(IFormService formService, ILogger<SimulatedDecryptionHandler> logger)
    : INotificationHandler<RecordFetchingNotification>
{
    // Forms' built-in short answer field type.
    private static readonly Guid ShortTextFieldType = new("3f92e01b-29e2-4a30-bf33-9df5580ed52c");

    public void Handle(RecordFetchingNotification notification)
    {
        HashSet<Guid> textFields = formService.Get(notification.Record.Form)?.AllFields
            .Where(f => f.FieldTypeId == ShortTextFieldType)
            .Select(f => f.Id)
            .ToHashSet() ?? [];

        int reversed = 0;
        foreach (var field in notification.Record.RecordFields.Values.Where(f => textFields.Contains(f.FieldId)))
        {
            field.Values = field.Values.Select(v => v is string s ? (object)new string(s.Reverse().ToArray()) : v).ToList();
            reversed++;
        }

        // Counts only: the values are entry data even on the demo site.
        logger.LogInformation("SimulatedEncryption reversed {Count} fields on read.", reversed);
    }
}
