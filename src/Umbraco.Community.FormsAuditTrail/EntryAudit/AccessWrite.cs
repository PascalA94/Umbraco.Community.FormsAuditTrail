using Umbraco.Community.FormsAuditTrail.Models;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// One access event handed to <see cref="IEntryAuditWriter"/>. The user is already resolved by the caller, so the writer
/// never touches the request or the back office security context. <c>RecordIds</c> is held for the writer that stores
/// child rows; this writer records only the count.
/// </summary>
public sealed record AccessWrite(
    AccessEventType EventType,
    Guid FormId,
    string? FormName,
    Guid? RecordId,
    int RecordCount,
    Guid UserKey,
    string UserName,
    EntryAuditDetail? Detail,
    IReadOnlyCollection<Guid> RecordIds,
    IReadOnlyList<EntryFieldChange> Changes)
{
    /// <summary>
    /// The dedupe key's filter part when the caller has one from the raw, pre-shaping detail (R33). Without it two
    /// different searches by one user hash the same once the term is dropped, and the second view is lost. It is held
    /// in memory only and never stored; null means hash the stored detail.
    /// </summary>
    public string? DedupeFilterHash { get; init; }

    /// <summary>
    /// True when the event must be written whatever the dedupe window says, and must not touch the dedupe cache. Set
    /// for an unresolved user: every such event carries <c>Guid.Empty</c>, so a shared key would hide different people
    /// behind the first one's view, and a failed write would release a key another request holds.
    /// </summary>
    public bool SkipDedupe { get; init; }

    /// <summary>
    /// When the event happened, if that is earlier than when it is stored. A retried configuration change keeps its own
    /// time so the off period it evidences is not shortened by the outage that delayed the write. Null means now.
    /// </summary>
    public DateTime? OccurredUtc { get; init; }
}

public enum AccessWriteStatus
{
    Written,
    Deduplicated,
    Failed,
}

public sealed record AccessWriteResult(AccessWriteStatus Status, long? AccessId, bool RecordIdsCaptured);
