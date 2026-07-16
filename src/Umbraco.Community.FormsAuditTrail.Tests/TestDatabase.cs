using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// In-memory SQLite database for tests that need a real relational provider
/// (cascade deletes, ExecuteDelete, translated queries). The connection must stay
/// open for the database to survive, so dispose the wrapper, not just the context.
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public SqliteFormsAuditDbContext Context { get; }

    public TestDatabase()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        DbContextOptions<SqliteFormsAuditDbContext> options = new DbContextOptionsBuilder<SqliteFormsAuditDbContext>()
            .UseSqlite(_connection)
            .Options;
        Context = new SqliteFormsAuditDbContext(options);
        Context.Database.EnsureCreated();
    }

    public FormAuditEntry AddEntry(Guid formId, string formName, DateTime timestampUtc, string userName = "Editor", int changeCount = 0)
    {
        var entry = new FormAuditEntry
        {
            FormId = formId,
            FormName = formName,
            UserKey = Guid.NewGuid(),
            UserName = userName,
            Timestamp = timestampUtc,
            EventType = (int)AuditEventType.Saved,
            BeforeSnapshot = "{}",
            AfterSnapshot = "{}",
        };
        for (var i = 0; i < changeCount; i++)
        {
            entry.Changes.Add(new FormAuditChange
            {
                ChangeType = (int)ChangeType.Modified,
                PropertyPath = $"Name{i}",
                FriendlyDescription = "changed",
            });
        }

        Context.AuditEntries.Add(entry);
        Context.SaveChanges();
        return entry;
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
