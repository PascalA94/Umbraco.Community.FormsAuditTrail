using Microsoft.EntityFrameworkCore;

namespace Umbraco.Community.FormsAuditTrail.Persistence;

/// <summary>
/// SQLite-specific audit context. EF Core migrations are provider-specific, so each supported
/// database provider gets its own derived context with its own migration set (Migrations/Sqlite).
/// </summary>
public class SqliteFormsAuditDbContext : FormsAuditDbContext
{
    public SqliteFormsAuditDbContext(DbContextOptions<SqliteFormsAuditDbContext> options)
        : base(options)
    {
    }
}
