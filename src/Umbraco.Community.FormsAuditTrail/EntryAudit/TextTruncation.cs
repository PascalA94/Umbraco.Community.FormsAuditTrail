namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

internal static class TextTruncation
{
    /// <summary>
    /// Cuts <paramref name="value"/> to at most <paramref name="maxLength"/> UTF-16 units. A cut between the halves of a
    /// surrogate pair would leave an invalid string that some providers reject or store as a replacement character, so
    /// the cut moves one unit earlier instead.
    /// </summary>
    public static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        int cut = maxLength > 0 && char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..cut];
    }
}
