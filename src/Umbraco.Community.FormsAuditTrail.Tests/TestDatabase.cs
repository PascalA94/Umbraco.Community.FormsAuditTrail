using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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

    public TestDatabase(params IInterceptor[] interceptors)
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        DbContextOptions<SqliteFormsAuditDbContext> options = new DbContextOptionsBuilder<SqliteFormsAuditDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(interceptors)
            .Options;
        Context = new SqliteFormsAuditDbContext(options);
        Context.Database.EnsureCreated();
    }

    public FormAuditEntry AddEntry(Guid formId, string formName, DateTime timestampUtc, string userName = "Editor", int changeCount = 0, string? afterSnapshot = "{}")
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
            AfterSnapshot = afterSnapshot,
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

    public FormAccessEntry AddAccess(
        AccessEventType type,
        Guid formId,
        DateTime timestampUtc,
        Guid? recordId = null,
        Guid? userKey = null,
        IEnumerable<Guid>? childRecordIds = null,
        IEnumerable<FormEntryChange>? changes = null,
        string? detail = null,
        Guid? correlationId = null)
    {
        var childIds = childRecordIds?.ToList() ?? new List<Guid>();
        var entry = new FormAccessEntry
        {
            Timestamp = timestampUtc,
            EventType = (int)type,
            FormId = formId,
            FormName = "Test form",
            RecordId = recordId,
            RecordCount = recordId is null ? childIds.Count : 1,
            UserKey = userKey ?? Guid.NewGuid(),
            UserName = "Editor",
            Detail = detail,
            CorrelationId = correlationId,
        };
        foreach (var id in childIds)
        {
            entry.Records.Add(new FormAccessRecord { RecordId = id });
        }

        foreach (var change in changes ?? Enumerable.Empty<FormEntryChange>())
        {
            entry.Changes.Add(change);
        }

        Context.AccessEntries.Add(entry);
        Context.SaveChanges();
        return entry;
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
