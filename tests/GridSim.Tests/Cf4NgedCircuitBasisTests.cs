using System;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Model;
using GridSim.Gda;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N347 (CF4, October 2026; Ruling R1): NGED's LTDS circuit impedances (Table 1 R1 / X1 / B1) are percent on 100 MVA,
/// proved against NGED's own Table 4 Thevenin impedances (see <see cref="GdaModelBuilder.NgedCircuitConvention"/>).
/// The proof's published figures are replayed here, and the reader is pinned on a synthetic NGED-shaped lake.
/// </summary>
public sealed class Cf4NgedCircuitBasisTests : IDisposable
{
    private readonly string _root;

    public Cf4NgedCircuitBasisTests()
    {
        // The N252 fixture shape: a 132 kV GSP, a 132/33 kV transformer (2 + j50 %) and a 33 kV cable (5 + j10 %, B 2 %).
        _root = Path.Combine(Path.GetTempPath(), "gridsim-cf4-n347-" + Guid.NewGuid().ToString("N"));
        string ltds = Path.Combine(_root, "NationalGrid", "RAWData", "LTDS Tabular Model");
        Directory.CreateDirectory(ltds);
        File.WriteAllText(Path.Combine(ltds, "ltds-attachment-8-node.csv"),
            "Licence,Code,Name,BSP Group,GSP Group,Status\n" +
            "NGED,GSP_1,Test GSP 132kV,BSP1,Test 132kV,Energised\n" +
            "NGED,BB_A,Primary A 33kV,BSP1,Test 132kV,Energised\n" +
            "NGED,LOAD_33,Load 33kV,BSP1,Test 132kV,Energised\n");
        File.WriteAllText(Path.Combine(ltds, "ltds-table-1-circuit.csv"),
            "Licence,GSP Group,BSP Group,Node 1,Node 2,Circuit Status,Operating Voltage (kV),Circuit Code,R1,X1,B1,R0,X0,Winter,Intermediate Cool,Intermediate Warm,Summer,Circuit Length (km),Equipment\n" +
            "NGED,Test 132kV,BSP1,BB_A,LOAD_33,Energised,33,C1,5,10,2,5,10,100,100,100,100,3.0,Circuit\n");
        File.WriteAllText(Path.Combine(ltds, "ltds-table-2a-two-winding-transformer.csv"),
            "Licence,GSP Group,Node 1,Operating Voltage 1,Node 2,Operating Voltage 2,Status,Vector Group,R1,X1,X0,Min Tap,Max Tap,Nominal Rating,Emergency Rating,Reverse Power Capability,Method of Earthing\n" +
            "NGED,Test 132kV,GSP_1,132,BB_A,33,Energised,Dyn11,2,50,45,-10,10,90,100,No,Solid\n");
        File.WriteAllText(Path.Combine(ltds, "ltds-table-2b-three-winding-transformer.csv"),
            "Licence,GSP Group,Node 1,Operating Voltage 1,Node 2,Operating Voltage 2,Node 3,Operating Voltage 3,Status,Vector Group,R1,X1 HV-LV,X1 LV-Tertiary,X1 HV-Tertiary,X0 HV-LV,X0 LV-Tertiary,X0 HV-Tertiary,Min Tap,Max Tap,Nominal Rating,Emergency Rating,Reverse Power Capability,Method of Earthing\n");
        File.WriteAllText(Path.Combine(ltds, "ltds-table-3-demand.csv"),
            "Licence,GSP Group,BSP Group,Primary Substation,Node,Status,Operating Voltage,Current Year Max Demand,Current Year Power Factor,Forecast Year 1,Forecast Year 2,Forecast Year 3,Forecast Year 4,Forecast Year 5,Firm Capacity of Substation,Minimum demand Scaling Factor (%)\n" +
            "NGED,Test 132kV,BSP1,LoadSub,LOAD_33,Energised,33,20,0.95,20,20,20,20,20,60,10\n");
        File.WriteAllText(Path.Combine(ltds, "table-5-generation.csv"),
            "Licence,GSP Group,Bulk Supply Point,Primary Substation,Connection Voltage,Agreed Export Capacity,Fuel Type,Connection Status\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void N347_nged_reads_its_circuits_as_percent_on_100_mva()
    {
        Assert.Equal(ImpedanceConvention.PercentOn100Mva, GdaModelBuilder.NgedCircuitConvention);
        LtdsDataset ds = GdaModelBuilder.LoadNged(new GdaPaths(_root));
        Assert.Equal(ImpedanceConvention.PercentOn100Mva, ds.Convention);
        Assert.True(ds.TransformerPercentOn100Mva);   // N252 unchanged

        GridModel m = GdaModelBuilder.Build(ds).Model;
        Branch cable = m.Branches.Single(b => !b.IsTransformer);
        // 5 + j10 % and B 2 % on 100 MVA, on the 100 MVA model base. Before (ohms and microsiemens referred to 132 kV,
        // Zbase 174.24): R 5 / 174.24, X 10 / 174.24, B 2e-6 * 174.24.
        Assert.Equal(5.0 / 100.0 * (100.0 / 100.0), cable.R);
        Assert.Equal(10.0 / 100.0 * (100.0 / 100.0), cable.X);
        Assert.Equal(2.0 / 100.0 / (100.0 / 100.0), cable.B);
        // The transformer keeps its N252 reading, bit for bit.
        Branch tx = m.Branches.Single(b => b.IsTransformer);
        Assert.Equal(50.0 / 100.0 * (100.0 / 100.0), tx.X);
        Assert.Equal(2.0 / 100.0 * (100.0 / 100.0), tx.R);
    }

    [Fact]
    public void N347_the_model_base_scales_impedance_and_susceptance_inversely()
    {
        LtdsDataset ds = GdaModelBuilder.LoadNged(new GdaPaths(_root));
        Branch cable = GdaModelBuilder.Build(ds, new GdaBuildOptions { BaseMva = 50.0 }).Model.Branches.Single(b => !b.IsTransformer);
        Assert.Equal(10.0 / 100.0 * (50.0 / 100.0), cable.X);
        Assert.Equal(2.0 / 100.0 / (50.0 / 100.0), cable.B);
    }

    [Fact]
    public void N347_a_caller_built_ohmic_dataset_is_still_read_as_ohms()
    {
        // The convention is the dataset's: a caller that says ohmic gets the old reading exactly.
        LtdsDataset ds = GdaModelBuilder.LoadNged(new GdaPaths(_root)) with { Convention = ImpedanceConvention.OhmicReferredToGsp };
        Branch cable = GdaModelBuilder.Build(ds).Model.Branches.Single(b => !b.IsTransformer);
        Assert.Equal(10.0 / (132.0 * 132.0 / 100.0), cable.X);
    }

    /// <summary>The proof's published figures (NGED LTDS tabular model, Table 1 circuit R1 / X1 and Table 4 Thevenin
    /// R1 / X1 of both ends, read 2026-10-11): (kV, circuit R1, X1, Thevenin at the fed site R, X, at the source R, X).</summary>
    public static TheoryData<string, double, double, double, double, double, double, double> Published => new()
    {
        { "RATS3 from TOTO3", 33, 12.2787, 15.1531, 0.1313, 0.2389, 0.0085, 0.0874 },
        { "GECW3 from WIGS3", 33, 5.8288, 9.5361, 0.0724, 0.2443, 0.0141, 0.1490 },
        { "FROG3 from BRIS3", 33, 5.133, 7.897, 0.2153, 0.4832, 0.1639, 0.4042 },
        { "TEWK6 from BROG6", 66, 7.6667, 11.6146, 0.1295, 0.3001, 0.0528, 0.1840 },
        { "HAGC1 from NOGR1", 132, 1.5578, 4.2482, 0.0209, 0.1038, 0.0053, 0.0614 },
    };

    [Theory]
    [MemberData(nameof(Published))]
    public void N347_the_published_thevenin_difference_is_the_circuit_read_as_percent(
        string pair, double kv, double r1, double x1, double rFed, double xFed, double rSrc, double xSrc)
    {
        // Radial: Z_th(fed) = Z_th(source) + Z_circuit. Table 4 is rounded to 4 decimals, so the percent reading must
        // agree within 1.5e-4 pu in R and in X (two roundings), while the ohmic reading is off by Zbase / 100.
        var dz = new Complex(rFed - rSrc, xFed - xSrc);
        var percent = new Complex(r1 / 100.0, x1 / 100.0);
        var ohmic = new Complex(r1, x1) / (kv * kv / 100.0);
        Assert.True(Math.Abs(dz.Real - percent.Real) < 1.5e-4 && Math.Abs(dz.Imaginary - percent.Imaginary) < 1.5e-4,
            $"{pair}: dZ {dz} vs percent {percent}");
        Assert.True((dz - ohmic).Magnitude > 0.1 * dz.Magnitude, $"{pair}: the ohmic reading {ohmic} should not match {dz}");
    }
}
