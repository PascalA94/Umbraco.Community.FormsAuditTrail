using Microsoft.Extensions.Logging;
using Umbraco.Forms.Core.Models;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Reads field captions and the sensitive-data flag (<c>Field.ContainsSensitiveData</c>, spike S4) from the form
/// definition. Scoped, like the Forms services it wraps.
/// </summary>
internal sealed class FormFieldInfoProvider(RequestFormCache forms, ILogger<FormFieldInfoProvider> logger) : IFormFieldInfoProvider
{
    public IReadOnlyDictionary<Guid, FieldInfo> GetFields(Guid formId)
    {
        try
        {
            Form? form = forms.Get(formId);
            if (form is null)
            {
                return new Dictionary<Guid, FieldInfo>();
            }

            return Map(form);
        }
        catch (Exception ex)
        {
            // Fail-open (E12): an empty map makes the differ fall back to ids and, under MaskSensitive, to masking every field.
            logger.LogWarning(ex, "Could not load field information for form {FormId}; field metadata unavailable, so unknown fields are treated as sensitive.", formId);
            return new Dictionary<Guid, FieldInfo>();
        }
    }

    public IReadOnlyDictionary<Guid, FieldInfo> GetLoadedFields(Guid formId) =>
        forms.GetLoaded(formId) is { } form ? Map(form) : new Dictionary<Guid, FieldInfo>();

    private static Dictionary<Guid, FieldInfo> Map(Form form)
    {
        var result = new Dictionary<Guid, FieldInfo>();
        foreach (Field field in form.AllFields)
        {
            result[field.Id] = new FieldInfo(field.Caption ?? field.Id.ToString(), field.ContainsSensitiveData);
        }

        return result;
    }
}
