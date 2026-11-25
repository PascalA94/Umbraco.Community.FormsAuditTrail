using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// The form definitions one request has read through <see cref="IFormService"/>. An audited edit needs its form three
/// times: for the before read through Forms' storage, for the field captions and sensitive flags of the diff, and for
/// the form name on the row. <see cref="IFormService.Get(Guid)"/> goes to the database each time (2 to 3 ms per call on
/// SQL Server, measured in the perf-edit report), which alone took the edit overhead past the 10 ms target.
/// </summary>
/// <remarks>
/// Scoped, so a definition never outlives the request that read it and the next request sees any change to the form.
/// A form that is not found is not remembered, so a later caller in the same request asks Forms again, as it did
/// before. Exceptions are not caught here; every caller already fails open on its own terms.
/// </remarks>
internal sealed class RequestFormCache(IFormService forms)
{
    private readonly Dictionary<Guid, Form> _loaded = [];

    public Form? Get(Guid formId)
    {
        if (_loaded.TryGetValue(formId, out Form? form))
        {
            return form;
        }

        form = forms.Get(formId);
        if (form is not null)
        {
            _loaded[formId] = form;
        }

        return form;
    }

    /// <summary>The form if this request already read it, without a read of its own.</summary>
    public Form? GetLoaded(Guid formId) => _loaded.GetValueOrDefault(formId);
}
