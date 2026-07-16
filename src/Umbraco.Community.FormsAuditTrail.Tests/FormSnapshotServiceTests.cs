using System.Text.Json;
using NSubstitute;
using Umbraco.Community.FormsAuditTrail.Services;
using Umbraco.Forms.Core;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Providers;
using Umbraco.Forms.Core.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class FormSnapshotServiceTests
{
    private readonly IFieldTypeStorage _fieldTypeStorage = Substitute.For<IFieldTypeStorage>();
    private readonly IWorkflowService _workflowService = Substitute.For<IWorkflowService>();
    private readonly FormSnapshotService _sut;

    public FormSnapshotServiceTests()
    {
        var workflowTypes = new WorkflowCollection(() => []);
        _sut = new FormSnapshotService(_fieldTypeStorage, workflowTypes, _workflowService);
    }

    private static Form BuildForm()
    {
        var field = new Field
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Alias = "email",
            Caption = "Email address",
            Mandatory = true,
        };

        var container = new FieldsetContainer
        {
            Width = 12,
            Fields = [field],
        };

        var fieldSet = new FieldSet
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Caption = "Details",
            Containers = [container],
        };

        var page = new Page
        {
            Caption = "Page 1",
            FieldSets = [fieldSet],
        };

        return new Form
        {
            Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            Name = "Contact form",
            Pages = [page],
        };
    }

    [Fact]
    public void Serialize_IncludesFormNameAndStructure()
    {
        Form form = BuildForm();
        _workflowService.Get(form).Returns([]);

        var json = _sut.Serialize(form);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("Contact form", doc.RootElement.GetProperty("Name").GetString());
        JsonElement page = doc.RootElement.GetProperty("Pages")[0];
        Assert.Equal("Page 1", page.GetProperty("Caption").GetString());
        JsonElement field = page.GetProperty("FieldSets")[0].GetProperty("Containers")[0].GetProperty("Fields")[0];
        Assert.Equal("Email address", field.GetProperty("Caption").GetString());
        Assert.True(field.GetProperty("Mandatory").GetBoolean());
    }

    [Fact]
    public void Serialize_IncludesWorkflowsFromWorkflowService()
    {
        Form form = BuildForm();
        var workflow = new Workflow
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Name = "Send email",
            Active = true,
        };
        _workflowService.Get(form).Returns([workflow]);

        var json = _sut.Serialize(form);
        using var doc = JsonDocument.Parse(json);

        JsonElement wf = doc.RootElement.GetProperty("Workflows")[0];
        Assert.Equal("Send email", wf.GetProperty("Name").GetString());
        Assert.True(wf.GetProperty("Active").GetBoolean());
    }

    [Fact]
    public void Serialize_ExcludesVolatileFormMetadata()
    {
        Form form = BuildForm();
        _workflowService.Get(form).Returns([]);

        var json = _sut.Serialize(form);
        using var doc = JsonDocument.Parse(json);

        // Timestamps and author metadata would make every diff noisy — they must not be snapshotted
        Assert.False(doc.RootElement.TryGetProperty("Created", out _));
        Assert.False(doc.RootElement.TryGetProperty("CreatedBy", out _));
    }
}
