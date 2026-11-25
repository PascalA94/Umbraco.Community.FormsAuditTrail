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

    [Fact]
    public void Escape_LeadingCarriageReturn_Guarded() // @M4-T18
        => Assert.Equal("\"'\r=1+1\"", CsvFormatter.Escape("\r=1+1"));

    [Theory]
    [InlineData(" =SUM(A1)", "' =SUM(A1)")]
    [InlineData("   @cmd", "'   @cmd")]
    [InlineData("\t+1", "'\t+1")]
    public void Escape_LeadingWhitespaceThenTrigger_Guarded(string input, string expected) // @M4-T18
        => Assert.Equal(expected, CsvFormatter.Escape(input));

    [Theory]
    [InlineData("   ")]
    [InlineData(" plain = text")]
    [InlineData("a=b")]
    public void Escape_WhitespaceOnlyOrTriggerNotFirst_NotGuarded(string input) // @M4-T18
        => Assert.Equal(input, CsvFormatter.Escape(input));

    [Theory]
    [InlineData("\uFF1DSUM(A1)")]
    [InlineData("\uFF0B1")]
    [InlineData("\uFF0D1")]
    [InlineData("\uFF20cmd")]
    [InlineData("  \uFF1D1+1")]
    public void Escape_FullWidthLookalikeTrigger_Guarded(string input) // SEC-L1
        => Assert.Equal("'" + input, CsvFormatter.Escape(input));
}
