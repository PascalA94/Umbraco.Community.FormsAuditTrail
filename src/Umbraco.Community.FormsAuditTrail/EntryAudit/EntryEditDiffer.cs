using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Umbraco.Community.FormsAuditTrail.Configuration;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

internal static class EntryEditDiffer
{
    internal const string MaskedValue = "[masked]";

    // Readable non-ASCII (Zoë stays Zoë) while HTML-sensitive characters stay escaped.
    internal static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    internal static IReadOnlyList<EntryFieldChange> Diff(
        IReadOnlyDictionary<Guid, IReadOnlyList<object?>> before,
        IReadOnlyDictionary<Guid, IReadOnlyList<object?>> after,
        IReadOnlyDictionary<Guid, FieldInfo> fields,
        StoreEditValuesMode mode,
        bool keepClearedValues)
    {
        var changes = new List<EntryFieldChange>();
        foreach (Guid id in after.Keys.Concat(before.Keys.Where(k => !after.ContainsKey(k))))
        {
            IReadOnlyList<object?> oldValues = before.TryGetValue(id, out var o) ? o : [];
            IReadOnlyList<object?> newValues = after.TryGetValue(id, out var n) ? n : [];
            if (!Changed(oldValues, newValues))
            {
                continue;
            }

            string? oldText = FormatValues(oldValues);
            string? newText = FormatValues(newValues);
            // From the values, not the text: ["", " "] serialises to a non-blank JSON array but is still a blanked field (SEC-03).
            bool cleared = IsBlank(newValues);
            bool known = fields.TryGetValue(id, out FieldInfo? info);
            // An unknown field cannot be shown to be non-sensitive, so MaskSensitive treats it as sensitive.
            bool sensitive = info?.IsSensitive ?? !known;
            bool masked = mode == StoreEditValuesMode.MaskSensitive && sensitive;

            string? storedOld;
            string? storedNew;
            if (mode == StoreEditValuesMode.None)
            {
                storedOld = storedNew = null;
            }
            else if (cleared)
            {
                // SEC-03: a blanked field keeps no old value unless the site owner opted in.
                storedOld = keepClearedValues ? (masked ? MaskedValue : oldText) : null;
                storedNew = null;
            }
            else
            {
                storedOld = masked ? MaskedValue : oldText;
                storedNew = masked ? MaskedValue : newText;
            }

            changes.Add(new EntryFieldChange(id, info?.Caption ?? id.ToString(), storedOld, storedNew, masked && mode != StoreEditValuesMode.None, cleared));
        }

        return changes;
    }

    /// <summary>No values gives null, one gives its text, several give a JSON array string.</summary>
    internal static string? FormatValues(IReadOnlyList<object?> values)
    {
        var texts = values.Select(Text).ToList();
        return texts.Count switch
        {
            0 => null,
            1 => texts[0],
            _ => JsonSerializer.Serialize(texts, JsonOptions),
        };
    }

    private static string? Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture);

    // Blank (nothing, or only whitespace) on both sides is no change: Forms may store "" where it held no value.
    private static bool Changed(IReadOnlyList<object?> before, IReadOnlyList<object?> after) =>
        !(IsBlank(before) && IsBlank(after)) && !before.Select(Text).SequenceEqual(after.Select(Text));

    private static bool IsBlank(IReadOnlyList<object?> values) => values.All(v => string.IsNullOrWhiteSpace(Text(v)));
}
