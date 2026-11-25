using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>Records the text of every non-query command, so a test can prove how many set-based statements ran.</summary>
internal sealed class CommandCounter : DbCommandInterceptor
{
    public List<string> Commands { get; } = new();

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
}
