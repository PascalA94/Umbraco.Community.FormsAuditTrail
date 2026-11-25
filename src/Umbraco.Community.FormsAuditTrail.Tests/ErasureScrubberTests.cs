using Microsoft.EntityFrameworkCore;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class ErasureScrubberTests : IDisposable
{
    private readonly CommandCounter _counter = new();
    private readonly TestDatabase _db;
    private static readonly DateTime Now = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);

    public ErasureScrubberTests() => _db = new TestDatabase(_counter);

    private static FormEntryChange Change(string? oldValue = "old", string? newValue = "new") => new()
    {
        FieldId = Guid.NewGuid(),
        FieldCaption = "Email",
        OldValue = oldValue,
        NewValue = newValue,
        Masked = true,
    };

    private FormAccessEntry Edit(Guid formId, Guid recordId) =>
        _db.AddAccess(AccessEventType.Edited, formId, Now.AddDays(-1), recordId: recordId, changes: [Change()]);

    [Fact]
    public async Task ScrubRecords_NullsValuesAndStampsOnlyThatRecord()
    {
        var formId = Guid.NewGuid();
        var erased = Guid.NewGuid();
        var kept = Guid.NewGuid();
        Edit(formId, erased);
        Edit(formId, kept);

        var count = await ErasureScrubber.ScrubRecordsAsync(_db.Context, [erased], Now, CancellationToken.None);

        Assert.Equal(1, count);
        List<FormEntryChange> rows = await _db.Context.EntryChanges.AsNoTracking().Include(c => c.Access).ToListAsync();
        FormEntryChange scrubbed = rows.Single(r => r.Access!.RecordId == erased);
        Assert.Null(scrubbed.OldValue);
        Assert.Null(scrubbed.NewValue);
        Assert.Equal(Now, scrubbed.ValuesScrubbedAt);
        FormEntryChange untouched = rows.Single(r => r.Access!.RecordId == kept);
        Assert.Equal("old", untouched.OldValue);
        Assert.Equal("new", untouched.NewValue);
        Assert.Null(untouched.ValuesScrubbedAt);
    }

    [Fact]
    public async Task ScrubRecords_KeepsWhoAndWhen()
    {
        var recordId = Guid.NewGuid();
        FormAccessEntry access = Edit(Guid.NewGuid(), recordId);
        FormEntryChange before = access.Changes.Single();

        await ErasureScrubber.ScrubRecordsAsync(_db.Context, [recordId], Now, CancellationToken.None);

        _db.Context.ChangeTracker.Clear();
        FormAccessEntry accessAfter = await _db.Context.AccessEntries.AsNoTracking().SingleAsync();
        FormEntryChange after = await _db.Context.EntryChanges.AsNoTracking().SingleAsync();
        Assert.Equal(access.UserKey, accessAfter.UserKey);
        Assert.Equal(access.UserName, accessAfter.UserName);
        Assert.Equal(access.Timestamp, accessAfter.Timestamp);
        Assert.Equal(before.FieldId, after.FieldId);
        Assert.Equal("Email", after.FieldCaption);
        Assert.True(after.Masked);
    }

    [Fact]
    public async Task ScrubForm_NullsEveryValueForThatFormOnly()
    {
        var formA = Guid.NewGuid();
        var formB = Guid.NewGuid();
        Edit(formA, Guid.NewGuid());
        Edit(formA, Guid.NewGuid());
        Edit(formB, Guid.NewGuid());

        var count = await ErasureScrubber.ScrubFormAsync(_db.Context, formA, Now, CancellationToken.None);

        Assert.Equal(2, count);
        List<FormEntryChange> rows = await _db.Context.EntryChanges.AsNoTracking().Include(c => c.Access).ToListAsync();
        Assert.All(rows.Where(r => r.Access!.FormId == formA), r =>
        {
            Assert.Null(r.NewValue);
            Assert.Equal(Now, r.ValuesScrubbedAt);
        });
        FormEntryChange other = Assert.Single(rows, r => r.Access!.FormId == formB);
        Assert.Equal("new", other.NewValue);
    }

    [Fact]
    public async Task ScrubRecords_AlreadyScrubbed_KeepsOriginalTimestamp()
    {
        var recordId = Guid.NewGuid();
        Edit(Guid.NewGuid(), recordId);
        DateTime first = Now.AddDays(-30);
        await ErasureScrubber.ScrubRecordsAsync(_db.Context, [recordId], first, CancellationToken.None);

        var count = await ErasureScrubber.ScrubRecordsAsync(_db.Context, [recordId], Now, CancellationToken.None);

        Assert.Equal(0, count);
        _db.Context.ChangeTracker.Clear();
        Assert.Equal(first, (await _db.Context.EntryChanges.AsNoTracking().SingleAsync()).ValuesScrubbedAt);
    }

    [Fact]
    public async Task ScrubRecords_1200Ids_IssuesThreeSetBasedUpdates()
    {
        var ids = Enumerable.Range(0, 1200).Select(_ => Guid.NewGuid()).ToList();
        Edit(Guid.NewGuid(), ids[700]);
        _counter.Commands.Clear();

        var count = await ErasureScrubber.ScrubRecordsAsync(_db.Context, ids, Now, CancellationToken.None);

        Assert.Equal(1, count);
        Assert.Equal(3, _counter.Count("UPDATE"));
    }

    public void Dispose() => _db.Dispose();
}
