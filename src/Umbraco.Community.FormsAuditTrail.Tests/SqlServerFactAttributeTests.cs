using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

// Sets CI and FORMSAUDIT_SQLSERVER for the process, so it must not overlap any other test.
[Collection("UmbracoHost")]
public class SqlServerFactAttributeTests
{
    [Fact]
    public void SqlServerFact_InCiWithoutConnectionString_DoesNotSkip()
    {
        var oldCi = Environment.GetEnvironmentVariable("CI");
        var oldConnection = Environment.GetEnvironmentVariable(SqlServerTestDatabase.ConnectionStringVariable);
        try
        {
            Environment.SetEnvironmentVariable("CI", "true");
            Environment.SetEnvironmentVariable(SqlServerTestDatabase.ConnectionStringVariable, null);

            Assert.Null(new SqlServerFactAttribute().Skip);
            var ex = Assert.Throws<InvalidOperationException>(SqlServerTestDatabase.Create);
            Assert.Equal("FORMSAUDIT_SQLSERVER must be set in CI", ex.Message);

            Environment.SetEnvironmentVariable("CI", null);
            Assert.NotNull(new SqlServerFactAttribute().Skip);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CI", oldCi);
            Environment.SetEnvironmentVariable(SqlServerTestDatabase.ConnectionStringVariable, oldConnection);
        }
    }
}
