using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Gda;
using Xunit;

namespace GridSim.Tests.Gda;

/// <summary>
/// End-to-end tests for the GDA ingestion built on a tiny *synthetic* LTDS dataset
/// (no real grid data  -  that stays on the authorised machine). The fixture is a
/// 132 kV GSP feeding a 33 kV primary (via a transformer) and a 33 kV load down a
/// cable, with a busbar tie that must collapse and one embedded CCGT.
/// </summary>
public class GdaBuildTests : IDisposable
{
    private readonly string _root;

    public GdaBuildTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gridsim-gda-" + Guid.NewGuid().ToString("N"));
        string ltds = Path.Combine(_root, "NationalGrid", "RAWData", "LTDS Tabular Model");
        Directory.CreateDirectory(ltds);

        File.WriteAllText(Path.Combine(ltds, "ltds-attachment-8-node.csv"),
            "Licence,Code,Name,BSP Group,GSP Group,Status\n" +
            "NGED,GSP_1,Test GSP 132kV,BSP1,Test 132kV,Energised\n" +
            "NGED,BB_A,Primary A 33kV,BSP1,Test 132kV,Energised\n" +
            "NGED,BB_B,Primary B 33kV,BSP1,Test 132kV,Energised\n" +
            "NGED,LOAD_33,Load 33kV,BSP1,Test 132kV,Energised\n");

        File.WriteAllText(Path.Combine(ltds, "ltds-table-1-circuit.csv"),
            "Licence,GSP Group,BSP Group,Node 1,Node 2,Circuit Status,Operating Voltage (kV),Circuit Code,R1,X1,B1,R0,X0,Winter,Intermediate Cool,Intermediate Warm,Summer,Circuit Length (km),Equipment\n" +
            "NGED,Test 132kV,BSP1,BB_A,BB_B,Energised,33,C0,0,0,0,0,0,0,0,0,0,0,Busbar\n" +
            "NGED,Test 132kV,BSP1,BB_B,LOAD_33,Energised,33,C1,5,10,2,5,10,100,100,100,100,3.0,Circuit\n");

        File.WriteAllText(Path.Combine(ltds, "ltds-table-2a-two-winding-transformer.csv"),
            "Licence,GSP Group,Node 1,Operating Voltage 1,Node 2,Operating Voltage 2,Status,Vector Group,R1,X1,X0,Min Tap,Max Tap,Nominal Rating,Emergency Rating,Reverse Power Capability,Method of Earthing\n" +
            "NGED,Test 132kV,GSP_1,132,BB_A,33,Energised,Dyn11,2,50,45,-10,10,90,100,No,Solid\n");

        File.WriteAllText(Path.Combine(ltds, "ltds-table-2b-three-winding-transformer.csv"),
            "Licence,GSP Group,Node 1,Operating Voltage 1,Node 2,Operating Voltage 2,Node 3,Operating Voltage 3,Status,Vector Group,R1,X1 HV-LV,X1 LV-Tertiary,X1 HV-Tertiary,X0 HV-LV,X0 LV-Tertiary,X0 HV-Tertiary,Min Tap,Max Tap,Nominal Rating,Emergency Rating,Reverse Power Capability,Method of Earthing\n");

        File.WriteAllText(Path.Combine(ltds, "ltds-table-3-demand.csv"),
            "Licence,GSP Group,BSP Group,Primary Substation,Node,Status,Operating Voltage,Current Year Max Demand,Current Year Power Factor,Forecast Year 1,Forecast Year 2,Forecast Year 3,Forecast Year 4,Forecast Year 5,Firm Capacity of Substation,Minimum demand Scaling Factor (%)\n" +
            "NGED,Test 132kV,BSP1,LoadSub,LOAD_33,Energised,33,50,0.95,50,50,50,50,50,60,10\n");

        File.WriteAllText(Path.Combine(ltds, "table-5-generation.csv"),
            "Licence,GSP Group,Bulk Supply Point,Primary Substation,Connection Voltage,Agreed Export Capacity,Fuel Type,Connection Status\n" +
            "NGED,Test 132kV,BSP1,LoadSub,33,10,CCGT,Connected\n");
    }

    private GdaBuildResult Build(GdaBuildOptions? o = null) =>
        GdaModelBuilder.Build(new GdaPaths(_root), o);

    [Fact]
    public void Collapses_busbar_tie_into_one_bus()
    {
        GdaBuildResult r = Build();
        Assert.Equal(3, r.Diagnostics.Buses);          // GSP, merged(BB_A=BB_B), LOAD
        Assert.Equal(1, r.Diagnostics.BusbarTiesMerged);
        Assert.Equal(1, r.Diagnostics.SeriesBranches);
        Assert.Equal(2, r.Model.Branches.Count);       // 1 transformer + 1 cable
    }

    [Fact]
    public void Refers_transformer_impedance_to_gsp_voltage()
    {
        GdaBuildResult r = Build();
        Branch tx = r.Model.Branches.Single(b => b.IsTransformer);
        // Moved 2026-10 (END-PASS CF3 row 4, end pass; N252, NGED transformer impedances proven percent on 100 MVA from
        // NGED's own LTDS Table 4 Thevenin impedance at Banbury Road (proof on LtdsDataset.TransformerPercentOn100Mva),
        // Ruling R1 and Mark's go on the end-pass moves 2026-10-11): this fixture is an NGED lake, which is now read as
        // NGED publishes it; the old "50 ohm referred to 132 kV" reading was the defect N252 fixed. The test name keeps
        // the old wording so existing references resolve; the GSP-referred ohmic reading of a caller-built dataset is
        // still pinned by Cf3OpenCodeFindingsTests.N252_a_caller_built_dataset_keeps_the_old_reading_unless_it_says_otherwise.
        // Before -> after: X 50 / (132^2 / 100) = 0.28696051423324148 -> 50 / 100 x (100 / 100) = 0.5 (observed: Actual 0.5,
        // 1.95.273-275 runs); tolerance (4 dp) and the nominal tap assertion unchanged.
        // 50 % on 100 MVA on the 100 MVA model base: 0.5 pu.
        Assert.Equal(50.0 / 100.0 * (100.0 / 100.0), tx.X, 4);
        Assert.Equal(1.0, tx.TapRatio, 6);             // referred frame => nominal tap
    }

    [Fact]
    public void Folds_demand_and_attaches_generation()
    {
        GdaBuildResult r = Build();
        Assert.Equal(50.0, r.Diagnostics.TotalDemandMw, 1);
        Generator g = Assert.Single(r.Model.Generators);
        Assert.Equal("ccgt", g.Type);
        Assert.Equal(10.0, g.MBase, 1);
    }

    [Fact]
    public void Picks_highest_voltage_bus_as_single_slack()
    {
        GdaBuildResult r = Build();
        Bus slack = Assert.Single(r.Model.Buses, b => b.Type == BusType.Slack);
        Assert.Equal(132, (int)slack.BaseKv);
    }

    [Fact]
    public void Builds_a_single_connected_island_that_solves_in_band()
    {
        GdaBuildResult r = Build();
        Assert.Equal(1, r.Diagnostics.Islands);
        PowerFlowResult pf = new SteadyStateSolver().Solve(r.Model);
        Assert.True(pf.Converged, $"did not converge (mismatch {pf.MaxMismatch:E2})");
        Assert.InRange(pf.Vm.Min(), 0.9, 1.1);
        Assert.InRange(pf.Vm.Max(), 0.9, 1.1);
    }

    [Fact]
    public void Materialised_model_round_trips_through_the_json_loader()
    {
        GdaBuildResult r = Build();
        string outDir = Path.Combine(_root, "out");
        GdaModelWriter.WriteDirectory(r.Model, outDir);

        GridModel reloaded = JsonGridLoader.LoadDirectory(outDir);
        Assert.Equal(r.Model.Buses.Count, reloaded.Buses.Count);
        // exactly one slack (the grid infeed the writer injects)
        Assert.Single(reloaded.Buses, b => b.Type == BusType.Slack);
        PowerFlowResult pf = new SteadyStateSolver().Solve(reloaded);
        Assert.True(pf.Converged);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }
}
