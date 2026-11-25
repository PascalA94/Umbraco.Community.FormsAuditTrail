using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;
using Umbraco.Community.FormsAuditTrail.Authorization;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
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
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();
    private readonly IAuditStateStore _state = Substitute.For<IAuditStateStore>();
    private readonly IForeignValueHandlerDetector _detector = Substitute.For<IForeignValueHandlerDetector>();
    private readonly EntryAuditHealth _health;
    private readonly FormsAuditApiController _sut;

    public FormsAuditApiControllerTests()
    {
        IOptionsMonitor<FormsAuditTrailOptions> optionsMonitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        optionsMonitor.CurrentValue.Returns(_options);
        _health = new EntryAuditHealth(_state, TimeProvider.System);
        _sut = new FormsAuditApiController(_db.Context, _formsSecurity, optionsMonitor, _authorization, _health, TimeProvider.System, _detector, Microsoft.Extensions.Logging.Abstractions.NullLogger<FormsAuditApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
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

    private void SetAccessLogAccess(bool allowed) =>
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), AccessLogPolicy.Name)
            .Returns(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());

    private async Task<AuditConfigDto> GetConfig() => AssertOk<AuditConfigDto>((await _sut.GetConfig()).Result);

    [Fact]
    public async Task GetConfig_ReflectsCsvExportSetting() // @M4-T15
    {
        SetAccessLogAccess(false);
        _options.EnableCsvExport = false;

        AuditConfigDto config = await GetConfig();

        Assert.False(config.CsvExportEnabled);
    }

    [Fact]
    public async Task GetConfig_ReportsEntryAuditFieldsAndAccess()
    {
        SetAccessLogAccess(true);
        _options.EntryAudit.Enabled = true;
        _options.EntryAudit.RetentionDays = 30;
        _options.EntryAudit.EditValueRetentionDays = 7;

        AuditConfigDto config = await GetConfig();

        Assert.True(config.EntryAuditEnabled);
        Assert.True(config.CanReadAccessLog);
        Assert.Equal(30, config.AccessRetentionDays);
        Assert.Equal(7, config.EditValueRetentionDays);
        Assert.NotNull(config.EntryAuditHealthy);
        Assert.NotNull(config.HealthReasons);
        Assert.Equal(0, config.WriteFailuresSinceStart);
        Assert.NotNull(config.OffPeriods);
    }

    [Fact]
    public async Task GetConfig_HealthFieldsOnlyForAccessLogUsers() // T8
    {
        _health.RecordWriteFailure(Models.AccessEventType.ListViewed, Guid.Empty);
        SetAccessLogAccess(false);

        AuditConfigDto config = await GetConfig();

        Assert.False(config.CanReadAccessLog);
        Assert.Null(config.EntryAuditHealthy);
        Assert.Null(config.HealthReasons);
        Assert.Null(config.LastWriteFailureUtc);
        Assert.Null(config.WriteFailuresSinceStart);
        Assert.Null(config.LastJobRunUtc);
        Assert.Null(config.ForeignValueHandlers);
        Assert.Null(config.OffPeriods);

        SetAccessLogAccess(true);
        AuditConfigDto reader = await GetConfig();

        Assert.False(reader.EntryAuditHealthy);
        Assert.Contains(EntryAuditHealth.WriteFailuresReason, reader.HealthReasons!);
        Assert.Equal(1, reader.WriteFailuresSinceStart);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetConfig_ForeignValueHandlers_ComesFromTheDetector_WhenEditValuesAreStored(bool found) // @M3-T13 R44
    {
        SetAccessLogAccess(true);
        _options.EntryAudit.StoreEditValues = StoreEditValuesMode.MaskSensitive;
        _detector.HasForeignValueHandlers.Returns(found);

        AuditConfigDto config = await GetConfig();

        Assert.Equal(found, config.ForeignValueHandlers);
        Assert.Equal(StoreEditValuesMode.MaskSensitive, config.StoreEditValues);
    }

    [Fact]
    public async Task GetConfig_ForeignValueHandlers_IsNull_WhenEditValuesAreNotStored() // @M3-T13 R44
    {
        SetAccessLogAccess(true);
        _options.EntryAudit.StoreEditValues = StoreEditValuesMode.None;
        _detector.HasForeignValueHandlers.Returns(true);

        AuditConfigDto config = await GetConfig();

        Assert.Null(config.ForeignValueHandlers);
        Assert.Equal(StoreEditValuesMode.None, config.StoreEditValues);
    }

    [Fact]
    public async Task GetConfig_StoreEditValues_OnlyForAccessLogUsers() // @M4-T17b R50
    {
        SetAccessLogAccess(false);
        _detector.HasForeignValueHandlers.Returns(true);

        AuditConfigDto config = await GetConfig();

        Assert.Null(config.StoreEditValues);
        Assert.Null(config.ForeignValueHandlers);
    }

    [Theory]
    [InlineData(StoreEditValuesMode.None, "None")]
    [InlineData(StoreEditValuesMode.MaskSensitive, "MaskSensitive")]
    [InlineData(StoreEditValuesMode.All, "All")]
    public async Task GetConfig_StoreEditValues_SerialisesAsTheEnumName(StoreEditValuesMode mode, string expected) // @M4-T17b R50
    {
        SetAccessLogAccess(true);
        _options.EntryAudit.StoreEditValues = mode;

        AuditConfigDto config = await GetConfig();
        string json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Contains($"\"storeEditValues\":\"{expected}\"", json);
    }

    [Fact]
    public async Task GetConfig_AuthorisationThrows_StillReturnsDefinitionFieldsWithoutAccessLog()
    {
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), AccessLogPolicy.Name)
            .Returns<AuthorizationResult>(_ => throw new InvalidOperationException("user lookup failed"));
        _options.EnableCsvExport = false;
        _options.EntryAudit.RetentionDays = 12;

        AuditConfigDto config = await GetConfig();

        Assert.False(config.CanReadAccessLog);
        Assert.False(config.CsvExportEnabled);
        Assert.Equal(12, config.AccessRetentionDays);
        Assert.Null(config.EntryAuditHealthy);
    }

    [Fact]
    public async Task GetConfig_EntryAuditCsvEnabled_FollowsSplitSetting() // T20
    {
        SetAccessLogAccess(false);
        _options.EnableCsvExport = true;
        _options.EntryAudit.EnableCsvExport = false;

        Assert.False((await GetConfig()).EntryAuditCsvEnabled);
        Assert.True((await GetConfig()).CsvExportEnabled);

        _options.EntryAudit.EnableCsvExport = null;
        _options.EnableCsvExport = false;

        Assert.False((await GetConfig()).EntryAuditCsvEnabled);
    }

    private static T AssertOk<T>(ActionResult? result)
    {
        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<T>(ok.Value);
    }

    public void Dispose() => _db.Dispose();
}
