using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// Phase 5c (2026-10-20): the asset overlay pin, for a substation topology that WARNS. A bundled case (data/uk through
/// its v2 projection) with an overlay whose switch states imply both a bus split (two sections of one bus, an open
/// coupler between them) and a coupling (a section of another bus joined by a closed coupler) loads to the same model,
/// to the bit, and solves to the same power flow, to the bit, as the case without it; the overlay is carried, its two
/// warnings are worked out from it, and the load is not refused (a warning is never an error).
/// </summary>
public sealed partial class V2CaseLoaderTests
{
    [Fact]
    public void Overlay_topology_warnings_change_nothing_the_solver_sees_and_never_refuse_the_load()
    {
        string dir = Path.Combine(DataRoot, "uk");
        using var plain = new TempFolder();
        using var overlaid = new TempFolder();
        string plainManifest = WriteProjection(dir, plain.Path);
        string overlaidManifest = WriteProjection(dir, overlaid.Path);

        // The first two buses of the case: sections bb1 and bb2 on the first, bb3 on the second.
        V2CaseLoadResult a = V2CaseLoader.LoadWithNotes(plainManifest, SchemasDir, DataRoot);
        long b1 = a.Model.Buses[0].Id, b2 = a.Model.Buses[1].Id;
        Assert.NotEqual(b1, b2);
        string caseDir = Path.GetDirectoryName(overlaidManifest)!;
        File.WriteAllText(Path.Combine(caseDir, "topology.assets.json"), $$"""
            { "schema": "gridsim-assets/2", "records": [
              { "id": "sub", "kind": "substation", "refs": {}, "params": {} },
              { "id": "bb1", "kind": "busbarSection", "parent": "sub", "refs": { "bus": { "bus": {{b1}} } }, "params": {} },
              { "id": "bb2", "kind": "busbarSection", "parent": "sub", "refs": { "bus": { "bus": {{b1}} } }, "params": {} },
              { "id": "bb3", "kind": "busbarSection", "parent": "sub", "refs": { "bus": { "bus": {{b2}} } }, "params": {} },
              { "id": "c12", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "bb1" }, "to": { "record": "bb2" } }, "params": { "device": "breaker", "normalOpen": true } },
              { "id": "c23", "kind": "switchingDevice", "parent": "sub", "refs": { "from": { "record": "bb2" }, "to": { "record": "bb3" } }, "params": { "device": "breaker", "normalOpen": false } }
            ] }
            """);
        AddAssetsMember(overlaidManifest, "topology.assets.json");
        V2CaseLoadResult b = V2CaseLoader.LoadWithNotes(overlaidManifest, SchemasDir, DataRoot);

        // Carried, with its two warnings; the notes gain exactly the carry note.
        Assert.Null(a.Overlay);
        CaseAssetOverlay ov = Assert.IsType<CaseAssetOverlay>(b.Overlay);
        Assert.Equal(6, ov.Records.Count);
        Assert.Equal(a.Notes.Append("overlay: 6 records carried, not used by the solver").ToArray(), b.Notes.ToArray());
        Assert.Equal(new[] { TopologyWarningKind.ImpliedSplit, TopologyWarningKind.ImpliedCoupling },
            SubstationTopology.WarningsOf(ov).Select(w => w.Kind));

        // The model, every field, doubles by their bits; the Newton solve to the bit.
        List<string> diffs = DeepDifferences(a.Model, b.Model);
        Assert.True(diffs.Count == 0, "the model with a warning topology differs:" + Environment.NewLine + string.Join(Environment.NewLine, diffs));
        GridModel ma = a.Model, mb = b.Model;
        AssertSameSolve(SolverSelector.CreateNewton(ma).Solve(ma), SolverSelector.CreateNewton(mb).Solve(mb), "data/uk with a warning topology");
    }
}
