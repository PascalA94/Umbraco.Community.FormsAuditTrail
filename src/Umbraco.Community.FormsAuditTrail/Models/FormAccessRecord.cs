namespace Umbraco.Community.FormsAuditTrail.Models;

/// <summary>A record id that was part of an access, so "which exports contained this entry" can be answered.</summary>
public class FormAccessRecord
{
    public long AccessId { get; set; }
    public Guid RecordId { get; set; }

    public FormAccessEntry? Access { get; set; }
}
