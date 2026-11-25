using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Community.FormsAuditTrail.Models;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class EntryAuditDeduplicatorTests
{
    private static readonly Guid User = Guid.NewGuid();
    private static readonly Guid Form = Guid.NewGuid();

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.Zero));

    private EntryAuditDeduplicator Create(int seconds = 60)
    {
        IOptionsMonitor<FormsAuditTrailOptions> monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        monitor.CurrentValue.Returns(new FormsAuditTrailOptions { EntryAudit = new EntryAuditOptions { DeduplicateSeconds = seconds } });
        return new EntryAuditDeduplicator(monitor, _time);
    }

    [Fact]
    public void Dedupe_SameKeyInsideWindow_Skipped()
    {
        using EntryAuditDeduplicator dedupe = Create();

        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "h"));
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.False(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "h"));
    }

    [Fact]
    public void Dedupe_DifferentPageOrFilter_Written()
    {
        using EntryAuditDeduplicator dedupe = Create();

        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "page1"));
        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "page2"));
        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, Guid.NewGuid(), Form, null, "page1"));
        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Guid.NewGuid(), null, "page1"));
        Assert.True(dedupe.ShouldWrite(AccessEventType.DetailViewed, User, Form, Guid.NewGuid(), "page1"));
    }

    [Fact]
    public void Dedupe_DifferentRecord_Written()
    {
        using EntryAuditDeduplicator dedupe = Create();

        Assert.True(dedupe.ShouldWrite(AccessEventType.DetailViewed, User, Form, Guid.NewGuid(), ""));
        Assert.True(dedupe.ShouldWrite(AccessEventType.DetailViewed, User, Form, Guid.NewGuid(), ""));
    }

    [Theory]
    [InlineData(AccessEventType.Edited)]
    [InlineData(AccessEventType.Deleted)]
    [InlineData(AccessEventType.Exported)]
    [InlineData(AccessEventType.ExportDownloaded)]
    [InlineData(AccessEventType.AuditExported)]
    public void Dedupe_NeverAppliesToEditedDeletedExportedOrDownloaded(AccessEventType type)
    {
        using EntryAuditDeduplicator dedupe = Create();

        Assert.True(dedupe.ShouldWrite(type, User, Form, null, "h"));
        Assert.True(dedupe.ShouldWrite(type, User, Form, null, "h"));
    }

    [Fact]
    public void Dedupe_ZeroSeconds_AlwaysWrites()
    {
        using EntryAuditDeduplicator dedupe = Create(seconds: 0);

        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "h"));
        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "h"));
    }

    [Theory]
    [InlineData(AccessEventType.ListViewed)]
    [InlineData(AccessEventType.DetailViewed)]
    [InlineData(AccessEventType.AuditViewed)]
    [InlineData(AccessEventType.AuditSearched)]
    [InlineData(AccessEventType.FileViewed)]
    [InlineData(AccessEventType.MemberSummaryViewed)]
    public void Dedupe_AppliesToAuditSearchedFileViewedAndMemberSummary(AccessEventType type)
    {
        using EntryAuditDeduplicator dedupe = Create();

        Assert.True(dedupe.ShouldWrite(type, User, Form, null, "h"));
        Assert.False(dedupe.ShouldWrite(type, User, Form, null, "h"));
    }

    [Theory]
    [InlineData(AccessEventType.WorkflowRetried)]
    [InlineData(AccessEventType.Created)]
    [InlineData(AccessEventType.ConfigChanged)]
    [InlineData(AccessEventType.AuditGap)]
    public void Dedupe_NeverAppliesToWorkflowRetriedCreatedConfigChangedOrGap(AccessEventType type)
    {
        using EntryAuditDeduplicator dedupe = Create();

        Assert.True(dedupe.ShouldWrite(type, User, Form, null, "h"));
        Assert.True(dedupe.ShouldWrite(type, User, Form, null, "h"));
    }

    [Fact]
    public void Dedupe_NegativeSeconds_AlwaysWrites()
    {
        using EntryAuditDeduplicator dedupe = Create(seconds: -5);

        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "h"));
        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "h"));
    }

    [Fact]
    public void Dedupe_CacheFull_WritesAndDoesNotThrow() // SEC-L6
    {
        using EntryAuditDeduplicator dedupe = Create(seconds: 3600);
        for (int i = 0; i < EntryAuditDeduplicator.MaxEntries; i++)
        {
            Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "k" + i));
        }

        // The cache is at its limit, so this key is not stored: it writes every time instead of being skipped.
        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "overflow"));
        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "overflow"));
    }

    [Fact]
    public void Dedupe_AfterDeduplicateSecondsElapse_WritesAgain() // P15a
    {
        using EntryAuditDeduplicator dedupe = Create();

        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "h"));
        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "h"));
        Assert.False(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "h"));
    }

    [Fact]
    public void Dedupe_WindowFollowsOptionChange()
    {
        IOptionsMonitor<FormsAuditTrailOptions> monitor = Substitute.For<IOptionsMonitor<FormsAuditTrailOptions>>();
        var options = new FormsAuditTrailOptions { EntryAudit = new EntryAuditOptions { DeduplicateSeconds = 60 } };
        monitor.CurrentValue.Returns(options);
        using var dedupe = new EntryAuditDeduplicator(monitor, _time);
        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "h"));

        options.EntryAudit.DeduplicateSeconds = 0;

        Assert.True(dedupe.ShouldWrite(AccessEventType.ListViewed, User, Form, null, "h"));
    }
}
