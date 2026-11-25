using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Web.Api.ManagementApi.Record;
using Xunit;
using Record = Umbraco.Forms.Core.Persistence.Dtos.Record;
using RecordField = Umbraco.Forms.Core.Persistence.Dtos.RecordField;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// The request-path cost of an audited edit (perf-edit, SQL Server edit overhead over the 10 ms target). Each form read
/// is a database round trip in Forms, and each statement here is one on the audit database, so these pin the counts
/// that the measured overhead came down to, with the stored row unchanged.
/// </summary>
public sealed class EditCaptureCostTests : IDisposable
{
    private static readonly Guid RecordId = Guid.NewGuid();
    private static readonly Guid NameField = Guid.NewGuid();
    private static readonly Guid EmailField = Guid.NewGuid();

    private readonly CommandCounter _commands = new();
    private readonly CaptureHarness _h;

    public EditCaptureCostTests()
    {
        _h = new CaptureHarness(true, false, _commands) { UseRealFieldInfo = true };
        _h.FormService.Get(CaptureHarness.FormId).Returns(_ => ContactForm());
    }

    public void Dispose() => _h.Dispose();

    private static Form ContactForm() => new()
    {
        Id = CaptureHarness.FormId,
        Name = "Contact us",
        Pages =
        [
            new Page
            {
                FieldSets =
                [
                    new FieldSet
                    {
                        Containers =
                        [
                            new FieldsetContainer
                            {
                                Fields =
                                [
                                    new Field { Id = NameField, Caption = "Name" },
                                    new Field { Id = EmailField, Caption = "Email", ContainsSensitiveData = true },
                                ],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    private static Record Stored(params (Guid Field, object[] Values)[] fields)
    {
        var record = new Record { Id = 7, UniqueId = RecordId, Form = CaptureHarness.FormId };
        foreach ((Guid field, object[] values) in fields)
        {
            record.RecordFields[Guid.NewGuid()] = new RecordField { FieldId = field, Values = [.. values] };
        }

        return record;
    }

    private Task RunEdit(params (Guid Field, object[] Values)[] after) =>
        _h.RunAsync(
            typeof(UpdateRecordController),
            new Dictionary<string, object?> { ["formId"] = CaptureHarness.FormId.ToString(), ["recordId"] = RecordId.ToString() },
            result: () => new OkResult(),
            duringAction: () => _h.SaveRecord(Stored(after)));

    [Fact]
    public async Task Edit_ReadsTheFormDefinitionOnce_ForBeforeValuesCaptionsMaskingAndFormName() // @PERF-EDIT
    {
        _h.StoredRecord = Stored((NameField, ["old"]), (EmailField, ["old@example.test"]));

        await RunEdit((NameField, ["new"]), (EmailField, ["new@example.test"]));

        _h.FormService.Received(1).Get(CaptureHarness.FormId);
        await using var db = _h.OpenDb();
        var row = await db.AccessEntries.Include(a => a.Changes).SingleAsync(a => a.EventType == (int)AccessEventType.Edited);
        // The one read still supplies everything the separate reads did: the form name, the captions and the sensitive flag.
        Assert.Equal("Contact us", row.FormName);
        var changes = row.Changes.OrderBy(c => c.FieldCaption).ToList();
        Assert.Equal(("Email", "[masked]", "[masked]", true), (changes[0].FieldCaption, changes[0].OldValue, changes[0].NewValue, changes[0].Masked));
        Assert.Equal(("Name", "old", "new", false), (changes[1].FieldCaption, changes[1].OldValue, changes[1].NewValue, changes[1].Masked));
    }

    [Fact]
    public async Task Edit_FormReadIsPerRequest_ASecondEditReadsTheFormAgain() // @PERF-EDIT
    {
        _h.StoredRecord = Stored((NameField, ["a"]));
        await RunEdit((NameField, ["b"]));
        _h.StoredRecord = Stored((NameField, ["b"]));
        await RunEdit((NameField, ["c"]));

        // Nothing outlives the request, so a form changed between two edits is seen by the second one.
        _h.FormService.Received(2).Get(CaptureHarness.FormId);
    }

    [Fact]
    public async Task Edit_FormRenamedBetweenRequests_SecondRowCarriesTheNewName() // @PERF-EDIT
    {
        _h.StoredRecord = Stored((NameField, ["a"]));
        await RunEdit((NameField, ["b"]));
        Form renamed = ContactForm();
        renamed.Name = "Contact us (renamed)";
        _h.FormService.Get(CaptureHarness.FormId).Returns(renamed);
        _h.StoredRecord = Stored((NameField, ["b"]));
        await RunEdit((NameField, ["c"]));

        await using var db = _h.OpenDb();
        string[] names = await db.AccessEntries.OrderBy(a => a.Id).Select(a => a.FormName).ToArrayAsync();
        Assert.Equal(["Contact us", "Contact us (renamed)"], names);
    }

    [Fact]
    public async Task Edit_NoClearedField_WritesParentAndChangesOnly_NoScrubStatement() // @PERF-EDIT SEC-03
    {
        _h.StoredRecord = Stored((NameField, ["old"]));

        await RunEdit((NameField, ["new"]));

        // One save: the parent insert and the change insert. The earlier-rows scrub only runs for a cleared field.
        Assert.Equal(2, _commands.Queries.Count(q => q.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(0, _commands.Count("UPDATE"));
        Assert.Equal(0, _commands.Count("INSERT"));
    }

    [Fact]
    public async Task Edit_ClearedField_AddsExactlyOneScrubStatement() // @PERF-EDIT SEC-03
    {
        _h.StoredRecord = Stored((NameField, ["old"]));

        await RunEdit((NameField, [""]));

        Assert.Equal(1, _commands.Count("UPDATE"));
    }
}
