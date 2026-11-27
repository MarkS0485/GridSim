using System.Globalization;
using System.Numerics;
using System.Text.Json;
using GridSim.Core.Distribution;
using GridSim.Core.Distribution.Meshed;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 wiring: <c>dist3ph-mesh --from-dist3ph &lt;feeder.json&gt;</c> lifts a legacy <c>dist3ph</c> feeder (transformers and regulators
/// included) into the phase frame with Core's <see cref="MultiPhaseFeederAdapter"/> and solves it. The oracles are the Core tests' own:
/// the published IEEE 123-node voltages within the bounds R415bTransformerRegulatorTests uses, and the Core adapter and solver called
/// directly with the same options (the CLI must add and lose nothing, bit for bit), also for the delta-secondary IEEE 4-node case that
/// needs a pinned reference.
/// </summary>
public sealed class R415dDist3phMeshFromFeederCliTests : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly string _dir = TestTemp.Under("gridsim-r415d-" + Guid.NewGuid().ToString("N"));

    public R415dDist3phMeshFromFeederCliTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string DataPath(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null);
        return Path.Combine(dir!.FullName, "data", "dist3ph", name);
    }

    private static JsonElement Read(string path)
    {
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
        return d.RootElement.Clone();
    }

    private static Dictionary<(string Node, string Wire), Complex> Volts(JsonElement doc)
    {
        var map = new Dictionary<(string, string), Complex>();
        foreach (JsonElement row in doc.GetProperty("tables").GetProperty("voltages").EnumerateArray())
            map[(row.GetProperty("node").GetString()!, row.GetProperty("wire").GetString()!)] = new Complex(row.GetProperty("v_re").GetDouble(), row.GetProperty("v_im").GetDouble());
        return map;
    }

    private static int BusId(MultiPhaseFeeder f, string node)
    {
        foreach (KeyValuePair<int, string> kv in f.BusNames) if (kv.Value == node) return kv.Key;
        return int.Parse(node, Inv);
    }

    private static MeshedResult Core(MultiPhaseFeeder f, double? switchZ, double tol, int maxIter, out FeederAdapterResult ad)
    {
        ad = MultiPhaseFeederAdapter.FromFeeder(f, new FeederAdapterOptions { SwitchZPu = switchZ });
        Assert.True(ad.Frame is not null, string.Join("; ", ad.Unsupported));
        return Newton3Ph.Solve(ad.Frame!.Build(ad.Frame.InitialTaps()),
            new MeshedOptions { ToleranceAmps = tol, MaxIterations = maxIter, Floating = ad.NeedsPinnedReference ? FloatingPolicy.PinFirstWire : FloatingPolicy.Refuse });
    }

    private static void SameBits(MeshedResult core, Dictionary<(string Node, string Wire), Complex> cli)
    {
        Assert.Equal(core.Voltages.Count, cli.Count);
        foreach (WireVoltage v in core.Voltages)
        {
            Complex c = cli[(v.Node, v.Wire)];
            Assert.True(BitConverter.DoubleToInt64Bits(v.V.Real) == BitConverter.DoubleToInt64Bits(c.Real)
                        && BitConverter.DoubleToInt64Bits(v.V.Imaginary) == BitConverter.DoubleToInt64Bits(c.Imaginary), $"{v.Node}.{v.Wire}: core {v.V} vs CLI {c}");
        }
    }

    [Fact]
    public void The_IEEE_123_node_feeder_matches_the_published_voltages_and_the_core_result_bit_for_bit()
    {
        string o = Path.Combine(_dir, "out.json");
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph-mesh", "--from-dist3ph", DataPath("ieee123.json"), "--switch-z-pu", "1e-6", "--tol", "1e-6", "--max-iter", "60", "--json", o);
        Assert.True(r.ExitCode == 0, r.ToString());
        JsonElement doc = Read(o);
        Assert.True(doc.GetProperty("summary").GetProperty("all_converged").GetBoolean());
        Assert.True(doc.GetProperty("summary").GetProperty("lifted_from_dist3ph").GetBoolean());

        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(DataPath("ieee123.json"));
        MeshedResult core = Core(f, 1e-6, 1e-6, 60, out FeederAdapterResult ad);
        Assert.Equal(4, ad.Frame!.Regulators.Count);
        int legs = ad.Frame.Regulators.Sum(g => g.Connection == RegulatorConnection.Wye ? g.Phases.Count : g.Connection == RegulatorConnection.OpenDelta ? 2 : 3);
        Assert.Equal(legs, doc.GetProperty("tables").GetProperty("regulators").GetArrayLength());
        var cli = Volts(doc);
        SameBits(core, cli);

        // The published profile (the Core test's own bounds: 6e-4 pu and 0.06 degree).
        double worstMag = 0, worstAng = 0;
        int compared = 0;
        foreach (string[] row in File.ReadAllLines(DataPath("ieee123.published-voltages.csv")).Where(l => l.Length > 0 && !l.StartsWith('#')).Skip(1).Select(l => l.Split(',')))
        {
            int id = BusId(f, row[0]);
            string wire = row[1].Substring(0, 1);
            Complex v = cli[(MultiPhaseFrameAdapter.Name(id), wire)] / f.VoltBaseLN(id);
            worstMag = Math.Max(worstMag, Math.Abs(v.Magnitude - double.Parse(row[2], Inv)));
            worstAng = Math.Max(worstAng, Math.Abs(Math.IEEERemainder(v.Phase * 180.0 / Math.PI - double.Parse(row[3], Inv), 360)));
            compared++;
        }
        Assert.Equal(268, compared);
        Assert.True(worstMag < 6e-4 && worstAng < 0.06, $"vs published: {worstMag:E3} pu, {worstAng:F4} deg");
    }

    [Fact]
    public void A_delta_secondary_feeder_is_pinned_and_noted_and_matches_core_bit_for_bit()
    {
        string o = Path.Combine(_dir, "yd.json");
        CliProcess.Result r = CliProcess.RunBuilt("dist3ph-mesh", "--from-dist3ph", DataPath("ieee4-yD-stepdown-unbalanced.json"), "--tol", "1e-7", "--max-iter", "60", "--json", o);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("first wire is pinned", r.StdOut, StringComparison.Ordinal);
        MeshedResult core = Core(MultiPhaseJsonLoader.Load(DataPath("ieee4-yD-stepdown-unbalanced.json")), null, 1e-7, 60, out FeederAdapterResult ad);
        Assert.True(ad.NeedsPinnedReference);
        SameBits(core, Volts(Read(o)));
    }

    [Fact]
    public void Two_runs_give_identical_bytes()
    {
        string o = Path.Combine(_dir, "d.json");
        CliProcess.Result a = CliProcess.RunBuilt("dist3ph-mesh", "--from-dist3ph", DataPath("ieee4-gyGy-stepdown-unbalanced.json"), "--json", o);
        byte[] first = File.ReadAllBytes(o);
        File.Delete(o);   // the same result path both times: the file records where it was written
        CliProcess.Result b = CliProcess.RunBuilt("dist3ph-mesh", "--from-dist3ph", DataPath("ieee4-gyGy-stepdown-unbalanced.json"), "--json", o);
        Assert.Equal(0, a.ExitCode);
        Assert.Equal(a.StdOut, b.StdOut);
        Assert.Equal(first, File.ReadAllBytes(o));
    }

    [Fact]
    public void Refusals_name_what_is_wrong_and_use_the_documented_exit_codes()
    {
        string mesh = Path.Combine(_dir, "mesh.json");
        File.WriteAllText(mesh, """{"schema":"gridsim-dist3ph-mesh/1","name":"x","nodes":[{"name":"S","wires":["a"]}],"sources":[{"node":"S","wires":["a"],"magnitude_v":[1],"angle_deg":[0]}]}""");
        string feeder = DataPath("ieee4-gyGy-stepdown-unbalanced.json");
        Assert.Equal(2, CliProcess.RunBuilt("dist3ph-mesh", mesh, "--from-dist3ph", feeder).ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("dist3ph-mesh", mesh, "--switch-z-pu", "1e-6").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("dist3ph-mesh", "--from-dist3ph", feeder, "--switch-z-pu", "0").ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("dist3ph-mesh", "--from-dist3ph", Path.Combine(_dir, "nothere.json")).ExitCode);

        // The 123-node feeder holds closed switches; without --switch-z-pu the adapter names them and nothing is solved.
        CliProcess.Result sw = CliProcess.RunBuilt("dist3ph-mesh", "--from-dist3ph", DataPath("ieee123.json"));
        Assert.Equal(1, sw.ExitCode);
        Assert.Contains("closed switch", sw.StdErr, StringComparison.Ordinal);
        Assert.Contains("cannot be lifted", sw.StdErr, StringComparison.Ordinal);
    }
}
