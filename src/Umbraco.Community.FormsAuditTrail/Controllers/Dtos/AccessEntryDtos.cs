namespace Umbraco.Community.FormsAuditTrail.Controllers.Dtos;

/// <summary>One access-log row as the list and the entry history show it.</summary>
public class AccessEntryListDto
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public Guid FormId { get; set; }
    public string FormName { get; set; } = string.Empty;
    public Guid? RecordId { get; set; }
    public int RecordCount { get; set; }

    /// <summary>From the row's detail; null when the row does not say (most rows carry no child ids).</summary>
    public bool? RecordIdsCaptured { get; set; }

    public Guid UserKey { get; set; }

    /// <summary>The name copied at write time. Presentation only: <see cref="UserKey"/> is the evidence (SEC-L2).</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>The user's name now, set only when it differs from <see cref="UserName"/> (SEC-L2).</summary>
    public string? CurrentUserName { get; set; }

    public string? Detail { get; set; }
    public Guid? CorrelationId { get; set; }
}

public class AccessEntryDetailDto : AccessEntryListDto
{
    public List<EntryChangeDto> Changes { get; set; } = new();

    /// <summary>The other half of an export and its download, matched by correlation id.</summary>
    public long? LinkedAccessId { get; set; }

    /// <summary>How many record ids are stored as child rows. The ids themselves are not returned.</summary>
    public int ChildRecordCount { get; set; }
}

public class EntryChangeDto
{
    public Guid FieldId { get; set; }
    public string FieldCaption { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public bool Masked { get; set; }
    public bool Cleared { get; set; }
    public DateTime? ValuesScrubbedAt { get; set; }
}

public class AccessUserSummaryDto
{
    public Guid UserKey { get; set; }
    public string UserName { get; set; } = string.Empty;

    /// <summary>Set only when the user's current name differs from the stored one (SEC-L2).</summary>
    public string? CurrentUserName { get; set; }
}
