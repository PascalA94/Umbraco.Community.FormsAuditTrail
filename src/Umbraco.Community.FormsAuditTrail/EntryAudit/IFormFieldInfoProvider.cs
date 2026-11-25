namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

internal sealed record FieldInfo(string Caption, bool IsSensitive);

public interface IFormFieldInfoProvider
{
    internal IReadOnlyDictionary<Guid, FieldInfo> GetFields(Guid formId);

    /// <summary>
    /// The fields of a form this request has already read, with no read of its own. For code that runs inside Forms'
    /// save scope, where a database read would open the SQLite snapshot ahead of Forms' own write. Empty when the form
    /// was not read earlier in the request.
    /// </summary>
    internal IReadOnlyDictionary<Guid, FieldInfo> GetLoadedFields(Guid formId);
}
