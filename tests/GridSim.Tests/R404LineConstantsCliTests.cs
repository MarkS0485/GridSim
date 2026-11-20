using System.Text.Json;
using GridSim.Core.Lines;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-04 (CLI part): <c>gridsim line-constants</c> as the built CLI. The CLI is a thin reader and printer over the Core
/// solver, so the oracles are (a) the perfect-ground closed forms, derived here from first principles, and (b) the Core
/// solver called directly on the same geometry (the CLI must add nothing and lose nothing).
/// </summary>
public sealed class R404LineConstantsCliTests : IDisposable
{
    /// <summary>A flat three-phase line with one ground wire, earth-integral method, as a gridsim-lineconstants/1 document.</summary>
    internal const string ThreePhaseGeometry = """
        {"schema":"gridsim-lineconstants/1","method":"earth-integral","frequency_hz":50,"earth_resistivity_ohm_m":100,"transposed":true,
         "conductors":[
          {"name":"A","role":"phase","x_m":-4.0,"height_m":15.0,"resistance_ohm_per_km":0.07,"gmr_m":0.0116,"radius_m":0.0149},
          {"name":"B","role":"phase","x_m":0.0,"height_m":15.0,"resistance_ohm_per_km":0.07,"gmr_m":0.0116,"radius_m":0.0149},
          {"name":"C","role":"phase","x_m":4.0,"height_m":15.0,"resistance_ohm_per_km":0.07,"gmr_m":0.0116,"radius_m":0.0149},
          {"name":"G","role":"ground-wire","x_m":0.0,"height_m":21.0,"resistance_ohm_per_km":2.5,"gmr_m":0.0031,"radius_m":0.004}]}
        """;

    private const string SingleConductor = """
        {"schema":"gridsim-lineconstants/1","method":"perfect-ground","frequency_hz":50,
         "conductors":[{"name":"A","role":"phase","x_m":0.0,"height_m":10.0,"resistance_ohm_per_km":0.0,"gmr_m":0.01,"radius_m":0.01}]}
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-r404-" + Guid.NewGuid().ToString("N"));

    public R404LineConstantsCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, string text)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    private static JsonElement Read(string path)
    {
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
        return d.RootElement.Clone();
    }

    private static long Bits(JsonElement e) => BitConverter.DoubleToInt64Bits(e.GetDouble());

    [Fact]
    public void A_single_perfect_ground_conductor_gives_the_closed_form_reactance_and_capacitance()
    {
        string g = Write("single.json", SingleConductor);
        string o = Path.Combine(_dir, "single-out.json");
        CliProcess.Result r = CliProcess.RunBuilt("line-constants", g, "--json", o);
        Assert.True(r.ExitCode == 0, r.ToString());
        JsonElement doc = Read(o);
        Assert.Equal("line-constants", doc.GetProperty("verb").GetString());
        // X = omega mu0 / (2 pi) ln(2h / r') in ohm/km; C = 2 pi eps0 / ln(2h / r) in F/km (derived here, not read from the code).
        double omega = 2.0 * Math.PI * 50.0;
        double xKm = omega * 4.0e-7 * Math.PI / (2.0 * Math.PI) * Math.Log(2.0 * 10.0 / 0.01) * 1000.0;
        double cKm = 2.0 * Math.PI * 8.8541878128e-12 / Math.Log(2.0 * 10.0 / 0.01) * 1000.0;
        JsonElement z = doc.GetProperty("tables").GetProperty("phase_z")[0];
        Assert.Equal(xKm, z.GetProperty("x_ohm_per_km").GetDouble(), 1e-9 * xKm);
        Assert.Equal(0.47758, xKm, 5e-6);
        Assert.Equal(cKm, doc.GetProperty("tables").GetProperty("phase_c")[0].GetProperty("c_f_per_km").GetDouble(), 1e-9 * cKm);
        Assert.Equal(7.319e-9, cKm, 1e-12);
        Assert.False(doc.GetProperty("summary").GetProperty("sequence_available").GetBoolean());
        Assert.Contains("Line constants", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void A_three_phase_line_with_a_ground_wire_matches_the_core_solver_bit_for_bit_including_per_unit()
    {
        string g = Write("three.json", ThreePhaseGeometry);
        string o = Path.Combine(_dir, "three-out.json");
        CliProcess.Result r = CliProcess.RunBuilt("line-constants", g, "--length-km", "80", "--base-kv", "132", "--base-mva", "100", "--json", o);
        Assert.True(r.ExitCode == 0, r.ToString());
        JsonElement s = Read(o).GetProperty("summary");

        var conductors = new List<ConductorData>
        {
            new("A", ConductorRole.Phase, -4.0, 15.0, 0.07, 0.0116, 0.0149),
            new("B", ConductorRole.Phase, 0.0, 15.0, 0.07, 0.0116, 0.0149),
            new("C", ConductorRole.Phase, 4.0, 15.0, 0.07, 0.0116, 0.0149),
            new("G", ConductorRole.GroundWire, 0.0, 21.0, 2.5, 0.0031, 0.004),
        };
        LineConstantsResult core = LineConstantsSolver.Solve(new LineGeometry(conductors, 50.0, 100.0, EarthReturnMethod.EarthIntegral, true));
        Assert.Equal(BitConverter.DoubleToInt64Bits(core.Sequence!.Z1.Real), Bits(s.GetProperty("z1_r_ohm_per_km")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(core.Sequence.Z1.Imaginary), Bits(s.GetProperty("z1_x_ohm_per_km")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(core.Sequence.Z0.Real), Bits(s.GetProperty("z0_r_ohm_per_km")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(core.Sequence.C1FaradPerKm), Bits(s.GetProperty("c1_f_per_km")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(core.IntegralTailBound), Bits(s.GetProperty("integral_tail_bound")));
        (double pr, double px, double pb) = core.PositiveSequencePerUnit(80.0, 132.0, 100.0);
        Assert.Equal(BitConverter.DoubleToInt64Bits(pr), Bits(s.GetProperty("r1_pu")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(px), Bits(s.GetProperty("x1_pu")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(pb), Bits(s.GetProperty("b1_pu")));
        // Independent sanity: a 132 kV line has a positive-sequence reactance of order 0.4 ohm/km.
        Assert.InRange(core.Sequence.Z1.Imaginary, 0.3, 0.5);
        // Every stage is a table: 4x4 primitive, 3x3 phase and transposed.
        JsonElement t = Read(o).GetProperty("tables");
        Assert.Equal(16, t.GetProperty("primitive_z").GetArrayLength());
        Assert.Equal(9, t.GetProperty("phase_z").GetArrayLength());
        Assert.Equal(9, t.GetProperty("transposed_c").GetArrayLength());
    }

    [Fact]
    public void Two_runs_give_identical_summary_and_tables()
    {
        string g = Write("rep.json", ThreePhaseGeometry);
        string a = Path.Combine(_dir, "a.json"), b = Path.Combine(_dir, "b.json");
        Assert.Equal(0, CliProcess.RunBuilt("line-constants", g, "--json", a).ExitCode);
        Assert.Equal(0, CliProcess.RunBuilt("line-constants", g, "--json", b).ExitCode);
        Assert.Equal(Read(a).GetProperty("summary").GetRawText(), Read(b).GetProperty("summary").GetRawText());
        Assert.Equal(Read(a).GetProperty("tables").GetRawText(), Read(b).GetProperty("tables").GetRawText());
    }

    [Fact]
    public void Csv_output_writes_the_stage_tables_and_the_summary()
    {
        string g = Write("csv.json", ThreePhaseGeometry);
        string csv = Path.Combine(_dir, "lc.csv");
        Assert.Equal(0, CliProcess.RunBuilt("line-constants", g, "--csv", csv).ExitCode);
        Assert.True(File.Exists(csv));
        Assert.True(File.Exists(Path.Combine(_dir, "lc-summary.csv")));
        Assert.True(File.Exists(Path.Combine(_dir, "lc-phase_z.csv")));
    }

    [Fact]
    public void Per_unit_values_on_a_single_phase_line_exit_3_with_the_matrices_still_written()
    {
        string g = Write("one.json", SingleConductor);
        string o = Path.Combine(_dir, "one-out.json");
        CliProcess.Result r = CliProcess.RunBuilt("line-constants", g, "--length-km", "10", "--base-kv", "11", "--base-mva", "10", "--json", o);
        Assert.Equal(3, r.ExitCode);
        Assert.Equal(3, Read(o).GetProperty("exitCode").GetInt32());
        Assert.Contains("OUT OF SCOPE", r.StdErr + r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Unusable_input_is_exit_1_and_bad_usage_is_exit_2()
    {
        Assert.Equal(1, CliProcess.RunBuilt("line-constants", Path.Combine(_dir, "missing.json")).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("line-constants", Write("bad-schema.json", SingleConductor.Replace("lineconstants/1", "lineconstants/2"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("line-constants", Write("unknown-key.json", SingleConductor.Replace("\"frequency_hz\":50,", "\"frequency_hz\":50,\"colour\":1,"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("line-constants", Write("no-rho.json", SingleConductor.Replace("perfect-ground", "earth-integral"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("line-constants", Write("bad-method.json", SingleConductor.Replace("perfect-ground", "carson-series"))).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("line-constants", Write("not-json.json", "{ not json")).ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("line-constants", Write("underground.json", SingleConductor.Replace("\"height_m\":10.0", "\"height_m\":-1.0"))).ExitCode);
        string overlap = ThreePhaseGeometry.Replace("\"x_m\":4.0", "\"x_m\":0.01");
        CliProcess.Result o = CliProcess.RunBuilt("line-constants", Write("overlap.json", overlap));
        Assert.Equal(1, o.ExitCode);
        Assert.Contains("coincide or overlap", o.StdErr, StringComparison.Ordinal);

        string good = Write("good.json", SingleConductor);
        Assert.Equal(2, CliProcess.RunBuilt("line-constants").ExitCode);                                              // no file
        Assert.Equal(2, CliProcess.RunBuilt("line-constants", good, "--length-km", "10").ExitCode);                   // one of three
        Assert.Equal(2, CliProcess.RunBuilt("line-constants", good, "--length-km", "10", "--base-kv", "11", "--base-mva", "0").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("line-constants", good, "--top", "3").ExitCode);                          // not a flag of this verb
        Assert.Equal(2, CliProcess.RunBuilt("line-constants", good, good).ExitCode);                                  // two files
    }

    [Fact]
    public void Help_names_the_flags_and_the_verb_is_listed()
    {
        CliProcess.Result h = CliProcess.RunBuilt("line-constants", "--help");
        Assert.Equal(0, h.ExitCode);
        Assert.Contains("--length-km", h.StdOut, StringComparison.Ordinal);
        Assert.Contains("gridsim-lineconstants/1", h.StdOut, StringComparison.Ordinal);
        string list = CliProcess.RunBuilt("--help").StdOut;
        Assert.Contains("line-constants", list, StringComparison.Ordinal);
        Assert.Contains("rpc", list, StringComparison.Ordinal);
    }
}
