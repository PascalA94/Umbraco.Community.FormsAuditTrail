using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Models;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Stops the same view being written again and again (refreshing a list, paging back). Only view events are
/// deduplicated; an edit, delete, export, download, retry, create, config change or gap is always written.
/// </summary>
/// <remarks>
/// Owns a private bounded <see cref="MemoryCache"/> instead of using the shared <c>IMemoryCache</c>: keys vary with the
/// filter hash and page, so one user can mint keys at request rate, and a bound on a shared cache would evict other
/// components' entries (SEC-L6). It holds no user state beyond opaque keys. Per server is enough in a load-balanced
/// site: a rare duplicate row is acceptable, a missing one is not.
/// Adapted from UserActivity <c>Security/ViewThrottle.cs</c> (the same "has this viewer's view of this subject been
/// logged inside the window" check), replacing its unbounded dictionary with a size-limited cache.
/// </remarks>
public sealed class EntryAuditDeduplicator(IOptionsMonitor<FormsAuditTrailOptions> options, TimeProvider time) : IDisposable
{
    internal const int MaxEntries = 50_000;

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = MaxEntries });
    private readonly object _gate = new();

    /// <summary>True when the event should be written. A write is recorded, so an identical event inside the window returns false.</summary>
    public bool ShouldWrite(AccessEventType type, Guid userKey, Guid formId, Guid? recordId, string filterHash)
    {
        int seconds = options.CurrentValue.EntryAudit.DeduplicateSeconds;
        if (seconds <= 0 || !IsDeduplicated(type))
        {
            return true;
        }

        var window = TimeSpan.FromSeconds(seconds);
        string key = Key(type, userKey, formId, recordId, filterHash);
        DateTimeOffset now = time.GetUtcNow();

        lock (_gate)
        {
            // Expiry is judged against the stored timestamp so the injected clock decides it (P15a). The cache's own
            // expiry below is only a memory backstop.
            if (_cache.TryGetValue(key, out DateTimeOffset last) && now - last < window)
            {
                return false;
            }

            // At the size limit MemoryCache silently declines to store the entry. That fails towards writing, which is
            // the safe direction for an audit log.
            _cache.Set(key, now, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = window });
            return true;
        }
    }

    /// <summary>
    /// Forgets an event whose write failed (R33), so the next identical view is written instead of being suppressed for
    /// the rest of the window by a row that never reached the database.
    /// </summary>
    public void Release(AccessEventType type, Guid userKey, Guid formId, Guid? recordId, string filterHash)
    {
        lock (_gate)
        {
            _cache.Remove(Key(type, userKey, formId, recordId, filterHash));
        }
    }

    private static string Key(AccessEventType type, Guid userKey, Guid formId, Guid? recordId, string filterHash) =>
        $"{(int)type}|{userKey:N}|{formId:N}|{recordId:N}|{filterHash}";

    public void Dispose() => _cache.Dispose();

    private static bool IsDeduplicated(AccessEventType type) => type is
        AccessEventType.ListViewed
        or AccessEventType.DetailViewed
        or AccessEventType.AuditViewed
        or AccessEventType.AuditSearched
        or AccessEventType.FileViewed
        or AccessEventType.MemberSummaryViewed;
}
