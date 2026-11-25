using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Controllers;
using Umbraco.Community.FormsAuditTrail.Controllers.Dtos;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Models;
using Record = Umbraco.Forms.Core.Persistence.Dtos.Record;
using RecordField = Umbraco.Forms.Core.Persistence.Dtos.RecordField;
using Umbraco.Forms.Core.Security;
using Umbraco.Forms.Core.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class FormsAuditAccessApiControllerTests : IDisposable
{
    private static readonly Guid FormA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FormB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid ViewerKey = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid Colleague = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid FieldEmail = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDatabase _db = new();
    private readonly IFormsSecurity _formsSecurity = Substitute.For<IFormsSecurity>();
    private readonly IFormService _formService = Substitute.For<IFormService>();
    private readonly IRecordStorage _recordStorage = Substitute.For<IRecordStorage>();
    private readonly IFormFieldInfoProvider _fieldInfo = Substitute.For<IFormFieldInfoProvider>();
    private readonly ISensitiveDataPermission _sensitive = Substitute.For<ISensitiveDataPermission>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IBackOfficeSecurityAccessor _security = Substitute.For<IBackOfficeSecurityAccessor>();
    private readonly UploadPathResolver _uploadPaths = new();
    private readonly FormsAuditTrailOptions _options = new();
    private readonly DedupingWriter _writer;
    private readonly FormsAuditAccessApiController _sut;

    public FormsAuditAccessApiControllerTests()
    {
        IOptionsMonitor<FormsAuditTrailOptions> monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        monitor.CurrentValue.Returns(_ => _options);
        _writer = new DedupingWriter(new EntryAuditDeduplicator(monitor, TimeProvider.System));

        IUser viewer = Substitute.For<IUser>();
        viewer.Key.Returns(ViewerKey);
        viewer.Name.Returns("Viewer");
        IBackOfficeSecurity backOffice = Substitute.For<IBackOfficeSecurity>();
        backOffice.CurrentUser.Returns(viewer);
        _security.BackOfficeSecurity.Returns(backOffice);

        _users.GetAsync(Arg.Any<IEnumerable<Guid>>()).Returns(Enumerable.Empty<IUser>());

        _sut = new FormsAuditAccessApiController(
            _db.Context, _formsSecurity, _formService, _recordStorage, _fieldInfo, _sensitive, _writer, _security, _users,
            _uploadPaths, monitor, NullLogger<FormsAuditAccessApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        GrantAccess(formA: true, formB: true);
    }

    public void Dispose() => _db.Dispose();

    private void GrantAccess(bool formA, bool formB)
    {
        _formsSecurity.HasAccessToForm(FormA).Returns(formA);
        _formsSecurity.HasAccessToForm(FormB).Returns(formB);
        _formsSecurity.HasAccessToForm(Guid.Empty).Returns(false);
        FormSlim a = new() { Id = FormA, Name = "Form A" };
        FormSlim b = new() { Id = FormB, Name = "Form B" };
        _formService.GetSlim().Returns([a, b]);
    }

    private static T OkValue<T>(IActionResult? result) => Assert.IsType<T>(Assert.IsType<OkObjectResult>(result).Value);

    private static T Ok<T>(ActionResult<T> result) => OkValue<T>(result.Result);

    private Task<ActionResult<PagedResultDto<AccessEntryListDto>>> List(
        Guid? formId = null, Guid? userKey = null, int? eventType = null, DateTime? fromDate = null, DateTime? toDate = null,
        Guid? recordId = null, int page = 1, int pageSize = 20) =>
        _sut.GetAccess(formId, userKey, eventType, fromDate, toDate, recordId, page, pageSize, CancellationToken.None);

    private static FormEntryChange Change(Guid fieldId, string? oldValue, string? newValue, bool masked = false, DateTime? scrubbed = null) =>
        new() { FieldId = fieldId, FieldCaption = "Email", OldValue = oldValue, NewValue = newValue, Masked = masked, ValuesScrubbedAt = scrubbed };

    // ---- list ----

    [Fact]
    public async Task GetAccess_FiltersByFormUserEventDatesAndPages() // @M4-T16
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now.AddDays(-3), userKey: Colleague);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now.AddDays(-2), userKey: Colleague);
        _db.AddAccess(AccessEventType.Exported, FormA, Now.AddDays(-1), userKey: Colleague);
        _db.AddAccess(AccessEventType.DetailViewed, FormB, Now.AddDays(-1), userKey: Colleague);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now.AddDays(-1));

        Assert.Equal(4, Ok(await List(formId: FormA)).Total);
        Assert.Equal(4, Ok(await List(userKey: Colleague)).Total);
        Assert.Equal(1, Ok(await List(formId: FormA, eventType: (int)AccessEventType.Exported)).Total);
        // A date-only upper bound includes that whole day.
        Assert.Equal(2, Ok(await List(formId: FormA, userKey: Colleague, fromDate: Now.AddDays(-2).Date, toDate: Now.AddDays(-1).Date)).Total);

        PagedResultDto<AccessEntryListDto> page2 = Ok(await List(page: 2, pageSize: 2));
        Assert.Equal(5, page2.Total);
        Assert.Equal(2, page2.Items.Count);
        PagedResultDto<AccessEntryListDto> all = Ok(await List(pageSize: 100));
        Assert.Equal(all.Items.Select(i => i.Timestamp).OrderByDescending(t => t), all.Items.Select(i => i.Timestamp));
        Assert.Equal("Exported", all.Items.Single(i => i.EventType == "Exported").EventType);
    }

    [Fact]
    public async Task GetAccess_RecordIdMatchesRowOrChildRow() // @M4-T16
    {
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: record);
        _db.AddAccess(AccessEventType.Exported, FormA, Now, childRecordIds: [Guid.NewGuid(), record]);
        _db.AddAccess(AccessEventType.Exported, FormA, Now, childRecordIds: [Guid.NewGuid()]);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: Guid.NewGuid());

        Assert.Equal(2, Ok(await List(recordId: record)).Total);
    }

    [Fact]
    public async Task GetAccess_InaccessibleFormFilter_Returns403() // @M4-T16
    {
        GrantAccess(formA: true, formB: false);
        _db.AddAccess(AccessEventType.DetailViewed, FormB, Now);

        Assert.IsType<ForbidResult>((await List(formId: FormB)).Result);
        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task GetAccess_GuidEmptyFormFilter_RestrictedUser_Returns403() // @M4-T16, SEC-L3
    {
        GrantAccess(formA: true, formB: false);

        Assert.IsType<ForbidResult>((await List(formId: Guid.Empty)).Result);
    }

    [Fact]
    public async Task GetAccess_HidesRowsForInaccessibleForms() // @M4-T16
    {
        GrantAccess(formA: true, formB: false);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        _db.AddAccess(AccessEventType.DetailViewed, FormB, Now);

        PagedResultDto<AccessEntryListDto> page = Ok(await List());

        Assert.Equal(1, page.Total);
        Assert.All(page.Items, i => Assert.Equal(FormA, i.FormId));
    }

    [Fact]
    public async Task GetAccess_PageSizeClampedTo100() // @M4-T16
    {
        for (int i = 0; i < 105; i++)
        {
            _db.AddAccess(AccessEventType.DetailViewed, FormA, Now.AddMinutes(-i));
        }

        PagedResultDto<AccessEntryListDto> page = Ok(await List(pageSize: 5000));

        Assert.Equal(100, page.PageSize);
        Assert.Equal(100, page.Items.Count);
        Assert.Equal(1, Ok(await List(pageSize: 0)).PageSize);
    }

    [Fact]
    public async Task GetAccess_WorksWhenEntryAuditDisabled() // @M4-T16 E10
    {
        _options.EntryAudit.Enabled = false;
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, userKey: Colleague);

        PagedResultDto<AccessEntryListDto> page = Ok(await List(userKey: Colleague));

        Assert.Equal(1, page.Total);
        // E10: with the master switch off nothing writes, the log's own reads included.
        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task GetAccess_UserKeyOrRecordIdFilter_WritesAuditSearched_Deduped() // @M4-T16 T13
    {
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, userKey: Colleague, recordId: record);

        await List(userKey: Colleague);
        await List(userKey: Colleague);
        await List(recordId: record);
        await List(recordId: record, formId: FormA);
        await List(userKey: Colleague, eventType: (int)AccessEventType.Edited);

        Assert.Equal(4, _writer.Written.Count);
        Assert.All(_writer.Written, w =>
        {
            Assert.Equal(AccessEventType.AuditSearched, w.EventType);
            Assert.Equal(ViewerKey, w.UserKey);
            Assert.NotNull(w.Detail?.Filter);
        });
        Assert.Equal(Guid.Empty, _writer.Written[0].FormId);
        Assert.Null(_writer.Written[0].RecordId);
        Assert.Equal(record, _writer.Written[1].RecordId);
        Assert.Equal(Guid.Empty, _writer.Written[1].FormId);
        Assert.Equal(FormA, _writer.Written[2].FormId);
        // SEC-06: the row says who and what was searched for, not only that a search happened.
        Assert.Equal(Colleague, _writer.Written[0].Detail!.SubjectUserKey);
        Assert.Null(_writer.Written[0].Detail!.EventType);
        Assert.Null(_writer.Written[1].Detail!.SubjectUserKey);
        Assert.Equal(Colleague, _writer.Written[3].Detail!.SubjectUserKey);
        Assert.Equal((int)AccessEventType.Edited, _writer.Written[3].Detail!.EventType);
    }

    [Fact]
    public async Task GetAccess_DifferentUserKeyFilters_AreDifferentSearches() // @M4-T16 T13
    {
        await List(userKey: Colleague);
        await List(userKey: Guid.NewGuid());

        Assert.Equal(2, _writer.Written.Count);
    }

    [Fact]
    public async Task GetAccess_OtherFilters_WriteNothing() // @M4-T16
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        await List();
        await List(formId: FormA, eventType: (int)AccessEventType.DetailViewed, fromDate: Now.AddDays(-1), toDate: Now);

        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task GetAccess_GuidEmptyRows_HiddenFromUserRestrictedToSomeForms() // @M4-T16 T19
    {
        GrantAccess(formA: true, formB: false);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        _db.AddAccess(AccessEventType.ConfigChanged, Guid.Empty, Now);
        _db.AddAccess(AccessEventType.MemberSummaryViewed, Guid.Empty, Now);

        PagedResultDto<AccessEntryListDto> page = Ok(await List());

        Assert.Equal(1, page.Total);
        Assert.DoesNotContain(page.Items, i => i.FormId == Guid.Empty);
    }

    [Fact]
    public async Task GetAccess_GuidEmptyRows_HiddenWhenEveryLoggedFormIsAccessibleButAnotherFormIsNot() // @M4-T16 SEC-L3
    {
        // Form B has no access rows, so "every logged form is accessible" would wrongly pass; the rule is every form.
        GrantAccess(formA: true, formB: false);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        _db.AddAccess(AccessEventType.AuditGap, Guid.Empty, Now);

        Assert.Equal(1, Ok(await List()).Total);
    }

    [Fact]
    public async Task GetAccess_GuidEmptyRows_ShownToAllFormsUser() // @M4-T16
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        _db.AddAccess(AccessEventType.ConfigChanged, Guid.Empty, Now);

        Assert.Equal(2, Ok(await List()).Total);
        Assert.Equal(1, Ok(await List(formId: Guid.Empty)).Total);
    }

    [Fact]
    public async Task GetAccess_FormsLookupFails_TreatedAsRestricted() // @M4-T16 fails closed
    {
        _formService.GetSlim().Returns(_ => throw new InvalidOperationException("boom"));
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        _db.AddAccess(AccessEventType.ConfigChanged, Guid.Empty, Now);

        // R73: with no live list no form can be proven to exist, so its rows follow the all-forms rule (hidden here).
        Assert.Equal(0, Ok(await List()).Total);
    }

    [Fact]
    public async Task GetAccess_GetAllTrashedKeysFails_LiveSetStillUsedAndTrashedTreatedConservatively() // @M4-FIX R73 item 1
    {
        _formService.GetAllTrashedKeys().Returns(_ => throw new InvalidOperationException("boom"));
        Guid gone = Guid.NewGuid();
        _formsSecurity.HasAccessToForm(gone).Returns(true);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        _db.AddAccess(AccessEventType.DetailViewed, gone, Now);

        // FormA is provably live and accessible; the unknown id and the all-forms rule fail closed.
        PagedResultDto<AccessEntryListDto> page = Ok(await List());
        Assert.Equal([FormA], page.Items.Select(i => i.FormId));
        Assert.False(await _sut.CanAccessEveryFormAsync());
    }

    [Fact]
    public async Task GetAccess_ATrashedIdReturnedByGetSlim_IsNotLive() // @M4-FIX R73 item 1
    {
        Guid trashed = Guid.NewGuid();
        _formsSecurity.HasAccessToForm(trashed).Returns(true);
        _formService.GetSlim().Returns([new FormSlim { Id = FormA, Name = "A" }, new FormSlim { Id = FormB, Name = "B" }, new FormSlim { Id = trashed, Name = "T" }]);
        _formService.GetAllTrashedKeys().Returns([trashed]);
        _db.AddAccess(AccessEventType.DetailViewed, trashed, Now);
        GrantAccess(formA: true, formB: false);
        _formService.GetSlim().Returns([new FormSlim { Id = FormA, Name = "A" }, new FormSlim { Id = FormB, Name = "B" }, new FormSlim { Id = trashed, Name = "T" }]);
        _formService.GetAllTrashedKeys().Returns([trashed]);

        // Restricted (denied B): security says yes for the trashed id, which must not decide visibility.
        Assert.Equal(0, Ok(await List()).Total);
    }

    [Fact]
    public async Task GetAccess_MapsRowFields() // @M4-T16
    {
        Guid correlation = Guid.NewGuid();
        _db.AddAccess(AccessEventType.Exported, FormA, Now, childRecordIds: [Guid.NewGuid()],
            detail: """{"v":1,"recordIdsCaptured":false}""", correlationId: correlation);

        AccessEntryListDto item = Assert.Single(Ok(await List()).Items);

        Assert.Equal("Exported", item.EventType);
        Assert.Equal("Test form", item.FormName);
        Assert.Equal(1, item.RecordCount);
        Assert.False(item.RecordIdsCaptured);
        Assert.Equal(correlation, item.CorrelationId);
        Assert.Equal("""{"v":1,"recordIdsCaptured":false}""", item.Detail);
        Assert.Equal(DateTimeKind.Utc, item.Timestamp.Kind);
    }

    // ---- detail ----

    [Fact]
    public async Task GetAccessEntry_InaccessibleForm_Returns404() // @M4-T16
    {
        GrantAccess(formA: true, formB: false);
        FormAccessEntry row = _db.AddAccess(AccessEventType.Edited, FormB, Now, changes: [Change(FieldEmail, "a", "b")]);

        Assert.IsType<NotFoundResult>((await _sut.GetAccessEntry(row.Id)).Result);
        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task GetAccessEntry_Unknown_Returns404() // @M4-T16
    {
        Assert.IsType<NotFoundResult>((await _sut.GetAccessEntry(12345)).Result);
    }

    [Fact]
    public async Task GetAccessEntry_GuidEmptyRow_RestrictedUser_Returns404() // @M4-T16 P10
    {
        GrantAccess(formA: true, formB: false);
        FormAccessEntry row = _db.AddAccess(AccessEventType.ConfigChanged, Guid.Empty, Now);

        Assert.IsType<NotFoundResult>((await _sut.GetAccessEntry(row.Id)).Result);
    }

    [Fact]
    public async Task GetAccessEntry_GuidEmptyRow_AllFormsUser_Returns200() // @M4-T16
    {
        FormAccessEntry row = _db.AddAccess(AccessEventType.ConfigChanged, Guid.Empty, Now);

        Assert.Equal(row.Id, Ok((await _sut.GetAccessEntry(row.Id))).Id);
    }

    [Fact]
    public async Task GetAccessEntry_IncludesChangesAndLinkedExport() // @M4-T16
    {
        Guid correlation = Guid.NewGuid();
        FormAccessEntry exported = _db.AddAccess(AccessEventType.Exported, FormA, Now.AddMinutes(-1),
            childRecordIds: [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()], correlationId: correlation);
        FormAccessEntry downloaded = _db.AddAccess(AccessEventType.ExportDownloaded, FormA, Now, correlationId: correlation);
        _fieldInfo.GetFields(FormA).Returns(new Dictionary<Guid, FieldInfo> { [FieldEmail] = new("Email", false) });
        FormAccessEntry edited = _db.AddAccess(AccessEventType.Edited, FormA, Now, recordId: Guid.NewGuid(),
            changes: [Change(FieldEmail, "old@example.test", "new@example.test")]);

        AccessEntryDetailDto exportDto = Ok(await _sut.GetAccessEntry(exported.Id));
        AccessEntryDetailDto downloadDto = Ok(await _sut.GetAccessEntry(downloaded.Id));
        AccessEntryDetailDto editDto = Ok(await _sut.GetAccessEntry(edited.Id));

        Assert.Equal(downloaded.Id, exportDto.LinkedAccessId);
        Assert.Equal(3, exportDto.ChildRecordCount);
        Assert.Equal(exported.Id, downloadDto.LinkedAccessId);
        Assert.Null(editDto.LinkedAccessId);
        EntryChangeDto change = Assert.Single(editDto.Changes);
        Assert.Equal(FieldEmail, change.FieldId);
        Assert.Equal("old@example.test", change.OldValue);
        Assert.Equal("new@example.test", change.NewValue);
        Assert.False(change.Masked);
    }

    [Fact]
    public async Task GetAccessEntry_SensitiveValueMaskedForViewerWithoutPermission() // @M4-T16 SEC-12
    {
        _options.EntryAudit.StoreEditValues = StoreEditValuesMode.All;
        _fieldInfo.GetFields(FormA).Returns(new Dictionary<Guid, FieldInfo> { [FieldEmail] = new("Email", true) });
        _sensitive.CurrentUserCanViewSensitiveData().Returns(false);
        FormAccessEntry row = _db.AddAccess(AccessEventType.Edited, FormA, Now, recordId: Guid.NewGuid(),
            changes: [Change(FieldEmail, "old@example.test", "new@example.test", masked: false)]);

        EntryChangeDto change = Assert.Single(Ok(await _sut.GetAccessEntry(row.Id)).Changes);

        Assert.Equal(ReadTimeMasker.MaskedValue, change.OldValue);
        Assert.Equal(ReadTimeMasker.MaskedValue, change.NewValue);
        Assert.True(change.Masked);
    }

    [Fact]
    public async Task GetAccessEntry_SensitiveValueShownToViewerWithPermission() // @M4-T16 SEC-12
    {
        _fieldInfo.GetFields(FormA).Returns(new Dictionary<Guid, FieldInfo> { [FieldEmail] = new("Email", true) });
        _sensitive.CurrentUserCanViewSensitiveData().Returns(true);
        FormAccessEntry row = _db.AddAccess(AccessEventType.Edited, FormA, Now, recordId: Guid.NewGuid(),
            changes: [Change(FieldEmail, "old@example.test", "new@example.test")]);

        EntryChangeDto change = Assert.Single(Ok(await _sut.GetAccessEntry(row.Id)).Changes);

        Assert.Equal("new@example.test", change.NewValue);
    }

    [Fact]
    public async Task GetAccessEntry_FieldLookupThrows_Masked() // @M4-T16 SEC-12 fails closed
    {
        _fieldInfo.GetFields(FormA).Returns(_ => throw new InvalidOperationException("boom"));
        FormAccessEntry row = _db.AddAccess(AccessEventType.Edited, FormA, Now, recordId: Guid.NewGuid(),
            changes: [Change(FieldEmail, "old@example.test", "new@example.test")]);

        EntryChangeDto change = Assert.Single(Ok(await _sut.GetAccessEntry(row.Id)).Changes);

        Assert.Equal(ReadTimeMasker.MaskedValue, change.NewValue);
    }

    [Fact]
    public async Task GetAccessEntry_UnscrubbedEdited_WritesAuditViewed_Deduped() // @M4-T16 T13
    {
        Guid record = Guid.NewGuid();
        FormAccessEntry row = _db.AddAccess(AccessEventType.Edited, FormA, Now, recordId: record,
            changes: [Change(FieldEmail, "a", "b")]);

        await _sut.GetAccessEntry(row.Id);
        await _sut.GetAccessEntry(row.Id);

        AccessWrite write = Assert.Single(_writer.Written);
        Assert.Equal(AccessEventType.AuditViewed, write.EventType);
        Assert.Equal(FormA, write.FormId);
        Assert.Equal(record, write.RecordId);
        Assert.Equal(ViewerKey, write.UserKey);
    }

    [Fact]
    public async Task GetAccessEntry_ScrubbedEdited_WritesNothing() // @M4-T16
    {
        FormAccessEntry scrubbed = _db.AddAccess(AccessEventType.Edited, FormA, Now, recordId: Guid.NewGuid(),
            changes: [Change(FieldEmail, null, null, scrubbed: Now)]);
        FormAccessEntry viewed = _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: Guid.NewGuid());

        await _sut.GetAccessEntry(scrubbed.Id);
        await _sut.GetAccessEntry(viewed.Id);

        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task GetAccess_List_NoStoreHeader() // @M4-T16 T20
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        await _sut.GetAccess();

        Assert.Equal("no-store", _sut.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetAccessEntry_NoStoreHeader() // @M4-T16 T20
    {
        FormAccessEntry row = _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        await _sut.GetAccessEntry(row.Id);

        Assert.Equal("no-store", _sut.Response.Headers.CacheControl.ToString());
    }

    // ---- record history ----

    private Record SeedRecord(Guid recordId, Guid formId, params string[] values)
    {
        var form = new Form { Id = formId, Name = "Form" };
        _formService.Get(formId).Returns(form);
        var record = new Record { UniqueId = recordId, Form = formId };
        record.RecordFields[Guid.NewGuid()] = new RecordField { Values = values.Cast<object>().ToList() };
        _recordStorage.GetRecordByUniqueId(recordId, form).Returns(record);
        return record;
    }

    private string FileViewedDetail(string path) => $$"""{"v":1,"filePathHash":"{{_uploadPaths.HashPath(new PathString(path))}}"}""";

    [Fact]
    public async Task GetRecordHistory_IncludesOpensEditsDeletesExportsAndTheirDownloads() // @M4-T16
    {
        Guid record = Guid.NewGuid();
        Guid correlation = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now.AddMinutes(-5), recordId: record);
        _db.AddAccess(AccessEventType.Edited, FormA, Now.AddMinutes(-4), recordId: record);
        _db.AddAccess(AccessEventType.Exported, FormA, Now.AddMinutes(-3), childRecordIds: [record], correlationId: correlation);
        _db.AddAccess(AccessEventType.ExportDownloaded, FormA, Now.AddMinutes(-2), correlationId: correlation);
        _db.AddAccess(AccessEventType.Deleted, FormA, Now.AddMinutes(-1), recordId: record);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: Guid.NewGuid());
        _db.AddAccess(AccessEventType.ExportDownloaded, FormA, Now, correlationId: Guid.NewGuid());

        List<AccessEntryListDto> history = Ok(await _sut.GetRecordHistory(record));

        Assert.Equal(
            ["Deleted", "ExportDownloaded", "Exported", "Edited", "DetailViewed"],
            history.Select(h => h.EventType));
    }

    [Fact]
    public async Task GetRecordHistory_IncludesBulkDeleteThroughChildRow() // @M4-T16 P8
    {
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now.AddMinutes(-1), recordId: record);
        _db.AddAccess(AccessEventType.Deleted, FormA, Now, childRecordIds: [Guid.NewGuid(), record], detail: """{"v":1,"bulk":true}""");

        List<AccessEntryListDto> history = Ok(await _sut.GetRecordHistory(record));

        Assert.Equal(["Deleted", "DetailViewed"], history.Select(h => h.EventType));
    }

    [Fact]
    public async Task GetRecordHistory_UnknownAndInaccessible_BothReturn404() // @M4-T16 Review Focus 3
    {
        GrantAccess(formA: true, formB: false);
        Guid onB = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormB, Now, recordId: onB);

        Assert.IsType<NotFoundResult>((await _sut.GetRecordHistory(Guid.NewGuid())).Result);
        Assert.IsType<NotFoundResult>((await _sut.GetRecordHistory(onB)).Result);
        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task GetRecordHistory_OnlyGuidEmptyRows_RestrictedUser_Returns404() // @M4-T16 SEC-L3
    {
        GrantAccess(formA: true, formB: false);
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, Guid.Empty, Now, recordId: record);

        Assert.IsType<NotFoundResult>((await _sut.GetRecordHistory(record)).Result);
    }

    [Fact]
    public async Task GetRecordHistory_WritesAuditViewed_Deduped() // @M4-T16
    {
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: record);

        await _sut.GetRecordHistory(record);
        await _sut.GetRecordHistory(record);

        AccessWrite write = Assert.Single(_writer.Written);
        Assert.Equal(AccessEventType.AuditViewed, write.EventType);
        Assert.Equal(FormA, write.FormId);
        Assert.Equal(record, write.RecordId);
        Assert.Equal("no-store", _sut.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetRecordHistory_IncludesFileViewedMatchedByPathHash() // @M4-T16 E13
    {
        Guid record = Guid.NewGuid();
        string upload = $"/media/forms/upload/form_{FormA:N}/abc123/cv.pdf";
        SeedRecord(record, FormA, "Jane", upload);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now.AddMinutes(-2), recordId: record);
        FormAccessEntry match = _db.AddAccess(AccessEventType.FileViewed, FormA, Now.AddMinutes(-1), detail: FileViewedDetail(upload));
        _db.AddAccess(AccessEventType.FileViewed, FormA, Now, detail: FileViewedDetail($"/media/forms/upload/form_{FormA:N}/other/x.pdf"));
        _db.AddAccess(AccessEventType.FileViewed, FormB, Now, detail: FileViewedDetail(upload));

        List<AccessEntryListDto> history = Ok(await _sut.GetRecordHistory(record));

        Assert.Equal(["FileViewed", "DetailViewed"], history.Select(h => h.EventType));
        Assert.Equal(match.Id, history[0].Id);
    }

    [Fact]
    public async Task GetRecordHistory_RecordGone_NoFileViewedMatching() // @M4-T16 E13
    {
        Guid record = Guid.NewGuid();
        string upload = $"/media/forms/upload/form_{FormA:N}/abc123/cv.pdf";
        _formService.Get(FormA).Returns(new Form { Id = FormA });
        _db.AddAccess(AccessEventType.Deleted, FormA, Now.AddMinutes(-2), recordId: record);
        _db.AddAccess(AccessEventType.FileViewed, FormA, Now.AddMinutes(-1), detail: FileViewedDetail(upload));

        List<AccessEntryListDto> history = Ok(await _sut.GetRecordHistory(record));

        Assert.Equal(["Deleted"], history.Select(h => h.EventType));
    }

    [Fact]
    public async Task GetRecordHistory_SearchOnAccessibleForm_DoesNotOpenRecordOnInaccessibleForm() // @M4-T16 fix1
    {
        GrantAccess(formA: true, formB: false);
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormB, Now, recordId: record);
        _writer.PersistTo = _db;

        await List(formId: FormA, recordId: record);
        ActionResult<List<AccessEntryListDto>> history = await _sut.GetRecordHistory(record);

        // The search row must not carry form A for a record that is not on form A, or it would steer the gate.
        AccessWrite search = Assert.Single(_writer.Written);
        Assert.Equal(AccessEventType.AuditSearched, search.EventType);
        Assert.Equal(Guid.Empty, search.FormId);
        Assert.IsType<NotFoundResult>(history.Result);
    }

    [Fact]
    public async Task GetRecordHistory_AuditRowsAloneNeverDecideTheForm() // @M4-T16 fix1
    {
        GrantAccess(formA: true, formB: false);
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormB, Now.AddMinutes(-1), recordId: record);
        // Newer than the primary row, on a form the caller can open: the old gate took the latest form id.
        _db.AddAccess(AccessEventType.AuditSearched, FormA, Now, recordId: record);
        _db.AddAccess(AccessEventType.AuditViewed, FormA, Now, recordId: record);

        Assert.IsType<NotFoundResult>((await _sut.GetRecordHistory(record)).Result);
        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task GetRecordHistory_PrimaryRowsOnTwoForms_OneInaccessible_Returns404() // @M4-T16 fix1
    {
        GrantAccess(formA: true, formB: false);
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormB, Now.AddMinutes(-1), recordId: record);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: record);

        Assert.IsType<NotFoundResult>((await _sut.GetRecordHistory(record)).Result);
    }

    [Fact]
    public async Task GetRecordHistory_OnlyAuditRows_Returns404() // @M4-T16 fix1
    {
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.AuditSearched, FormA, Now, recordId: record);

        Assert.IsType<NotFoundResult>((await _sut.GetRecordHistory(record)).Result);
    }

    [Fact]
    public async Task GetAccess_SearchStampsFormWhenARecordEventPlacesTheRecordThere() // @M4-T16 fix1
    {
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.AuditViewed, FormB, Now, recordId: record);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: record);

        await List(formId: FormA, recordId: record);
        await List(formId: FormB, recordId: record);

        Assert.Equal([FormA, Guid.Empty], _writer.Written.Select(w => w.FormId));
    }

    [Fact]
    public async Task GetRecordHistory_CorrelatedRowOnInaccessibleForm_Excluded() // @M4-T16 fix1
    {
        GrantAccess(formA: true, formB: false);
        Guid record = Guid.NewGuid();
        Guid correlation = Guid.NewGuid();
        _db.AddAccess(AccessEventType.Exported, FormA, Now.AddMinutes(-1), childRecordIds: [record], correlationId: correlation);
        _db.AddAccess(AccessEventType.ExportDownloaded, FormB, Now, correlationId: correlation);

        List<AccessEntryListDto> history = Ok(await _sut.GetRecordHistory(record));

        Assert.Equal(["Exported"], history.Select(h => h.EventType));
    }

    [Fact]
    public async Task GetRecordHistory_CorrelatedRowOnAnotherAccessibleForm_Excluded() // @M4-T16 fix1
    {
        Guid record = Guid.NewGuid();
        Guid correlation = Guid.NewGuid();
        _db.AddAccess(AccessEventType.Exported, FormA, Now.AddMinutes(-1), childRecordIds: [record], correlationId: correlation);
        _db.AddAccess(AccessEventType.ExportDownloaded, FormB, Now, correlationId: correlation);

        List<AccessEntryListDto> history = Ok(await _sut.GetRecordHistory(record));

        Assert.Equal(["Exported"], history.Select(h => h.EventType));
    }

    [Fact]
    public async Task GetRecordHistory_CapsRowsAndFlagsTruncation() // @M4-T16 fix1
    {
        Assert.Equal(1000, _sut.HistoryLimit);
        _sut.HistoryLimit = 3;
        Guid record = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
        {
            _db.AddAccess(AccessEventType.DetailViewed, FormA, Now.AddMinutes(-i), recordId: record);
        }

        List<AccessEntryListDto> history = Ok(await _sut.GetRecordHistory(record));

        Assert.Equal(3, history.Count);
        Assert.Equal(Now, history[0].Timestamp);
        Assert.Equal("history", _sut.Response.Headers[FormsAuditAccessApiController.TruncatedHeader].ToString());
    }

    [Fact]
    public async Task GetRecordHistory_UnderTheCap_NoTruncationHeader() // @M4-T16 fix1
    {
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: record);

        await _sut.GetRecordHistory(record);

        Assert.False(_sut.Response.Headers.ContainsKey(FormsAuditAccessApiController.TruncatedHeader));
    }

    [Fact]
    public async Task GetRecordHistory_FileViewScanCapped_FlagsTruncation() // @M4-T16 fix1
    {
        Assert.Equal(50_000, _sut.FileViewScanLimit);
        _sut.FileViewScanLimit = 2;
        Guid record = Guid.NewGuid();
        string upload = $"/media/forms/upload/form_{FormA:N}/abc123/cv.pdf";
        SeedRecord(record, FormA, upload);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now.AddMinutes(-10), recordId: record);
        // The match is the oldest file view, past the scan cap, which reads newest first.
        _db.AddAccess(AccessEventType.FileViewed, FormA, Now.AddMinutes(-5), detail: FileViewedDetail(upload));
        for (int i = 0; i < 3; i++)
        {
            _db.AddAccess(AccessEventType.FileViewed, FormA, Now.AddMinutes(-i), detail: FileViewedDetail($"/media/forms/upload/form_{FormA:N}/o{i}/x.pdf"));
        }

        List<AccessEntryListDto> history = Ok(await _sut.GetRecordHistory(record));

        Assert.Equal(["DetailViewed"], history.Select(h => h.EventType));
        Assert.Equal("fileViews", _sut.Response.Headers[FormsAuditAccessApiController.TruncatedHeader].ToString());
    }

    [Fact]
    public async Task GetAccess_ToDateAtTheLastRepresentableDay_Returns200() // @M4-T16 fix1
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        Assert.Equal(1, Ok(await List(toDate: DateTime.MaxValue.Date)).Total);
        Assert.Equal(1, Ok(await List(toDate: DateTime.MaxValue)).Total);
    }

    // ---- forms and users ----

    [Fact]
    public async Task Forms_And_Users_OnlyAccessibleForms() // @M4-T16
    {
        GrantAccess(formA: true, formB: false);
        Guid onA = Guid.NewGuid();
        Guid onB = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, userKey: onA);
        _db.AddAccess(AccessEventType.DetailViewed, FormB, Now, userKey: onB);
        _db.AddAccess(AccessEventType.ConfigChanged, Guid.Empty, Now, userKey: Guid.NewGuid());

        List<FormSummaryDto> forms = Ok(await _sut.GetForms());
        List<AccessUserSummaryDto> users = Ok(await _sut.GetUsers());

        Assert.Equal([FormA], forms.Select(f => f.FormId));
        Assert.Equal([onA], users.Select(u => u.UserKey));
    }

    [Fact]
    public async Task Forms_And_Users_SendNoStore() // @M4-FIX item 8
    {
        await _sut.GetForms();
        Assert.Equal("no-store", _sut.Response.Headers.CacheControl.ToString());

        _sut.Response.Headers.Remove("Cache-Control");
        await _sut.GetUsers();
        Assert.Equal("no-store", _sut.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetAccess_RowsOfAPermanentlyDeletedForm_VisibleToAnAllFormsUser() // @M4-FIX item 10, R73 item 4
    {
        // A deleted form is gone from IFormService, so its rows follow the all-forms rule (item 14): this user can open
        // every form that exists, so they see the rows and the form in the filter. A restricted user sees none (see the item 14 tests).
        Guid deleted = Guid.NewGuid();
        _formsSecurity.HasAccessToForm(deleted).Returns(true);
        _db.AddAccess(AccessEventType.Deleted, deleted, Now, detail: """{"v":1,"deleteKind":"form"}""");

        PagedResultDto<AccessEntryListDto> admitted = Ok(await List());
        Assert.Equal(["Deleted"], admitted.Items.Select(i => i.EventType));
        Assert.Equal([deleted], Ok(await _sut.GetForms()).Select(f => f.FormId));
        Assert.Equal(1, Ok(await List(formId: deleted)).Total);
    }

    [Fact]
    public async Task RowsOfAFormThatNoLongerExists_HiddenFromARestrictedUser_WhateverSecurityAnswers() // @M4-FIX item 14
    {
        // Denied form B makes this user restricted. Security says yes for the deleted id, which must not matter.
        GrantAccess(formA: true, formB: false);
        Guid deleted = Guid.NewGuid();
        _formsSecurity.HasAccessToForm(deleted).Returns(true);
        _db.AddAccess(AccessEventType.Deleted, deleted, Now, detail: """{"v":1,"deleteKind":"form"}""");
        FormAccessEntry row = _db.AddAccess(AccessEventType.DetailViewed, deleted, Now, recordId: Guid.NewGuid());

        Assert.Equal(0, Ok(await List()).Total);
        Assert.Empty(Ok(await _sut.GetForms()));
        Assert.IsType<ForbidResult>((await List(formId: deleted)).Result);
        Assert.IsType<NotFoundResult>((await _sut.GetAccessEntry(row.Id)).Result);
        Assert.IsType<NotFoundResult>((await _sut.GetRecordHistory(row.RecordId!.Value)).Result);
    }

    [Fact]
    public async Task RowsOfATrashedForm_VisibleToAnAllFormsUser() // @M4-FIX item 14
    {
        Guid trashed = Guid.NewGuid();
        _formsSecurity.HasAccessToForm(trashed).Returns(true);
        _formService.GetAllTrashedKeys().Returns([trashed]);
        _db.AddAccess(AccessEventType.DetailViewed, trashed, Now, recordId: Guid.NewGuid());

        Assert.Equal(1, Ok(await List()).Total);
    }

    [Fact]
    public async Task RowsOfATrashedForm_HiddenFromARestrictedUser() // @M4-FIX item 14
    {
        Guid trashed = Guid.NewGuid();
        GrantAccess(formA: true, formB: false);
        _formsSecurity.HasAccessToForm(trashed).Returns(true);
        _formService.GetAllTrashedKeys().Returns([trashed]);
        _db.AddAccess(AccessEventType.DetailViewed, trashed, Now, recordId: Guid.NewGuid());

        Assert.Equal(0, Ok(await List()).Total);
    }

    [Fact]
    public async Task AllFormsUser_IsDeniedByATrashedFormTheyCannotOpen() // @M4-FIX item 16
    {
        Guid trashed = Guid.NewGuid();
        _formsSecurity.HasAccessToForm(trashed).Returns(false);
        _formService.GetAllTrashedKeys().Returns([trashed]);

        Assert.False(await _sut.CanAccessEveryFormAsync());
    }

    [Fact]
    public async Task Users_IncludeCurrentNameWhenChanged() // @M4-T16 SEC-L2
    {
        Guid renamed = Guid.NewGuid();
        Guid same = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, userKey: renamed);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, userKey: same);
        IUser renamedUser = Substitute.For<IUser>();
        renamedUser.Key.Returns(renamed);
        renamedUser.Name.Returns("Edith Renamed");
        IUser sameUser = Substitute.For<IUser>();
        sameUser.Key.Returns(same);
        sameUser.Name.Returns("Editor");
        _users.GetAsync(Arg.Any<IEnumerable<Guid>>()).Returns([renamedUser, sameUser]);

        List<AccessUserSummaryDto> users = Ok(await _sut.GetUsers());
        PagedResultDto<AccessEntryListDto> list = Ok(await List());

        Assert.Equal("Edith Renamed", users.Single(u => u.UserKey == renamed).CurrentUserName);
        Assert.Null(users.Single(u => u.UserKey == same).CurrentUserName);
        Assert.Equal("Editor", users.Single(u => u.UserKey == renamed).UserName);
        Assert.Equal("Edith Renamed", list.Items.Single(i => i.UserKey == renamed).CurrentUserName);
        Assert.Null(list.Items.Single(i => i.UserKey == same).CurrentUserName);
    }

    [Fact]
    public async Task Users_LookupFails_StillReturnsStoredNames() // @M4-T16
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        _users.GetAsync(Arg.Any<IEnumerable<Guid>>()).Returns<Task<IEnumerable<IUser>>>(_ => throw new InvalidOperationException("boom"));

        AccessUserSummaryDto user = Assert.Single(Ok(await _sut.GetUsers()));

        Assert.Equal("Editor", user.UserName);
        Assert.Null(user.CurrentUserName);
    }

    /// <summary>Stands in for the real writer's dedupe step, so the controller's dedupe keys are tested end to end.</summary>
    private sealed class DedupingWriter(EntryAuditDeduplicator deduplicator) : IEntryAuditWriter
    {
        public List<AccessWrite> Written { get; } = [];

        /// <summary>When set, a write also lands in the database, so a caller-written row can be tested for side effects.</summary>
        public TestDatabase? PersistTo { get; set; }

        public Task<AccessWriteResult> WriteAsync(AccessWrite write)
        {
            string hash = write.DedupeFilterHash ?? write.Detail?.FilterHash() ?? string.Empty;
            if (!deduplicator.ShouldWrite(write.EventType, write.UserKey, write.FormId, write.RecordId, hash))
            {
                return Task.FromResult(new AccessWriteResult(AccessWriteStatus.Deduplicated, null, false));
            }

            Written.Add(write);
            PersistTo?.AddAccess(write.EventType, write.FormId, Now.AddMinutes(30), recordId: write.RecordId, userKey: write.UserKey);
            return Task.FromResult(new AccessWriteResult(AccessWriteStatus.Written, Written.Count, false));
        }
    }
}
