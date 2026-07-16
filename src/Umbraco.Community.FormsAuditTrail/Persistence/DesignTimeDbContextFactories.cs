using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Umbraco.Community.FormsAuditTrail.Persistence;

// Used by the EF CLI tools at design time only, e.g.:
//   dotnet ef migrations add <Name> --context SqliteFormsAuditDbContext --output-dir Migrations/Sqlite
//   dotnet ef migrations add <Name> --context SqlServerFormsAuditDbContext --output-dir Migrations/SqlServer
// The connection strings are never opened when adding migrations.

public class SqliteFormsAuditDbContextFactory : IDesignTimeDbContextFactory<SqliteFormsAuditDbContext>
{
    public SqliteFormsAuditDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SqliteFormsAuditDbContext>();
        optionsBuilder.UseSqlite(
            "Data Source=audit-design-time.db",
            sqlite => sqlite.MigrationsHistoryTable(FormsAuditDbContext.MigrationsHistoryTableName));
        return new SqliteFormsAuditDbContext(optionsBuilder.Options);
    }
}

public class SqlServerFormsAuditDbContextFactory : IDesignTimeDbContextFactory<SqlServerFormsAuditDbContext>
{
    public SqlServerFormsAuditDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SqlServerFormsAuditDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=(localdb)\\mssqllocaldb;Database=FormsAuditDesignTime;Trusted_Connection=True;",
            sqlServer => sqlServer.MigrationsHistoryTable(FormsAuditDbContext.MigrationsHistoryTableName));
        return new SqlServerFormsAuditDbContext(optionsBuilder.Options);
    }
}
