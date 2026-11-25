using NSubstitute;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class RequestFormCacheTests
{
    private readonly IFormService _forms = Substitute.For<IFormService>();
    private readonly Guid _formId = Guid.NewGuid();

    [Fact]
    public void Get_TwiceInOneRequest_ReadsFormsOnce() // @PERF-EDIT
    {
        var form = new Form { Id = _formId, Name = "Contact" };
        _forms.Get(_formId).Returns(form);
        var cache = new RequestFormCache(_forms);

        Assert.Same(form, cache.Get(_formId));
        Assert.Same(form, cache.Get(_formId));
        _forms.Received(1).Get(_formId);
    }

    [Fact]
    public void Get_FormNotFound_IsNotRemembered() // @PERF-EDIT
    {
        _forms.Get(_formId).Returns((Form?)null);
        var cache = new RequestFormCache(_forms);

        Assert.Null(cache.Get(_formId));
        Assert.Null(cache.Get(_formId));
        Assert.Null(cache.GetLoaded(_formId));
        _forms.Received(2).Get(_formId);
    }

    [Fact]
    public void GetLoaded_NeverReadsForms() // @PERF-EDIT
    {
        var cache = new RequestFormCache(_forms);

        Assert.Null(cache.GetLoaded(_formId));
        _forms.DidNotReceive().Get(Arg.Any<Guid>());
    }

    [Fact]
    public void Get_FormsThrows_PropagatesAndRemembersNothing() // @PERF-EDIT E12: callers fail open themselves
    {
        _forms.Get(_formId).Returns(_ => throw new InvalidOperationException("db down"));
        var cache = new RequestFormCache(_forms);

        Assert.Throws<InvalidOperationException>(() => cache.Get(_formId));
        Assert.Null(cache.GetLoaded(_formId));
    }
}
