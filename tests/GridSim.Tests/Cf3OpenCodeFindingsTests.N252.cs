using System;
using System.IO;
using System.Linq;
using GridSim.Core.Model;
using GridSim.Gda;
using GridSim.Gda.Ltds;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N252 (CF3, October 2026; Ruling R1): NGED's LTDS transformer impedances (Table 2a R1 / X1, Table 2b R1 / X1 HV-LV /
/// LV-Tertiary / HV-Tertiary) are percent on 100 MVA, not ohms referred to the GSP voltage as the reader assumed (the
/// proof, NGED's own Table 4 Thevenin impedance at Banbury Road, is on <see cref="LtdsDataset.TransformerPercentOn100Mva"/>).
/// The circuits keep their ohmic reading (a separate question, recorded in the notes); UKPN and NPg are unchanged.
/// </summary>
public sealed partial class Cf3OpenCodeFindingsTests
{
    /// <summary>An NGED-shaped lake in a temporary folder: a 132/33 kV two-winding unit (R1 2, X1 50 on 90 MVA), a
    /// 132/33/11 kV three-winding unit, one 33 kV cable (R1 5, X1 10), demand and a generator.</summary>
    private static string N252Lake()
    {
        string root = Path.Combine(Path.GetTempPath(), "gridsim-cf3-n252-" + Guid.NewGuid().ToString("N"));
        string ltds = Path.Combine(root, "NationalGrid", "RAWData", "LTDS Tabular Model");
        Directory.CreateDirectory(ltds);
        File.WriteAllText(Path.Combine(ltds, "ltds-attachment-8-node.csv"),
            "Licence,Code,Name,BSP Group,GSP Group,Status\n" +
            "NGED,GSP_1,Test GSP 132kV,BSP1,Test 132kV,Energised\n" +
            "NGED,BB_A,Primary A 33kV,BSP1,Test 132kV,Energised\n" +
            "NGED,LOAD_33,Load 33kV,BSP1,Test 132kV,Energised\n" +
            "NGED,T3_LV,Three winding LV 33kV,BSP1,Test 132kV,Energised\n" +
            "NGED,T3_TV,Three winding TV 11kV,BSP1,Test 132kV,Energised\n");
        File.WriteAllText(Path.Combine(ltds, "ltds-table-1-circuit.csv"),
            "Licence,GSP Group,BSP Group,Node 1,Node 2,Circuit Status,Operating Voltage (kV),Circuit Code,R1,X1,B1,R0,X0,Winter,Intermediate Cool,Intermediate Warm,Summer,Circuit Length (km),Equipment\n" +
            "NGED,Test 132kV,BSP1,BB_A,LOAD_33,Energised,33,C1,5,10,2,5,10,100,100,100,100,3.0,Circuit\n");
        File.WriteAllText(Path.Combine(ltds, "ltds-table-2a-two-winding-transformer.csv"),
            "Licence,GSP Group,Node 1,Operating Voltage 1,Node 2,Operating Voltage 2,Status,Vector Group,R1,X1,X0,Min Tap,Max Tap,Nominal Rating,Emergency Rating,Reverse Power Capability,Method of Earthing\n" +
            "NGED,Test 132kV,GSP_1,132,BB_A,33,Energised,Dyn11,2,50,45,-10,10,90,100,No,Solid\n");
        File.WriteAllText(Path.Combine(ltds, "ltds-table-2b-three-winding-transformer.csv"),
            "Licence,GSP Group,Node 1,Operating Voltage 1,Node 2,Operating Voltage 2,Node 3,Operating Voltage 3,Status,Vector Group,R1,X1 HV-LV,X1 LV-Tertiary,X1 HV-Tertiary,X0 HV-LV,X0 LV-Tertiary,X0 HV-Tertiary,Min Tap,Max Tap,Nominal Rating,Emergency Rating,Reverse Power Capability,Method of Earthing\n" +
            "NGED,Test 132kV,GSP_1,132,T3_LV,33,T3_TV,11,Energised,YNyn0d1,1.5,90,60,80,900,950,1000,-20,10,60,78,-,Solid\n");
        File.WriteAllText(Path.Combine(ltds, "ltds-table-3-demand.csv"),
            "Licence,GSP Group,BSP Group,Primary Substation,Node,Status,Operating Voltage,Current Year Max Demand,Current Year Power Factor,Forecast Year 1,Forecast Year 2,Forecast Year 3,Forecast Year 4,Forecast Year 5,Firm Capacity of Substation,Minimum demand Scaling Factor (%)\n" +
            "NGED,Test 132kV,BSP1,LoadSub,LOAD_33,Energised,33,20,0.95,20,20,20,20,20,60,10\n");
        File.WriteAllText(Path.Combine(ltds, "table-5-generation.csv"),
            "Licence,GSP Group,Bulk Supply Point,Primary Substation,Connection Voltage,Agreed Export Capacity,Fuel Type,Connection Status\n" +
            "NGED,Test 132kV,BSP1,LoadSub,33,10,CCGT,Connected\n");
        return root;
    }

    [Fact]
    public void N252_nged_reads_its_transformers_as_percent_on_100_mva()
    {
        LtdsDataset ds = GdaModelBuilder.LoadNged(new GdaPaths(N252Lake()));
        Assert.True(ds.TransformerPercentOn100Mva);
        // Moved 2026-10 (END-PASS CF4 row 6, end pass; N347, NGED circuits proven percent on 100 MVA from NGED's own LTDS
        // Table 4 Thevenin impedances (five radial pairs at 33, 66 and 132 kV, agreeing in R and X to 4 decimals; the
        // proof is on GdaModelBuilder.NgedCircuitConvention), Ruling R1 and Mark's go on the end-pass moves 2026-10-11):
        // the circuits' ohmic reading was N252's scope, not a finding; N347 corrected it. The old ohmic reading still
        // exists for a caller-built dataset and is pinned by N252_a_caller_built_dataset_keeps_the_old_reading_unless_it_says_otherwise.
        // Before -> after: ds.Convention OhmicReferredToGsp -> PercentOn100Mva (observed: Expected OhmicReferredToGsp,
        // Actual PercentOn100Mva, 1.95.273-275 runs); cable X 10 / 174.24 = 0.0573921028466483 -> 10 / 100 x (100 / 100) =
        // 0.1 (derived, not observed: the run stopped at the convention assertion; the value is CircuitPu's percent branch,
        // raw / 100.0 * (BaseMva / 100.0), the same arithmetic as written here). Transformer asserts unchanged.
        Assert.Equal(ImpedanceConvention.PercentOn100Mva, ds.Convention);   // N347: the circuits are percent on 100 MVA too

        GridModel m = GdaModelBuilder.Build(ds).Model;
        // Two-winding: 2 % and 50 % on 100 MVA are 0.02 and 0.5 pu on the 100 MVA model base. (Before: 50 / 174.24.)
        Branch tx = m.Branches.Single(b => b.IsTransformer && b.Name!.StartsWith("TX ", StringComparison.Ordinal));
        Assert.Equal(2.0 / 100.0 * (100.0 / 100.0), tx.R);
        Assert.Equal(50.0 / 100.0 * (100.0 / 100.0), tx.X);
        // Three-winding star legs from the published 90 / 60 / 80 %: H 55, L 35, T 25 % -> 0.55, 0.35, 0.25 pu.
        Branch h = m.Branches.Single(b => b.Name!.StartsWith("TX3 H", StringComparison.Ordinal));
        Branch l = m.Branches.Single(b => b.Name!.StartsWith("TX3 L", StringComparison.Ordinal));
        Branch t = m.Branches.Single(b => b.Name!.StartsWith("TX3 T", StringComparison.Ordinal));
        Assert.Equal(0.5 * (90.0 + 80.0 - 60.0) / 100.0 * (100.0 / 100.0), h.X);
        Assert.Equal(0.5 * (90.0 + 60.0 - 80.0) / 100.0 * (100.0 / 100.0), l.X);
        Assert.Equal(0.5 * (80.0 + 60.0 - 90.0) / 100.0 * (100.0 / 100.0), t.X);
        Assert.Equal(1.5 / 100.0 * (100.0 / 100.0), h.R);
        // N347 (moved 2026-10, see the convention assertion above): the cable is 10 % on 100 MVA, 0.1 pu on the 100 MVA
        // model base (was ohms referred to the 132 kV GSP: 10 / 174.24).
        Branch cable = m.Branches.Single(b => !b.IsTransformer);
        Assert.Equal(10.0 / 100.0 * (100.0 / 100.0), cable.X);
    }

    [Fact]
    public void N252_the_model_base_scales_the_transformer_percent_as_a_fixed_100_mva_base()
    {
        // On a 50 MVA model base, 50 % on 100 MVA is 0.25 pu: the percent's own base is 100 MVA, not the unit's 90 MVA.
        LtdsDataset ds = GdaModelBuilder.LoadNged(new GdaPaths(N252Lake()));
        GridModel m = GdaModelBuilder.Build(ds, new GdaBuildOptions { BaseMva = 50.0 }).Model;
        Branch tx = m.Branches.Single(b => b.IsTransformer && b.Name!.StartsWith("TX ", StringComparison.Ordinal));
        Assert.Equal(50.0 / 100.0 * (50.0 / 100.0), tx.X);
    }

    [Fact]
    public void N252_a_caller_built_dataset_keeps_the_old_reading_unless_it_says_otherwise()
    {
        // The flag defaults to false: UKPN, NPg and any dataset a caller builds itself are read exactly as before.
        LtdsDataset nged = GdaModelBuilder.LoadNged(new GdaPaths(N252Lake()));
        // Moved 2026-10 (END-PASS CF4 row 7, end pass; N347, NGED circuits percent on 100 MVA (Table 4 Thevenin proof on
        // GdaModelBuilder.NgedCircuitConvention), Ruling R1 and Mark's go on the end-pass moves 2026-10-11): LoadNged now
        // returns the percent convention, so clearing only the transformer flag read the transformer as percent on its
        // 90 MVA rating (observed: Expected 0.28696051423324148, Actual 0.55555555555555558, 1.95.273-275 runs). The old
        // reading this test pins (a caller-built ohmic dataset) still exists, so the dataset copy now says so explicitly
        // and the old value is kept exactly. Before -> after: `nged with { TransformerPercentOn100Mva = false }` ->
        // `nged with { Convention = ImpedanceConvention.OhmicReferredToGsp, TransformerPercentOn100Mva = false }`;
        // expected X 50 / 174.24 = 0.2869605142332415 unchanged.
        LtdsDataset plain = nged with { Convention = ImpedanceConvention.OhmicReferredToGsp, TransformerPercentOn100Mva = false };
        Branch tx = GdaModelBuilder.Build(plain).Model.Branches.Single(b => b.IsTransformer && b.Name!.StartsWith("TX ", StringComparison.Ordinal));
        Assert.Equal(50.0 / (132.0 * 132.0 / 100.0), tx.X);
        Assert.False(new LtdsDataset("x", ImpedanceConvention.PercentOn100Mva, Array.Empty<LtdsNode>(), Array.Empty<LtdsCircuit>(),
            Array.Empty<LtdsTransformer2>(), Array.Empty<LtdsTransformer3>(), Array.Empty<LtdsDemand>(), Array.Empty<LtdsGeneration>())
            .TransformerPercentOn100Mva);
    }
}
