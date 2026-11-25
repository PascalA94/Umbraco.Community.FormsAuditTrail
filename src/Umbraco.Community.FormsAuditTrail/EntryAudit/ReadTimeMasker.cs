using Umbraco.Community.FormsAuditTrail.Controllers.Dtos;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Read-time masking of stored edit values, which fails closed (SEC-12, design 7.5 step 5). The write-time
/// <c>Masked</c> flag reflects the field as it was at edit time, so a field flagged sensitive later, a deleted field or a
/// form that cannot be loaded are all masked too.
/// </summary>
/// <remarks>
/// Spike S4 found Forms' public permission check, so the spec's fallback is not used: a viewer with "view sensitive
/// data" sees past every condition except the write-time flag, as Forms itself would show them the value. A value masked
/// at write time was never stored, so nothing can lift that one.
/// </remarks>
internal static class ReadTimeMasker
{
    public const string MaskedValue = "[masked]";

    public static EntryChangeDto Apply(
        EntryChangeDto change,
        IReadOnlyDictionary<Guid, FieldInfo> currentFields,
        bool formLoaded,
        bool viewerCanSeeSensitive)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(currentFields);

        bool mask = change.Masked || (!viewerCanSeeSensitive && !IsKnownAndNotSensitive(change.FieldId, currentFields, formLoaded));
        if (!mask)
        {
            return Copy(change, change.OldValue, change.NewValue, masked: false);
        }

        // A null stays null: it means scrubbed, cleared or never stored, and "[masked]" would claim a value exists.
        return Copy(change, change.OldValue is null ? null : MaskedValue, change.NewValue is null ? null : MaskedValue, masked: true);
    }

    private static bool IsKnownAndNotSensitive(Guid fieldId, IReadOnlyDictionary<Guid, FieldInfo> fields, bool formLoaded) =>
        formLoaded && fields.TryGetValue(fieldId, out FieldInfo? info) && !info.IsSensitive;

    private static EntryChangeDto Copy(EntryChangeDto source, string? oldValue, string? newValue, bool masked) => new()
    {
        FieldId = source.FieldId,
        FieldCaption = source.FieldCaption,
        OldValue = oldValue,
        NewValue = newValue,
        Masked = masked,
        Cleared = source.Cleared,
        ValuesScrubbedAt = source.ValuesScrubbedAt,
    };
}
