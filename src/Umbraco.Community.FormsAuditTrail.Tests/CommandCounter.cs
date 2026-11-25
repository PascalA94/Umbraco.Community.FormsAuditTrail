using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>Records the text of every non-query command, so a test can prove how many set-based statements ran.</summary>
internal sealed class CommandCounter : DbCommandInterceptor
{
    public List<string> Commands { get; } = new();

    /// <summary>The text of every query (reader) command, for tests that prove how a read was split into statements.</summary>
    public List<string> Queries { get; } = new();

    public int Count(string prefix) =>
        Commands.Count(c => c.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Commands.Add(command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Queries.Add(command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
