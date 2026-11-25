using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class UploadAuditMiddlewareTests
{
    private static readonly Guid FormId = Guid.NewGuid();
    private static readonly UploadUser Admin = new(Guid.NewGuid(), "Admin");

    private readonly UploadPathResolver _resolver = new();
    private readonly IUploadUserResolver _users = Substitute.For<IUploadUserResolver>();
    private readonly IEntryAuditWriter _writer = Substitute.For<IEntryAuditWriter>();
    private readonly FormsAuditTrailOptions _options = new();
    private readonly IOptionsMonitor<FormsAuditTrailOptions> _monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
    private readonly List<AccessWrite> _writes = [];
    private readonly EntryAuditHealth _health = new(Substitute.For<IAuditStateStore>(), new FakeTimeProvider());

    public UploadAuditMiddlewareTests()
    {
        _monitor.CurrentValue.Returns(_options);
        _users.Resolve(Arg.Any<HttpContext>()).Returns(Admin);
        _writer.WriteAsync(Arg.Do<AccessWrite>(_writes.Add)).Returns(new AccessWriteResult(AccessWriteStatus.Written, 1, true));
    }

    private static string UploadUrl(string file = "Jane_Smith_CV.pdf") => $"/media/forms/upload/form_{FormId}/{Guid.NewGuid()}/{file}";

    private async Task<(DefaultHttpContext Context, bool NextCalled)> RunAsync(
        string path, int status, IUploadPathResolver? resolver = null, ILogger<UploadAuditMiddleware>? logger = null, bool nextThrows = false, bool startsResponse = false)
    {
        bool nextCalled = false;
        var response = new StartableResponseFeature();
        var middleware = new UploadAuditMiddleware(ctx =>
        {
            nextCalled = true;
            if (startsResponse)
            {
                // Forms set the status and began streaming the file before the failure.
                ctx.Response.StatusCode = status;
                response.Start();
            }

            if (nextThrows)
            {
                throw new InvalidOperationException("downstream");
            }

            ctx.Response.StatusCode = status;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(response);
        context.Request.Path = path;
        await middleware.InvokeAsync(context, resolver ?? _resolver, _users, _writer, _monitor, _health, logger ?? NullLogger<UploadAuditMiddleware>.Instance);
        return (context, nextCalled);
    }

    [Fact]
    public async Task Middleware_NonUploadPath_CallsNextOnly() // @M2-T12a
    {
        (_, bool nextCalled) = await RunAsync("/umbraco/forms/management/api/v1/export", 200);

        Assert.True(nextCalled);
        Assert.Empty(_writes);
        _users.DidNotReceiveWithAnyArgs().Resolve(default!);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(206)]
    [InlineData(304)]
    public async Task Middleware_ServedStatus_WritesFileViewedWithHashNotFileName(int status) // @M2-T12a T2
    {
        string url = UploadUrl();
        await RunAsync(url, status);

        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(AccessEventType.FileViewed, write.EventType);
        Assert.Equal(FormId, write.FormId);
        Assert.Null(write.RecordId);
        Assert.Equal(1, write.RecordCount);
        Assert.Equal(Admin.Key, write.UserKey);
        string hash = _resolver.HashPath(new PathString(url));
        Assert.Equal(hash, write.Detail!.FilePathHash);
        Assert.Equal(hash, write.DedupeFilterHash);
        Assert.Null(write.Detail.FormUnresolved);
        Assert.Null(write.Detail.UserUnresolved);

        // T2: the name of the file never reaches the write, in any property.
        string everything = write.Detail.ToJson() + write.DedupeFilterHash + write.FormName + write.UserName;
        Assert.DoesNotContain("Jane_Smith", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/media/", everything, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)] // T2: Forms refused the request
    [InlineData(404)]
    [InlineData(500)]
    public async Task Middleware_NotServed_WritesNothing(int status) // @M2-T12a T2
    {
        await RunAsync(UploadUrl(), status);

        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Middleware_Disabled_WritesNothing() // @M2-T12a
    {
        _options.EntryAudit.Enabled = false;

        (_, bool nextCalled) = await RunAsync(UploadUrl(), 200);

        Assert.True(nextCalled);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Middleware_SameFileTwiceInWindow_Deduplicated() // @M2-T12a
    {
        // The writer owns dedupe; this proves the middleware gives it a key that is equal for one file and distinct for two.
        string url = UploadUrl();
        await RunAsync(url, 206);
        await RunAsync(url, 206);
        await RunAsync(UploadUrl(), 206);

        Assert.Equal(3, _writes.Count);
        Assert.Equal(_writes[0].DedupeFilterHash, _writes[1].DedupeFilterHash);
        Assert.NotEqual(_writes[0].DedupeFilterHash, _writes[2].DedupeFilterHash);

        var dedupe = new EntryAuditDeduplicator(_monitor, new FakeTimeProvider());
        bool Should(AccessWrite w) => dedupe.ShouldWrite(w.EventType, w.UserKey, w.FormId, w.RecordId, w.DedupeFilterHash!);
        Assert.True(Should(_writes[0]));
        Assert.False(Should(_writes[1]));
        Assert.True(Should(_writes[2]));
    }

    [Fact]
    public async Task Middleware_WriterFailed_ResponseUnchanged() // @M2-T12a
    {
        _writer.WriteAsync(Arg.Any<AccessWrite>()).Throws(new InvalidOperationException("boom"));

        (DefaultHttpContext context, _) = await RunAsync(UploadUrl(), 200);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.False(context.Response.HasStarted);
    }

    [Fact]
    public async Task Middleware_UserResolverThrows_ResponseUnchangedAndNothingWritten() // @M2-T12a
    {
        _users.Resolve(Arg.Any<HttpContext>()).Throws(new InvalidOperationException("bad cookie"));

        (DefaultHttpContext context, _) = await RunAsync(UploadUrl(), 206);

        Assert.Equal(206, context.Response.StatusCode);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Middleware_ResolverThrowsOnPath_CallsNextAndWritesNothing() // @M2-T12a
    {
        IUploadPathResolver broken = Substitute.For<IUploadPathResolver>();
        broken.IsUploadRequest(Arg.Any<PathString>()).Throws(new InvalidOperationException("x"));

        (_, bool nextCalled) = await RunAsync(UploadUrl(), 200, broken);

        Assert.True(nextCalled);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Middleware_FormIdUnresolved_WritesFormUnresolved() // @M2-T12a
    {
        await RunAsync("/media/forms/upload/stray/a.pdf", 200);

        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(Guid.Empty, write.FormId);
        Assert.True(write.Detail!.FormUnresolved);
    }

    [Fact]
    public async Task Middleware_UserUnresolved_WritesUnknownNotSystem() // @M2-T12a SEC-L5
    {
        _users.Resolve(Arg.Any<HttpContext>()).Returns((UploadUser?)null);

        await RunAsync(UploadUrl(), 200);

        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(Guid.Empty, write.UserKey);
        Assert.Equal("Unknown", write.UserName);
        Assert.True(write.Detail!.UserUnresolved);
    }

    [Fact]
    public async Task Middleware_UserUnresolved_EveryOpenSkipsDedupe() // @M2-T12a fix1 item 4, @M2-FIX A2
    {
        _users.Resolve(Arg.Any<HttpContext>()).Returns((UploadUser?)null);
        string url = UploadUrl();

        await RunAsync(url, 200);
        await RunAsync(url, 200);

        // One shared Guid.Empty key would hide different unresolved openers behind the first, so each open bypasses
        // dedupe in the writer rather than being given a random key.
        Assert.Equal(2, _writes.Count);
        Assert.All(_writes, w => Assert.True(w.SkipDedupe));
        Assert.All(_writes, w => Assert.Equal(_resolver.HashPath(new PathString(url)), w.DedupeFilterHash));
    }

    [Fact]
    public async Task Middleware_UserResolved_DoesNotSkipDedupe() // @M2-FIX A2
    {
        await RunAsync(UploadUrl(), 200);

        Assert.False(Assert.Single(_writes).SkipDedupe);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(206)]
    public async Task Middleware_NextThrowsAfterTheFileStarted_WritesThenRethrowsTheSameException(int status) // @M2-FIX B9 (L3)
    {
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(UploadUrl(), status, nextThrows: true, startsResponse: true));

        Assert.Equal("downstream", thrown.Message);
        Assert.Equal(AccessEventType.FileViewed, Assert.Single(_writes).EventType);
    }

    [Theory]
    [InlineData(304)]
    [InlineData(500)]
    public async Task Middleware_NextThrowsAfterStartWithOtherStatus_WritesNothing(int status) // @M2-FIX B9
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(UploadUrl(), status, nextThrows: true, startsResponse: true));

        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Middleware_NextThrowsAfterStart_WriterThrows_OriginalExceptionStillPropagates() // @M2-FIX B9
    {
        _writer.WriteAsync(Arg.Any<AccessWrite>()).ThrowsAsync(new TimeoutException("writer"));

        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(UploadUrl(), 200, nextThrows: true, startsResponse: true));

        Assert.Equal("downstream", thrown.Message);
        Assert.Equal(1, _health.WriteFailuresSinceStart);
    }

    [Theory]
    [InlineData("/MEDIA/FORMS/UPLOAD/form_{0}/a/b.pdf")]
    [InlineData("/Media/Forms/UpLoad/form_{0}/a/b.pdf")]
    [InlineData("/media\\forms\\upload\\form_{0}\\a\\b.pdf")]
    [InlineData("/media/forms\\UPLOAD/form_{0}/a/b.pdf")]
    public async Task Middleware_UpperCaseOrBackslashUploadPath_PassesTheEarlyReject(string template) // @M2-FIX A7
    {
        IUploadPathResolver spy = Substitute.For<IUploadPathResolver>();

        await RunAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, FormId), 200, spy);

        spy.Received(1).IsUploadRequest(Arg.Any<PathString>());
    }

    [Theory]
    [InlineData("/media/forms/upload./form_{0}/g/file.pdf")]
    [InlineData("/media/forms/upload../form_{0}/g/file.pdf")]
    [InlineData("/media/forms/upload. /form_{0}/g/file.pdf")]
    [InlineData("/media/forms/upload /form_{0}/g/file.pdf")]
    [InlineData("/media/forms/UPLOAD./form_{0}/g/file.pdf")]
    [InlineData("/media/forms/upload.\\form_{0}\\g\\file.pdf")]
    [InlineData("/media/forms/upload/form_{0}./g/file.pdf")]
    [InlineData("/media/forms/upload/form_{0}/g./file.pdf")]
    [InlineData("/media/forms/upload/form_{0}/g/file.pdf.")]
    [InlineData("/media/forms/upload/form_{0}/g/file.pdf ")]
    [InlineData("/media/forms/upload/./form_{0}/g/file.pdf")]
    [InlineData("/media/forms/upload//form_{0}/g/../g/file.pdf")]
    public async Task Middleware_WindowsEquivalentPathSegments_WriteOneRowForTheForm(string template) // @M2-FIX path segment normalisation
    {
        (DefaultHttpContext context, bool nextCalled) = await RunAsync(
            string.Format(System.Globalization.CultureInfo.InvariantCulture, template, FormId), 200);

        Assert.True(nextCalled);
        Assert.Equal(200, context.Response.StatusCode);
        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(AccessEventType.FileViewed, write.EventType);
        Assert.Equal(FormId, write.FormId);
    }

    [Fact]
    public async Task Middleware_ParentSegmentBeforeSecondFormFolder_RowCarriesTheSecondForm() // @M2-FIX path segment normalisation
    {
        var other = Guid.NewGuid();

        await RunAsync($"/media/forms/upload/form_{FormId}/..\\form_{other}/g/file.pdf", 200);

        Assert.Equal(other, Assert.Single(_writes).FormId);
    }

    [Fact]
    public async Task Middleware_NormalisedPathWithUnresolvableForm_StillWritesRowFlaggedFormUnresolved() // @M2-FIX path segment normalisation
    {
        await RunAsync("/media/forms/upload./form_notaguid./g/file.pdf", 200);

        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(Guid.Empty, write.FormId);
        Assert.True(write.Detail!.FormUnresolved);
    }

    [Fact]
    public async Task Middleware_ExpiredSession_KeepsUserAndFlagsDetail() // @M2-T12a fix1 item 2
    {
        _users.Resolve(Arg.Any<HttpContext>()).Returns(Admin with { SessionExpired = true });

        await RunAsync(UploadUrl(), 200);

        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(Admin.Key, write.UserKey);
        Assert.True(write.Detail!.SessionExpired);
        Assert.Null(write.Detail.UserUnresolved);
        Assert.Contains("\"sessionExpired\":true", write.Detail.ToJson());
    }

    [Fact]
    public async Task Middleware_PathResolverThrows_RecordsHealthFailureAndLogsError() // @M2-T12a fix1 item 1
    {
        IUploadPathResolver broken = Substitute.For<IUploadPathResolver>();
        broken.IsUploadRequest(Arg.Any<PathString>()).Throws(new InvalidOperationException("x"));
        var logger = new RecordingLogger();

        await RunAsync(UploadUrl(), 200, broken, logger);

        Assert.Equal(1, _health.WriteFailuresSinceStart);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("FileViewed") && e.Message.Contains("InvalidOperationException"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Jane_Smith"));
    }

    [Fact]
    public async Task Middleware_UserResolverThrows_RecordsHealthFailure() // @M2-T12a fix1 item 1
    {
        _users.Resolve(Arg.Any<HttpContext>()).Throws(new InvalidOperationException("bad"));

        await RunAsync(UploadUrl(), 200);

        Assert.Equal(1, _health.WriteFailuresSinceStart);
    }

    [Fact]
    public async Task Middleware_LoggerThrowsOn304_StatusUnchanged() // @M2-T12a fix1 item 1
    {
        _users.Resolve(Arg.Any<HttpContext>()).Throws(new InvalidOperationException("bad"));
        var logger = new RecordingLogger { Throw = true };

        (DefaultHttpContext context, _) = await RunAsync(UploadUrl(), 304, logger: logger);

        Assert.Equal(304, context.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_NextThrows_NothingWrittenAndExceptionPropagates() // @M2-T12a fix1 item 6
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(UploadUrl(), 200, nextThrows: true));

        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Middleware_OptionsAccessorThrows_StillAuditedAndResponseUnchanged() // @M2-T12a fix1 item 6
    {
        _monitor.CurrentValue.Returns(_ => throw new InvalidOperationException("options"));

        (DefaultHttpContext context, _) = await RunAsync(UploadUrl(), 200);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Single(_writes);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/products/shoes")]
    [InlineData("/media/forms/forms.css")]
    public async Task Middleware_PathWithoutUpload_NeverReachesTheResolver(string path) // @M2-T12a fix1 item 5
    {
        IUploadPathResolver spy = Substitute.For<IUploadPathResolver>();

        (_, bool nextCalled) = await RunAsync(path, 200, spy);

        Assert.True(nextCalled);
        spy.DidNotReceiveWithAnyArgs().IsUploadRequest(default);
    }

    /// <summary>A response feature whose <c>HasStarted</c> a test can flip, as Kestrel does once headers are sent.</summary>
    private sealed class StartableResponseFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
    {
        private bool _started;

        public override bool HasStarted => _started;

        public void Start() => _started = true;
    }

    private sealed class RecordingLogger : ILogger<UploadAuditMiddleware>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public bool Throw { get; init; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (Throw)
            {
                throw new InvalidOperationException("logger down");
            }

            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task Middleware_DoubleSlashUpload_IsAudited() // @M2-T12a S11
    {
        await RunAsync("//media/forms/upload/form_" + FormId + "/a/b.pdf", 200);

        Assert.Single(_writes);
    }
}
