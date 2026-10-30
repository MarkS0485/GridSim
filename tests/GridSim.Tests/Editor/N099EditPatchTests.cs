using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using Xunit;
using static GridSim.Tests.Editor.CaseSessionTests;
using static GridSim.Tests.Editor.EditorTestSupport;

namespace GridSim.Tests.Editor;

/// <summary>
/// N099 (1.95.240): SetField hands the transaction the text it already parsed instead of parsing it twice, and builds
/// the reference index only for key and reference fields. The new patch path must give the same document and the same
/// undo step as the old one, and must refuse a text that is no longer current.
/// </summary>
public class N099EditPatchTests
{
    [Fact]
    public void A_patch_with_the_applied_text_matches_a_plain_patch_and_undoes_exactly()
    {
        CaseSession a = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        CaseSession b = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        CaseDocument da = a.Doc("generation.json"), db = b.Doc("generation.json");
        byte[] original = da.Text.ToArray();
        JsonSourceNode value = da.Text.Resolve("/0")!.FindMember("capacityMva", da.KeyComparer)!.Value;
        IReadOnlyList<JsonTextEdit> edits = JsonTextOps.ReplaceValue(da.Text, value, "123.5");

        using (CaseTransaction tx = a.Begin("plain")) { tx.Patch(da, edits); tx.Commit(); }
        JsonSourceText before = db.Text;
        using (CaseTransaction tx = b.Begin("applied")) { tx.Patch(db, before, before.Apply(edits)); tx.Commit(); }

        Assert.Equal(da.Text.ToArray(), db.Text.ToArray());
        b.Undo();
        Assert.Equal(original, db.Text.ToArray());
        b.Redo();
        Assert.Equal(da.Text.ToArray(), db.Text.ToArray());
    }

    [Fact]
    public void A_patch_with_a_stale_text_is_refused()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        CaseDocument d = s.Doc("generation.json");
        JsonSourceText stale = d.Text;
        IReadOnlyList<JsonTextEdit> edits = JsonTextOps.ReplaceValue(stale, stale.Resolve("/0")!.FindMember("capacityMva", d.KeyComparer)!.Value, "123.5");
        Assert.True(CaseOps.SetField(s, d.Id, "/1", "capacityMva", "77").Changed);
        using CaseTransaction tx = s.Begin("stale");
        Assert.Throws<InvalidOperationException>(() => tx.Patch(d, stale, stale.Apply(edits)));
    }

    [Fact]
    public void A_plain_value_edit_and_a_reference_edit_still_check_what_they_did()
    {
        CaseSession s = CaseSession.OpenV1(CaseDir("uk"), Unlimited());
        int gen = s.Doc("generation.json").Id;
        Assert.True(CaseOps.SetField(s, gen, "/0", "capacityMva", "321").Changed);
        // A bus reference to a bus that does not exist is still refused (the index is built for reference fields).
        OpResult bad = CaseOps.SetField(s, gen, "/0", "bus", "987654");
        Assert.False(bad.Ok);
        Assert.Contains("does not exist", bad.Message, StringComparison.Ordinal);
    }
}
