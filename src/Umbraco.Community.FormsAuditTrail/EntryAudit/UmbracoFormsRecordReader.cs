using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Reads only <c>UniqueId</c> from <c>UFRecords</c>. Forms exposes no cheap "which of these exist" API and loading
/// records would pull their (personal) data into this package, so the one column is read directly.
/// </summary>
internal sealed class UmbracoFormsRecordReader(IScopeProvider scopeProvider) : IFormsRecordReader
{
    // Each id is sent twice (upper and lower case), so 250 ids keep a statement at 500 parameters, well under
    // SQL Server's 2,100 limit.
    internal const int BatchSize = 250;

    public Task<IReadOnlySet<Guid>> FindExistingAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken ct)
    {
        var found = new HashSet<Guid>();
        if (recordIds.Count == 0)
        {
            return Task.FromResult<IReadOnlySet<Guid>>(found);
        }

        // A synchronous scope: Umbraco's NPoco database is not safe to use across awaits on a different thread.
        // No catch: an unreadable table must surface as an exception, never as an empty "nothing exists" answer,
        // because the caller scrubs every id this method does not report.
        using IScope scope = scopeProvider.CreateScope(autoComplete: true);
        var table = scope.SqlContext.SqlSyntax.GetQuotedTableName("UFRecords");
        var column = scope.SqlContext.SqlSyntax.GetQuotedColumnName("UniqueId");
        var sql = $"SELECT {column} FROM {table} WHERE {column} IN (@0)";

        foreach (Guid[] batch in recordIds.Chunk(BatchSize))
        {
            ct.ThrowIfCancellationRequested();

            // Matched as text instead of leaning on the provider's Guid binding. On SQLite UniqueId is a TEXT column
            // with binary collation, so a parameter in another case or format silently matches nothing, and an
            // empty result would read as "every record was erased". Forms writes upper-case "D" text; the lower-case
            // twin covers a row written by anything else. SQL Server converts the text to uniqueidentifier, so
            // the duplicates are harmless there.
            var texts = batch.Select(id => id.ToString("D").ToUpperInvariant())
                .Concat(batch.Select(id => id.ToString("D").ToLowerInvariant()))
                .ToArray();

            // NPoco expands one collection argument into one parameter per item. The collection must be wrapped in
            // an object[]: passed bare, params binds it as the argument array itself (string[] is covariant to
            // object[]) and each id becomes its own positional argument, so @0 would be only the first id.
            foreach (Guid id in scope.Database.Fetch<Guid>(sql, new object[] { texts }))
            {
                found.Add(id);
            }
        }

        return Task.FromResult<IReadOnlySet<Guid>>(found);
    }

    public Task<Guid?> FindAnyExistingIdAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using IScope scope = scopeProvider.CreateScope(autoComplete: true);
        var table = scope.SqlContext.SqlSyntax.GetQuotedTableName("UFRecords");
        var column = scope.SqlContext.SqlSyntax.GetQuotedColumnName("UniqueId");

        // Ordered so that SQL Server's paging has the ORDER BY it requires; the index on UniqueId serves it.
        List<Guid> first = scope.Database.SkipTake<Guid>(0, 1, scope.SqlContext.Sql($"SELECT {column} FROM {table} ORDER BY {column}"));
        return Task.FromResult<Guid?>(first.Count == 0 ? null : first[0]);
    }
}
