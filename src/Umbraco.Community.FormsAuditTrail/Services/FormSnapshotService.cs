using System.Text.Json;
using System.Text.Json.Serialization;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Providers;
using Umbraco.Forms.Core.Services;

namespace Umbraco.Community.FormsAuditTrail.Services;

public class FormSnapshotService : IFormSnapshotService
{
    private readonly IFieldTypeStorage _fieldTypeStorage;
    private readonly WorkflowCollection _workflowTypes;
    private readonly IWorkflowService _workflowService;

    private static readonly JsonSerializerOptions _options = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        WriteIndented = false,
    };

    public FormSnapshotService(IFieldTypeStorage fieldTypeStorage, WorkflowCollection workflowTypes, IWorkflowService workflowService)
    {
        _fieldTypeStorage = fieldTypeStorage;
        _workflowTypes = workflowTypes;
        _workflowService = workflowService;
    }

    public string Serialize(Form form)
    {
        FormSnapshot snapshot = MapToSnapshot(form);
        return JsonSerializer.Serialize(snapshot, _options);
    }

    private FormSnapshot MapToSnapshot(Form form) => new()
    {
        Id = form.Id,
        Name = form.Name,
        CssClass = form.CssClass,
        SubmitLabel = form.SubmitLabel,
        NextLabel = form.NextLabel,
        PreviousLabel = form.PrevLabel,
        GoToPageOnSubmit = form.GoToPageOnSubmit,
        MessageOnSubmit = form.MessageOnSubmit,
        ShowValidationSummary = form.ShowValidationSummary,
        HideFieldValidation = form.HideFieldValidation,
        StoreRecordsLocally = form.StoreRecordsLocally,
        Pages = form.Pages?.Select(MapPage).ToList() ?? [],
        Workflows = _workflowService.Get(form)?.Select(MapWorkflow).ToList() ?? [],
    };

    private PageSnapshot MapPage(Page page) => new()
    {
        Id = page.Id,
        Caption = page.Caption,
        FieldSets = page.FieldSets?.Select(MapFieldSet).ToList() ?? [],
    };

    private FieldSetSnapshot MapFieldSet(FieldSet fs) => new()
    {
        Id = fs.Id,
        Caption = fs.Caption,
        Containers = fs.Containers?.Select(MapContainer).ToList() ?? [],
    };

    private ContainerSnapshot MapContainer(FieldsetContainer container) => new()
    {
        Width = container.Width,
        Fields = container.Fields?.Select(MapField).ToList() ?? [],
    };

    private FieldSnapshot MapField(Field field)
    {
        Dictionary<string, string> settings = field.Settings?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? string.Empty)
                       ?? new Dictionary<string, string>();
        var fieldTypeName = _fieldTypeStorage.GetFieldTypeByField(field.FieldTypeId, settings)?.Name;
        return new()
        {
            Id = field.Id,
            Alias = field.Alias,
            Caption = field.Caption,
            ToolTip = field.ToolTip,
            Mandatory = field.Mandatory,
            RequiredErrorMessage = field.RequiredErrorMessage,
            Regex = field.RegEx,
            InvalidErrorMessage = field.InvalidErrorMessage,
            FieldTypeId = field.FieldTypeId,
            FieldTypeName = fieldTypeName,
            PreValues = field.PreValues?.Select(pv => new PreValueSnapshot { Value = pv.Value, Caption = pv.Caption }).ToList() ?? [],
            Settings = field.Settings?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? string.Empty) ?? [],
            Condition = field.Condition != null ? new ConditionSnapshot
            {
                ActionType = field.Condition.ActionType.ToString(),
                LogicType = field.Condition.LogicType.ToString(),
                Rules = field.Condition.Rules?.Select(r => new ConditionRuleSnapshot
                {
                    Field = r.Field.ToString(),
                    Operator = r.Operator.ToString(),
                    Value = r.Value,
                }).ToList() ?? [],
            } : null,
        };
    }

    private WorkflowSnapshot MapWorkflow(Workflow workflow) => new()
    {
        Id = workflow.Id,
        Name = workflow.Name,
        Active = workflow.Active,
        WorkflowTypeId = workflow.WorkflowTypeId,
        // Tolerant lookup: the collection's indexer throws for unregistered workflow types
        // (e.g. a provider package that has since been uninstalled)
        WorkflowTypeName = _workflowTypes.FirstOrDefault(t => t.Id == workflow.WorkflowTypeId)?.Name,
        ExecutesOn = workflow.ExecutesOn.ToString(),
        Settings = workflow.Settings?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.ToString() ?? string.Empty) ?? [],
    };
}

// Snapshot DTOs — plain data classes for clean serialization
internal record FormSnapshot
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? CssClass { get; init; }
    public string? SubmitLabel { get; init; }
    public string? NextLabel { get; init; }
    public string? PreviousLabel { get; init; }
    public string? GoToPageOnSubmit { get; init; }
    public string? MessageOnSubmit { get; init; }
    public bool ShowValidationSummary { get; init; }
    public bool HideFieldValidation { get; init; }
    public bool StoreRecordsLocally { get; init; }
    public List<PageSnapshot> Pages { get; init; } = [];
    public List<WorkflowSnapshot> Workflows { get; init; } = [];
}

internal record PageSnapshot
{
    public Guid Id { get; init; }
    public string? Caption { get; init; }
    public List<FieldSetSnapshot> FieldSets { get; init; } = [];
}

internal record WorkflowSnapshot
{
    public Guid Id { get; init; }
    public string? Name { get; init; }
    public bool Active { get; init; }
    public Guid WorkflowTypeId { get; init; }
    public string? WorkflowTypeName { get; init; }
    public string? ExecutesOn { get; init; }
    public Dictionary<string, string> Settings { get; init; } = [];
}

internal record FieldSetSnapshot
{
    public Guid Id { get; init; }
    public string? Caption { get; init; }
    public List<ContainerSnapshot> Containers { get; init; } = [];
}

internal record ContainerSnapshot
{
    public int Width { get; init; }
    public List<FieldSnapshot> Fields { get; init; } = [];
}

internal record FieldSnapshot
{
    public Guid Id { get; init; }
    public string? Alias { get; init; }
    public string? Caption { get; init; }
    public string? ToolTip { get; init; }
    public bool Mandatory { get; init; }
    public string? RequiredErrorMessage { get; init; }
    public string? Regex { get; init; }
    public string? InvalidErrorMessage { get; init; }
    public Guid FieldTypeId { get; init; }
    public string? FieldTypeName { get; init; }
    public List<PreValueSnapshot> PreValues { get; init; } = [];
    public Dictionary<string, string> Settings { get; init; } = [];
    public ConditionSnapshot? Condition { get; init; }
}

internal record PreValueSnapshot
{
    public string? Value { get; init; }
    public string? Caption { get; init; }
}

internal record ConditionSnapshot
{
    public string? ActionType { get; init; }
    public string? LogicType { get; init; }
    public List<ConditionRuleSnapshot> Rules { get; init; } = [];
}

internal record ConditionRuleSnapshot
{
    public string? Field { get; init; }
    public string? Operator { get; init; }
    public string? Value { get; init; }
}
