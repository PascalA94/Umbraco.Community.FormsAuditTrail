namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Asks Umbraco Forms which records still exist. Read-only, and it never reads record data (SEC-04).
/// </summary>
internal interface IFormsRecordReader
{
    /// <summary>
    /// Returns the subset of <paramref name="recordIds"/> that exist in Forms. Throws on any database error and never
    /// returns an empty set to mean "could not read": the caller would treat every record as erased and scrub it (SEC-I1).
    /// </summary>
    Task<IReadOnlySet<Guid>> FindExistingAsync(IReadOnlyCollection<Guid> recordIds, CancellationToken ct);

    /// <summary>
    /// Returns one existing <c>UniqueId</c>, or null when the table is empty. Parameter-free and read-only. It is the
    /// self-test for <see cref="FindExistingAsync"/>: an id the table really holds must be found by the same matching
    /// path, or "not found" cannot be trusted. Throws on any database error.
    /// </summary>
    Task<Guid?> FindAnyExistingIdAsync(CancellationToken ct);
}
