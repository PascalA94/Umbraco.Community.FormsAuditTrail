using System.Runtime.CompilerServices;
using System.Text;
using Umbraco.Community.FormsAuditTrail.Controllers.Dtos;
using Umbraco.Community.FormsAuditTrail.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class AccessCsvWriterTests
{
    private const string Header = "Id,Timestamp,Event,FormId,FormName,UserKey,UserName,RecordId,RecordCount,Detail";

    private static AccessEntryListDto Row(long id) => new()
    {
        Id = id,
        Timestamp = new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc),
        EventType = "DetailViewed",
        FormId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        FormName = "Form",
        UserKey = Guid.Parse("99999999-9999-9999-9999-999999999999"),
        UserName = "Editor",
        RecordCount = 1,
    };

    private static async IAsyncEnumerable<AccessEntryListDto> Rows(int count, Action<int>? onPull = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            onPull?.Invoke(i);
            await Task.Yield();
            yield return Row(i + 1);
        }
    }

    private static string Text(MemoryStream stream) => new StreamReader(new MemoryStream(stream.ToArray()), Encoding.UTF8, true).ReadToEnd();

    [Fact]
    public async Task Writer_WritesHeaderAndOneLinePerRow() // @M4-T16
    {
        var body = new MemoryStream();

        int written = await AccessCsvWriter.WriteAsync(body, Rows(3));

        string[] lines = Text(body).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, written);
        Assert.Equal(4, lines.Length);
        Assert.Equal(Header, lines[0]);
        Assert.StartsWith("1,2026-11-20T12:00:00.0000000Z,DetailViewed,", lines[1]);
    }

    [Fact]
    public async Task Writer_NoRows_WritesHeaderOnlyAndNoTruncationLine() // @M4-T16
    {
        var body = new MemoryStream();

        Assert.Equal(0, await AccessCsvWriter.WriteAsync(body, Rows(0)));

        Assert.Equal(Header + "\r\n", Text(body));
    }

    [Fact]
    public async Task Writer_ExactlyAtCap_DoesNotTruncate() // @M4-T16
    {
        var body = new MemoryStream();

        Assert.Equal(1000, await AccessCsvWriter.WriteAsync(body, Rows(1000), maxRows: 1000));

        Assert.DoesNotContain("# truncated", Text(body));
    }

    [Fact]
    public async Task Writer_StopsAtCap_AppendsTruncationLine() // T16
    {
        var body = new MemoryStream();

        int written = await AccessCsvWriter.WriteAsync(body, Rows(1500), maxRows: 1000);

        string[] lines = Text(body).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1000, written);
        Assert.Equal(1 + 1000 + 1, lines.Length);
        Assert.Equal("# truncated at 1000 rows; narrow the filter", lines[^1]);
        Assert.DoesNotContain(lines, l => l.StartsWith("1001,", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Writer_DefaultCapIs100000_AndPullsOnlyOneRowPastIt() // SEC-10
    {
        var body = new CountingStream();
        int pulled = 0;

        int written = await AccessCsvWriter.WriteAsync(body, Rows(100_050, _ => pulled++));

        Assert.Equal(100_000, written);
        // The extra pull is how the writer knows the cap was hit; it must not drain the rest of the table.
        Assert.Equal(100_001, pulled);
    }

    [Fact]
    public async Task Writer_DropsSearchTermFromDetail_ButKeepsTheRest() // SEC-02
    {
        AccessEntryListDto row = Row(1);
        row.Detail = "{\"v\":1,\"filter\":{\"searchTerm\":\"jane@example.test\",\"searchTermUsed\":true},\"page\":2}";
        var body = new MemoryStream();

        await AccessCsvWriter.WriteAsync(body, Rows1(row));

        string text = Text(body);
        Assert.DoesNotContain("jane@example.test", text);
        Assert.Contains("page", text);
        Assert.Contains("searchTermUsed", text);
    }

    [Fact]
    public async Task Writer_UnparseableDetailMentioningSearchTerm_IsBlankedNotLeaked() // SEC-02
    {
        AccessEntryListDto row = Row(1);
        row.Detail = "not json searchTerm jane@example.test";
        var body = new MemoryStream();

        await AccessCsvWriter.WriteAsync(body, Rows1(row));

        Assert.DoesNotContain("jane@example.test", Text(body));
    }

    [Theory]
    [InlineData("{\"filter\":{\"searchTerm\":\"jane@example.test\"},\"filter\":{}}")]
    [InlineData("{\"filter\":{\"searchTerm\":\"jane@example.test\",\"searchTerm\":\"x\"}}")]
    public async Task Writer_DetailThatParsesButCannotBeRebuilt_IsBlankedAndTheExportCarriesOn(string detail) // @M4-T16b merge-fix 6
    {
        // Duplicate keys parse but throw when the object is materialised (ArgumentException, not JsonException); one
        // odd row must not abort a streamed download whose headers are already sent.
        AccessEntryListDto odd = Row(1);
        odd.Detail = detail;
        var body = new MemoryStream();

        await AccessCsvWriter.WriteAsync(body, RowsOf(odd, Row(2)));

        string text = Text(body);
        Assert.DoesNotContain("jane@example.test", text);
        Assert.Equal(3, text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Writer_EscapesTextCells() // T18
    {
        AccessEntryListDto row = Row(1);
        row.FormName = "=HYPERLINK(1)";
        row.UserName = "a,\"b\"";
        row.Detail = "{\"fileName\":\"=cmd|' /C calc'!A0.csv\"}";
        var body = new MemoryStream();

        await AccessCsvWriter.WriteAsync(body, Rows1(row));

        string line = Text(body).Split("\r\n")[1];
        Assert.Contains(",'=HYPERLINK(1),", line);
        Assert.Contains(",\"a,\"\"b\"\"\",", line);
        Assert.Contains("\"{\"\"fileName\"\":\"\"=cmd|' /C calc'!A0.csv\"\"}\"", line);
    }

    [Fact]
    public async Task Writer_Streams_PullsRowsOnlyAsWritten() // P15b
    {
        await AssertStreams(rowCount: 5_000);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public async Task Writer_500kRows_StreamsWithoutBuffering_OnSlow() // T16 at full size, excluded from default CI
    {
        await AssertStreams(rowCount: 500_000);
    }

    private static async Task AssertStreams(int rowCount)
    {
        var body = new CountingStream();
        var receivedAtPull = new long[rowCount];

        int written = await AccessCsvWriter.WriteAsync(body, Rows(rowCount, i => receivedAtPull[i] = body.Received), maxRows: int.MaxValue);

        Assert.Equal(rowCount, written);
        long bytesPerRow = body.Received / rowCount;
        long worstBacklogRows = 0;
        for (int i = 0; i < rowCount; i++)
        {
            worstBacklogRows = Math.Max(worstBacklogRows, i - receivedAtPull[i] / bytesPerRow);
        }

        // The writer flushes every 1,000 rows, so it may hold back about that many (plus the encoder buffer), never the lot.
        Assert.True(worstBacklogRows <= 1_100, $"The writer held back about {worstBacklogRows} rows before the stream saw them.");
        Assert.True(receivedAtPull[rowCount - 1] > 0, "Nothing reached the stream before the last row was pulled.");
    }

    private static async IAsyncEnumerable<AccessEntryListDto> Rows1(AccessEntryListDto row)
    {
        await Task.Yield();
        yield return row;
    }

    private static async IAsyncEnumerable<AccessEntryListDto> RowsOf(params AccessEntryListDto[] rows)
    {
        foreach (AccessEntryListDto row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    private sealed class CountingStream : Stream
    {
        public long Received { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => Received; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Received += count;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Received += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }
}
