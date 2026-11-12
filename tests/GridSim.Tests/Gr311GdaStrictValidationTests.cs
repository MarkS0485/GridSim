using GridSim.Gda;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-311: every GridModel.Validate finding of a GDA build was folded into the build notes and the model returned, so a
/// build with an Error finding (for example a non-positive system base) "succeeded". GdaBuildOptions.StrictValidation is
/// a new opt-in: when set, any Error finding makes the build throw InvalidDataException listing the errors. Default off.
/// Pinned: default options still return the model with the error in the notes (so pinned builds do not move); strict
/// options throw and name the error; strict options on a clean fixture build return the same model and notes as default.
/// </summary>
public class Gr311GdaStrictValidationTests : IDisposable
{
    private readonly string _root;

    public Gr311GdaStrictValidationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gridsim-gr311-" + Guid.NewGuid().ToString("N"));
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


    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private GdaBuildResult Build(GdaBuildOptions o) => GdaModelBuilder.Build(new GdaPaths(_root), o);

    [Fact]
    public void Gr311_by_default_a_validation_error_is_a_note_and_the_model_is_returned()
    {
        GdaBuildResult r = Build(new GdaBuildOptions { BaseMva = -100.0 });
        Assert.Contains(r.Diagnostics.Notes, n => n.StartsWith("validation: error:", StringComparison.Ordinal));
    }

    [Fact]
    public void Gr311_strict_validation_throws_naming_the_error()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Build(new GdaBuildOptions { BaseMva = -100.0, StrictValidation = true }));
        Assert.Contains("strict validation", ex.Message, StringComparison.Ordinal);
        Assert.Contains("System base", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr311_strict_validation_on_a_clean_build_changes_nothing()
    {
        GdaBuildResult plain = Build(new GdaBuildOptions());
        GdaBuildResult strict = Build(new GdaBuildOptions { StrictValidation = true });
        Assert.Equal(plain.Diagnostics.Notes, strict.Diagnostics.Notes);
        Assert.Equal(plain.Model.Buses.Count, strict.Model.Buses.Count);
    }
}
