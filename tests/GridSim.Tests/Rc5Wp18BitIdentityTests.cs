using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Model;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Placed;
using GridSim.Core.Protection.Workflow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP18 bit-identity: device files without the new 87T and 87B members give exactly the output they gave before WP18,
/// and the old refusals keep their words. Method: this file was written and built against the 2.1.113 binaries (commit
/// c32c5ad9, before any WP18 edit), run once to read each SHA-256 below, and the values were pinned; the same tests then
/// ran unchanged against the WP18 build. The 2.1.113 Release binaries the pins came from had SHA-256 9cbeb509...e80788
/// (GridSim.Core.dll) and fd562959...d99f59d (gridsim.dll). The hashed text is:
/// <list type="bullet">
/// <item>Core: <see cref="PlacedElementEvaluator.ToCsv"/> on a case with a transformer and a line (87L, 51 and a WP17 21 on
/// the transformer) over bus and line faults of every type, and a canonical text of <see cref="PlacedCoordination.Run"/>
/// on the same set.</item>
/// <item>The refusal messages for a device file that gives an 87 (or a 21 without onTransformer) on a transformer, and an
/// 87 with no differential setting.</item>
/// <item>CLI: protection-eval and protection-grade on case9 with a line 87L, a 51 and a 21 (console lines less the "wrote"
/// line, plus the JSON result's exit code, summary and tables), and an 87 on case9's branch 0 (1-4). MATPOWER case9 marks
/// no branch as a transformer, so 2.1.113 evaluates that device as an 87L; its exit code and console text are pinned.</item>
/// </list>
/// The WP4 and WP17 fixtures are already pinned by Rc5Wp17BitIdentityTests, which runs unchanged.
/// </summary>
public sealed class Rc5Wp18BitIdentityTests
{
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static void Pin(string expected, string actual) => Assert.True(expected == actual, "pinned " + expected + ", got " + actual);
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static Generator Gen(int bus) => new(bus, 0.0, Vg: 1.0, MBase: 100.0, SubtransientXdPu: 0.2, NegativeSeqXPu: 0.2);

    // Bus 1 (132 kV, source) - transformer 1-2 (YNd, 132/33 kV) - bus 2 (33 kV) - line 2-3 - bus 3 (33 kV, load).
    private static GridModel Case() => new()
    {
        Name = "wp18pin",
        BaseMva = 100.0,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: 132.0), new Bus(2, BusType.Pq, BaseKv: 33.0), new Bus(3, BusType.Pq, Pd: 20.0, Qd: 5.0, BaseKv: 33.0),
        },
        Generators = new[] { Gen(1) },
        Branches = new[]
        {
            new Branch(1, 2, R: 0.0, X: 0.2, IsTransformer: true, FromWinding: WindingConnection.WyeGrounded, ToWinding: WindingConnection.Delta),
            new Branch(2, 3, R: 0.02, X: 0.1, X0: 0.3),
        },
    };

    private const string Devices = """
    { "manifestVersion": "gridsim-devices/1", "devices": [
      { "id": "L23", "type": "relay", "function": "87", "branch": { "index": 1, "from": 2, "to": 3, "end": "from" },
        "ct": { "primaryA": 800, "secondaryA": 1 }, "differential": { "is1A": 0.2, "k1": 0.3, "is2A": 2.0, "k2": 1.5 } },
      { "id": "O12", "type": "relay", "branch": { "index": 0, "from": 1, "to": 2, "end": "from" },
        "ct": { "primaryA": 400, "secondaryA": 1 }, "shots": [ { "curve": "iec-si", "pickupA": 0.5, "timeDial": 0.1 } ] },
      { "id": "Z12", "type": "relay", "function": "21", "branch": { "index": 0, "from": 1, "to": 2, "end": "from" },
        "ct": { "primaryA": 400, "secondaryA": 1 }, "vt": { "primaryV": 132000, "secondaryV": 110 },
        "distance": { "characteristic": "mho", "loops": "phase", "zones": [ { "reachOhms": 10, "angleDeg": 85, "timeS": 0 } ], "onTransformer": true } } ] }
    """;

    private static readonly FaultType[] Types = { FaultType.ThreePhase, FaultType.SingleLineToGround, FaultType.LineToLine, FaultType.DoubleLineToGround };

    private static BoundDeviceSet Bound() => DeviceSetValidator.Bind(DeviceSet.Parse(Encoding.UTF8.GetBytes(Devices)), Case(), new CurveLibrary());

    private static string CoreText()
    {
        BoundDeviceSet b = Bound();
        var sb = new StringBuilder();
        foreach (FaultSpec f in new[] { FaultSpec.AtBus(1), FaultSpec.AtBus(2), FaultSpec.AtBus(3), FaultSpec.AlongBranch(1, 0.5) })
            foreach (FaultType t in Types)
            {
                FaultPhasors ph = FaultPhasors.Build(b.Model, f, t);
                sb.Append(f.Label).Append(' ').Append(FaultPhasors.TypeName(t)).Append('\n');
                sb.Append(ph.NotEvaluated is null ? PlacedElementEvaluator.ToCsv(PlacedElementEvaluator.Evaluate(b, ph)) : "not evaluated: " + ph.NotEvaluated + "\n");
            }
        PlacedCoordinationResult r = PlacedCoordination.Run(b, null, Types, 0.3);
        sb.Append(r.Verdict).Append('\n');
        foreach (PlacedPairRow p in r.Pairs)
            sb.Append(p.Fault).Append(',').Append(p.PrimaryId).Append(',').Append(p.BackupId).Append(',').Append(N(p.PrimaryTimeS)).Append(',').Append(N(p.BackupTimeS))
              .Append(',').Append(N(p.MarginS)).Append(',').Append(p.Evaluated).Append(',').Append(p.Coordinated).Append(',').Append(p.Reason).Append('\n');
        foreach (PlacedCheck c in r.Checks)
            sb.Append(c.Check).Append(',').Append(c.DeviceId).Append(',').Append(c.Passed).Append(',').Append(N(c.Value)).Append(',').Append(c.Detail).Append('\n');
        foreach (NotEvaluatedCheck n in r.NotEvaluated) sb.Append(n.Subject).Append(',').Append(n.Check).Append(',').Append(n.Reason).Append('\n');
        foreach (PlacedFaultResult f in r.Faults)
            sb.Append(f.Label).Append('\n').Append(f.NotEvaluated is null ? PlacedElementEvaluator.ToCsv(f.Rows) : f.NotEvaluated + "\n");
        return sb.ToString();
    }

    private static string Refusal(string devices)
    {
        try
        {
            DeviceSetValidator.Bind(DeviceSet.Parse(Encoding.UTF8.GetBytes(devices)), Case(), new CurveLibrary());
            return "bound";
        }
        catch (DeviceSetException ex) { return ex.Message; }
    }

    /// <summary>The 2.1.113 refusal of an 87 with a differential setting on a transformer, word for word.</summary>
    public const string Old87TransformerRefusal = "Device 'T1': a 87 on a transformer needs vector-group compensation; out of scope.";

    internal const string Diff87OnTransformer = """
    { "manifestVersion": "gridsim-devices/1", "devices": [
      { "id": "T1", "type": "relay", "function": "87", "branch": { "index": 0, "from": 1, "to": 2, "end": "from" },
        "ct": { "primaryA": 400, "secondaryA": 1 }, "differential": { "is1A": 0.2, "k1": 0.3, "is2A": 2.0, "k2": 1.5 } } ] }
    """;

    private static string RefusalText()
    {
        string noDiff = """
        { "manifestVersion": "gridsim-devices/1", "devices": [
          { "id": "T2", "type": "relay", "function": "87", "branch": { "index": 0, "from": 1, "to": 2, "end": "from" }, "ct": { "primaryA": 400, "secondaryA": 1 } } ] }
        """;
        string dist = """
        { "manifestVersion": "gridsim-devices/1", "devices": [
          { "id": "T3", "type": "relay", "function": "21", "branch": { "index": 0, "from": 1, "to": 2, "end": "from" },
            "ct": { "primaryA": 400, "secondaryA": 1 }, "vt": { "primaryV": 132000, "secondaryV": 110 },
            "distance": { "characteristic": "mho", "zones": [ { "reachOhms": 10, "angleDeg": 85, "timeS": 0 } ] } } ] }
        """;
        return Refusal(Diff87OnTransformer) + "\n" + Refusal(noDiff) + "\n" + Refusal(dist) + "\n";
    }

    [Fact]
    public void Core_evaluation_grading_and_refusals_are_byte_identical_to_2_1_113()
    {
        Assert.Equal(Old87TransformerRefusal, Refusal(Diff87OnTransformer));
        // ANA-02 (re-pinned, 2.1.161; proof corrected and asserted, R5-01): only Z12's phase loops at a bolted fault on its
        // own bus (bus 1) differ. Their loop voltage is double rounding (about 1.9e-16 pu, asserted below) and so is their
        // current: nothing feeds the fault from the bus 2 side, so the current into the transformer is a residue of about
        // 3.7e-10 pu. The 2.1.160 pin was the ratio of the two, 1.7e-5 to 5.2e-5 ohm secondary (about 5e-7 pu), one of them
        // inside zone 1. Those rows are now reverse by the memory voltage, and every other row is the 2.1.160 text: with the
        // "close-in fault" rows taken out, the text hashes to the 2.1.160 text with the same rows taken out (1bca336c...,
        // a constant recorded once from the 2.1.160 evaluator, not recomputed here).
        string text = CoreText();
        Pin("aa2f2e70fc6ca098e3aa22bd262e7efc1484831207ee9b307e43f8853b97bac8", Sha(text));
        Pin("1bca336c4ef6e650b5814369e8cdf3a09b86d634fac03889a10b53a6114f0aba",
            Sha(string.Join("\n", text.Split('\n').Where(l => !l.Contains("close-in fault", StringComparison.Ordinal)))));
        Assert.Equal(6, text.Split('\n').Count(l => l.Contains("close-in fault", StringComparison.Ordinal)));
        foreach (FaultType t in Types)
        {
            FaultPhasors ph = FaultPhasors.Build(Case(), FaultSpec.AtBus(1), t);
            Assert.True(ph.TryBus(1, out int bi));
            (System.Numerics.Complex va, System.Numerics.Complex vb, System.Numerics.Complex vc) = ph.PhaseVoltages(bi);
            (System.Numerics.Complex ia, System.Numerics.Complex ib, System.Numerics.Complex ic) = ph.PhaseCurrents(0, true);
            foreach ((System.Numerics.Complex v, System.Numerics.Complex i) in new[] { (va - vb, ia - ib), (vb - vc, ib - ic), (vc - va, ic - ia) })
                if (i.Magnitude < 1e-8)   // a loop with no real current: its voltage is rounding, far below the threshold
                    Assert.True(v.Magnitude <= 1e-12, $"{t}: loop voltage {v.Magnitude:R} pu with residual current {i.Magnitude:R} pu");
        }
        Pin("7e74d0cc9dcea3b9255734edc3e399e873ed5cc642bd39abc6e88cd4fddd639b", Sha(RefusalText()));
    }

    private const string Case9Devices = """
    { "manifestVersion": "gridsim-devices/1", "devices": [
      { "id": "Z89", "type": "relay", "function": "21", "branch": { "index": 7, "from": 8, "to": 9, "end": "from" },
        "ct": { "primaryA": 1000, "secondaryA": 1 }, "vt": { "primaryV": 345000, "secondaryV": 110 },
        "distance": { "characteristic": "mho", "zones": [ { "reachOhms": 10, "angleDeg": 80, "timeS": 0 }, { "reachOhms": 20, "angleDeg": 80, "timeS": 0.4 } ] } },
      { "id": "D89", "type": "relay", "function": "87", "branch": { "index": 7, "from": 8, "to": 9, "end": "to" },
        "ct": { "primaryA": 1000, "secondaryA": 1 }, "differential": { "is1A": 0.2, "k1": 0.3, "is2A": 2.0, "k2": 1.5 } },
      { "id": "O89", "type": "relay", "branch": { "index": 7, "from": 8, "to": 9, "end": "from" },
        "ct": { "primaryA": 1000, "secondaryA": 1 }, "shots": [ { "curve": "iec-si", "pickupA": 0.5, "timeDial": 0.1 } ] } ] }
    """;

    private const string Case9Transformer87 = """
    { "manifestVersion": "gridsim-devices/1", "devices": [
      { "id": "T14", "type": "relay", "function": "87", "branch": { "index": 0, "from": 1, "to": 4, "end": "from" },
        "ct": { "primaryA": 1000, "secondaryA": 1 }, "differential": { "is1A": 0.2, "k1": 0.3, "is2A": 2.0, "k2": 1.5 } } ] }
    """;

    private static string ConsoleText(string stdout) =>
        string.Join("\n", stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => !l.Contains("] wrote ", StringComparison.Ordinal)));

    private static string JsonBody(string path)
    {
        using JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(path));
        return d.RootElement.GetProperty("exitCode").GetRawText() + "\n" + Rc5LineEndings.ToCrLf(d.RootElement.GetProperty("summary").GetRawText()) + "\n"
            + Rc5LineEndings.ToCrLf(d.RootElement.GetProperty("tables").GetRawText());
    }

    [Fact]
    public void Cli_protection_verbs_are_byte_identical_to_2_1_113()
    {
        string dir = TestTemp.Under("gridsim-wp18-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dev = Path.Combine(dir, "devices.json");
        File.WriteAllText(dev, Case9Devices);
        string bad = Path.Combine(dir, "t87.json");
        File.WriteAllText(bad, Case9Transformer87);
        string[][] runs =
        {
            new[] { "protection-eval", "case9", "--devices", dev, "--branch", "7", "--fraction", "0.3" },
            new[] { "protection-eval", "case9", "--devices", dev, "--bus", "9", "--type", "llg" },
            new[] { "protection-grade", "case9", "--devices", dev, "--types", "3ph,ll" },
        };
        var console = new StringBuilder();
        var json = new StringBuilder();
        for (int k = 0; k < runs.Length; k++)
        {
            string jp = Path.Combine(dir, $"r{k}.json");
            CliProcess.Result r = CliProcess.RunBuilt(runs[k].Concat(new[] { "--json", jp }).ToArray());
            console.Append(r.ExitCode.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(ConsoleText(r.StdOut)).Append('\n');
            json.Append(JsonBody(jp)).Append('\n');
        }
        CliProcess.Result refused = CliProcess.RunBuilt("protection-eval", "case9", "--devices", bad, "--bus", "4");
        string refusal = refused.ExitCode.ToString(CultureInfo.InvariantCulture) + "\n" + ConsoleText(refused.StdOut) + "\n"
            + refused.StdErr.Replace("\r\n", "\n", StringComparison.Ordinal).Replace(bad, "<file>", StringComparison.Ordinal);
        Pin("906d04557457816d65e2c340136852f02d6d861d866850b8b20934136b87e8aa", Sha(console.ToString()));
        Pin("27e8d56f30db1986af33a3e23f7462c2752e65880ccd93109b2fcbe946ef6252", Sha(json.ToString()));
        Pin("b932f340cb89791dd6ab3213e810412286cf035925dce713141b7e1cdee96225", Sha(refusal));
    }
}
