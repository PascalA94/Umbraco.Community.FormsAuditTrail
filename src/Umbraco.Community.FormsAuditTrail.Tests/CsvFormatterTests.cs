using Umbraco.Community.FormsAuditTrail.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class CsvFormatterTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    public void PlainValues_PassThrough(string? input, string expected)
        => Assert.Equal(expected, CsvFormatter.Escape(input));

    [Theory]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("line1\rline2", "\"line1\rline2\"")]
    public void SpecialCharacters_AreQuotedAndEscaped(string input, string expected)
        => Assert.Equal(expected, CsvFormatter.Escape(input));

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil\")", "\"'=HYPERLINK(\"\"http://evil\"\")\"")]
    [InlineData("+SUM(A1)", "'+SUM(A1)")]
    [InlineData("-1+2", "'-1+2")]
    [InlineData("@cmd", "'@cmd")]
    public void FormulaPrefixes_AreNeutralised(string input, string expected)
        => Assert.Equal(expected, CsvFormatter.Escape(input));
}
