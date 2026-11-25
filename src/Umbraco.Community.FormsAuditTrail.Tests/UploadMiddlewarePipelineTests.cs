using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Umbraco.Cms.Web.Common.ApplicationBuilder;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

/// <summary>
/// P9: the real middleware in the order S11 recorded, with a stand-in for Forms' protection middleware (403 unless
/// <c>X-Test-Allowed</c>) and a terminal standing in for the media file provider. It pins that our middleware sits
/// outside Forms', so a Forms 403 is observed and skipped.
/// </summary>
public sealed class UploadMiddlewarePipelineTests : IAsyncDisposable
{
    private static readonly Guid FormId = Guid.NewGuid();
    private static readonly string FilePath = $"/media/forms/upload/form_{FormId}/{Guid.NewGuid()}/Jane_Smith_CV.pdf";

    private readonly List<AccessWrite> _writes = [];
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero));
    private IHost? _host;

    private async Task<HttpClient> StartAsync()
    {
        var options = new FormsAuditTrailOptions();
        IOptionsMonitor<FormsAuditTrailOptions> monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        monitor.CurrentValue.Returns(options);
        var dedupe = new EntryAuditDeduplicator(monitor, _time);

        // Dedupes exactly as the real writer does (same call, same key), so a range burst shows as one row.
        IEntryAuditWriter writer = Substitute.For<IEntryAuditWriter>();
        writer.WriteAsync(Arg.Any<AccessWrite>()).Returns(call =>
        {
            AccessWrite w = call.Arg<AccessWrite>();
            if (!dedupe.ShouldWrite(w.EventType, w.UserKey, w.FormId, w.RecordId, w.DedupeFilterHash ?? string.Empty))
            {
                return new AccessWriteResult(AccessWriteStatus.Deduplicated, null, false);
            }

            _writes.Add(w);
            return new AccessWriteResult(AccessWriteStatus.Written, _writes.Count, true);
        });

        IUploadUserResolver users = Substitute.For<IUploadUserResolver>();
        users.Resolve(Arg.Any<HttpContext>()).Returns(new UploadUser(Guid.NewGuid(), "Admin"));

        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddSingleton<IUploadPathResolver, UploadPathResolver>();
                    services.AddSingleton(users);
                    services.AddSingleton(writer);
                    services.AddSingleton(monitor);
                    services.AddSingleton(new EntryAuditHealth(Substitute.For<IAuditStateStore>(), _time));
                })
                .Configure(app =>
                {
                    UploadAuditPipeline.Use(app);
                    // Stand-in for ProtectFormUploadRequestsMiddleware: denies without calling next, like the real one.
                    app.Use(async (ctx, next) =>
                    {
                        if (!ctx.Request.Headers.ContainsKey("X-Test-Allowed"))
                        {
                            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return;
                        }

                        await next();
                    });
                    // Stand-in for the media file provider: status from the query, a body and a header to compare.
                    app.Run(async ctx =>
                    {
                        ctx.Response.StatusCode = int.Parse(ctx.Request.Query["status"].ToString());
                        ctx.Response.Headers["X-Probe"] = "kept";
                        await ctx.Response.WriteAsync("file-bytes");
                    });
                }))
            .StartAsync();
        return _host.GetTestClient();
    }

    private static HttpRequestMessage Get(string status, bool allowed = true, string? range = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{FilePath}?status={status}");
        if (allowed)
        {
            request.Headers.Add("X-Test-Allowed", "1");
        }

        if (range is not null)
        {
            request.Headers.Add("Range", range);
        }

        return request;
    }

    [Theory]
    [InlineData("200")]
    [InlineData("304")]
    public async Task Pipeline_ServedStatus_WritesFileViewed(string status) // @M2-T12a
    {
        HttpClient client = await StartAsync();

        await client.SendAsync(Get(status));

        AccessWrite write = Assert.Single(_writes);
        Assert.Equal(AccessEventType.FileViewed, write.EventType);
        Assert.Equal(FormId, write.FormId);
        Assert.DoesNotContain("Jane_Smith", write.Detail!.ToJson());
    }

    [Fact]
    public async Task Pipeline_Upload206_TwoRangeRequests_OneDedupedRow() // @M2-T12a
    {
        HttpClient client = await StartAsync();

        await client.SendAsync(Get("206", range: "bytes=0-9"));
        await client.SendAsync(Get("206", range: "bytes=10-19"));

        Assert.Single(_writes);
    }

    [Fact]
    public async Task Pipeline_FormsReturns403_WritesNothing() // @M2-T12a T2
    {
        HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.SendAsync(Get("200", allowed: false));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Pipeline_Upload404_WritesNothing() // @M2-T12a
    {
        HttpClient client = await StartAsync();

        await client.SendAsync(Get("404"));

        Assert.Empty(_writes);
    }

    [Fact]
    public async Task Pipeline_ResponseBodyAndHeadersUnchanged() // @M2-T12a
    {
        HttpClient client = await StartAsync();

        HttpResponseMessage response = await client.SendAsync(Get("200"));

        Assert.Equal("file-bytes", await response.Content.ReadAsStringAsync());
        Assert.Equal("kept", response.Headers.GetValues("X-Probe").Single());
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Pipeline_Filter_IsInsertedImmediatelyBeforeFormsFilterAtPrePipeline() // @M2-T12a P9
    {
        var options = new UmbracoPipelineOptions();
        options.AddFilter(new UmbracoPipelineFilter("Backoffice"));
        options.AddFilter(new UmbracoPipelineFilter(UploadAuditPipeline.FormsFilterName));
        options.AddFilter(new UmbracoPipelineFilter("Other"));

        UploadAuditPipeline.Insert(options);
        UploadAuditPipeline.Insert(options); // idempotent

        Assert.Equal(["Backoffice", UploadAuditPipeline.FilterName, UploadAuditPipeline.FormsFilterName, "Other"], options.PipelineFilters.Select(f => f.Name));
        var ours = Assert.IsType<UmbracoPipelineFilter>(options.PipelineFilters[1]);
        Assert.NotNull(ours.PrePipeline);
        Assert.Null(ours.PreRouting);
    }

    [Fact]
    public void Pipeline_FormsFilterMissing_FilterGoesFirst() // @M2-T12a
    {
        var options = new UmbracoPipelineOptions();
        options.AddFilter(new UmbracoPipelineFilter("Backoffice"));

        UploadAuditPipeline.Insert(options);

        Assert.Equal(UploadAuditPipeline.FilterName, options.PipelineFilters[0].Name);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
