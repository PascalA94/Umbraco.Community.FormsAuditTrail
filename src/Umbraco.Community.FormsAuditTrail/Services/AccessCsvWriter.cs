using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Umbraco.Community.FormsAuditTrail.Controllers.Dtos;

namespace Umbraco.Community.FormsAuditTrail.Services;

/// <summary>
/// Streams the access log as CSV. The size cap and the formula-injection guard are adapted from UserActivity
/// <c>Export/CsvWriter.cs</c> and <c>Export/ExportGate.cs</c>; the streaming is new, because UserActivity builds its file
/// in memory and the access log can hold millions of rows (SEC-10).
/// </summary>
internal static class AccessCsvWriter
{
    public const int DefaultMaxRows = 100_000;
    private const int FlushEvery = 1_000;
    private const string Header = "Id,Timestamp,Event,FormId,FormName,UserKey,UserName,RecordId,RecordCount,Detail";

    // UTF-8 with a BOM so Excel reads non-ASCII form and user names correctly, as UserActivity's export does.
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// Writes the header and one line per row as the row arrives, flushing every 1,000 rows, so memory stays flat however
    /// large the log is. At <paramref name="maxRows"/> it stops and appends a comment line; the one extra row pulled to
    /// find out whether there is more is never written. Returns the number of rows written.
    /// </summary>
    public static async Task<int> WriteAsync(
        Stream body, IAsyncEnumerable<AccessEntryListDto> rows, int maxRows = DefaultMaxRows, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(body, Utf8WithBom, bufferSize: 16 * 1024, leaveOpen: true) { NewLine = "\r\n" };
        await writer.WriteLineAsync(Header);

        int written = 0;
        await foreach (AccessEntryListDto row in rows.WithCancellation(ct))
        {
            if (written >= maxRows)
            {
                await writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"# truncated at {maxRows} rows; narrow the filter"));
                break;
            }

            await writer.WriteLineAsync(Line(row));
            written++;

            if (written % FlushEvery == 0)
            {
                await writer.FlushAsync(ct);
            }
        }

        await writer.FlushAsync(ct);
        return written;
    }

    private static string Line(AccessEntryListDto row) => string.Join(
        ',',
        row.Id.ToString(CultureInfo.InvariantCulture),
        row.Timestamp.ToString("O", CultureInfo.InvariantCulture),
        CsvFormatter.Escape(row.EventType),
        row.FormId.ToString(),
        CsvFormatter.Escape(row.FormName),
        row.UserKey.ToString(),
        CsvFormatter.Escape(row.UserName),
        row.RecordId?.ToString() ?? string.Empty,
        row.RecordCount.ToString(CultureInfo.InvariantCulture),
        // The stored Detail JSON is exported whole: it is allow-listed at write time (SEC-02), so any key added to it,
        // such as the searched user key, reaches the CSV without this writer changing.
        CsvFormatter.Escape(WithoutSearchTerm(row.Detail)));

    // A captured free-text search term can be an email address or a name. The stored row keeps it for the DPO who set
    // CaptureSearchTerms; a file that leaves the backoffice does not. Anything unparseable is dropped rather than risk it.
    private static string? WithoutSearchTerm(string? detail)
    {
        if (detail is null || !detail.Contains("searchTerm", StringComparison.OrdinalIgnoreCase))
        {
            return detail;
        }

        try
        {
            if (JsonNode.Parse(detail) is JsonObject root)
            {
                if (root["filter"] is JsonObject filter)
                {
                    filter.Remove("searchTerm");
                }

                return root.ToJsonString();
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            // JsonNode parses lazily: a duplicate key only throws (ArgumentException) when the object is touched, and a
            // node shape surprise throws InvalidOperationException. The headers are already sent, so one odd row must
            // not abort the download; it is blanked like any other unreadable detail.
        }

        return null;
    }
}
