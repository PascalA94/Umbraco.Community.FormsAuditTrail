using Umbraco.Community.FormsAuditTrail.EntryAudit;
using Umbraco.Forms.Core.Persistence.Dtos;
using Xunit;
using Record = Umbraco.Forms.Core.Persistence.Dtos.Record;

namespace Umbraco.Community.FormsAuditTrail.Tests;

public class RecordValueSnapshotTests
{
    private static RecordField Field(Guid fieldId, params object[] values) =>
        new() { FieldId = fieldId, Values = [.. values] };

    private static Record RecordWith(params RecordField[] fields)
    {
        var record = new Record();
        foreach (RecordField f in fields)
        {
            record.RecordFields[Guid.NewGuid()] = f; // S2: keyed by the record-field key, not the form field id
        }

        return record;
    }

    [Fact]
    public void Snapshot_CopiesValues_LaterMutationDoesNotLeak() // @M3-T13 SEC-04
    {
        Guid id = Guid.NewGuid();
        RecordField field = Field(id, "before");
        Record record = RecordWith(field);

        var snapshot = RecordValueSnapshot.From(record);
        field.Values.Clear();
        field.Values.Add("after");

        Assert.Equal(["before"], snapshot[id]);
    }

    [Fact]
    public void Snapshot_MultiValueAndUpload_Shapes() // @M3-T13 S2 value shapes
    {
        Guid multi = Guid.NewGuid(), upload = Guid.NewGuid(), blank = Guid.NewGuid();
        Record record = RecordWith(
            Field(multi, "a", "b"),
            Field(upload, "/media/forms/cv.pdf"),
            Field(blank));

        var snapshot = RecordValueSnapshot.From(record);

        Assert.Equal(["a", "b"], snapshot[multi]);
        Assert.Equal(["/media/forms/cv.pdf"], snapshot[upload]);
        // A blanked field keeps its entry with an empty list (S2), so a diff sees it as cleared rather than absent.
        Assert.True(snapshot.ContainsKey(blank));
        Assert.Empty(snapshot[blank]);
    }

    private sealed class Poco
    {
        public string Name { get; set; } = "Zoë";
    }

    [Fact]
    public void Snapshot_NonPrimitiveWithoutToString_FallsBackToJson_NotTheTypeName() // @M3-T13 R44
    {
        Guid id = Guid.NewGuid();
        var snapshot = RecordValueSnapshot.From(RecordWith(Field(id, new Poco())));

        string text = Assert.IsType<string>(Assert.Single(snapshot[id]));
        Assert.Contains("Zoë", text);
        Assert.DoesNotContain(nameof(Poco), text);
    }
}
