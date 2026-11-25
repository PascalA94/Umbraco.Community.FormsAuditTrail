using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
using Umbraco.Community.FormsAuditTrail.Persistence;
using Umbraco.Forms.Core.Data.Storage;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Security;
using Umbraco.Forms.Core.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// The access-log queries that SQLite cannot vouch for on SQL Server: the restricted-scope <c>Contains</c> lists, the
/// child-row <c>EXISTS</c>, the correlation join and the capped, ordered history.
/// </summary>
public class FormsAuditAccessApiSqlServerTests
{
    private static readonly Guid FormA = Guid.NewGuid();
    private static readonly Guid FormB = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

    [SqlServerFact]
    [Trait("Category", "SqlServer")]
    public async Task ListAndRecordHistory_RestrictedScope_OnSqlServer() // @M4-T16 fix1
    {
        using var database = SqlServerTestDatabase.Create();
        await using SqlServerFormsAuditDbContext db = database.CreateContext();
        await db.Database.MigrateAsync();

        Guid record = Guid.NewGuid();
        Guid correlation = Guid.NewGuid();
        db.AccessEntries.AddRange(
            Row(AccessEventType.DetailViewed, FormA, Now.AddMinutes(-3), record),
            Row(AccessEventType.Exported, FormA, Now.AddMinutes(-2), null, correlation, childIds: [Guid.NewGuid(), record]),
            Row(AccessEventType.ExportDownloaded, FormA, Now.AddMinutes(-1), null, correlation),
            Row(AccessEventType.ExportDownloaded, FormB, Now, null, correlation),
            Row(AccessEventType.DetailViewed, FormB, Now, Guid.NewGuid()),
            Row(AccessEventType.ConfigChanged, Guid.Empty, Now, null));
        await db.SaveChangesAsync();

        IFormsSecurity security = Substitute.For<IFormsSecurity>();
        security.HasAccessToForm(FormA).Returns(true);
        security.HasAccessToForm(FormB).Returns(false);
        IFormService forms = Substitute.For<IFormService>();
        forms.GetSlim().Returns([new FormSlim { Id = FormA }, new FormSlim { Id = FormB }]);
        IOptionsMonitor<FormsAuditTrailOptions> options = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        options.CurrentValue.Returns(new FormsAuditTrailOptions());
        IUserService users = Substitute.For<IUserService>();
        users.GetAsync(Arg.Any<IEnumerable<Guid>>()).Returns(Enumerable.Empty<IUser>());

        var sut = new FormsAuditAccessApiController(
            db, security, forms, Substitute.For<IRecordStorage>(), Substitute.For<IFormFieldInfoProvider>(),
            Substitute.For<ISensitiveDataPermission>(), Substitute.For<IEntryAuditWriter>(), Substitute.For<IBackOfficeSecurityAccessor>(),
            users, new UploadPathResolver(), options, NullLogger<FormsAuditAccessApiController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var list = Assert.IsType<PagedResultDto<AccessEntryListDto>>(
            Assert.IsType<OkObjectResult>((await sut.GetAccess(recordId: null, toDate: DateTime.MaxValue.Date)).Result).Value);
        Assert.Equal(3, list.Total);
        Assert.All(list.Items, i => Assert.Equal(FormA, i.FormId));

        var byRecord = Assert.IsType<PagedResultDto<AccessEntryListDto>>(
            Assert.IsType<OkObjectResult>((await sut.GetAccess(recordId: record)).Result).Value);
        Assert.Equal(2, byRecord.Total);

        sut.HistoryLimit = 2;
        var history = Assert.IsType<List<AccessEntryListDto>>(
            Assert.IsType<OkObjectResult>((await sut.GetRecordHistory(record)).Result).Value);
        Assert.Equal(["ExportDownloaded", "Exported"], history.Select(h => h.EventType));
        Assert.Equal(DateTimeKind.Utc, history[0].Timestamp.Kind);
        Assert.Equal("history", sut.Response.Headers[FormsAuditAccessApiController.TruncatedHeader].ToString());
    }

    private static FormAccessEntry Row(
        AccessEventType type, Guid formId, DateTime timestamp, Guid? recordId, Guid? correlationId = null, Guid[]? childIds = null)
    {
        var row = new FormAccessEntry
        {
            EventType = (int)type,
            FormId = formId,
            FormName = "Form",
            Timestamp = timestamp,
            RecordId = recordId,
            RecordCount = recordId is null ? childIds?.Length ?? 0 : 1,
            UserKey = Guid.NewGuid(),
            UserName = "Editor",
            CorrelationId = correlationId,
        };
        foreach (Guid id in childIds ?? [])
        {
            row.Records.Add(new FormAccessRecord { RecordId = id });
        }

        return row;
    }
}
