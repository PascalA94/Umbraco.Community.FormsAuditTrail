namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

public sealed record FieldInfo(string Caption, bool IsSensitive);

public interface IFormFieldInfoProvider
{
    IReadOnlyDictionary<Guid, FieldInfo> GetFields(Guid formId);
}
