namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Whether the current backoffice user holds Forms' "view sensitive data" permission. Scoped: it answers for the
/// request's user and is never held by a singleton (P2).
/// </summary>
public interface ISensitiveDataPermission
{
    internal bool CurrentUserCanViewSensitiveData();
}
