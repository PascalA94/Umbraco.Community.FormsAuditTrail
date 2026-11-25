using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Umbraco.Community.FormsAuditTrail.EntryAudit;

/// <summary>
/// List and export filters as the user set them. A raw search term is dropped by <see cref="EntryAuditDetail.Shape"/>
/// unless capture is on, because a free-text term can be an email address or a name. Named <c>EntryAuditFilter</c>
/// rather than <c>Filter</c> because <see cref="EntryAuditDetail.Filter"/> is the property that holds it and C# does
/// not allow a nested type and a member of the same name.
/// </summary>
public sealed record EntryAuditFilter(
    DateTime? From,
    DateTime? To,
    string? State,
    string? SortBy,
    string? SortDirection,
    bool? SearchTermUsed,
    string? SearchTerm);

/// <summary>An option's value before and after a <c>ConfigChanged</c> row (E17).</summary>
public sealed record ConfigValueChange(string? Old, string? New);

/// <summary>What an <c>AuditGap</c> row summarises: the events the writer could not store (E18).</summary>
public sealed record AuditGapInfo(int Count, DateTime FirstUtc, DateTime LastUtc, IReadOnlyList<string> EventTypes);

/// <summary>
/// The minimised context stored in the access row's <c>Detail</c> column. Every property is an allow-listed key: a
/// field value cannot reach the database through here because there is no property to hold one (SEC-02). The JSON is
/// versioned (<c>"v":1</c>), camelCase and omits nulls.
/// </summary>
public sealed record EntryAuditDetail
{
    public const int DefaultMaxBytes = 8192;

    private const int FileNameMaxLength = 260;
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public EntryAuditFilter? Filter { get; init; }
    public int? Page { get; init; }
    public int? PageSize { get; init; }
    public string? ExportType { get; init; }
    public string? FileName { get; init; }
    public bool? RecordIdsCaptured { get; init; }
    public bool? Bulk { get; init; }
    public string? DeleteKind { get; init; }
    public bool? FormUnresolved { get; init; }
    public bool? UserUnresolved { get; init; }
    public bool? SessionExpired { get; init; }
    public bool? Truncated { get; init; }
    public IReadOnlyDictionary<string, int>? OriginalLengths { get; init; }
    public Guid? WorkflowId { get; init; }
    public IReadOnlyList<string>? FieldCaptions { get; init; }
    public string? FilePathHash { get; init; }
    public string? MemberKey { get; init; }
    public bool? MemberIdentifierUsed { get; init; }
    public IReadOnlyDictionary<string, ConfigValueChange>? Config { get; init; }
    public AuditGapInfo? Gap { get; init; }
    public bool? BeforeUnavailable { get; init; }

    /// <summary>
    /// Applies the privacy rules before anything is stored: a search term is kept only when
    /// <paramref name="captureSearchTerms"/> is on (otherwise the fact of a search is kept), a file name is capped, and
    /// a member identifier is kept only when it is a key (E16), since a GUID or integer cannot be an email address.
    /// </summary>
    public static EntryAuditDetail Shape(EntryAuditDetail raw, bool captureSearchTerms)
    {
        ArgumentNullException.ThrowIfNull(raw);
        EntryAuditDetail shaped = raw;

        if (raw.Filter is { SearchTerm: { } term } filter && !string.IsNullOrWhiteSpace(term) && !captureSearchTerms)
        {
            shaped = shaped with { Filter = filter with { SearchTerm = null, SearchTermUsed = true } };
        }

        if (raw.FileName is { Length: > FileNameMaxLength } fileName)
        {
            shaped = shaped with { FileName = TextTruncation.Truncate(fileName, FileNameMaxLength) };
        }

        if (raw.MemberKey is { } memberKey && !IsKey(memberKey))
        {
            shaped = shaped with { MemberKey = null, MemberIdentifierUsed = true };
        }

        return shaped;
    }

    /// <summary>
    /// Serialises to at most <paramref name="maxBytes"/> UTF-8 bytes (SEC-02). When over, the least useful parts go first:
    /// the filter, then the original lengths, then the field captions, and <c>truncated</c> is set. There is deliberately
    /// no parameterless overload (P19): a caller must say which limit it is writing against.
    /// </summary>
    public string ToJson(int maxBytes = DefaultMaxBytes)
    {
        string json = Serialize(this);
        if (Fits(json, maxBytes))
        {
            return json;
        }

        EntryAuditDetail reduced = this with { Truncated = true };
        foreach (Func<EntryAuditDetail, EntryAuditDetail> drop in new Func<EntryAuditDetail, EntryAuditDetail>[]
        {
            d => d with { Filter = null },
            d => d with { OriginalLengths = null },
            d => d with { FieldCaptions = null },
        })
        {
            reduced = drop(reduced);
            json = Serialize(reduced);
            if (Fits(json, maxBytes))
            {
                return json;
            }
        }

        // Config and gap payloads can still be large. The row must be written, so they go too, rather than the write failing.
        reduced = reduced with { Config = null, Gap = null };
        json = Serialize(reduced);
        if (Fits(json, maxBytes))
        {
            return json;
        }

        return Serialize(new EntryAuditDetail { Truncated = true });
    }

    /// <summary>
    /// SHA-256 (lower-case hex) over this detail's filter JSON and paging, the dedupe key's filter part. It hashes
    /// whatever detail it is called on: the capture core calls it on the raw, unshaped detail so two different searches
    /// stay apart when terms are not stored (R33), and uses the result for dedupe only. The hash is never stored.
    /// </summary>
    public string FilterHash()
    {
        string filterJson = Filter is null ? string.Empty : JsonSerializer.Serialize(Filter, JsonOptions);
        // Page size is included so a different page length is a different view and still gets its own row.
        byte[] bytes = Encoding.UTF8.GetBytes($"{filterJson}|{Page}|{PageSize}");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static bool IsKey(string value) =>
        Guid.TryParse(value, out _)
        || (value.Length <= 19 && value.All(char.IsAsciiDigit) && value.Length > 0);

    private static bool Fits(string json, int maxBytes) => Encoding.UTF8.GetByteCount(json) <= maxBytes;

    private static string Serialize(EntryAuditDetail detail)
    {
        JsonObject node = JsonSerializer.SerializeToNode(detail, JsonOptions)!.AsObject();
        // The version leads so a reader can pick a parser before it reads anything else.
        node.Insert(0, "v", CurrentVersion);
        return node.ToJsonString(JsonOptions);
    }
}
