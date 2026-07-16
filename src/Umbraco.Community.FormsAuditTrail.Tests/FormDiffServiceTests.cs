using System.Text.Json;
using Umbraco.Community.FormsAuditTrail.Models;
using Umbraco.Community.FormsAuditTrail.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class FormDiffServiceTests
{
    private readonly FormDiffService _sut = new();

    private static readonly Guid _pageId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _fieldSetId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _fieldId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid _workflowId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static string Snapshot(
        string formName = "Contact form",
        object[]? fields = null,
        object[]? secondContainerFields = null,
        object[]? workflows = null)
    {
        // Two containers are always present so a field moving between them
        // produces no container-level noise in the diff
        var containers = new List<object>
        {
            new { Width = 12, Fields = fields ?? [] },
            new { Width = 12, Fields = secondContainerFields ?? [] },
        };

        var snapshot = new
        {
            Id = Guid.Empty,
            Name = formName,
            Pages = new object[]
            {
                new
                {
                    Id = _pageId,
                    Caption = "Page 1",
                    FieldSets = new object[]
                    {
                        new { Id = _fieldSetId, Caption = "Set 1", Containers = containers },
                    },
                },
            },
            Workflows = workflows ?? [],
        };
        return JsonSerializer.Serialize(snapshot);
    }

    private static object Field(
        Guid? id = null,
        string caption = "Name",
        string alias = "name",
        bool mandatory = false,
        string fieldTypeName = "Short answer") => new
        {
            Id = id ?? _fieldId,
            Alias = alias,
            Caption = caption,
            Mandatory = mandatory,
            FieldTypeName = fieldTypeName,
        };

    private static object Workflow(string name = "Send email", string typeName = "Send email workflow", bool active = true) => new
    {
        Id = _workflowId,
        Name = name,
        Active = active,
        WorkflowTypeName = typeName,
        Settings = new Dictionary<string, string> { ["Email"] = "a@b.com" },
    };

    [Fact]
    public void IdenticalSnapshots_ProduceNoChanges()
    {
        var json = Snapshot(fields: [Field()]);

        FormDiffResult result = _sut.ComputeDiff(json, json);

        Assert.Empty(result.Changes);
        Assert.Equal("No changes", result.Summary);
    }

    [Fact]
    public void MalformedBeforeSnapshot_ReturnsParseFailureSummary()
    {
        FormDiffResult result = _sut.ComputeDiff("{not json", Snapshot());

        Assert.Equal("Could not parse before snapshot", result.Summary);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void FormRename_IsReportedAsModifiedFormSetting()
    {
        FormDiffResult result = _sut.ComputeDiff(Snapshot("Old name"), Snapshot("New name"));

        FormChange change = Assert.Single(result.Changes);
        Assert.Equal(ChangeType.Modified, change.ChangeType);
        Assert.Equal("FormSetting", change.Category);
        Assert.Equal("Form renamed from 'Old name' to 'New name'", change.FriendlyDescription);
    }

    [Fact]
    public void FieldCaptionChange_IsReportedAsRename()
    {
        var before = Snapshot(fields: [Field(caption: "Name")]);
        var after = Snapshot(fields: [Field(caption: "Full name")]);

        FormDiffResult result = _sut.ComputeDiff(before, after);

        FormChange change = Assert.Single(result.Changes);
        Assert.Equal(ChangeType.Modified, change.ChangeType);
        Assert.Equal("Field", change.Category);
        Assert.Equal("'Name' field renamed to 'Full name'", change.FriendlyDescription);
    }

    [Fact]
    public void NewField_IsCollapsedToSingleAddedChange()
    {
        var before = Snapshot(fields: []);
        var after = Snapshot(fields: [Field(caption: "Email", fieldTypeName: "Email")]);

        FormDiffResult result = _sut.ComputeDiff(before, after);

        FormChange change = Assert.Single(result.Changes);
        Assert.Equal(ChangeType.Added, change.ChangeType);
        Assert.Equal("Field", change.Category);
        Assert.Equal("'Email' field added (Email)", change.FriendlyDescription);
    }

    [Fact]
    public void RemovedField_IsCollapsedToSingleRemovedChange()
    {
        var before = Snapshot(fields: [Field(caption: "Email", fieldTypeName: "Email")]);
        var after = Snapshot(fields: []);

        FormDiffResult result = _sut.ComputeDiff(before, after);

        FormChange change = Assert.Single(result.Changes);
        Assert.Equal(ChangeType.Removed, change.ChangeType);
        Assert.Equal("'Email' field removed (Email)", change.FriendlyDescription);
    }

    [Fact]
    public void MandatoryToggle_IsReportedWithFieldContext()
    {
        var before = Snapshot(fields: [Field(mandatory: false)]);
        var after = Snapshot(fields: [Field(mandatory: true)]);

        FormDiffResult result = _sut.ComputeDiff(before, after);

        FormChange change = Assert.Single(result.Changes);
        Assert.Equal("Field 'Name' made mandatory", change.FriendlyDescription);
    }

    [Fact]
    public void MovedField_IsReportedAsMovedNotAddRemove()
    {
        var before = Snapshot(fields: [Field()]);
        var after = Snapshot(fields: [], secondContainerFields: [Field()]);

        FormDiffResult result = _sut.ComputeDiff(before, after);

        Assert.Contains(result.Changes, c => c.ChangeType == ChangeType.Moved);
        Assert.DoesNotContain(result.Changes, c => c.ChangeType is ChangeType.Added or ChangeType.Removed);
    }

    [Fact]
    public void AddedWorkflow_IsCollapsedToSingleChange()
    {
        var before = Snapshot();
        var after = Snapshot(workflows: [Workflow()]);

        FormDiffResult result = _sut.ComputeDiff(before, after);

        FormChange change = Assert.Single(result.Changes);
        Assert.Equal(ChangeType.Added, change.ChangeType);
        Assert.Equal("Workflow", change.Category);
        Assert.Equal("Workflow 'Send email (Send email workflow)' added", change.FriendlyDescription);
    }

    [Fact]
    public void WorkflowSettingChange_IsDescribedWithWorkflowContext()
    {
        var before = Snapshot(workflows: [Workflow()]);
        var afterWorkflow = new
        {
            Id = _workflowId,
            Name = "Send email",
            Active = true,
            WorkflowTypeName = "Send email workflow",
            Settings = new Dictionary<string, string> { ["Email"] = "c@d.com" },
        };
        var after = Snapshot(workflows: [afterWorkflow]);

        FormDiffResult result = _sut.ComputeDiff(before, after);

        FormChange change = Assert.Single(result.Changes);
        Assert.Equal(ChangeType.Modified, change.ChangeType);
        Assert.Contains("setting 'Email' changed from 'a@b.com' to 'c@d.com'", change.FriendlyDescription);
    }

    [Fact]
    public void WorkflowDeactivation_IsReported()
    {
        var before = Snapshot(workflows: [Workflow(active: true)]);
        var after = Snapshot(workflows: [Workflow(active: false)]);

        FormDiffResult result = _sut.ComputeDiff(before, after);

        FormChange change = Assert.Single(result.Changes);
        Assert.Equal("Workflow 'Send email (Send email workflow)' deactivated", change.FriendlyDescription);
    }

    [Fact]
    public void AliasChange_OnExistingField_IsFilteredAsNoise()
    {
        var before = Snapshot(fields: [Field(alias: "name")]);
        var after = Snapshot(fields: [Field(alias: "fullName")]);

        FormDiffResult result = _sut.ComputeDiff(before, after);

        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Summary_GroupsCountsByCategory()
    {
        var before = Snapshot("Old", fields: []);
        var after = Snapshot("New", fields: [Field()]);

        FormDiffResult result = _sut.ComputeDiff(before, after);

        Assert.Contains("Field: 1 added", result.Summary);
        Assert.Contains("FormSetting: 1 modified", result.Summary);
    }
}
