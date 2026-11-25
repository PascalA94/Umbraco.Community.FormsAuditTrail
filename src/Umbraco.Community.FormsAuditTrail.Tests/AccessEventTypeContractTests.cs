using System.Text.Json;
using Umbraco.Community.FormsAuditTrail.Models;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// The dashboard's event filter list is checked in as JSON and imported by the client, which tests it against the same
/// file. This is the server half: the enum cannot gain, lose or renumber a member without this file changing too.
/// </summary>
public class AccessEventTypeContractTests
{
    private sealed record Entry(int Value, string Name);

    [Fact]
    public void AccessEventType_MatchesTheCheckedInClientList() // @M4-FIX item 9
    {
        string path = Path.Combine(FindRepoRoot(), "src", "Umbraco.Community.FormsAuditTrail", "Client", "src", "utils", "access-event-types.json");
        Entry[] fromFile = JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Entry[] fromEnum = Enum.GetValues<AccessEventType>()
            .OrderBy(v => (int)v)
            .Select(v => new Entry((int)v, v.ToString()))
            .ToArray();

        Assert.Equal(fromEnum, fromFile);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (CLAUDE.md) not found.");
    }
}
