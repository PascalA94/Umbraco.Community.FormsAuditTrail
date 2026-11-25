using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Persistence;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Scrubs the stored edit values of the entries a backoffice delete request has just removed, before the response goes
/// back, so a backoffice single or bulk delete never waits for the daily reconciliation (design 7.x "In-request scrub",
/// both S5 branches). Set-based through <see cref="ErasureScrubber"/>: 500 ids per statement, never one per record (SEC-10).
/// </summary>
/// <remarks>
/// Singleton, like the writer: it holds only the scope factory and opens its own scope per scrub, so it never holds
/// a scoped context (P2). It runs after Forms' delete scope has completed, which is the only safe time to write on SQLite
/// (S5). It never throws: a failed scrub is logged and left to the reconciliation, which finds the records gone and
/// scrubs them within 24 hours. <see cref="CancellationToken.None"/> throughout, so a client that drops the connection
/// cannot cancel an erasure.
/// </remarks>
internal sealed class DeletedRecordScrubber(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<DeletedRecordScrubber> logger)
{
    /// <summary>Returns false when the scrub failed; the caller has nothing to do about it beyond knowing.</summary>
    internal async Task<bool> ScrubAsync(Guid? formId, IReadOnlyCollection<Guid> recordIds, Guid userKey)
    {
        if (recordIds.Count == 0)
        {
            return true;
        }

        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            FormsAuditDbContext db = scope.ServiceProvider.GetRequiredService<FormsAuditDbContext>();
            await ErasureScrubber.ScrubRecordsAsync(db, recordIds, time.GetUtcNow().UtcDateTime, CancellationToken.None, formId);
            return true;
        }
        catch (Exception ex)
        {
            // E12 identifiers, and the exception type only: a database error can quote the values it was updating.
            try
            {
                logger.LogError(
                    "FormsAuditTrail could not scrub the stored edit values of deleted entries ({ExceptionType}): user {UserKey}, event {EventType}, form {FormId}, records {RecordCount}; the daily reconciliation will scrub them.",
                    ex.GetType().Name,
                    userKey,
                    AccessEventType.Deleted,
                    formId,
                    recordIds.Count);
            }
            catch
            {
                // A throwing logger cannot be reported through itself.
            }

            return false;
        }
    }
}
