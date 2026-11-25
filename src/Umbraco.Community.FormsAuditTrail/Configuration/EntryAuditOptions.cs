namespace Umbraco.Community.FormsAuditTrail.Configuration;

/// <summary>Settings under <c>FormsAuditTrail:EntryAudit</c> (design 7.7).</summary>
public sealed class EntryAuditOptions
{
    /// <summary>Group alias used when none is configured (D2).</summary>
    internal const string DefaultAccessLogGroup = "admin";

    /// <summary>
    /// The properties whose changes the config-change recorder writes as ConfigChanged rows (E17). Every public
    /// property is listed; a test enforces it so a new option forces a decision on whether it is recorded.
    /// Nothing secret is stored in these options.
    /// </summary>
    internal static readonly IReadOnlyList<string> RecordedKeys =
    [
        nameof(Enabled),
        nameof(LogListViews),
        nameof(LogListRecordIds),
        nameof(DeduplicateSeconds),
        nameof(StoreEditValues),
        nameof(RetentionDays),
        nameof(EditValueRetentionDays),
        nameof(CaptureSearchTerms),
        nameof(AccessLogUserGroups),
        nameof(KeepClearedValues),
        nameof(FailClosedOnExport),
        nameof(EnableCsvExport),
        nameof(EmitLogEvents),
        nameof(EmitLogEventRecordIds),
    ];

    /// <summary>Master switch for writing access rows.</summary>
    public bool Enabled { get; set; } = true;

    public bool LogListViews { get; set; } = true;

    /// <summary>Off by default (D7): a list view then stores a count, not the ids.</summary>
    public bool LogListRecordIds { get; set; }

    /// <summary>Seconds within which a repeat of the same view is not written again. 0 turns dedupe off.</summary>
    public int DeduplicateSeconds { get; set; } = 60;

    public StoreEditValuesMode StoreEditValues { get; set; } = StoreEditValuesMode.MaskSensitive;

    /// <summary>Days to keep access rows. 0 keeps them forever.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>Days after which stored edit values are nulled. 0 never nulls them.</summary>
    public int EditValueRetentionDays { get; set; } = 90;

    /// <summary>Free-text search terms can contain personal data, so they are not stored unless asked for.</summary>
    public bool CaptureSearchTerms { get; set; }

    /// <summary>
    /// Group aliases that can read the access log. Empty here, not <c>["admin"]</c>: the configuration binder appends
    /// to an initialised list, so a default could never be removed. <see cref="FormsAuditTrailOptionsSetup"/> applies it.
    /// </summary>
    public List<string> AccessLogUserGroups { get; set; } = [];

    /// <summary>Keep the old value when an edit blanks a field (SEC-03). Off by default.</summary>
    public bool KeepClearedValues { get; set; }

    /// <summary>Return 503 instead of an export or download whose audit row cannot be written (SEC-02).</summary>
    public bool FailClosedOnExport { get; set; }

    /// <summary>Gates the access-log CSV only. Null means follow <see cref="FormsAuditTrailOptions.EnableCsvExport"/> (SEC-L4).</summary>
    public bool? EnableCsvExport { get; set; }

    /// <summary>One structured log event per access row, as an independent copy (SEC-08).</summary>
    public bool EmitLogEvents { get; set; }

    public bool EmitLogEventRecordIds { get; set; }
}
