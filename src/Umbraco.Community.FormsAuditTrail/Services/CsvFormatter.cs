namespace Umbraco.Community.FormsAuditTrail.Services;

internal static class CsvFormatter
{
    private static readonly char[] _quoteTriggers = [',', '"', '\n', '\r'];

    /// <summary>
    /// Escapes a value for a CSV cell, including a guard against spreadsheet formula
    /// injection (a form or user name starting with '=' must not execute when opened in Excel).
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t')
        {
            value = "'" + value;
        }

        if (value.IndexOfAny(_quoteTriggers) >= 0)
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }
}
