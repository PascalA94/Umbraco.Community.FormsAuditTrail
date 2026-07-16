namespace Umbraco.Community.FormsAuditTrail.Persistence;

internal static class UmbracoDatabaseProviderDetector
{
    /// <summary>
    /// Decides whether the configured Umbraco database is SQLite. The provider name setting
    /// (umbracoDbDSN_ProviderName) wins when present, but it is not always configured — some
    /// setups (e.g. Umbraco Cloud local development) expose only the connection string, so the
    /// connection string itself is sniffed as a fallback. Defaults to SQL Server when unsure.
    /// </summary>
    public static bool IsSqlite(string? providerName, string? connectionString)
    {
        if (!string.IsNullOrEmpty(providerName))
        {
            return providerName.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);
        }

        if (string.IsNullOrEmpty(connectionString))
        {
            return false;
        }

        if (connectionString.Contains("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
            && connectionString.Contains(".db", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("Database=", StringComparison.OrdinalIgnoreCase);
    }
}
