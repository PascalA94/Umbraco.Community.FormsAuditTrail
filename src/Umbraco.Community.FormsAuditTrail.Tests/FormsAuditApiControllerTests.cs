using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.Controllers;
using Umbraco.Community.FormsAuditTrail.Controllers.Dtos;
using Umbraco.Forms.Core.Security;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class FormsAuditApiControllerTests : IDisposable
{
    private static readonly Guid _formA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid _formB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private readonly TestDatabase _db = new();
    private readonly IFormsSecurity _formsSecurity = Substitute.For<IFormsSecurity>();
    private readonly FormsAuditTrailOptions _options = new();
    private readonly FormsAuditApiController _sut;

    public FormsAuditApiControllerTests()
    {
        IOptionsMonitor<FormsAuditTrailOptions> optionsMonitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        optionsMonitor.CurrentValue.Returns(_options);
        _sut = new FormsAuditApiController(_db.Context, _formsSecurity, optionsMonitor);
        _db.AddEntry(_formA, "Form A", DateTime.UtcNow.AddHours(-2));
        _db.AddEntry(_formA, "Form A", DateTime.UtcNow.AddHours(-1));
        _db.AddEntry(_formB, "Form B", DateTime.UtcNow);
    }

    private void GrantAccess(bool formA, bool formB)
    {
        _formsSecurity.HasAccessToForm(_formA).Returns(formA);
        _formsSecurity.HasAccessToForm(_formB).Returns(formB);
    }

    [Fact]
    public async Task GetEntries_WithFullAccess_ReturnsEverything()
    {
        GrantAccess(formA: true, formB: true);

        ActionResult<PagedResultDto<AuditEntryListDto>> result = await _sut.GetEntries(formId: null);
        PagedResultDto<AuditEntryListDto> page = AssertOk<PagedResultDto<AuditEntryListDto>>(result.Result);

        Assert.Equal(3, page.Total);
    }

    [Fact]
    public async Task GetEntries_HidesFormsTheUserCannotAccess()
    {
        GrantAccess(formA: true, formB: false);

        ActionResult<PagedResultDto<AuditEntryListDto>> result = await _sut.GetEntries(formId: null);
        PagedResultDto<AuditEntryListDto> page = AssertOk<PagedResultDto<AuditEntryListDto>>(result.Result);

        Assert.Equal(2, page.Total);
        Assert.All(page.Items, i => Assert.Equal(_formA, i.FormId));
    }

    [Fact]
    public async Task GetEntries_ExplicitlyRequestingInaccessibleForm_IsForbidden()
    {
        GrantAccess(formA: true, formB: false);

        ActionResult<PagedResultDto<AuditEntryListDto>> result = await _sut.GetEntries(formId: _formB);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task GetEntry_ForInaccessibleForm_IsNotFound()
    {
        GrantAccess(formA: false, formB: false);
        var entryId = _db.Context.AuditEntries.First().Id;

        ActionResult<AuditEntryDetailDto> result = await _sut.GetEntry(entryId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetForms_OnlyListsAccessibleForms()
    {
        GrantAccess(formA: false, formB: true);

        ActionResult<List<FormSummaryDto>> result = await _sut.GetForms(CancellationToken.None);
        List<FormSummaryDto> forms = AssertOk<List<FormSummaryDto>>(result.Result);

        Assert.Equal(_formB, Assert.Single(forms).FormId);
    }

    [Fact]
    public async Task GetUsers_OnlyIncludesUsersOfAccessibleForms()
    {
        GrantAccess(formA: true, formB: false);
        _db.AddEntry(_formB, "Form B", DateTime.UtcNow, userName: "Form B only editor");

        ActionResult<List<UserSummaryDto>> result = await _sut.GetUsers(CancellationToken.None);
        List<UserSummaryDto> users = AssertOk<List<UserSummaryDto>>(result.Result);

        Assert.DoesNotContain(users, u => u.UserName == "Form B only editor");
    }

    [Fact]
    public async Task ExportCsv_WhenDisabledInConfig_IsNotFound()
    {
        GrantAccess(formA: true, formB: true);
        _options.EnableCsvExport = false;

        IActionResult result = await _sut.ExportCsv(null, null, null, null, null);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ExportCsv_WhenEnabled_ReturnsCsvFile()
    {
        GrantAccess(formA: true, formB: true);

        IActionResult result = await _sut.ExportCsv(null, null, null, null, null);

        FileContentResult file = Assert.IsType<FileContentResult>(result);
        Assert.Equal("text/csv", file.ContentType);
    }

    [Fact]
    public void GetConfig_ReflectsCsvExportSetting()
    {
        _options.EnableCsvExport = false;

        AuditConfigDto config = AssertOk<AuditConfigDto>(_sut.GetConfig().Result);

        Assert.False(config.CsvExportEnabled);
    }

    private static T AssertOk<T>(ActionResult? result)
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<T>(ok.Value);
    }

    public void Dispose() => _db.Dispose();
}
