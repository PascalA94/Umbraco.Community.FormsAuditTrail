using System.Text.Encodings.Web;
using System.Text.Json;
using NSubstitute;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Xunit;
using H = Umbraco.Community.FormsAuditTrail.Tests.CapturePipelineHarness;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// Task 14a (spec 9, P7): earlier tests prove masking at the differ and at the writer call. These prove it end to end:
/// the real pipeline runs with sentinel values and every value in the new tables is read back, so a value that reaches
/// the database by any path (a column added later included) fails here.
/// </summary>
public class SentinelScanTests
{
    private static readonly string UploadPath = $"/media/forms/upload/form_{H.FormId}/{Guid.NewGuid()}/{H.UploadFileName}";

    private static readonly string[] PlainValues =
    [
        H.NameOld, H.NameNew, H.MultiAOld, H.MultiBOld, H.MultiANew, H.MultiBNew, H.UnicodeOld, H.UnicodeNew, H.QuoteOld, H.QuoteNew,
    ];

    private static readonly string[] SensitiveValues = [H.SensitiveOld, H.SensitiveNew];

    private static readonly string[] AllValues = [.. PlainValues, .. SensitiveValues, H.ClearedOld, H.SensitiveClearedOld];

    /// <summary>Every capture path that can see an entry value, driven with sentinels. Deletes and config changes have their own tests.</summary>
    private static async Task RunEveryEventAsync(H h)
    {
        await h.RunEditAsync();
        await h.RunDetailAsync();
        await h.RunListAsync(H.SearchTerm);
        await h.RunMemberSummaryAsync(H.MemberIdentifier);
        await h.RunCreateAsync();
        await h.RunUploadOpenAsync(UploadPath);
        await h.RunExportAsync(H.ExportSearchTerm, H.ExportFileName);
        await h.RunExportDownloadAsync(H.ExportFileName);
    }

    private static readonly AccessEventType[] DrivenEvents =
    [
        AccessEventType.Edited, AccessEventType.DetailViewed, AccessEventType.ListViewed, AccessEventType.MemberSummaryViewed,
        AccessEventType.Created, AccessEventType.FileViewed, AccessEventType.Exported, AccessEventType.ExportDownloaded,
    ];

    /// <summary>
    /// A stored value can differ from the sentinel as typed: JSON escapes quotes, backslashes and non-ASCII (the default
    /// encoder as surrogate pairs, the relaxed one only what it must), and a URL encodes them. Each form is searched, so
    /// an escaped copy cannot slip past a raw Contains.
    /// </summary>
    private static IReadOnlyList<string> Forms(string sentinel)
    {
        static string Json(string s, JavaScriptEncoder? encoder)
        {
            string json = JsonSerializer.Serialize(s, new JsonSerializerOptions { Encoder = encoder });
            return json[1..^1];
        }

        return [sentinel, Uri.EscapeDataString(sentinel), Json(sentinel, null), Json(sentinel, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)];
    }

    /// <summary>
    /// The forms plus the leading ASCII run (up to the first space, quote or non-ASCII character), which survives every
    /// escaping. The run is skipped when it is just the shared "SENTINEL" prefix, which would match every sentinel.
    /// </summary>
    private static IEnumerable<string> LeakTokens(string sentinel)
    {
        foreach (string form in Forms(sentinel))
        {
            yield return form;
        }

        string prefix = new(sentinel.TakeWhile(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (prefix.Length < sentinel.Length && prefix != "SENTINEL")
        {
            yield return prefix;
        }
    }

    private static bool Present(IEnumerable<string> stored, string sentinel) =>
        stored.Any(v => Forms(sentinel).Any(f => v.Contains(f, StringComparison.OrdinalIgnoreCase)));

    private static bool Leaked(IEnumerable<string> stored, string sentinel) =>
        stored.Any(v => LeakTokens(sentinel).Any(t => v.Contains(t, StringComparison.OrdinalIgnoreCase)));

    private static void AssertNoneLeaked(IEnumerable<string> stored, params string[] sentinels)
    {
        string[] haystack = stored.ToArray();
        foreach (string sentinel in sentinels)
        {
            Assert.False(Leaked(haystack, sentinel), $"A stored value contains sentinel '{sentinel}'.");
        }
    }

    [Theory]
    [InlineData(StoreEditValuesMode.None)]
    [InlineData(StoreEditValuesMode.MaskSensitive)]
    [InlineData(StoreEditValuesMode.All)]
    public async Task Scan_StoreEditValuesMode_OnlyAllowedSentinelsStored(StoreEditValuesMode mode) // @M3-T14a P7
    {
        using var h = new H(mode);

        await RunEveryEventAsync(h);

        IReadOnlyList<string> stored = h.ScanTextColumns();
        // Guards against a scan that passes because nothing was written: exactly the events driven landed.
        Assert.Equal(DrivenEvents.ToHashSet(), h.EventTypes());

        bool plainAllowed = mode is StoreEditValuesMode.MaskSensitive or StoreEditValuesMode.All;
        bool sensitiveAllowed = mode == StoreEditValuesMode.All;

        foreach (string value in PlainValues)
        {
            Assert.Equal(plainAllowed, Present(stored, value));
            if (!plainAllowed)
            {
                Assert.False(Leaked(stored, value), $"'{value}' leaked under {mode}.");
            }
        }

        foreach (string value in SensitiveValues)
        {
            Assert.Equal(sensitiveAllowed, Present(stored, value));
            if (!sensitiveAllowed)
            {
                Assert.False(Leaked(stored, value), $"'{value}' leaked under {mode}.");
            }
        }

        AssertNoneLeaked(stored, H.ClearedOld, H.SensitiveClearedOld); // KeepClearedValues is off.
    }

    [Theory]
    [InlineData(StoreEditValuesMode.None, true, false)]
    [InlineData(StoreEditValuesMode.MaskSensitive, true, false)]
    [InlineData(StoreEditValuesMode.All, true, true)]
    [InlineData(StoreEditValuesMode.All, false, false)]
    public async Task Scan_KeepClearedValues_ClearedOldValuesStoredOnlyWhereTheRulesAllow(
        StoreEditValuesMode mode, bool keep, bool expectSensitiveStored) // @M3-T14a SEC-03
    {
        using var h = new H(mode, keepClearedValues: keep);

        await h.RunEditAsync();

        IReadOnlyList<string> stored = h.ScanTextColumns();
        var cleared = h.EntryChangeRows().ToDictionary(r => r.Caption);

        // Plain cleared old value: stored only with Keep and a mode that stores values at all.
        bool plainStored = keep && mode != StoreEditValuesMode.None;
        Assert.Equal(plainStored, Present(stored, H.ClearedOld));
        // Sensitive cleared old value: raw only under All; MaskSensitive keeps the placeholder, never the value.
        Assert.Equal(keep && expectSensitiveStored, Present(stored, H.SensitiveClearedOld));
        if (!(keep && expectSensitiveStored))
        {
            Assert.False(Leaked(stored, H.SensitiveClearedOld));
        }

        if (mode == StoreEditValuesMode.MaskSensitive && keep)
        {
            Assert.Equal("[masked]", cleared["Passport number"].Old);
        }

        if (mode == StoreEditValuesMode.None)
        {
            Assert.All(cleared.Values, row => Assert.True(row.Old is null && row.New is null, "A value was stored under None."));
        }
    }

    [Theory]
    [InlineData(StoreEditValuesMode.None)]
    [InlineData(StoreEditValuesMode.All)]
    public async Task Scan_CaptureSearchTermsOff_SearchTermNowhere(StoreEditValuesMode mode) // @M3-T14a SEC-11
    {
        using var h = new H(mode, captureSearchTerms: false);

        await RunEveryEventAsync(h);

        // The list and export rows exist and say a search was used; otherwise "absent" could mean "never recorded".
        foreach (AccessEventType type in new[] { AccessEventType.ListViewed, AccessEventType.Exported })
        {
            string detail = Assert.Single(h.DetailsOf(type));
            JsonElement filter = JsonDocument.Parse(detail).RootElement.GetProperty("filter");
            Assert.True(filter.GetProperty("searchTermUsed").GetBoolean(), $"{type} did not record that a search was used.");
            Assert.False(filter.TryGetProperty("searchTerm", out JsonElement term) && term.ValueKind != JsonValueKind.Null, $"{type} stored the search term.");
        }

        IReadOnlyList<string> stored = h.ScanTextColumns();
        AssertNoneLeaked(stored, H.SearchTerm, H.ExportSearchTerm);
        Assert.False(Leaked(stored, "sentinel-search"));
        Assert.False(Leaked(stored, "sentinel-export-search"));
    }

    [Fact]
    public async Task Scan_CaptureSearchTermsOn_SearchTermOnlyInDetail() // @M3-T14a
    {
        using var h = new H(StoreEditValuesMode.None, captureSearchTerms: true);

        await RunEveryEventAsync(h);

        foreach (string term in new[] { H.SearchTerm, H.ExportSearchTerm })
        {
            var hits = h.ScanWithOrigin().Where(v => v.Value.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.NotEmpty(hits);
            Assert.All(hits, hit => Assert.Equal(("formsAuditTrailAccess", "Detail"), (hit.Table, hit.Column)));
        }
    }

    [Fact]
    public async Task Scan_ExportFileName_StoredOnlyInDetail_ForCorrelation() // @M3-T14a design 7.3
    {
        // Forms generates the export file name, and the design stores it so a download matches its export. Pinned so a
        // change to where it lands (another column, a child table) is noticed.
        using var h = new H(StoreEditValuesMode.None);

        await RunEveryEventAsync(h);

        var hits = h.ScanWithOrigin().Where(v => v.Value.Contains(H.ExportFileName, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(hits);
        Assert.All(hits, hit => Assert.Equal(("formsAuditTrailAccess", "Detail"), (hit.Table, hit.Column)));
    }

    [Theory]
    [InlineData(StoreEditValuesMode.None, false)]
    [InlineData(StoreEditValuesMode.MaskSensitive, false)]
    [InlineData(StoreEditValuesMode.All, true)]
    public async Task Scan_AnyMode_NeverStoresMemberEmailUploadFileNameOrCreatedValue(StoreEditValuesMode mode, bool captureSearchTerms) // @M3-T14a P4 SEC-11
    {
        using var h = new H(mode, keepClearedValues: true, captureSearchTerms: captureSearchTerms);

        await RunEveryEventAsync(h);
        // The same file opened with its name percent-encoded in the request path, as a browser sends it.
        await h.RunUploadOpenAsync($"/media/forms/upload/form_{H.FormId}/{Guid.NewGuid()}/{Uri.EscapeDataString(H.UploadFileNameSpaced)}");

        IReadOnlyList<string> stored = h.ScanTextColumns();
        AssertNoneLeaked(stored, H.MemberIdentifier, H.UploadFileName, H.UploadFileNameSpaced, H.CreatedValue);
        Assert.False(Leaked(stored, "sentinel-member"));
        // Absence is only meaningful if the rows that would carry these values exist.
        Assert.Contains(AccessEventType.MemberSummaryViewed, h.EventTypes());
        foreach (AccessEventType type in new[] { AccessEventType.ListViewed, AccessEventType.Exported })
        {
            string detail = Assert.Single(h.DetailsOf(type));
            JsonElement filter = JsonDocument.Parse(detail).RootElement.GetProperty("filter");
            if (!captureSearchTerms)
            {
                Assert.True(filter.GetProperty("searchTermUsed").GetBoolean(), $"{type} did not record that a search was used.");
            }
        }

        foreach (string fragment in new[] { "Jane_Smith", "Jane Smith", "Jane%20Smith", "Smith_CV" })
        {
            Assert.False(Leaked(stored, fragment), $"A fragment of an uploaded file name was stored: {fragment}.");
        }

        Assert.Equal(2, h.DetailsOf(AccessEventType.FileViewed).Count);
    }

    [Theory]
    [InlineData(StoreEditValuesMode.None)]
    [InlineData(StoreEditValuesMode.MaskSensitive)]
    [InlineData(StoreEditValuesMode.All)]
    public async Task Scan_DetailViewed_StoresNoEntryValuesInAnyMode(StoreEditValuesMode mode) // @M3-T14a
    {
        // On its own, so the edit's allowed values cannot mask a value the detail view leaked.
        using var h = new H(mode, keepClearedValues: true);

        await h.RunDetailAsync();

        Assert.Equal([AccessEventType.DetailViewed], h.EventTypes());
        AssertNoneLeaked(h.ScanTextColumns(), AllValues);
    }

    [Fact]
    public async Task Scan_ExportAndCreate_AloneStoreNoEntryValues() // @M3-T14a
    {
        using var h = new H(StoreEditValuesMode.All, keepClearedValues: true, captureSearchTerms: true);

        await h.RunExportAsync(H.ExportSearchTerm, H.ExportFileName);
        await h.RunCreateAsync();

        AssertNoneLeaked(h.ScanTextColumns(), [.. AllValues, H.CreatedValue]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scan_Delete_ScrubsTheStoredEditValuesOfThatEntry(bool bulk) // @M3-T14a erasure
    {
        using var h = new H(StoreEditValuesMode.All, keepClearedValues: true);
        await h.RunEditAsync();
        IReadOnlyList<string> before = h.ScanTextColumns();
        // Precondition: the edit really stored values, otherwise "scrubbed" proves nothing.
        Assert.True(Present(before, H.NameNew) && Present(before, H.SensitiveOld) && Present(before, H.ClearedOld));

        if (bulk)
        {
            await h.RunBulkDeleteAsync(H.RecordId, Guid.NewGuid());
        }
        else
        {
            await h.RunDeleteAsync();
        }

        Assert.Contains(AccessEventType.Deleted, h.EventTypes());
        AssertNoneLeaked(h.ScanTextColumns(), AllValues);
        Assert.All(h.EntryChangeRows(), row =>
        {
            Assert.True(row.Scrubbed, "A stored change was not stamped as scrubbed.");
            Assert.True(row.Old is null && row.New is null);
        });
    }

    [Fact]
    public async Task Scan_PermanentFormDelete_ScrubsTheFormsStoredValues_NoSentinelRemains() // @M3-T14 @M3-T14a erasure R63
    {
        // After a permanent delete Forms holds none of the form's entries: UFRecords finds nothing and has nothing to probe.
        IFormsRecordReader reader = Substitute.For<IFormsRecordReader>();
        reader.FindExistingAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());
        reader.FindAnyExistingIdAsync(Arg.Any<CancellationToken>()).Returns((Guid?)null);
        using var h = new H(StoreEditValuesMode.All, keepClearedValues: true, recordReader: reader);
        await h.RunEditAsync();
        // Absence below is only meaningful if the values were stored to begin with.
        Assert.True(Present(h.ScanTextColumns(), H.NameOld) && Present(h.ScanTextColumns(), H.SensitiveNew));

        await h.RunPermanentFormDeleteAsync();

        Assert.Contains(AccessEventType.Deleted, h.EventTypes());
        Assert.All(h.EntryChangeRows(), row =>
        {
            Assert.True(row.Scrubbed, $"The stored change for '{row.Caption}' was not stamped as scrubbed.");
            Assert.Null(row.Old);
            Assert.Null(row.New);
        });
        AssertNoneLeaked(h.ScanTextColumns(), AllValues);
    }

    [Fact]
    public async Task Scan_ConfigChange_StateAndRowCarryNoEntryValues() // @M3-T14a SEC-07
    {
        using var h = new H(StoreEditValuesMode.None, accessLogGroupAlias: H.GroupAlias);

        await RunEveryEventAsync(h);
        await h.RunConfigChangeAsync();

        Assert.Contains(AccessEventType.ConfigChanged, h.EventTypes());
        var scanned = h.ScanWithOrigin();
        Assert.Contains(scanned, v => v.Table == "formsAuditTrailState");
        // The alias is an option value, recorded by design, so it proves the recorder's row and snapshot were scanned.
        var aliasHits = scanned.Where(v => v.Value.Contains(H.GroupAlias, StringComparison.Ordinal)).ToList();
        Assert.Contains(aliasHits, v => v.Table == "formsAuditTrailState");
        Assert.Contains(aliasHits, v => (v.Table, v.Column) == ("formsAuditTrailAccess", "Detail"));
        AssertNoneLeaked(scanned.Select(v => v.Value), [.. AllValues, H.SearchTerm, H.ExportSearchTerm, H.MemberIdentifier, H.UploadFileName, H.CreatedValue]);
    }

    [Fact]
    public async Task Scan_AccessLogApi_ListCsvAndDetailReturnNoEntryValuesBeyondTheRules() // @M3-T14a SEC-12
    {
        using var h = new H(StoreEditValuesMode.All, keepClearedValues: true);
        await RunEveryEventAsync(h);
        // Precondition: the values are in the database, so the API output below is what the masking let through.
        Assert.True(Present(h.ScanTextColumns(), H.SensitiveOld));

        string list = await h.ReadAccessLogListAsync();
        string csv = await h.ReadAccessLogCsvAsync();
        string detail = await h.ReadAccessLogEntryAsync(h.IdOfFirst(AccessEventType.Edited));

        Assert.Contains("Edited", list, StringComparison.Ordinal);
        Assert.Contains("Edited", csv, StringComparison.Ordinal);
        string[] never = [.. AllValues, H.SearchTerm, H.MemberIdentifier, H.UploadFileName, H.CreatedValue];
        AssertNoneLeaked([list], never);
        AssertNoneLeaked([csv], never);

        // The entry detail is the one place values are shown, and the viewer has no sensitive-data permission: plain
        // values appear, sensitive ones never (read-time masking, SEC-12).
        Assert.True(Present([detail], H.NameOld), "The detail did not return the plain change, so the check below proves nothing.");
        AssertNoneLeaked([detail], [.. SensitiveValues, H.SensitiveClearedOld]);

        // The reads above wrote their own audit rows (a view of the edit, the CSV export); none may carry search terms,
        // member emails or upload names. Entry values are legitimately in the edit row under All, so they are not asserted here.
        AssertNoneLeaked(h.ScanTextColumns(), H.SearchTerm, H.MemberIdentifier, H.UploadFileName, H.CreatedValue);
    }

    [Fact]
    public async Task Scan_AccessLogApi_PermittedViewerSeesSensitiveValueInDetail_ButCsvStillHasNoValues() // @M3-T14a SEC-12
    {
        using var h = new H(StoreEditValuesMode.All, keepClearedValues: true, viewerCanSeeSensitive: true);
        await RunEveryEventAsync(h);

        string detail = await h.ReadAccessLogEntryAsync(h.IdOfFirst(AccessEventType.Edited));
        string csv = await h.ReadAccessLogCsvAsync();

        Assert.True(Present([detail], H.SensitiveOld) && Present([detail], H.SensitiveNew), "A permitted viewer should see the sensitive values in the detail.");
        Assert.Contains("Edited", csv, StringComparison.Ordinal);
        AssertNoneLeaked([csv], AllValues);
    }
}
