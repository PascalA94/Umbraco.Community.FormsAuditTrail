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
    private readonly Dictionary<Guid, int> _deletedRecordCounts = [];

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

    /// <summary>
    /// Counts the records Forms deleted in this request, per form, with or without a window: the deliberate exception to
    /// the no-op rule above. A permanent form delete is not an audited controller, so it never opens a window, yet its
    /// <c>Deleted</c> row needs the count (Task 14). Only a number is held, never ids or values, and only for the request.
    /// </summary>
    /// <remarks>
    /// A plain sum: Forms' Delete record-set action publishes <c>RecordDeleting</c> twice per record (S5), but that path
    /// takes its ids from the window, and the form-delete path publishes once per batch.
    /// </remarks>
    internal void CountDeletedRecords(Guid formId, int count)
    {
        lock (_gate)
        {
            _deletedRecordCounts[formId] = _deletedRecordCounts.GetValueOrDefault(formId) + count;
        }
    }

    internal int DeletedRecordCount(Guid formId)
    {
        lock (_gate)
        {
            return _deletedRecordCounts.GetValueOrDefault(formId);
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
