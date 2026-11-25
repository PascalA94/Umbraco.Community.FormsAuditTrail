using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Forms.Web.Api.ManagementApi.Record;
using Umbraco.Community.FormsAuditTrail.Models;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// SEC-01 completeness guard: every public type in the Forms entry API namespaces must be either audited or
/// explicitly allow-listed with a reason, so a new Forms endpoint that returns entry data cannot ship unaudited.
/// The lists are read from <see cref="FormsControllerMap"/>, the same map the action filter matches against.
/// </summary>
public class FormsManagementApiCompletenessTests
{
    private static readonly string[] EntryApiNamespaces =
    [
        "Umbraco.Forms.Web.Api.ManagementApi.Record",
        "Umbraco.Forms.Web.Api.ManagementApi.Export",
        "Umbraco.Forms.Web.Api.ManagementApi.Member",
        "Umbraco.Forms.Web.Api.ManagementApi.Media",
    ];

    private static readonly HashSet<string> Audited =
        FormsControllerMap.Audited.Keys.Select(t => t.FullName!).ToHashSet();

    // Reasons come from spikes S1 and S11 (18.2.2 and 17.6.2, each confirmed at runtime).
    private static readonly Dictionary<string, string> NotAudited =
        FormsControllerMap.NotAudited.ToDictionary(p => p.Key.FullName!, p => p.Value);

    [Fact]
    public void EveryFormsEntryApiType_IsAuditedOrAllowListed_ForSec01()
    {
        var unknown = typeof(GetCollectionController).Assembly.GetExportedTypes()
            .Where(t => EntryApiNamespaces.Any(ns => t.Namespace == ns))
            .Select(t => t.FullName!)
            .Where(name => !Audited.Contains(name) && !NotAudited.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(unknown.Count == 0,
            "Forms added entry API types that are neither audited nor allow-listed: " + string.Join(", ", unknown));
    }

    [Fact]
    public void NotAudited_EveryEntryHasAReason_ForSec01()
    {
        foreach (var (type, reason) in NotAudited)
        {
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{type} has no reason");
            Assert.DoesNotContain('\n', reason);
            Assert.DoesNotContain('\r', reason);
        }
    }

    [Fact]
    public void AuditedAndNotAudited_DoNotOverlap_ForSec01()
    {
        Assert.Empty(Audited.Intersect(NotAudited.Keys));
    }

    [Fact]
    public void FormsControllerMap_HasTenAuditedAndReasonedNotAudited()
    {
        Assert.Equal(10, FormsControllerMap.Audited.Count);
        Assert.Equal(13, FormsControllerMap.NotAudited.Count);
        Assert.Equal(AccessEventType.Deleted, FormsControllerMap.Audited[typeof(ExecuteActionController)]);
        Assert.Equal(AccessEventType.WorkflowRetried, FormsControllerMap.Audited[typeof(RetryWorkflowController)]);
        Assert.Equal(AccessEventType.Created, FormsControllerMap.Audited[typeof(CreateRecordController)]);
        Assert.Equal(AccessEventType.MemberSummaryViewed,
            FormsControllerMap.Audited[typeof(Umbraco.Forms.Web.Api.ManagementApi.Member.GetMemberFormSummariesController)]);
        Assert.All(FormsControllerMap.NotAudited.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }
}
