using Umbraco.Forms.Core.Models;

namespace Umbraco.Community.FormsAuditTrail.Services;

public interface IFormSnapshotService
{
    public string Serialize(Form form);
}
