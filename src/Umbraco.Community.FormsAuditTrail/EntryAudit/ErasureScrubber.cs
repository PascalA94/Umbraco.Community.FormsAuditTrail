using Microsoft.EntityFrameworkCore;
using Umbraco.Community.FormsAuditTrail.Persistence;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Nulls the stored edit values of erased entries while keeping who changed which field and when, so the access trail
/// stays complete once the personal data it quoted is gone (SEC-04).
/// </summary>
internal static class ErasureScrubber
{
    /// <summary>Ids per statement: keeps the parameter count well under SQL Server's 2,100 and avoids a statement per record (SEC-10).</summary>
    internal const int RecordBatchSize = 500;

    /// <summary>Rows per statement when a whole form is scrubbed, so one form with millions of edits is not one transaction.</summary>
    internal const int FormBatchSize = 5000;

    public static async Task<int> ScrubRecordsAsync(
        FormsAuditDbContext db,
        IReadOnlyCollection<Guid> recordIds,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var total = 0;
        foreach (Guid?[] batch in recordIds.Distinct().Select(id => (Guid?)id).Chunk(RecordBatchSize))
        {
            // A batch can hold more rows than RecordBatchSize when a record was edited many times; that is one
            // statement over indexed rows, bounded by the number of edits of 500 records.
            total += await db.EntryChanges
                .Where(c => c.ValuesScrubbedAt == null && batch.Contains(c.Access!.RecordId))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.OldValue, (string?)null)
                        .SetProperty(c => c.NewValue, (string?)null)
                        .SetProperty(c => c.ValuesScrubbedAt, (DateTime?)nowUtc),
                    ct);
        }

        return total;
    }

    public static async Task<int> ScrubFormAsync(FormsAuditDbContext db, Guid formId, DateTime nowUtc, CancellationToken ct)
    {
        var total = 0;
        int affected;
        do
        {
            // Rows already stamped drop out of the filter, so the loop ends once a batch comes back short.
            affected = await db.EntryChanges
                .Where(c => c.ValuesScrubbedAt == null && c.Access!.FormId == formId)
                .OrderBy(c => c.Id)
                .Take(FormBatchSize)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(c => c.OldValue, (string?)null)
                        .SetProperty(c => c.NewValue, (string?)null)
                        .SetProperty(c => c.ValuesScrubbedAt, (DateTime?)nowUtc),
                    ct);
            total += affected;
        }
        while (affected >= FormBatchSize);

        return total;
    }
}
