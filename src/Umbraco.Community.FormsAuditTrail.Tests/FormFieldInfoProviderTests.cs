using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Forms.Core.Models;
using Umbraco.Forms.Core.Services;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class FormFieldInfoProviderTests
{
    private readonly IFormService _forms = Substitute.For<IFormService>();

    [Fact]
    public void FieldInfo_FormNotLoaded_ReturnsEmpty() // @M3-T13
    {
        Guid formId = Guid.NewGuid();
        _forms.Get(formId).Returns((Form?)null);

        Assert.Empty(new FormFieldInfoProvider(new RequestFormCache(_forms), NullLogger<FormFieldInfoProvider>.Instance).GetFields(formId));
    }

    [Fact]
    public void FieldInfo_FormServiceThrows_ReturnsEmpty() // @M3-T13 fail-open E12
    {
        Guid formId = Guid.NewGuid();
        _forms.Get(formId).Returns(_ => throw new InvalidOperationException("db down"));

        Assert.Empty(new FormFieldInfoProvider(new RequestFormCache(_forms), NullLogger<FormFieldInfoProvider>.Instance).GetFields(formId));
    }

    [Fact]
    public void FieldInfo_SensitiveFlagFromS4Member() // @M3-T13 S4
    {
        Guid formId = Guid.NewGuid();
        var email = new Field { Id = Guid.NewGuid(), Caption = "Email", ContainsSensitiveData = true };
        var name = new Field { Id = Guid.NewGuid(), Caption = "Name", ContainsSensitiveData = false };
        var form = new Form
        {
            Id = formId,
            Pages = [new Page { FieldSets = [new FieldSet { Containers = [new FieldsetContainer { Fields = [email, name] }] }] }],
        };
        _forms.Get(formId).Returns(form);

        var fields = new FormFieldInfoProvider(new RequestFormCache(_forms), NullLogger<FormFieldInfoProvider>.Instance).GetFields(formId);

        Assert.Equal(new FieldInfo("Email", true), fields[email.Id]);
        Assert.Equal(new FieldInfo("Name", false), fields[name.Id]);
    }

    [Fact]
    public void LoadedFields_FormNotYetRead_ReturnsEmptyWithoutReadingTheDatabase() // @M3-T13b SQLite create hang
    {
        Guid formId = Guid.NewGuid();

        var fields = new FormFieldInfoProvider(new RequestFormCache(_forms), NullLogger<FormFieldInfoProvider>.Instance).GetLoadedFields(formId);

        Assert.Empty(fields);
        Assert.Empty(_forms.ReceivedCalls());
    }

    [Fact]
    public void LoadedFields_FormAlreadyRead_ReturnsItsFieldsWithoutAnotherRead() // @M3-T13b SQLite create hang
    {
        Guid formId = Guid.NewGuid();
        var email = new Field { Id = Guid.NewGuid(), Caption = "Email", ContainsSensitiveData = true };
        _forms.Get(formId).Returns(new Form
        {
            Id = formId,
            Pages = [new Page { FieldSets = [new FieldSet { Containers = [new FieldsetContainer { Fields = [email] }] }] }],
        });
        var cache = new RequestFormCache(_forms);
        cache.Get(formId);

        var fields = new FormFieldInfoProvider(cache, NullLogger<FormFieldInfoProvider>.Instance).GetLoadedFields(formId);

        Assert.Equal(new FieldInfo("Email", true), fields[email.Id]);
        _forms.Received(1).Get(formId);
    }
}
