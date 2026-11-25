namespace Umbraco.Community.FormsAuditTrail.Configuration;

/// <summary>How much of an edited entry value the access log stores (design 7.7, D5).</summary>
public enum StoreEditValuesMode
{
    /// <summary>Record that a field changed, never its values.</summary>
    None,

    /// <summary>Store values, masking fields Forms marks as sensitive before they reach the database.</summary>
    MaskSensitive,

    /// <summary>Store every value unmasked. Opt-in: the site owner takes the GDPR decision.</summary>
    All,
}
