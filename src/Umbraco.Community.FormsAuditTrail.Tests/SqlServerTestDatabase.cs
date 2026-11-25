using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Umbraco.Community.FormsAuditTrail.Persistence;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// A uniquely named, throwaway database on the server named by FORMSAUDIT_SQLSERVER. The variable holds a connection
/// string to the server (any database, normally master); the database is dropped on dispose.
/// </summary>
internal sealed class SqlServerTestDatabase : IDisposable
{
    public const string ConnectionStringVariable = "FORMSAUDIT_SQLSERVER";

    private readonly string _serverConnectionString;
    private readonly string _databaseName;

    private SqlServerTestDatabase(string serverConnectionString, string databaseName)
    {
        _serverConnectionString = serverConnectionString;
        _databaseName = databaseName;
        ConnectionString = new SqlConnectionStringBuilder(serverConnectionString)
        {
            InitialCatalog = databaseName,
        }.ConnectionString;
    }

    public string ConnectionString { get; }

    public static SqlServerTestDatabase Create()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrEmpty(connectionString))
        {
            throw new InvalidOperationException($"{ConnectionStringVariable} must be set in CI");
        }

        var databaseName = "FormsAudit_" + Guid.NewGuid().ToString("N");
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE [{databaseName}]";
        command.ExecuteNonQuery();
        return new SqlServerTestDatabase(connectionString, databaseName);
    }

    public SqlServerFormsAuditDbContext CreateContext()
    {
        DbContextOptions<SqlServerFormsAuditDbContext> options = new DbContextOptionsBuilder<SqlServerFormsAuditDbContext>()
            .UseSqlServer(ConnectionString, sql => sql.MigrationsHistoryTable(FormsAuditDbContext.MigrationsHistoryTableName))
            .Options;
        return new SqlServerFormsAuditDbContext(options);
    }

    public void Dispose()
    {
        // Pooled connections would keep the database busy and make DROP fail.
        SqlConnection.ClearAllPools();
        using var connection = new SqlConnection(_serverConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}]";
        command.ExecuteNonQuery();
    }
}
