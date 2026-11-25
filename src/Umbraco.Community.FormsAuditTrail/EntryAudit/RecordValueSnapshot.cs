using Umbraco.Forms.Core.Persistence.Dtos;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

internal static class RecordValueSnapshot
{
    /// <summary>
    /// Copies the values so a later change to the same <see cref="Record"/> instance cannot alter what was captured
    /// (SEC-04). Keyed by <c>RecordField.FieldId</c>: the dictionary key on a stored record is the record-field key,
    /// not the form field id (S2). A blanked field keeps its entry with an empty list.
    /// </summary>
    internal static IReadOnlyDictionary<Guid, IReadOnlyList<object?>> From(Record record)
    {
        var result = new Dictionary<Guid, IReadOnlyList<object?>>();
        foreach (RecordField field in record.RecordFields.Values)
        {
            // Strings and value types are immutable, so sharing them is a copy. Anything else is flattened to text
            // because the differ compares text anyway and a shared mutable object would defeat the snapshot.
            result[field.FieldId] = field.Values?.Select(Copy).ToList() ?? [];
        }

        return result;
    }

    private static object? Copy(object? value)
    {
        if (value is null or string or ValueType)
        {
            return value;
        }

        // A type without its own ToString would give its type name, which would hide a real edit; JSON keeps the content.
        string? text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        return text is null || text == value.GetType().ToString()
            ? System.Text.Json.JsonSerializer.Serialize(value, EntryEditDiffer.JsonOptions)
            : text;
    }
}
