using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// Runs a test against a real SQL Server. Skips only when neither FORMSAUDIT_SQLSERVER nor CI is set, so a developer
/// without a server is not blocked. In CI a missing connection string is deliberately not a skip:
/// <see cref="SqlServerTestDatabase.Create"/> throws and the test fails, because a silently skipped provider test
/// would let a SQL Server-only migration break ship unnoticed (P6).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!HasEnv(SqlServerTestDatabase.ConnectionStringVariable) && !HasEnv("CI"))
        {
            Skip = $"Set {SqlServerTestDatabase.ConnectionStringVariable} to run SQL Server tests.";
        }
    }

    internal static bool HasEnv(string name) => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));
}
