using System.Text.Json;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class EntryAuditDetailTests
{
    private static readonly HashSet<string> AllowedKeys =
    [
        "v", "filter", "page", "pageSize", "exportType", "fileName", "recordIdsCaptured", "bulk", "deleteKind",
        "formUnresolved", "userUnresolved", "truncated", "originalLengths", "workflowId", "fieldCaptions",
        "filePathHash", "memberKey", "memberIdentifierUsed", "config", "gap", "beforeUnavailable", "requestedCount",
        "subjectUserKey", "eventType", "server", "sessionExpired", "valuesScrubIncomplete",
    ];

    private static EntryAuditDetail Everything() => new()
    {
        Filter = new EntryAuditFilter(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), null, "Approved", "created", "desc", true, "x"),
        Page = 1,
        PageSize = 20,
        ExportType = "csv",
        FileName = "a.csv",
        RecordIdsCaptured = true,
        Bulk = false,
        DeleteKind = "single",
        FormUnresolved = false,
        UserUnresolved = false,
        SessionExpired = false,
        Truncated = false,
        OriginalLengths = new Dictionary<string, int> { ["formName"] = 600 },
        WorkflowId = Guid.NewGuid(),
        FieldCaptions = ["Name"],
        FilePathHash = "abc",
        MemberKey = Guid.NewGuid().ToString(),
        MemberIdentifierUsed = false,
        Config = new Dictionary<string, ConfigValueChange> { ["Enabled"] = new("true", "false") },
        Gap = new AuditGapInfo(2, DateTime.UtcNow, DateTime.UtcNow, ["ListViewed"]),
        BeforeUnavailable = false,
        ValuesScrubIncomplete = true,
        SubjectUserKey = Guid.NewGuid(),
        EventType = 12,
        Server = "web-1",
        RequestedCount = 3,
    };

    [Fact]
    public void Detail_EveryPublicProperty_IsAnAllowedKey() // merge: both tracks added keys; a new one must be listed here
    {
        IEnumerable<string> keys = typeof(EntryAuditDetail)
            .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name));

        Assert.All(keys, k => Assert.Contains(k, AllowedKeys));
    }

    [Fact]
    public void Detail_ToJson_WritesVersionAndOnlyAllowedKeys()
    {
        using JsonDocument doc = JsonDocument.Parse(Everything().ToJson());

        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("v", doc.RootElement.EnumerateObject().First().Name);
        Assert.All(doc.RootElement.EnumerateObject().Select(p => p.Name), k => Assert.Contains(k, AllowedKeys));
        Assert.Equal(AllowedKeys.Count, doc.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void Detail_ToJson_OmitsNulls()
    {
        using JsonDocument doc = JsonDocument.Parse(new EntryAuditDetail { Page = 3 }.ToJson());

        Assert.Equal(["v", "page"], doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void Detail_ToJson_Over8KB_TruncatesAndFlags() // SEC-02
    {
        EntryAuditDetail detail = Everything() with
        {
            FieldCaptions = Enumerable.Range(0, 400).Select(i => new string('c', 40) + i).ToList(),
        };

        string json = detail.ToJson();

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= 8192);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("fieldCaptions", out _));
        // Dropped in order: filter and originalLengths go before fieldCaptions.
        Assert.False(doc.RootElement.TryGetProperty("filter", out _));
        Assert.False(doc.RootElement.TryGetProperty("originalLengths", out _));
        Assert.Equal("a.csv", doc.RootElement.GetProperty("fileName").GetString());
    }

    [Fact]
    public void Detail_ToJson_DropsOnlyAsMuchAsNeeded()
    {
        EntryAuditDetail detail = Everything() with
        {
            Filter = new EntryAuditFilter(null, null, null, null, null, null, new string('s', 9000)),
        };

        using JsonDocument doc = JsonDocument.Parse(detail.ToJson());

        Assert.False(doc.RootElement.TryGetProperty("filter", out _));
        Assert.True(doc.RootElement.TryGetProperty("originalLengths", out _));
        Assert.True(doc.RootElement.TryGetProperty("fieldCaptions", out _));
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Detail_ToJson_NeverExceedsMaxBytesEvenWhenNothingDroppableIsLeft()
    {
        var detail = new EntryAuditDetail
        {
            Config = Enumerable.Range(0, 500).ToDictionary(i => "Key" + i, i => new ConfigValueChange(new string('o', 30), new string('n', 30))),
        };

        string json = detail.ToJson(maxBytes: 8192);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(json) <= 8192);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Detail_Shape_CapsFileNameAt260()
    {
        EntryAuditDetail shaped = EntryAuditDetail.Shape(new EntryAuditDetail { FileName = new string('f', 400) }, false);

        Assert.Equal(260, shaped.FileName!.Length);
    }

    [Fact]
    public void Detail_Shape_FileNameEmojiAtTheCut_DoesNotSplitSurrogatePair() // @M2-FIX A4
    {
        // 259 letters then an emoji (two UTF-16 units) puts the high surrogate at the last allowed position.
        string name = new string('f', 259) + "\U0001F600.csv";

        string shaped = EntryAuditDetail.Shape(new EntryAuditDetail { FileName = name }, false).FileName!;

        Assert.Equal(259, shaped.Length);
        Assert.False(char.IsHighSurrogate(shaped[^1]));
    }

    [Fact]
    public void Detail_Shape_MemberEmailDropped_KeyKept() // E16
    {
        EntryAuditDetail email = EntryAuditDetail.Shape(new EntryAuditDetail { MemberKey = "jane@example.com" }, false);
        Guid key = Guid.NewGuid();
        EntryAuditDetail guid = EntryAuditDetail.Shape(new EntryAuditDetail { MemberKey = key.ToString() }, false);
        EntryAuditDetail number = EntryAuditDetail.Shape(new EntryAuditDetail { MemberKey = "1234" }, false);

        Assert.Null(email.MemberKey);
        Assert.True(email.MemberIdentifierUsed);
        Assert.Equal(key.ToString(), guid.MemberKey);
        Assert.Null(guid.MemberIdentifierUsed);
        Assert.Equal("1234", number.MemberKey);
        Assert.DoesNotContain("jane@example.com", email.ToJson());
    }

    [Fact]
    public void Detail_Shape_DropsSearchTermUnlessCaptureOn()
    {
        var raw = new EntryAuditDetail { Filter = new EntryAuditFilter(null, null, null, null, null, null, "jane@example.com") };

        string off = EntryAuditDetail.Shape(raw, false).ToJson();
        string on = EntryAuditDetail.Shape(raw, true).ToJson();

        Assert.DoesNotContain("jane@example.com", off);
        Assert.Contains("\"searchTermUsed\":true", off);
        Assert.Contains("jane@example.com", on);
    }

    [Fact]
    public void Detail_Shape_NoTerm_LeavesFilterAlone()
    {
        var raw = new EntryAuditDetail { Filter = new EntryAuditFilter(null, null, "Approved", null, null, null, "  ") };

        EntryAuditDetail shaped = EntryAuditDetail.Shape(raw, false);

        Assert.Null(shaped.Filter!.SearchTermUsed);
        Assert.Equal("Approved", shaped.Filter.State);
    }

    [Fact]
    public void Detail_FilterHash_DiffersByPageAndFilter_SameForSame()
    {
        EntryAuditDetail a = new() { Page = 1, Filter = new EntryAuditFilter(null, null, "Approved", null, null, null, null) };
        EntryAuditDetail same = new() { Page = 1, Filter = new EntryAuditFilter(null, null, "Approved", null, null, null, null) };
        EntryAuditDetail otherPage = a with { Page = 2 };
        EntryAuditDetail otherFilter = a with { Filter = a.Filter! with { State = "Pending" } };

        Assert.Equal(a.FilterHash(), same.FilterHash());
        Assert.NotEqual(a.FilterHash(), otherPage.FilterHash());
        Assert.NotEqual(a.FilterHash(), otherFilter.FilterHash());
        Assert.Equal(64, a.FilterHash().Length);
    }
}
