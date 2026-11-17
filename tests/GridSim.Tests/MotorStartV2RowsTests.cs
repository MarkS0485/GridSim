using GridSim.Core.Analysis;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.MotorStartTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC3 P8: the optional placed-motor rows of a native v2 network file (network.schema.json "motors"): absent and
/// populated rows, the loader's semantic checks (bus reference, units, names), the model staying exactly the model
/// without them, the v2 writer's round trip and the case editor's preservation on save.
/// </summary>
public sealed class MotorStartV2RowsTests
{
    private sealed class Temp : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "gridsim-motor-rows-" + Guid.NewGuid().ToString("N"));
        public Temp() => Directory.CreateDirectory(Dir);
        public string Write(string name, string text) { string p = Path.Combine(Dir, name); File.WriteAllText(p, text); return p; }
        public string Copy(string fixture) { string p = Path.Combine(Dir, fixture); File.Copy(Fixture(fixture), p); return p; }
        public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } }
    }

    private static string StiffNetwork(string motors) =>
        File.ReadAllText(Fixture("stiff.network.json")).Replace(MotorsOf(File.ReadAllText(Fixture("stiff.network.json"))), motors, StringComparison.Ordinal);

    private static string MotorsOf(string text)
    {
        int at = text.IndexOf(",\n  \"motors\"", StringComparison.Ordinal);
        if (at < 0) at = text.IndexOf(",\r\n  \"motors\"", StringComparison.Ordinal);
        int end = text.LastIndexOf('}');
        return text[at..end];
    }

    private static string Row(string extra = "", string bus = "1", string name = "M1", string r1 = "0.012", bool inertia = true) =>
        ",\n  \"motors\": [ { \"name\": \"" + name + "\", \"bus\": " + bus + ", \"ratedMva\": 5, \"ratedKv\": 11, \"raPu\": 0.01, \"xaPu\": 0.1, \"xmPu\": 3.0, \"r1Pu\": " + r1
        + ", \"x1Pu\": 0.08" + (inertia ? ", \"inertiaH\": 0.6" : "") + ", \"loadTorque\": { \"quadraticPu\": 0.7 } }" + extra + " ]\n";

    private static string Object(string row) => row[row.IndexOf('{', StringComparison.Ordinal)..(row.LastIndexOf('}') + 1)];

    private static V2CaseLoadResult LoadIn(Temp t, string network)
    {
        t.Copy("stiff.case.json");
        t.Write("stiff.network.json", network);
        return V2CaseLoader.LoadWithNotes(Path.Combine(t.Dir, "stiff.case.json"), Schemas, DataRoot);
    }

    [Fact]
    public void A_populated_row_reaches_the_load_result_with_every_field()
    {
        V2CaseLoadResult r = V2CaseLoader.LoadWithNotes(Fixture("prestart.case.json"), Schemas, DataRoot);
        PlacedMotor m = Assert.Single(r.Motors);
        Assert.Equal(new PlacedMotor("M1", 4, 5, 11, 0.01, 0.1, 3.0, 0.012, 0.08, 0.6, new MotorLoadTorque(0, 0, 0.7),
            "5 MVA 11 kV induction motor driving a fan; single-cage circuit on the motor base; H covers rotor and fan."), m);
        Assert.Contains(r.Notes, n => n.StartsWith("motors: 1 placed motor row carried beside the model", StringComparison.Ordinal));
        Assert.Empty(r.Model.InductionMachines);   // never in the power-flow model
    }

    [Fact]
    public void An_omitted_motor_array_leaves_the_model_notes_and_solve_unchanged()
    {
        using var with = new Temp();
        using var without = new Temp();
        V2CaseLoadResult a = LoadIn(with, File.ReadAllText(Fixture("stiff.network.json")));
        V2CaseLoadResult b = LoadIn(without, StiffNetwork("\n"));
        Assert.Empty(b.Motors);
        Assert.DoesNotContain(b.Notes, n => n.StartsWith("motors:", StringComparison.Ordinal));
        Assert.Null(b.Overlay);
        Assert.Equal(a.Model.Buses, b.Model.Buses);
        Assert.Equal(a.Model.AllGenerators, b.Model.AllGenerators);
        Assert.Equal(a.Model.Branches, b.Model.Branches);
        PowerFlowResult sa = SolverSelector.CreateNewton(a.Model).Solve(a.Model, ContingencyAnalysis.StudyOptions);
        PowerFlowResult sb = SolverSelector.CreateNewton(b.Model).Solve(b.Model, ContingencyAnalysis.StudyOptions);
        Assert.Equal(sa.Vm.Select(BitConverter.DoubleToInt64Bits), sb.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(sa.VaDeg.Select(BitConverter.DoubleToInt64Bits), sb.VaDeg.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(sa.Iterations, sb.Iterations);
    }

    [Theory]
    [InlineData("bus", "/motors/0/bus: motor 'M1' is placed at bus 99, which no network file defines.")]
    [InlineData("r1", "r1Pu")]
    [InlineData("inertia", "inertiaH")]
    [InlineData("duplicate", "motor name 'M1' is used by more than one motor")]
    [InlineData("demand", "motor 'Load' has the name of a demand")]
    [InlineData("unknown", "ratedMw")]
    public void Invalid_motor_rows_are_refused_naming_the_problem(string kind, string expected)
    {
        using var t = new Temp();
        string row = kind switch
        {
            "bus" => Row(bus: "99"),
            "r1" => Row(r1: "-0.012"),
            "inertia" => Row(inertia: false),
            "duplicate" => Row(extra: ", " + Object(Row())),
            "demand" => Row(name: "Load"),
            _ => Row().Replace("\"ratedKv\": 11,", "\"ratedKv\": 11, \"ratedMw\": 4,", StringComparison.Ordinal),
        };
        var ex = Assert.Throws<InvalidDataException>(() => LoadIn(t, StiffNetwork(row)));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_v2_writer_round_trips_motor_rows_and_is_byte_unchanged_without_them()
    {
        using var t = new Temp();
        V2CaseLoadResult loaded = V2CaseLoader.LoadWithNotes(Fixture("prestart.case.json"), Schemas, DataRoot, dispatch: false);
        V2CaseWriteResult plain = V2CaseWriter.Write(loaded.Model, Path.Combine(t.Dir, "a", "plain.case.json"));
        V2CaseWriteResult nulls = V2CaseWriter.Write(loaded.Model, Path.Combine(t.Dir, "b", "plain.case.json"), null);
        Assert.Equal(File.ReadAllBytes(plain.NetworkPath), File.ReadAllBytes(nulls.NetworkPath));
        Assert.Equal(File.ReadAllBytes(plain.ManifestPath), File.ReadAllBytes(nulls.ManifestPath));

        V2CaseWriteResult written = V2CaseWriter.Write(loaded.Model, Path.Combine(t.Dir, "c", "motor.case.json"), loaded.Motors);
        V2CaseLoadResult back = V2CaseLoader.LoadWithNotes(written.ManifestPath, Schemas, DataRoot);
        Assert.Equal(loaded.Motors, back.Motors);
        Assert.Equal(loaded.Model.Buses, back.Model.Buses);
        Assert.Equal(loaded.Model.AllGenerators.Select(g => g.Pg), back.Model.AllGenerators.Select(g => g.Pg));
        // Written again from the read-back state: the same bytes.
        V2CaseWriteResult again = V2CaseWriter.Write(back.Model, Path.Combine(t.Dir, "d", "motor.case.json"), back.Motors);
        Assert.Equal(File.ReadAllBytes(written.NetworkPath), File.ReadAllBytes(again.NetworkPath));
    }

    [Fact]
    public void The_case_editor_preserves_motor_rows_through_an_edit_and_save()
    {
        using var t = new Temp();
        string manifest = t.Copy("prestart.case.json");
        string network = t.Copy("prestart.network.json");
        string before = File.ReadAllText(network);
        CaseSession s = CaseSession.OpenV2(manifest, GridSim.Tests.Editor.CaseSessionTests.Unlimited());
        Assert.DoesNotContain(s.Collections, c => c.Role == "motors");   // carried as text, not an editor section
        OpResult r = CaseOps.SetField(s, s.Doc("prestart.network.json").Id, "/demands/1", "pMw", "4.5");
        Assert.True(r.Changed, r.Message);
        SaveReport saved = SaveGate.Run(s);
        Assert.True(saved.Saved, string.Join(Environment.NewLine, saved.Lines()));
        string after = File.ReadAllText(network);
        int at = before.IndexOf("\"motors\"", StringComparison.Ordinal);
        Assert.Equal(before[at..], after[after.IndexOf("\"motors\"", StringComparison.Ordinal)..]);
        V2CaseLoadResult back = V2CaseLoader.LoadWithNotes(manifest, Schemas, DataRoot);
        Assert.Equal(V2CaseLoader.LoadWithNotes(Fixture("prestart.case.json"), Schemas, DataRoot).Motors, back.Motors);
    }
}
