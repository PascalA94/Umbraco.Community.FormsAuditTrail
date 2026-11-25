using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Umbraco.Community.FormsAuditTrail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Controllers;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Security;
using Umbraco.Forms.Core.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class AccessCsvExportTests : IDisposable
{
    private const string Header = "Id,Timestamp,Event,FormId,FormName,UserKey,UserName,RecordId,RecordCount,Detail";
    private static readonly Guid FormA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid FormB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid ViewerKey = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly CommandCounter _counter = new();
    private readonly TestDatabase _db;
    private readonly IFormsSecurity _formsSecurity = Substitute.For<IFormsSecurity>();
    private readonly IFormService _formService = Substitute.For<IFormService>();
    private readonly IBackOfficeSecurityAccessor _security = Substitute.For<IBackOfficeSecurityAccessor>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly FormsAuditTrailOptions _options = new();
    private readonly RecordingWriter _writer = new();
    private readonly IOptionsMonitor<FormsAuditTrailOptions> _monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
    private readonly FormsAuditAccessApiController _sut;
    private readonly DefaultHttpContext _http = new();
    private readonly MemoryStream _body = new();

    public AccessCsvExportTests()
    {
        _db = new TestDatabase(_counter);
        _monitor.CurrentValue.Returns(_ => _options);
        IUser viewer = Substitute.For<IUser>();
        viewer.Key.Returns(ViewerKey);
        viewer.Name.Returns("Viewer");
        IBackOfficeSecurity backOffice = Substitute.For<IBackOfficeSecurity>();
        backOffice.CurrentUser.Returns(viewer);
        _security.BackOfficeSecurity.Returns(backOffice);
        _users.GetAsync(Arg.Any<IEnumerable<Guid>>()).Returns(Enumerable.Empty<IUser>());
        _http.Response.Body = _body;

        _sut = new FormsAuditAccessApiController(
            _db.Context, _formsSecurity, _formService, Substitute.For<IRecordStorage>(), Substitute.For<IFormFieldInfoProvider>(),
            Substitute.For<ISensitiveDataPermission>(), _writer, _security, _users, new UploadPathResolver(), _monitor,
            NullLogger<FormsAuditAccessApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = _http },
        };

        Grant(formA: true, formB: true);
    }

    public void Dispose() => _db.Dispose();

    private void Grant(bool formA, bool formB)
    {
        _formsSecurity.HasAccessToForm(FormA).Returns(formA);
        _formsSecurity.HasAccessToForm(FormB).Returns(formB);
        _formsSecurity.HasAccessToForm(Guid.Empty).Returns(false);
        FormSlim a = new() { Id = FormA, Name = "Form A" };
        FormSlim b = new() { Id = FormB, Name = "Form B" };
        _formService.GetSlim().Returns([a, b]);
    }

    private async Task<IActionResult> Export(Guid? formId = null, Guid? userKey = null, int? eventType = null, DateTime? fromDate = null, DateTime? toDate = null, Guid? recordId = null)
        => await _sut.ExportAccess(formId, userKey, eventType, fromDate, toDate, recordId, CancellationToken.None);

    private string[] Lines()
    {
        string text = new StreamReader(new MemoryStream(_body.ToArray()), Encoding.UTF8, true).ReadToEnd();
        return text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    [Fact]
    public async Task Export_EscapesEveryTextCell_AndHasSpecColumns() // @M4-T18
    {
        FormAccessEntry row = _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: Guid.NewGuid());
        row.FormName = "=HYPERLINK(1)";
        _db.Context.SaveChanges();

        IActionResult result = await Export();

        Assert.IsType<EmptyResult>(result);
        string[] lines = Lines();
        Assert.Equal(Header, lines[0]);
        Assert.Equal(2, lines.Length);
        Assert.Contains(",'=HYPERLINK(1),", lines[1]);
        Assert.Contains("text/csv", _http.Response.ContentType);
    }

    [Fact]
    public async Task Export_HostileFileNameInDetail_AndUserName_Escaped() // @M4-T18
    {
        FormAccessEntry row = _db.AddAccess(AccessEventType.FileViewed, FormA, Now, detail: "{\"v\":1,\"fileName\":\"=cmd|' /C calc'!A0.csv\"}");
        row.UserName = "@SUM(1+1)";
        _db.Context.SaveChanges();

        await Export();

        string line = Lines()[1];
        Assert.Contains(",'@SUM(1+1),", line);
        // The detail JSON starts with a brace, so it is quoted, and its quotes are doubled; the hostile name stays inert inside it.
        Assert.Contains("\"{\"\"v\"\":1,\"\"fileName\"\":\"\"=cmd|' /C calc'!A0.csv\"\"}\"", line);
    }

    [Fact]
    public async Task Export_NoStoreHeader() // @M4-T18
    {
        await Export();

        Assert.Equal("no-store", _http.Response.Headers.CacheControl.ToString());
        Assert.Contains("attachment", _http.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task Export_NeverContainsEditValues() // @M4-T18
    {
        _db.AddAccess(
            AccessEventType.Edited, FormA, Now, recordId: Guid.NewGuid(),
            changes: [new FormEntryChange { FieldId = Guid.NewGuid(), FieldCaption = "Email", OldValue = "old-secret@example.test", NewValue = "new-secret@example.test" }]);

        await Export();

        string text = string.Join("\n", Lines());
        Assert.DoesNotContain("old-secret", text);
        Assert.DoesNotContain("new-secret", text);
        Assert.Equal(2, Lines().Length);
    }

    [Fact]
    public async Task Export_DisabledByEnableCsvExport_Returns404() // @M4-T18
    {
        _options.EnableCsvExport = false;
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        IActionResult result = await Export();

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(_body.ToArray());
        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task Export_EntryAuditCsvOff_DefinitionCsvOn_AccessCsv404_DefinitionCsv200() // T20
    {
        _options.EnableCsvExport = true;
        _options.EntryAudit.EnableCsvExport = false;
        _db.AddEntry(FormA, "Form A", DateTime.UtcNow);

        IActionResult access = await Export();

        var definition = new FormsAuditApiController(
            _db.Context, _formsSecurity, _monitor, Substitute.For<IAuthorizationService>(),
            new EntryAuditHealth(Substitute.For<IAuditStateStore>(), TimeProvider.System), TimeProvider.System,
            Substitute.For<IForeignValueHandlerDetector>(), NullLogger<FormsAuditApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        IActionResult definitionResult = await definition.ExportCsv(null, null, null, null, null);

        Assert.IsType<NotFoundResult>(access);
        Assert.IsType<FileContentResult>(definitionResult);
    }

    [Fact]
    public async Task Export_WritesAuditExported_GuidEmptyFormWhenUnfiltered() // @M4-T18
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        _db.AddAccess(AccessEventType.DetailViewed, FormB, Now);

        await Export();

        AccessWrite write = Assert.Single(_writer.Written);
        Assert.Equal(AccessEventType.AuditExported, write.EventType);
        Assert.Equal(Guid.Empty, write.FormId);
        Assert.Equal(ViewerKey, write.UserKey);
        Assert.Equal(2, write.RecordCount);
        Assert.Equal("accessLogCsv", write.Detail!.ExportType);
        Assert.Matches(@"^access-log-\d{8}-\d{4}\.csv$", write.Detail.FileName);
    }

    [Fact]
    public async Task Export_UserAndEventFilters_AreRecordedInTheAuditExportedDetail() // @M4-T16b SEC-06 R49
    {
        Guid colleague = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, userKey: colleague);

        await Export(userKey: colleague, eventType: (int)AccessEventType.DetailViewed);

        EntryAuditDetail detail = Assert.Single(_writer.Written).Detail!;
        Assert.Equal(colleague, detail.SubjectUserKey);
        Assert.Equal((int)AccessEventType.DetailViewed, detail.EventType);
    }

    [Fact]
    public async Task Export_NoUserOrEventFilter_LeavesThoseDetailKeysUnset() // @M4-T16b SEC-06 R49
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        await Export();

        EntryAuditDetail detail = Assert.Single(_writer.Written).Detail!;
        Assert.Null(detail.SubjectUserKey);
        Assert.Null(detail.EventType);
    }

    [Fact]
    public async Task Export_FormFilter_WritesAuditExportedForThatForm() // @M4-T18
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        await Export(formId: FormA);

        Assert.Equal(FormA, Assert.Single(_writer.Written).FormId);
    }

    [Fact]
    public async Task Export_FormAndRecordFilter_StampsTheFormOnlyWhenTheRecordWasSeenThere() // @M4-FIX item 15
    {
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormB, Now, recordId: record);

        await Export(formId: FormA, recordId: record);
        await Export(formId: FormB, recordId: record);

        Assert.Equal([Guid.Empty, FormB], _writer.Written.Select(w => w.FormId));
    }

    [Fact]
    public async Task Export_WhenEntryAuditDisabled_StillExportsButWritesNoRow() // E10
    {
        _options.EntryAudit.Enabled = false;
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        await Export();

        Assert.Equal(2, Lines().Length);
        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task Export_AuditWriteFails_StillStreamsTheExport() // E12
    {
        _writer.Fail = true;
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        IActionResult result = await Export();

        Assert.IsType<EmptyResult>(result);
        Assert.Equal(2, Lines().Length);
    }

    [Fact]
    public async Task Export_UnresolvedUser_RecordedAsUnknown() // SEC-L5
    {
        _security.BackOfficeSecurity!.CurrentUser.Returns((IUser?)null);

        await Export();

        AccessWrite write = Assert.Single(_writer.Written);
        Assert.Equal(Guid.Empty, write.UserKey);
        Assert.Equal(AuditUserNames.Unknown, write.UserName);
        Assert.True(write.Detail!.UserUnresolved);
    }

    [Fact]
    public async Task Export_ExcludesInaccessibleFormRows_AndGuidEmptyRowsForRestrictedUser() // P10
    {
        Grant(formA: true, formB: false);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        _db.AddAccess(AccessEventType.DetailViewed, FormB, Now);
        _db.AddAccess(AccessEventType.AuditGap, Guid.Empty, Now);

        await Export();

        string[] lines = Lines();
        Assert.Equal(2, lines.Length);
        Assert.Contains(FormA.ToString(), lines[1]);
        Assert.DoesNotContain(FormB.ToString(), string.Join("\n", lines));
        Assert.DoesNotContain(Guid.Empty.ToString(), string.Join("\n", lines));
        Assert.Equal(1, Assert.Single(_writer.Written).RecordCount);
    }

    [Fact]
    public async Task Export_AllFormsUser_IncludesGuidEmptyRows() // SEC-L3
    {
        _db.AddAccess(AccessEventType.AuditGap, Guid.Empty, Now);

        await Export();

        Assert.Equal(2, Lines().Length);
    }

    [Fact]
    public async Task Export_InaccessibleFormFilter_Returns403() // P10
    {
        Grant(formA: true, formB: false);
        _db.AddAccess(AccessEventType.DetailViewed, FormB, Now);

        IActionResult result = await Export(formId: FormB);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(_body.ToArray());
        Assert.Empty(_writer.Written);
    }

    [Fact]
    public async Task Export_AppliesTheListFilters() // @M4-T18
    {
        Guid colleague = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, userKey: colleague);
        _db.AddAccess(AccessEventType.Exported, FormA, Now, userKey: colleague);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        await Export(userKey: colleague, eventType: (int)AccessEventType.Exported);

        Assert.Equal(2, Lines().Length);
    }

    [Fact]
    public async Task Export_RowsAreNewestFirst() // @M4-T18
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now.AddDays(-2));
        _db.AddAccess(AccessEventType.Exported, FormA, Now);

        await Export();

        string[] lines = Lines();
        Assert.Contains(",Exported,", lines[1]);
        Assert.Contains(",DetailViewed,", lines[2]);
    }

    [Fact]
    public async Task Export_FailClosedOnExport_WriteFails_Returns503BeforeAnyByte() // R56
    {
        _options.EntryAudit.FailClosedOnExport = true;
        _writer.Fail = true;
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        IActionResult result = await Export();

        Assert.Equal(503, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Empty(_body.ToArray());
        Assert.False(_http.Response.HasStarted);
    }

    [Fact]
    public async Task Export_FailClosedOnExport_WriterThrows_Returns503() // R56
    {
        _options.EntryAudit.FailClosedOnExport = true;
        _writer.Throw = true;
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        Assert.Equal(503, Assert.IsType<StatusCodeResult>(await Export()).StatusCode);
        Assert.Empty(_body.ToArray());
    }

    [Fact]
    public async Task Export_FailClosedOnExport_WriteSucceeds_Streams() // R56
    {
        _options.EntryAudit.FailClosedOnExport = true;
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        Assert.IsType<EmptyResult>(await Export());
        Assert.Equal(2, Lines().Length);
    }

    [Fact]
    public async Task Export_FailClosedOff_WriterThrows_StillStreams() // E12
    {
        _writer.Throw = true;
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        Assert.IsType<EmptyResult>(await Export());
        Assert.Equal(2, Lines().Length);
    }

    [Fact]
    public async Task Export_FailClosedOnExport_AuditingDisabled_StillStreams() // E10
    {
        _options.EntryAudit.FailClosedOnExport = true;
        _options.EntryAudit.Enabled = false;
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);

        Assert.IsType<EmptyResult>(await Export());
    }

    [Fact]
    public async Task Export_RecordIdFilter_RecordedOnAuditExported() // SEC-06
    {
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: record);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: Guid.NewGuid());

        await Export(recordId: record);

        AccessWrite write = Assert.Single(_writer.Written);
        Assert.Equal(record, write.RecordId);
        Assert.Equal(1, write.RecordCount);
    }

    [Fact]
    public async Task Export_RecordIdFilter_ReturnsOnlyThatRecordsRows() // SEC-06
    {
        Guid record = Guid.NewGuid();
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: record);
        _db.AddAccess(AccessEventType.Exported, FormA, Now, childRecordIds: [record, Guid.NewGuid()]);
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now, recordId: Guid.NewGuid());

        await Export(recordId: record);

        Assert.Equal(3, Lines().Length);
    }

    [Fact]
    public async Task Export_NoRecordIdFilter_RecordsNoRecordId() // SEC-06
    {
        await Export();

        Assert.Null(Assert.Single(_writer.Written).RecordId);
    }

    [Fact]
    public async Task Export_ReadsInSeparateKeysetBatches_AllRowsOnceInOrder() // SEC-10
    {
        // All at one instant, so the (Timestamp, Id) tie-break is what keeps batches from repeating or skipping rows.
        for (int i = 0; i < 2_500; i++)
        {
            _db.Context.AccessEntries.Add(new FormAccessEntry
            {
                Timestamp = Now, EventType = (int)AccessEventType.DetailViewed, FormId = FormA, FormName = "F", UserKey = ViewerKey, UserName = "U",
            });
        }

        _db.Context.SaveChanges();
        _counter.Queries.Clear();

        await Export();

        string[] lines = Lines();
        long[] ids = lines.Skip(1).Select(l => long.Parse(l.Split(',')[0])).ToArray();
        Assert.Equal(2_500, ids.Length);
        Assert.Equal(ids.OrderByDescending(i => i), ids);
        Assert.Equal(2_500, ids.Distinct().Count());
        // Three separate row queries (1,000 + 1,000 + 500), not one reader held open for the whole response.
        Assert.Equal(3, _counter.Queries.Count(q => q.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Export_DetailSearchTerm_NotInCsv_StillInStoredRow() // SEC-02
    {
        const string detail = "{\"v\":1,\"filter\":{\"searchTerm\":\"jane@example.test\"}}";
        FormAccessEntry row = _db.AddAccess(AccessEventType.ListViewed, FormA, Now, detail: detail);

        await Export();

        Assert.DoesNotContain("jane@example.test", string.Join("\n", Lines()));
        Assert.Contains("jane@example.test", _db.Context.AccessEntries.Single(e => e.Id == row.Id).Detail);
    }

    [Fact]
    public async Task Export_ClientAbortsMidStream_IsSwallowed() // R56
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        using var cts = new CancellationTokenSource();
        _http.Response.Body = new AbortingStream(cts);

        IActionResult result = await _sut.ExportAccess(null, null, null, null, null, null, cts.Token);

        Assert.IsType<EmptyResult>(result);
        Assert.All(_writer.Written, w => Assert.Equal(AccessEventType.AuditExported, w.EventType));
    }

    [Fact]
    public async Task Export_IoErrorWhileClientStillConnected_Propagates() // R56
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        _http.Response.Body = new AbortingStream(null);

        await Assert.ThrowsAsync<IOException>(() => Export());
    }

    [Fact]
    public async Task Export_SecondConcurrentExportBySameUser_Returns429AndWritesNothing() // R56
    {
        _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        Assert.True(AccessExportGate.TryEnter(ViewerKey));
        try
        {
            IActionResult result = await Export();

            Assert.Equal(429, Assert.IsType<StatusCodeResult>(result).StatusCode);
            Assert.Empty(_body.ToArray());
            Assert.Empty(_writer.Written);
        }
        finally
        {
            AccessExportGate.Exit(ViewerKey);
        }
    }

    [Fact]
    public async Task Export_GateIsReleased_AfterSuccessAndAfterFailClosed503() // R56
    {
        _options.EntryAudit.FailClosedOnExport = true;
        _writer.Fail = true;
        Assert.Equal(503, Assert.IsType<StatusCodeResult>(await Export()).StatusCode);
        _writer.Fail = false;

        Assert.IsType<EmptyResult>(await Export());
        Assert.IsType<EmptyResult>(await Export());
    }

    [Fact]
    public async Task Export_ExportGateIsPerUser() // R56
    {
        Guid other = Guid.NewGuid();
        Assert.True(AccessExportGate.TryEnter(other));
        try
        {
            Assert.IsType<EmptyResult>(await Export());
        }
        finally
        {
            AccessExportGate.Exit(other);
        }
    }

    [Theory]
    [InlineData("\r=1+1", "\"'\r=1+1\"")]
    [InlineData("\t=1+1", "'\t=1+1")]
    [InlineData("  =1+1", "'  =1+1")]
    [InlineData("\uFF1D1+1", "'\uFF1D1+1")]
    public async Task Export_LeadingTriggerVariants_GuardedInFormNameAndUserName(string hostile, string expectedCell) // SEC-L1
    {
        FormAccessEntry row = _db.AddAccess(AccessEventType.DetailViewed, FormA, Now);
        row.FormName = hostile;
        row.UserName = hostile;
        _db.Context.SaveChanges();

        await Export();

        string text = new StreamReader(new MemoryStream(_body.ToArray()), Encoding.UTF8, true).ReadToEnd();
        // Columns: FormId,FormName,UserKey,UserName, so both text cells are checked in place.
        Assert.Contains($"{FormA},{expectedCell},{row.UserKey},{expectedCell},", text);
    }

    private sealed class AbortingStream(CancellationTokenSource? cts) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Fail();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw FailException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw FailException();
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private void Fail() => throw FailException();

        private IOException FailException()
        {
            cts?.Cancel();
            return new IOException("client gone");
        }
    }

    private sealed class RecordingWriter : IEntryAuditWriter
    {
        public List<AccessWrite> Written { get; } = [];
        public bool Fail { get; set; }
        public bool Throw { get; set; }

        public Task<AccessWriteResult> WriteAsync(AccessWrite write)
        {
            if (Throw)
            {
                throw new InvalidOperationException("writer down");
            }

            if (Fail)
            {
                return Task.FromResult(new AccessWriteResult(AccessWriteStatus.Failed, null, false));
            }

            Written.Add(write);
            return Task.FromResult(new AccessWriteResult(AccessWriteStatus.Written, Written.Count, false));
        }
    }
}
