using Umbraco.Community.FormsAuditTrail.Configuration;
using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Xunit;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class EntryEditDifferTests
{
    private static readonly Guid Name = Guid.NewGuid();
    private static readonly Guid Email = Guid.NewGuid();
    private static readonly Guid Tags = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, FieldInfo> Fields = new Dictionary<Guid, FieldInfo>
    {
        [Name] = new("Name", false),
        [Email] = new("Email", true),
        [Tags] = new("Tags", false),
    };

    private static IReadOnlyDictionary<Guid, IReadOnlyList<object?>> Values(params (Guid Id, object?[] Values)[] pairs) =>
        pairs.ToDictionary(p => p.Id, p => (IReadOnlyList<object?>)p.Values);

    private static IReadOnlyList<EntryFieldChange> Diff(
        IReadOnlyDictionary<Guid, IReadOnlyList<object?>> before,
        IReadOnlyDictionary<Guid, IReadOnlyList<object?>> after,
        StoreEditValuesMode mode = StoreEditValuesMode.MaskSensitive,
        bool keepCleared = false,
        IReadOnlyDictionary<Guid, FieldInfo>? fields = null) =>
        EntryEditDiffer.Diff(before, after, fields ?? Fields, mode, keepCleared);

    [Fact]
    public void Diff_ReturnsChangedFieldsOnly() // @M3-T13
    {
        var changes = Diff(
            Values((Name, ["a"]), (Tags, ["x"])),
            Values((Name, ["b"]), (Tags, ["x"])));

        EntryFieldChange change = Assert.Single(changes);
        Assert.Equal(Name, change.FieldId);
        Assert.Equal("Name", change.FieldCaption);
        Assert.Equal("a", change.OldValue);
        Assert.Equal("b", change.NewValue);
        Assert.False(change.Masked);
        Assert.False(change.Cleared);
    }

    [Fact]
    public void Diff_MaskSensitive_SensitiveFieldStoredAsMaskedBothSides() // @M3-T13
    {
        EntryFieldChange change = Assert.Single(Diff(Values((Email, ["a@x.test"])), Values((Email, ["b@x.test"]))));

        Assert.Equal("[masked]", change.OldValue);
        Assert.Equal("[masked]", change.NewValue);
        Assert.True(change.Masked);
    }

    [Fact]
    public void Diff_MaskSensitive_NormalFieldStoredInClear() // @M3-T13
    {
        EntryFieldChange change = Assert.Single(Diff(Values((Name, ["a"])), Values((Name, ["b"]))));

        Assert.Equal("a", change.OldValue);
        Assert.False(change.Masked);
    }

    [Fact]
    public void Diff_None_StoresIdsAndCaptionsWithNullValues() // @M3-T13
    {
        EntryFieldChange change = Assert.Single(Diff(
            Values((Name, ["a"])), Values((Name, ["b"])), StoreEditValuesMode.None));

        Assert.Equal(Name, change.FieldId);
        Assert.Equal("Name", change.FieldCaption);
        Assert.Null(change.OldValue);
        Assert.Null(change.NewValue);
        Assert.False(change.Masked);
    }

    [Fact]
    public void Diff_All_StoresSensitiveInClear_MaskedFalse() // @M3-T13
    {
        EntryFieldChange change = Assert.Single(Diff(
            Values((Email, ["a@x.test"])), Values((Email, ["b@x.test"])), StoreEditValuesMode.All));

        Assert.Equal("a@x.test", change.OldValue);
        Assert.Equal("b@x.test", change.NewValue);
        Assert.False(change.Masked);
    }

    [Fact]
    public void Diff_MultiValue_SerialisedAsJsonArray() // @M3-T13
    {
        EntryFieldChange change = Assert.Single(Diff(Values((Tags, ["a"])), Values((Tags, ["a", "b"]))));

        Assert.Equal("a", change.OldValue);
        Assert.Equal("[\"a\",\"b\"]", change.NewValue);
    }

    [Fact]
    public void FormatValues_NoneOneMany() // @M3-T13
    {
        Assert.Null(EntryEditDiffer.FormatValues([]));
        Assert.Equal("one", EntryEditDiffer.FormatValues(["one"]));
        Assert.Equal("[\"a\",\"b\"]", EntryEditDiffer.FormatValues(["a", "b"]));
    }

    [Fact]
    public void Diff_FieldMissingFromForm_UsesFieldIdAsCaption() // @M3-T13 Review Focus 4
    {
        Guid gone = Guid.NewGuid();

        EntryFieldChange change = Assert.Single(Diff(Values((gone, ["a"])), Values((gone, ["b"]))));

        Assert.Equal(gone.ToString(), change.FieldCaption);
    }

    [Fact]
    public void Diff_FieldMissingFromForm_ValuesMaskedUnderMaskSensitive() // @M3-T13
    {
        // Without a flag the field cannot be proven non-sensitive, so it fails closed.
        Guid gone = Guid.NewGuid();

        EntryFieldChange change = Assert.Single(Diff(Values((gone, ["a"])), Values((gone, ["b"]))));

        Assert.Equal("[masked]", change.OldValue);
        Assert.Equal("[masked]", change.NewValue);
        Assert.True(change.Masked);
    }

    [Fact]
    public void Diff_NoValueChanges_ReturnsEmpty() // @M3-T13
    {
        var same = Values((Name, ["a"]), (Tags, ["x", "y"]));

        Assert.Empty(Diff(same, Values((Name, ["a"]), (Tags, ["x", "y"]))));
    }

    [Fact]
    public void Diff_BlankedField_NoValuesStored_ClearedTrue() // @M3-T13 T10 SEC-03
    {
        EntryFieldChange change = Assert.Single(Diff(Values((Name, ["secret"])), Values((Name, []))));

        Assert.True(change.Cleared);
        Assert.Null(change.OldValue);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public void Diff_BlankedField_KeepClearedValues_OldValueKept_ClearedTrue() // @M3-T13 T10
    {
        EntryFieldChange change = Assert.Single(Diff(Values((Name, ["old"])), Values((Name, [])), keepCleared: true));

        Assert.True(change.Cleared);
        Assert.Equal("old", change.OldValue);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public void Diff_BlankedSensitiveField_KeepClearedValues_OldStaysMasked() // @M3-T13
    {
        EntryFieldChange change = Assert.Single(Diff(Values((Email, ["a@x.test"])), Values((Email, [])), keepCleared: true));

        Assert.Equal("[masked]", change.OldValue);
        Assert.Null(change.NewValue);
        Assert.True(change.Masked);
        Assert.True(change.Cleared);
    }

    [Fact]
    public void Diff_BlankedField_ModeNone_KeepClearedStillStoresNoValues() // @M3-T13
    {
        EntryFieldChange change = Assert.Single(Diff(
            Values((Name, ["old"])), Values((Name, [])), StoreEditValuesMode.None, keepCleared: true));

        Assert.True(change.Cleared);
        Assert.Null(change.OldValue);
    }

    [Fact]
    public void Diff_WhitespaceOnly_CountsAsCleared() // @M3-T13
    {
        EntryFieldChange change = Assert.Single(Diff(Values((Name, ["old"])), Values((Name, ["   "]))));

        Assert.True(change.Cleared);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public void Diff_FieldAbsentAfter_TreatedAsCleared() // @M3-T13
    {
        EntryFieldChange change = Assert.Single(Diff(Values((Name, ["old"])), Values()));

        Assert.True(change.Cleared);
    }

    [Fact]
    public void Diff_FieldFilledFromEmpty_NotCleared() // @M3-T13
    {
        EntryFieldChange change = Assert.Single(Diff(Values((Name, [])), Values((Name, ["new"]))));

        Assert.False(change.Cleared);
        Assert.Null(change.OldValue);
        Assert.Equal("new", change.NewValue);
    }

    [Fact]
    public void Diff_EmptyToWhitespace_IsNotAChange() // @M3-T13
    {
        Assert.Empty(Diff(Values((Name, [])), Values((Name, [" "]))));
    }

    [Theory]
    [InlineData("", " ")]
    [InlineData(null, null)]
    public void Diff_MultiValueAllBlank_IsCleared_NoValuesStored(string? a, string? b) // @M3-T13 SEC-03
    {
        EntryFieldChange change = Assert.Single(Diff(Values((Tags, ["x"])), Values((Tags, [a, b]))));

        Assert.True(change.Cleared);
        Assert.Null(change.OldValue);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public void Diff_MultiValue_NonAsciiStaysReadable_HtmlStillEscaped() // @M3-T13
    {
        EntryFieldChange change = Assert.Single(Diff(Values((Tags, ["x"])), Values((Tags, ["Zoë", "<b>"]))));

        Assert.Contains("Zoë", change.NewValue);
        Assert.DoesNotContain("<", change.NewValue);
    }

    [Fact]
    public void Diff_None_SensitiveFieldStoresNeitherValue_MaskedFalse() // @M3-T13
    {
        EntryFieldChange change = Assert.Single(Diff(
            Values((Email, ["a@x.test"])), Values((Email, ["b@x.test"])), StoreEditValuesMode.None));

        Assert.Null(change.OldValue);
        Assert.Null(change.NewValue);
        Assert.False(change.Masked);
    }
}
