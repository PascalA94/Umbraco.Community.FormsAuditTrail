using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Forms.Web.Api.ManagementApi.Export;
using Umbraco.Forms.Web.Api.ManagementApi.Member;
using Umbraco.Forms.Web.Api.ManagementApi.Record;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// Every public type in the Forms entry API namespaces is either audited or listed with the reason it is not (SEC-01,
/// design 7.3 as amended by spike S11, confirmed at runtime on Forms 18.2.2 and 17.6.2). Both sets use <c>typeof</c>,
/// so a Forms rename or removal breaks this package's build instead of silently un-auditing an endpoint, and the
/// completeness contract test fails when Forms adds a type that is in neither set.
/// </summary>
internal static class FormsControllerMap
{
    /// <summary>The controllers the action filter matches, and the event each one records.</summary>
    public static IReadOnlyDictionary<Type, AccessEventType> Audited { get; } = new Dictionary<Type, AccessEventType>
    {
        [typeof(GetCollectionController)] = AccessEventType.ListViewed,
        [typeof(GetAuditTrailController)] = AccessEventType.DetailViewed,
        [typeof(UpdateRecordController)] = AccessEventType.Edited,
        [typeof(DeleteRecordController)] = AccessEventType.Deleted,
        // The Delete record-set action is how the backoffice deletes, one key or many (S1), so it is a Deleted event.
        [typeof(ExecuteActionController)] = AccessEventType.Deleted,
        [typeof(PostGenerateExportController)] = AccessEventType.Exported,
        [typeof(ExportController)] = AccessEventType.ExportDownloaded,
        [typeof(RetryWorkflowController)] = AccessEventType.WorkflowRetried,
        [typeof(CreateRecordController)] = AccessEventType.Created,
        [typeof(GetMemberFormSummariesController)] = AccessEventType.MemberSummaryViewed,
    };

    /// <summary>Types deliberately not audited, each with a one-line reason shown in the README and dashboard.</summary>
    public static IReadOnlyDictionary<Type, string> NotAudited { get; } = new Dictionary<Type, string>
    {
        [typeof(GetMetadataController)] = "Returns the entry count and last submitted date only.",
        [typeof(GetWorkflowAuditTrailController)] = "Returns workflow run status only, no field values or record ids.",
        [typeof(GetPageNumberController)] = "Returns a page number only; it takes a record id but returns none.",
        [typeof(GetRecordSetActionCollectionController)] = "Returns the list of available record-set actions.",
        [typeof(Umbraco.Forms.Web.Api.ManagementApi.Media.GetByPathMediarController)] = "Reads CMS media only and cannot reach Forms uploads.",
        [typeof(GetLinkableMemberPropertiesController)] = "Returns member type property names only.",
        [typeof(GetExportTypesController)] = "Returns the list of exporters only.",
        [typeof(RecordControllerBase)] = "Abstract base class, not an endpoint.",
        [typeof(GetRecordsControllerBase)] = "Abstract base class, not an endpoint.",
        [typeof(Umbraco.Forms.Web.Api.ManagementApi.Media.MediaControllerBase)] = "Abstract base class, not an endpoint.",
        [typeof(PostGenerateExportController.GenerateExportResponseModel)] = "Response model, not an endpoint.",
        [typeof(LinkableMemberProperty)] = "Response model, not an endpoint.",
        [typeof(MemberFormSummary)] = "Response model, not an endpoint.",
    };
}
