using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Microsoft.Extensions.Logging.Abstractions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Searchers;
using Umbraco.Forms.Core.Services.Notifications;
using Umbraco.Forms.Web.Api.ManagementApi.Export;
using Umbraco.Forms.Web.Api.ManagementApi.Member;
using Umbraco.Forms.Web.Api.ManagementApi.Record;
using Umbraco.Forms.Core.Persistence.Dtos;
using Xunit;
using FormsRecord = Umbraco.Forms.Core.Persistence.Dtos.Record;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// Pins the Umbraco Forms surface the entry audit depends on (SEC-01, spikes S1, S4, S5, S11). Forms is a range
/// dependency, so a Forms release that renames or moves one of these must fail CI here rather than silently
/// stop auditing at runtime. The CI latest leg runs these against the newest 18.x.
/// </summary>
public class FormsContractTests
{
    public static TheoryData<Type> ControllerTypes => new()
    {
        typeof(GetCollectionController),
        typeof(GetAuditTrailController),
        typeof(UpdateRecordController),
        typeof(DeleteRecordController),
        typeof(ExecuteActionController),
        typeof(PostGenerateExportController),
        typeof(ExportController),
        typeof(RetryWorkflowController),
        typeof(CreateRecordController),
        typeof(GetMemberFormSummariesController),
    };

    [Theory]
    [MemberData(nameof(ControllerTypes))]
    public void FormsController_IsPublicAndAController_ForFormsContract(Type controller)
    {
        Assert.True(controller.IsPublic, $"{controller.FullName} is no longer public");
        Assert.True(typeof(ControllerBase).IsAssignableFrom(controller), $"{controller.FullName} is no longer a controller");
    }

    [Fact]
    public void EntrySearchResultFetchingNotification_ExposesEntrySearchResultWithUniqueId_ForFormsContract()
    {
        var result = typeof(EntrySearchResultFetchingNotification).GetProperty(nameof(EntrySearchResultFetchingNotification.EntrySearchResult));
        Assert.NotNull(result);
        Assert.Equal(typeof(EntrySearchResult), result!.PropertyType);

        var uniqueId = typeof(EntrySearchResult).GetProperty(nameof(EntrySearchResult.UniqueId));
        Assert.NotNull(uniqueId);
        Assert.Equal(typeof(Guid), uniqueId!.PropertyType);
    }

    [Fact]
    public void RecordSavingNotification_ExposesRecordWithIdUniqueIdFormAndRecordFields_ForFormsContract()
    {
        var saved = typeof(RecordSavingNotification).GetProperty(nameof(RecordSavingNotification.SavedEntities));
        Assert.NotNull(saved);
        Assert.Equal(typeof(IEnumerable<FormsRecord>), saved!.PropertyType);

        Assert.Equal(typeof(int), typeof(FormsRecord).GetProperty(nameof(FormsRecord.Id))?.PropertyType);
        Assert.Equal(typeof(Guid), typeof(FormsRecord).GetProperty(nameof(FormsRecord.UniqueId))?.PropertyType);
        // Form is the form's id here, not a Form model.
        Assert.Equal(typeof(Guid), typeof(FormsRecord).GetProperty(nameof(FormsRecord.Form))?.PropertyType);
        Assert.Equal(typeof(Dictionary<Guid, RecordField>), typeof(FormsRecord).GetProperty(nameof(FormsRecord.RecordFields))?.PropertyType);
    }

    [Fact]
    public void RecordDeletingAndDeleted_ExposeDeletedRecords_ForFormsContract()
    {
        var deleting = typeof(RecordDeletingNotification).GetProperty(nameof(RecordDeletingNotification.DeletedEntities));
        Assert.NotNull(deleting);
        Assert.Equal(typeof(IEnumerable<FormsRecord>), deleting!.PropertyType);

        // S5: Deleted is a state-change notification carrying one record and its form.
        Assert.Equal(typeof(FormsRecord), typeof(RecordDeletedNotification).GetProperty(nameof(RecordDeletedNotification.Record))?.PropertyType);
        Assert.Equal(typeof(Form), typeof(RecordDeletedNotification).GetProperty(nameof(RecordDeletedNotification.Form))?.PropertyType);
    }

    [Fact]
    public void FormsFieldModel_HasSensitiveFlag_ForFormsContract()
    {
        // S4: the flag lives on Field, not on a separate field model.
        var flag = typeof(Field).GetProperty(nameof(Field.ContainsSensitiveData));
        Assert.NotNull(flag);
        Assert.Equal(typeof(bool), flag!.PropertyType);
        Assert.True(flag.CanRead && flag.CanWrite);
    }

    [Fact]
    public void ProtectFormUploadRequestsMiddleware_NamespaceAndAnchor_ForFormsContract()
    {
        // S11 (P9): the middleware is internal to Forms' pipeline, so it is found by name, not by typeof.
        var webAssembly = typeof(GetCollectionController).Assembly;
        var middleware = webAssembly.GetType("Umbraco.Forms.Web.HttpModules.ProtectFormUploadRequestsMiddleware");
        Assert.NotNull(middleware);
        Assert.True(typeof(IMiddleware).IsAssignableFrom(middleware));

        // Forms registers its pipeline filter under this name; the upload audit is inserted immediately before it.
        var services = new ServiceCollection();
        var typeLoader = new TypeLoader(Substitute.For<ITypeFinder>(), NullLogger<TypeLoader>.Instance, []);
        IUmbracoBuilder builder = new UmbracoBuilder(services, new ConfigurationBuilder().Build(), typeLoader);
        Umbraco.Forms.Web.Extensions.UmbracoBuilderExtensions.AddUmbracoFormsWeb(builder);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<UmbracoPipelineOptions>>().Value;
        var filter = options.PipelineFilters.SingleOrDefault(f => f.Name == "ProtectFormUploadRequestsMiddleware");
        var pipelineFilter = Assert.IsType<UmbracoPipelineFilter>(filter);
        Assert.NotNull(pipelineFilter.PrePipeline);
    }
}
