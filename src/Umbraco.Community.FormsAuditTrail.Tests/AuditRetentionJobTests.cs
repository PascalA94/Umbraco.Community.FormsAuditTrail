using Microsoft.EntityFrameworkCore;
using Umbraco.Community.FormsAuditTrail.BackgroundJobs;
using Umbraco.Community.FormsAuditTrail.Models;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class AuditRetentionJobTests : IDisposable
{
    private readonly TestDatabase _db = new();

    [Fact]
    public async Task DeleteEntriesOlderThan_RemovesOldEntriesAndCascadesChanges()
    {
        var formId = Guid.NewGuid();
        _db.AddEntry(formId, "Old form", DateTime.UtcNow.AddDays(-400), changeCount: 2);
        FormAuditEntry recent = _db.AddEntry(formId, "Recent form", DateTime.UtcNow.AddDays(-5), changeCount: 1);

        var deleted = await AuditRetentionJob.DeleteEntriesOlderThanAsync(_db.Context, DateTime.UtcNow.AddDays(-365));

        Assert.Equal(1, deleted);
        List<FormAuditEntry> remaining = await _db.Context.AuditEntries.ToListAsync();
        Assert.Equal(recent.Id, Assert.Single(remaining).Id);
        // The old entry's change rows must be gone too (database-level cascade)
        Assert.Equal(1, await _db.Context.AuditChanges.CountAsync());
    }

    [Fact]
    public async Task DeleteEntriesOlderThan_WithNothingOld_DeletesNothing()
    {
        _db.AddEntry(Guid.NewGuid(), "Form", DateTime.UtcNow.AddDays(-5));

        var deleted = await AuditRetentionJob.DeleteEntriesOlderThanAsync(_db.Context, DateTime.UtcNow.AddDays(-365));

        Assert.Equal(0, deleted);
        Assert.Equal(1, await _db.Context.AuditEntries.CountAsync());
    }

    public void Dispose() => _db.Dispose();
}
