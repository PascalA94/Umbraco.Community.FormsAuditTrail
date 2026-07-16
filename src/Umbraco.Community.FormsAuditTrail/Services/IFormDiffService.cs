using Umbraco.Community.FormsAuditTrail.Models;

namespace Umbraco.Community.FormsAuditTrail.Services;

public interface IFormDiffService
{
    public FormDiffResult ComputeDiff(string beforeJson, string afterJson);
}
