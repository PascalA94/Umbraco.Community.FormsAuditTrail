using Microsoft.AspNetCore.Http;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class UploadPathResolverTests
{
    private static readonly Guid FormId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly UploadPathResolver _resolver = new();

    private static string FilePath(string root = "/media/forms/upload") => $"{root}/form_{FormId}/{Guid.NewGuid()}/Jane_Smith_CV.pdf";

    [Fact]
    public void Resolver_HashIsStableAcrossCaseAndSlashes() // @M2-T12a
    {
        var a = new PathString("/media/forms/upload/form_x/abc/File.PDF");
        var b = new PathString("//MEDIA//forms/Upload/form_x//abc/file.pdf");

        Assert.Equal(_resolver.HashPath(a), _resolver.HashPath(b));
        Assert.Matches("^[0-9a-f]{64}$", _resolver.HashPath(a));
        Assert.NotEqual(_resolver.HashPath(a), _resolver.HashPath(new PathString("/media/forms/upload/form_x/abc/other.pdf")));
    }

    [Fact]
    public void Resolver_HashDoesNotContainTheFileName() // @M2-T12a T2
    {
        Assert.DoesNotContain("Jane", _resolver.HashPath(new PathString("/media/forms/upload/form_x/abc/Jane_Smith_CV.pdf")));
    }

    [Fact]
    public void Resolver_UploadRootIsFormsLiteral() // @M2-T12a
    {
        Assert.Equal(["media", "forms", "upload"], UploadPathResolver.UploadRoot);
        Assert.True(_resolver.IsUploadRequest(new PathString(FilePath())));
        Assert.False(_resolver.IsUploadRequest(new PathString("/media/other/upload/form_x/a.pdf")));
        Assert.False(_resolver.IsUploadRequest(new PathString("/media/forms/uploads/form_x/a.pdf")));
        Assert.False(_resolver.IsUploadRequest(new PathString("/umbraco/forms/management/api/v1/export")));
        Assert.False(_resolver.IsUploadRequest(new PathString("/media/forms/upload")));
        Assert.False(_resolver.IsUploadRequest(PathString.Empty));
    }

    [Theory]
    [InlineData("//media/forms/upload/form_x/a.pdf")]
    [InlineData("/media//forms/upload/form_x/a.pdf")]
    [InlineData("/media/forms//upload/form_x/a.pdf")]
    [InlineData("/MEDIA/Forms/UPLOAD/form_x/a.pdf")]
    public void Resolver_DoubleSlashAndCaseMatchLikeForms(string path) // @M2-T12a
    {
        Assert.True(_resolver.IsUploadRequest(new PathString(path)));
    }

    [Theory]
    [InlineData("/media/forms\\upload\\form_x\\a.pdf")]
    [InlineData("/\\media\\forms\\upload\\form_x\\a.pdf")]
    [InlineData("/media\\forms/upload\\form_x/a.pdf")]
    public void Resolver_BackslashSeparators_MatchAndHashLikeSlashes(string path) // @M2-T12a fix1 item 3
    {
        var plain = new PathString("/media/forms/upload/form_x/a.pdf");

        Assert.True(_resolver.IsUploadRequest(new PathString(path)));
        Assert.Equal(_resolver.HashPath(plain), _resolver.HashPath(new PathString(path)));
    }

    [Fact]
    public void Resolver_BackslashSeparators_ResolveFormId() // @M2-T12a fix1 item 3
    {
        Assert.Equal(FormId, _resolver.ResolveFormId(new PathString($"/media/forms\\upload\\form_{FormId}\\x.pdf")));
    }

    [Fact]
    public void Resolver_FormIdFromFirstLowerCaseFormSegment() // @M2-T12a
    {
        Assert.Equal(FormId, _resolver.ResolveFormId(new PathString(FilePath())));
        // Case-sensitive: Forms answers 403 for FORM_, so no form is resolved.
        Assert.Null(_resolver.ResolveFormId(new PathString($"/media/forms/upload/FORM_{FormId}/a/b.pdf")));
        Assert.Null(_resolver.ResolveFormId(new PathString("/media/forms/upload/form_notaguid/a/b.pdf")));
        Assert.Null(_resolver.ResolveFormId(new PathString("/media/forms/upload/a/b.pdf")));
        // The first form_ segment decides, even when a later one would parse.
        Assert.Null(_resolver.ResolveFormId(new PathString($"/media/forms/upload/form_bad/form_{FormId}/b.pdf")));
    }

    [Fact]
    public void Matcher_MatchesRecordUploadValue() // @M2-T12a
    {
        string value = FilePath();
        string hash = _resolver.HashPath(new PathString(value));

        Assert.True(FileViewedMatcher.Matches(hash, ["not a path", "", value], _resolver));
        Assert.True(FileViewedMatcher.Matches(hash, [value.ToUpperInvariant() + "?download=1"], _resolver));
        Assert.False(FileViewedMatcher.Matches(hash, [FilePath()], _resolver));
        Assert.False(FileViewedMatcher.Matches(hash, [], _resolver));
    }
}
