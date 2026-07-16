using Umbraco.Community.FormsAuditTrail.Persistence;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class UmbracoDatabaseProviderDetectorTests
{
    [Theory]
    [InlineData("Microsoft.Data.Sqlite", null, true)]
    [InlineData("Microsoft.Data.SqlClient", null, false)]
    // Explicit provider name wins over whatever the connection string looks like
    [InlineData("Microsoft.Data.SqlClient", "Data Source=|DataDirectory|/Umbraco.sqlite.db", false)]
    public void ProviderName_WinsWhenPresent(string providerName, string? connectionString, bool expected)
        => Assert.Equal(expected, UmbracoDatabaseProviderDetector.IsSqlite(providerName, connectionString));

    [Theory]
    [InlineData("Data Source=|DataDirectory|/Umbraco.sqlite.db;Cache=Shared;Foreign Keys=True;Pooling=True", true)]
    [InlineData("Data Source=./umbraco/Data/site.db;Cache=Shared", true)]
    [InlineData("Server=tcp:example.database.windows.net;Database=umbraco;User Id=u;Password=p;", false)]
    [InlineData("Data Source=tcp:example.database.windows.net;Initial Catalog=umbraco;User Id=u;Password=p;", false)]
    [InlineData("Server=(localdb)\\mssqllocaldb;Database=Umbraco;Trusted_Connection=True;", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void WithoutProviderName_ConnectionStringIsSniffed(string? connectionString, bool expected)
        => Assert.Equal(expected, UmbracoDatabaseProviderDetector.IsSqlite(null, connectionString));
}
