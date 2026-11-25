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

        if (StartsLikeFormula(value))
        {
            value = "'" + value;
        }

        if (value.IndexOfAny(_quoteTriggers) >= 0)
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    // SEC-L1: Excel and Sheets skip leading blanks before deciding a cell is a formula, so " =1+1" is as dangerous as
    // "=1+1". A leading tab or carriage return is dangerous on its own because some importers strip it as whitespace.
    private static bool StartsLikeFormula(string value)
    {
        if (value[0] is '\t' or '\r')
        {
            return true;
        }

        foreach (char c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            // The full-width forms are what some spreadsheet locales and IMEs normalise to the ASCII trigger.
            return c is '=' or '+' or '-' or '@' or '\uFF0B' or '\uFF0D' or '\uFF1D' or '\uFF20';
        }

        return false;
    }
}
