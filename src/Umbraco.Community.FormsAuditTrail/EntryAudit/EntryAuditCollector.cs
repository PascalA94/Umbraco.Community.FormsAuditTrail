using Umbraco.Community.FormsAuditTrail.Models;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>One field's change in an edit. A cleared field carries no values (SEC-03).</summary>
public sealed record EntryFieldChange(Guid FieldId, string FieldCaption, string? OldValue, string? NewValue, bool Masked, bool Cleared);

/// <summary>
/// Per-request scratch space that links the action filter to the Forms notification handlers. Every mutator is a
/// no-op when no window is open, so a notification raised by a workflow, the Delivery API or a background job (no
/// filtered request) records nothing (S1).
/// </summary>
public sealed class EntryAuditCollector
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _seenRecordIds = [];

    internal CaptureWindow? Window { get; private set; }

    internal void Open(AccessEventType kind, Guid? formId, Guid? recordId, EntryAuditDetail detail)
    {
        lock (_gate)
        {
            _seenRecordIds.Clear();
            Window = new CaptureWindow { Kind = kind, FormId = formId, RecordId = recordId, Detail = detail };
        }
    }

    internal bool IsOpen(AccessEventType kind) => Window?.Kind == kind;

    internal void SetBeforeValues(IReadOnlyDictionary<Guid, IReadOnlyList<object?>> values)
    {
        lock (_gate)
        {
            if (Window is { } window)
            {
                window.BeforeValues = values;
            }
        }
    }

    /// <summary>The before read failed: the edit is still written, flagged, with no changes (E12).</summary>
    internal void MarkBeforeUnavailable()
    {
        lock (_gate)
        {
            if (Window is { } window)
            {
                window.BeforeValues = null;
                window.Detail = window.Detail with { BeforeUnavailable = true };
            }
        }
    }

    internal void AddFetchedRecord(Guid recordId)
    {
        lock (_gate)
        {
            if (Window is not { } window)
            {
                return;
            }

            window.FetchedRowCount++;
            if (_seenRecordIds.Add(recordId))
            {
                window.RecordIds.Add(recordId);
            }
        }
    }

    internal void SetFormIdIfMissing(Guid formId)
    {
        lock (_gate)
        {
            if (Window is { FormId: null } window)
            {
                window.FormId = formId;
            }
        }
    }

    internal void SetEdit(Guid formId, Guid recordId, IReadOnlyList<EntryFieldChange> changes)
    {
        lock (_gate)
        {
            if (Window is not { } window)
            {
                return;
            }

            window.FormId ??= formId;
            window.RecordId = recordId;
            window.Changes.Clear();
            window.Changes.AddRange(changes);
        }
    }

    /// <summary>Records the new entry's id and the captions of the fields submitted, never their values (P4).</summary>
    internal void SetCreated(Guid formId, Guid recordId, IReadOnlyList<string> fieldCaptions)
    {
        lock (_gate)
        {
            if (Window is not { } window)
            {
                return;
            }

            window.FormId ??= formId;
            window.RecordId = recordId;
            window.Detail = window.Detail with { FieldCaptions = fieldCaptions };
        }
    }

    internal void AddDeletedRecord(Guid formId, Guid recordId)
    {
        lock (_gate)
        {
            Window?.DeletedRecords.Add((formId, recordId));
        }
    }
}
