using System.Collections.Concurrent;

namespace Umbraco.Community.FormsAuditTrail.Services;

/// <summary>
/// Allows one access-log export per user at a time. An export reads a large table and holds a response open, so a
/// script firing many at once would be a cheap way to degrade the backoffice. The state is only user keys, never a
/// request or user object, so a static holds no scoped service (P2). In memory: with several instances the limit is per
/// instance, which is enough to blunt a single abusive session.
/// </summary>
internal static class AccessExportGate
{
    private static readonly ConcurrentDictionary<Guid, byte> Running = new();

    public static bool TryEnter(Guid userKey) => Running.TryAdd(userKey, 0);

    public static void Exit(Guid userKey) => Running.TryRemove(userKey, out _);
}
