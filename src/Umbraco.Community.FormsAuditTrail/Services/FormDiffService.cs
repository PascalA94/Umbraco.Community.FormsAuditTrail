using System.Text.Json;
using System.Text.Json.Nodes;
using Umbraco.Community.FormsAuditTrail.Models;

namespace Umbraco.Community.FormsAuditTrail.Services;

public class FormDiffService : IFormDiffService
{
    public FormDiffResult ComputeDiff(string beforeJson, string afterJson)
    {
        JsonNode? before, after;
        try
        { before = JsonNode.Parse(beforeJson); }
        catch { return new FormDiffResult { Summary = "Could not parse before snapshot" }; }
        try
        { after = JsonNode.Parse(afterJson); }
        catch { return new FormDiffResult { Summary = "Could not parse after snapshot" }; }

        Dictionary<string, string> beforeFlat = Flatten(before);
        Dictionary<string, string> afterFlat = Flatten(after);

        var changes = new List<FormChange>();

        // Detect removed and modified
        foreach ((string? path, string? oldVal) in beforeFlat)
        {
            if (!afterFlat.TryGetValue(path, out var newVal))
            {
                changes.Add(BuildChange(ChangeType.Removed, path, oldVal, null, beforeFlat, afterFlat));
            }
            else if (oldVal != newVal)
            {
                changes.Add(BuildChange(ChangeType.Modified, path, oldVal, newVal, beforeFlat, afterFlat));
            }
        }

        // Detect added
        foreach ((string? path, string? newVal) in afterFlat)
        {
            if (!beforeFlat.ContainsKey(path))
            {
                changes.Add(BuildChange(ChangeType.Added, path, null, newVal, beforeFlat, afterFlat));
            }
        }

        // Detect moved fields: same field GUID appears under a different parent path
        Dictionary<string, string> beforeFieldPaths = ExtractFieldParentPaths(beforeFlat);
        Dictionary<string, string> afterFieldPaths = ExtractFieldParentPaths(afterFlat);
        var movedPrefixesToRemove = new HashSet<string>();
        var movedEntries = new List<FormChange>();
        foreach ((string? fieldId, string? beforeParent) in beforeFieldPaths)
        {
            if (afterFieldPaths.TryGetValue(fieldId, out var afterParent) && beforeParent != afterParent)
            {
                var caption = afterFlat.TryGetValue($"{afterParent}.Caption", out var c) ? c : fieldId;
                movedPrefixesToRemove.Add(beforeParent);
                movedPrefixesToRemove.Add(afterParent);
                movedEntries.Add(new FormChange
                {
                    ChangeType = ChangeType.Moved,
                    PropertyPath = afterParent,
                    FriendlyDescription = $"Field '{caption}' moved",
                    Category = "Field",
                });
            }
        }
        if (movedPrefixesToRemove.Count > 0)
        {
            changes = changes
                .Where(ch => !(movedPrefixesToRemove.Any(p => ch.PropertyPath.StartsWith(p))
                               && ch.ChangeType is ChangeType.Removed or ChangeType.Added))
                .Concat(movedEntries)
                .ToList();
        }

        changes = CollapseFieldChanges(changes, beforeFlat, afterFlat);
        changes = FilterNoisyFieldProperties(changes);

        var summary = BuildSummary(changes);
        return new FormDiffResult { Changes = changes, Summary = summary };
    }

    private static Dictionary<string, string> Flatten(JsonNode? node, string prefix = "")
    {
        var result = new Dictionary<string, string>();
        FlattenInto(node, prefix, result);
        return result;
    }

    private static void FlattenInto(JsonNode? node, string path, Dictionary<string, string> result)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> prop in obj)
                {
                    var childPath = string.IsNullOrEmpty(path) ? prop.Key : $"{path}.{prop.Key}";

                    // For pages array use index; for fieldsets/fields/workflows use GUID id
                    FlattenInto(prop.Value, childPath, result);
                }
                break;

            case JsonArray arr:
                // Determine if items have an "id" property for stable identity
                for (int i = 0; i < arr.Count; i++)
                {
                    JsonNode? item = arr[i];
                    var id = (item?["Id"] ?? item?["id"])?.GetValue<string>();
                    var key = id != null ? $"{path}[{id}]" : $"{path}[{i}]";
                    FlattenInto(item, key, result);
                }
                break;

            case JsonValue val:
                result[path] = val.GetValueKind() == System.Text.Json.JsonValueKind.String
                    ? val.GetValue<string>()
                    : val.ToJsonString();
                break;

            case null:
                result[path] = "";
                break;
        }
    }

    private static FormChange BuildChange(
        ChangeType type,
        string path,
        string? oldVal,
        string? newVal,
        Dictionary<string, string> beforeFlat,
        Dictionary<string, string> afterFlat)
    {
        var category = DetermineCategory(path);
        var description = BuildFriendlyDescription(type, path, oldVal, newVal, beforeFlat, afterFlat);

        return new FormChange
        {
            ChangeType = type,
            PropertyPath = path,
            FriendlyDescription = description,
            OldValue = oldVal,
            NewValue = newVal,
            Category = category,
        };
    }

    private static string DetermineCategory(string path)
    {
        if (path.StartsWith("Pages") && path.Contains("Fields"))
        {
            return "Field";
        }

        if (path.StartsWith("Pages") && path.Contains("FieldSets") && !path.Contains("Fields"))
        {
            return "FieldSet";
        }

        if (path.StartsWith("Pages"))
        {
            return "Page";
        }

        if (path.StartsWith("Workflows"))
        {
            return "Workflow";
        }

        if (path.Contains("Condition"))
        {
            return "Condition";
        }

        return "FormSetting";
    }

    private static string BuildFriendlyDescription(
        ChangeType type,
        string path,
        string? oldVal,
        string? newVal,
        Dictionary<string, string> beforeFlat,
        Dictionary<string, string> afterFlat)
    {
        // Form-level name
        if (path == "Name")
        {
            return type == ChangeType.Modified
                ? $"Form renamed from '{oldVal}' to '{newVal}'"
                : $"Form name set to '{newVal ?? oldVal}'";
        }

        // Field caption change
        if (path.Contains("Fields[") && path.EndsWith(".Caption"))
        {
            return type == ChangeType.Modified
                ? $"'{oldVal}' field renamed to '{newVal}'"
                : type == ChangeType.Added
                    ? $"'{newVal}' field added"
                    : $"'{oldVal}' field removed";
        }

        // Field added/removed (detected via Caption)
        if (path.Contains("Fields[") && path.EndsWith("].Caption"))
        {
            return type == ChangeType.Added ? $"'{newVal}' field added" : $"'{oldVal}' field removed";
        }

        // Field mandatory
        if (path.Contains("Fields[") && path.EndsWith(".Mandatory"))
        {
            var caption = GetFieldCaption(path, afterFlat) ?? GetFieldCaption(path, beforeFlat) ?? "Field";
            return newVal == "true"
                ? $"Field '{caption}' made mandatory"
                : $"Field '{caption}' made optional";
        }

        // Page caption
        if (path.StartsWith("Pages[") && path.EndsWith(".Caption"))
        {
            return type == ChangeType.Modified
                ? $"Page renamed from '{oldVal}' to '{newVal}'"
                : type == ChangeType.Added
                    ? $"Page '{newVal}' added"
                    : $"Page '{oldVal}' removed";
        }

        // Whole page added/removed (path ends at the GUID bracket)
        if (path.StartsWith("Pages[") && !path.Contains('.'))
        {
            var caption = afterFlat.TryGetValue($"{path}.Caption", out var ac) ? ac
                        : beforeFlat.TryGetValue($"{path}.Caption", out var bc) ? bc : "Page";
            return type == ChangeType.Added ? $"Page '{caption}' added" : $"Page '{caption}' removed";
        }

        // Workflow added/removed (path ends at GUID bracket)
        if (path.StartsWith("Workflows[") && !path.Contains('.'))
        {
            var wfName = GetWorkflowContext(path, afterFlat, beforeFlat);
            return type == ChangeType.Added ? $"Workflow '{wfName}' added" : $"Workflow '{wfName}' removed";
        }

        // Workflow active state
        if (path.Contains("Workflows[") && path.EndsWith(".Active"))
        {
            var name = GetWorkflowContext(path, afterFlat, beforeFlat);
            return newVal == "true" ? $"Workflow '{name}' activated" : $"Workflow '{name}' deactivated";
        }

        // Workflow name
        if (path.Contains("Workflows[") && path.EndsWith(".Name"))
        {
            return type == ChangeType.Modified
                ? $"Workflow renamed from '{oldVal}' to '{newVal}'"
                : $"Workflow '{newVal ?? oldVal}'";
        }

        // Workflow settings
        if (path.Contains("Workflows[") && path.Contains(".Settings."))
        {
            var name = GetWorkflowContext(path, afterFlat, beforeFlat);
            var settingKey = path.Split(".Settings.").LastOrDefault() ?? path;
            return $"Workflow '{name}' setting '{settingKey}' changed from '{oldVal}' to '{newVal}'";
        }

        // Generic fallback — include field context if this property belongs to a field
        var segment = path.Split('.').LastOrDefault() ?? path;
        var fieldContext = GetFieldContext(path, afterFlat, beforeFlat);
        var forField = fieldContext != null ? $" for '{fieldContext}'" : "";
        return type switch
        {
            ChangeType.Added => $"{segment} added: '{newVal}'{forField}",
            ChangeType.Removed => $"{segment} removed: '{oldVal}'{forField}",
            ChangeType.Modified => $"{segment} changed from '{oldVal}' to '{newVal}'{forField}",
            _ => $"{segment} changed{forField}",
        };
    }

    private static string? GetFieldCaption(string fieldPath, Dictionary<string, string> flat)
    {
        // Extract the path up to the field GUID bracket, then look for .Caption
        var idx = fieldPath.LastIndexOf("Fields[");
        if (idx < 0)
        {
            return null;
        }

        var end = fieldPath.IndexOf(']', idx);
        if (end < 0)
        {
            return null;
        }

        var basePath = fieldPath[..(end + 1)];
        return flat.TryGetValue($"{basePath}.Caption", out var c) ? c : null;
    }

    private static string? GetFieldContext(string path, Dictionary<string, string> afterFlat, Dictionary<string, string> beforeFlat)
    {
        if (!path.Contains("Fields["))
        {
            return null;
        }

        var caption = GetFieldCaption(path, afterFlat) ?? GetFieldCaption(path, beforeFlat);
        if (caption == null)
        {
            return null;
        }

        var idx = path.LastIndexOf("Fields[");
        var end = path.IndexOf(']', idx);
        if (end < 0)
        {
            return caption;
        }

        var basePath = path[..(end + 1)];

        Dictionary<string, string> flat = afterFlat.ContainsKey($"{basePath}.FieldTypeName") ? afterFlat : beforeFlat;
        flat.TryGetValue($"{basePath}.FieldTypeName", out var typeName);
        return string.IsNullOrEmpty(typeName) ? caption : $"{caption} ({typeName})";
    }

    private static string? GetWorkflowName(string workflowPath, Dictionary<string, string> flat)
    {
        var idx = workflowPath.IndexOf("Workflows[");
        if (idx < 0)
        {
            return null;
        }

        var end = workflowPath.IndexOf(']', idx);
        if (end < 0)
        {
            return null;
        }

        var basePath = workflowPath[..(end + 1)];
        return flat.TryGetValue($"{basePath}.Name", out var n) ? n : null;
    }

    private static string GetWorkflowContext(string path, Dictionary<string, string> afterFlat, Dictionary<string, string> beforeFlat)
    {
        var name = GetWorkflowName(path, afterFlat) ?? GetWorkflowName(path, beforeFlat) ?? "Workflow";
        var idx = path.IndexOf("Workflows[");
        var end = path.IndexOf(']', idx);
        if (end < 0)
        {
            return name;
        }

        var basePath = path[..(end + 1)];
        Dictionary<string, string> flat = afterFlat.ContainsKey($"{basePath}.WorkflowTypeName") ? afterFlat : beforeFlat;
        flat.TryGetValue($"{basePath}.WorkflowTypeName", out var typeName);
        return string.IsNullOrEmpty(typeName) ? name : $"{name} ({typeName})";
    }

    private static Dictionary<string, string> ExtractFieldParentPaths(Dictionary<string, string> flat)
    {
        var result = new Dictionary<string, string>();
        foreach (var key in flat.Keys)
        {
            var idx = key.LastIndexOf("Fields[");
            if (idx < 0)
            {
                continue;
            }

            var end = key.IndexOf(']', idx);
            if (end < 0)
            {
                continue;
            }

            var fieldPath = key[..(end + 1)];
            var fieldId = fieldPath[(idx + 7)..^1]; // strip "Fields[" and "]"
            if (!result.ContainsKey(fieldId))
            {
                result[fieldId] = fieldPath;
            }
        }
        return result;
    }

    // Collapse all property-level changes for a brand-new or fully-removed field into one line
    private static List<FormChange> CollapseFieldChanges(
        List<FormChange> changes,
        Dictionary<string, string> beforeFlat,
        Dictionary<string, string> afterFlat)
    {
        var result = new List<FormChange>();
        var handledPaths = new HashSet<string>();

        // Collect all field base paths present in changes
        var fieldBasePaths = changes
            .Where(c => c.PropertyPath.Contains("Fields["))
            .Select(c =>
            {
                var idx = c.PropertyPath.LastIndexOf("Fields[");
                var end = c.PropertyPath.IndexOf(']', idx);
                return end >= 0 ? c.PropertyPath[..(end + 1)] : null;
            })
            .Where(p => p != null)
            .Distinct()
            .ToList();

        foreach (var basePath in fieldBasePaths)
        {
            var fieldChanges = changes.Where(c => c.PropertyPath.StartsWith(basePath!)).ToList();
            var isNewField = fieldChanges.All(c => c.ChangeType == ChangeType.Added) && !beforeFlat.Keys.Any(k => k.StartsWith(basePath!));
            var isRemovedField = fieldChanges.All(c => c.ChangeType == ChangeType.Removed) && !afterFlat.Keys.Any(k => k.StartsWith(basePath!));

            if (isNewField || isRemovedField)
            {
                foreach (FormChange? ch in fieldChanges)
                {
                    handledPaths.Add(ch.PropertyPath);
                }

                var caption = isNewField
                    ? (afterFlat.TryGetValue($"{basePath}.Caption", out var ac) ? ac : "Unknown")
                    : (beforeFlat.TryGetValue($"{basePath}.Caption", out var bc) ? bc : "Unknown");

                Dictionary<string, string> flat = isNewField ? afterFlat : beforeFlat;
                flat.TryGetValue($"{basePath}.FieldTypeName", out var fieldTypeName);
                var typeLabel = string.IsNullOrEmpty(fieldTypeName) ? "" : $" ({fieldTypeName})";

                result.Add(new FormChange
                {
                    ChangeType = isNewField ? ChangeType.Added : ChangeType.Removed,
                    PropertyPath = basePath!,
                    FriendlyDescription = isNewField ? $"'{caption}' field added{typeLabel}" : $"'{caption}' field removed{typeLabel}",
                    Category = "Field",
                });
            }
        }

        // Collapse whole-workflow adds/removes
        var workflowBasePaths = changes
            .Where(c => c.PropertyPath.StartsWith("Workflows["))
            .Select(c =>
            {
                var end = c.PropertyPath.IndexOf(']');
                return end >= 0 ? c.PropertyPath[..(end + 1)] : null;
            })
            .Where(p => p != null)
            .Distinct()
            .ToList();

        foreach (var basePath in workflowBasePaths)
        {
            var wfChanges = changes.Where(c => c.PropertyPath.StartsWith(basePath!)).ToList();
            var isNew = wfChanges.All(c => c.ChangeType == ChangeType.Added) && !beforeFlat.Keys.Any(k => k.StartsWith(basePath!));
            var isRemoved = wfChanges.All(c => c.ChangeType == ChangeType.Removed) && !afterFlat.Keys.Any(k => k.StartsWith(basePath!));

            if (isNew || isRemoved)
            {
                foreach (FormChange? ch in wfChanges)
                {
                    handledPaths.Add(ch.PropertyPath);
                }

                Dictionary<string, string> flat = isNew ? afterFlat : beforeFlat;
                flat.TryGetValue($"{basePath}.Name", out var wfName);
                flat.TryGetValue($"{basePath}.WorkflowTypeName", out var wfType);
                var label = string.IsNullOrEmpty(wfType) ? (wfName ?? "Workflow") : $"{wfName ?? "Workflow"} ({wfType})";
                result.Add(new FormChange
                {
                    ChangeType = isNew ? ChangeType.Added : ChangeType.Removed,
                    PropertyPath = basePath!,
                    FriendlyDescription = isNew ? $"Workflow '{label}' added" : $"Workflow '{label}' removed",
                    Category = "Workflow",
                });
            }
        }

        // Add all non-collapsed changes
        result.AddRange(changes.Where(c => !handledPaths.Contains(c.PropertyPath)));
        return result;
    }

    // Strip internal/system property changes on existing (modified) fields — these are never meaningful to show
    private static readonly HashSet<string> _noisyFieldSuffixes =
    [
        ".Id", ".FieldTypeId", ".FieldTypeName", ".ShowLabel", ".ActionType", ".LogicType",
        ".RequiredErrorMessage", ".InvalidErrorMessage", ".Alias",
    ];

    private static readonly HashSet<string> _noisyWorkflowSuffixes =
    [
        ".WorkflowTypeId", ".WorkflowTypeName",
    ];

    private static List<FormChange> FilterNoisyFieldProperties(List<FormChange> changes) =>
        changes.Where(c =>
        {
            if (c.PropertyPath.Contains("Fields["))
            {
                return !_noisyFieldSuffixes.Any(suffix => c.PropertyPath.EndsWith(suffix));
            }

            if (c.PropertyPath.StartsWith("Workflows["))
            {
                return !_noisyWorkflowSuffixes.Any(suffix => c.PropertyPath.EndsWith(suffix));
            }

            return true;
        }).ToList();

    private static string BuildSummary(List<FormChange> changes)
    {
        var parts = new List<string>();
        IEnumerable<IGrouping<string, FormChange>> byCategory = changes.GroupBy(c => c.Category ?? "Other");
        foreach (IGrouping<string, FormChange> grp in byCategory)
        {
            var added = grp.Count(c => c.ChangeType == ChangeType.Added);
            var removed = grp.Count(c => c.ChangeType == ChangeType.Removed);
            var modified = grp.Count(c => c.ChangeType == ChangeType.Modified);
            var moved = grp.Count(c => c.ChangeType == ChangeType.Moved);
            var items = new List<string>();
            if (added > 0)
            {
                items.Add($"{added} added");
            }

            if (removed > 0)
            {
                items.Add($"{removed} removed");
            }

            if (modified > 0)
            {
                items.Add($"{modified} modified");
            }

            if (moved > 0)
            {
                items.Add($"{moved} moved");
            }

            if (items.Count > 0)
            {
                parts.Add($"{grp.Key}: {string.Join(", ", items)}");
            }
        }
        return parts.Count > 0 ? string.Join("; ", parts) : "No changes";
    }
}
