namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

internal sealed record FieldInfo(string Caption, bool IsSensitive);

public interface IFormFieldInfoProvider
{
    internal IReadOnlyDictionary<Guid, FieldInfo> GetFields(Guid formId);
}
