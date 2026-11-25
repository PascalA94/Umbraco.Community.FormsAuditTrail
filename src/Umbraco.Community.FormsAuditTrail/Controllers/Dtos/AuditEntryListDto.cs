namespace Umbraco.Community.FormsAuditTrail.Controllers.Dtos;

public class AuditEntryListDto
{
    public int Id { get; set; }
    public Guid FormId { get; set; }
    public string FormName { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public Guid UserKey { get; set; }
    public DateTime Timestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? ChangeSummary { get; set; }
}

public class AuditEntryDetailDto : AuditEntryListDto
{
    public string BeforeSnapshot { get; set; } = string.Empty;
    public string? AfterSnapshot { get; set; }
    public List<AuditChangeDto> Changes { get; set; } = new();
}

public class AuditChangeDto
{
    public string ChangeType { get; set; } = string.Empty;
    public string PropertyPath { get; set; } = string.Empty;
    public string FriendlyDescription { get; set; } = string.Empty;
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public string? Category { get; set; }
}

public class FormSummaryDto
{
    public Guid FormId { get; set; }
    public string FormName { get; set; } = string.Empty;
}

public class UserSummaryDto
{
    public Guid UserKey { get; set; }
    public string UserName { get; set; } = string.Empty;
}

public class AuditConfigDto
{
    public bool CsvExportEnabled { get; set; }

    public bool EntryAuditEnabled { get; set; }

    public bool CanReadAccessLog { get; set; }

    public int AccessRetentionDays { get; set; }

    public int EditValueRetentionDays { get; set; }

    public bool EntryAuditCsvEnabled { get; set; }

    // The members below are null unless CanReadAccessLog: health detail is for the people who read the log (T8).
    public bool? EntryAuditHealthy { get; set; }

    public List<string>? HealthReasons { get; set; }

    public DateTime? LastWriteFailureUtc { get; set; }

    public int? WriteFailuresSinceStart { get; set; }

    public DateTime? LastJobRunUtc { get; set; }

    /// <summary>Null when edit values are not stored (<c>StoreEditValues</c> is None), since then there is nothing to warn about (R44).</summary>
    public bool? ForeignValueHandlers { get; set; }

    /// <summary>
    /// The configured edit-value mode. Sent as the enum name, not its number, so the dashboard does not depend on the
    /// member order (R50).
    /// </summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Configuration.StoreEditValuesMode>))]
    public Configuration.StoreEditValuesMode? StoreEditValues { get; set; }

    public List<OffPeriodDto>? OffPeriods { get; set; }
}

/// <param name="StartUnknown">True when the span was already open at the start of the retention window, so its real start is older and no longer stored.</param>
public record OffPeriodDto(DateTime FromUtc, DateTime? ToUtc, bool StartUnknown = false);

public class PagedResultDto<T>
{
    public List<T> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)Total / PageSize);
}
