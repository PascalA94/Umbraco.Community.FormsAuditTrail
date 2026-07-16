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
}

public class PagedResultDto<T>
{
    public List<T> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)Total / PageSize);
}
