using Microsoft.EntityFrameworkCore;

namespace Umbraco.Community.FormsAuditTrail.Persistence;

/// <summary>
/// SQL Server-specific audit context. EF Core migrations are provider-specific, so each supported
/// database provider gets its own derived context with its own migration set (Migrations/SqlServer).
/// </summary>
public class SqlServerFormsAuditDbContext : FormsAuditDbContext
{
    public SqlServerFormsAuditDbContext(DbContextOptions<SqlServerFormsAuditDbContext> options)
        : base(options)
    {
    }
}
