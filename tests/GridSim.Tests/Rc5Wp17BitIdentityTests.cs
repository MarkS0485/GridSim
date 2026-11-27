using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Model;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Protection.Placed;
using GridSim.Core.Protection.ShortCircuit;
using GridSim.Core.Protection.Workflow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP17 bit-identity: device files without the new distance members give exactly the output they gave before WP17.
/// Method: this file was written and built against the 2.1.112 binaries (commit 611801b0, before any WP17 edit), run once
/// to read each SHA-256 below, and the values were pinned; the same tests then ran unchanged against the WP17 build.
/// The hashed text is:
/// <list type="bullet">
/// <item>Core: <see cref="PlacedElementEvaluator.ToCsv"/> of the WP4 fixtures (the Rc5Wp4PlacedProtectionTests T-cases,
/// rebuilt here) over a list of faults and every fault type, and a canonical text of <see cref="PlacedCoordination.Run"/>
/// (every pair, check and not-evaluated entry, numbers in round-trip form) on the same fixtures.</item>
/// <item>CLI: protection-eval, protection-grade and sc-trace on case9 with the WP4 device file: the console lines (less
/// the "wrote" line, which names a temporary path) and the JSON result's summary and tables (the header carries the
/// version, the commit and the temporary paths, so it is left out).</item>
/// </list>
/// </summary>
public sealed class Rc5Wp17BitIdentityTests
{
    private const double Kv = 132.0, Mva = 100.0, OhmPerPu = 174.24 / 3.0;
    private static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    private static void Pin(string expected, string actual) => Assert.True(expected == actual, "pinned " + expected + ", got " + actual);
    private static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static Generator Gen(int bus, bool zeroSeq = false) => new(bus, 0.0, Vg: 1.0, MBase: 100.0, SubtransientXdPu: 0.2, NegativeSeqXPu: 0.2)
    {
        ZeroSequenceZPu = zeroSeq ? new MachineZeroSequence(0.0, 0.05) : null,
    };

    private static GridModel Model(Branch[] branches, int buses, params Generator[] gens) => new()
    {
        Name = "wp4",
        BaseMva = Mva,
        Buses = Enumerable.Range(1, buses).Select(i => new Bus(i, i == 1 ? BusType.Slack : gens.Any(g => g.Bus == i) ? BusType.Pv : BusType.Pq, BaseKv: Kv)).ToArray(),
        Generators = gens,
        Branches = branches,
    };

    private const string CtVt = "\"ct\": { \"primaryA\": 400, \"secondaryA\": 1 }, \"vt\": { \"primaryV\": 132000, \"secondaryV\": 110 }";

    private static string At(int branch, int from, int to, string end) =>
        "\"branch\": { \"index\": " + branch + ", \"from\": " + from + ", \"to\": " + to + ", \"end\": \"" + end + "\" }";

    private static string Zone(double reach, double angle, double t, string extra = "") =>
        "{ \"reachOhms\": " + N(reach) + ", \"angleDeg\": " + N(angle) + ", \"timeS\": " + N(t) + extra + " }";

    private static string Distance(string id, string at, string loops, params string[] zones) =>
        "{ \"id\": \"" + id + "\", \"type\": \"relay\", \"function\": \"21\", " + at + ", " + CtVt
        + ", \"distance\": { \"characteristic\": \"mho\", \"loops\": \"" + loops + "\", \"zones\": [ " + string.Join(", ", zones) + " ] } }";

    private static string Earth(string id, string function, string at, string body) =>
        "{ \"id\": \"" + id + "\", \"type\": \"relay\", \"function\": \"" + function + "\", " + at + ", " + CtVt + ", " + body + " }";

    private static string Dir67(string id, string at, string pol, double rca, string direction = "forward") =>
        Earth(id, "67", at, "\"shots\": [ { \"curve\": \"iec-si\", \"pickupA\": 0.2, \"timeDial\": 0.1 } ], \"directional\": { \"polarisation\": \"" + pol
            + "\", \"characteristicAngleDeg\": " + N(rca) + ", \"direction\": \"" + direction + "\" }");

    private static string Doc(params string[] devices) => "{ \"manifestVersion\": \"gridsim-devices/1\", \"devices\": [ " + string.Join(", ", devices) + " ] }";

    private static BoundDeviceSet Bind(GridModel m, params string[] devices) =>
        DeviceSetValidator.Bind(DeviceSet.Parse(Encoding.UTF8.GetBytes(Doc(devices))), m, new CurveLibrary());

    private static readonly System.Numerics.Complex ZLsec = new System.Numerics.Complex(0.04, 0.4) * OhmPerPu;
    private static readonly double ThetaL = Math.Atan2(0.4, 0.04) * 180.0 / Math.PI;

    private static GridModel T1() => Model(new[] { new Branch(1, 2, R: 0.04, X: 0.4) }, 2, Gen(1));
    private static GridModel T2() => Model(new[] { new Branch(1, 2, R: 0.0, X: 0.4, X0: 1.2), new Branch(2, 3, R: 0.0, X: 0.4, X0: 1.2) }, 3, Gen(1, zeroSeq: true));
    private static GridModel T3() => Model(new[] { new Branch(1, 2, R: 0.0, X: 0.4) }, 2, Gen(1), Gen(2));
    private static GridModel T4() => Model(new[] { new Branch(1, 2, R: 0.0, X: 0.2, X0: 0.6), new Branch(2, 3, R: 0.0, X: 0.4, X0: 1.2) }, 3, Gen(1, zeroSeq: true));

    private const string Diff = "{ \"id\": \"L1\", \"type\": \"relay\", \"function\": \"87\", \"branch\": { \"index\": 0, \"from\": 1, \"to\": 2, \"end\": \"from\" }, "
        + "\"ct\": { \"primaryA\": 400, \"secondaryA\": 1 }, \"differential\": { \"is1A\": 0.2, \"k1\": 0.3, \"is2A\": 2.0, \"k2\": 1.5 } }";

    private static IEnumerable<(BoundDeviceSet Set, FaultSpec[] Faults)> Fixtures()
    {
        string quad = "{ \"id\": \"Q\", \"type\": \"relay\", \"function\": \"21\", " + At(0, 1, 2, "from") + ", " + CtVt
            + ", \"distance\": { \"characteristic\": \"quad\", \"loops\": \"both\", \"zones\": [ "
            + Zone(0.8 * ZLsec.Magnitude, ThetaL, 0.0, ", \"resistiveReachOhms\": 10") + " ] } }";
        yield return (Bind(T1(),
            Distance("R1", At(0, 1, 2, "from"), "both", Zone(0.8 * ZLsec.Magnitude, ThetaL, 0.0), Zone(1.2 * ZLsec.Magnitude, ThetaL, 0.4), Zone(2.0 * ZLsec.Magnitude, ThetaL, 1.0)),
            Dir67("F", At(0, 1, 2, "from"), "quadrature", 45.0), quad),
            new[] { FaultSpec.AtBus(2), FaultSpec.AlongBranch(0, 0.5), FaultSpec.AlongBranch(0, 0.85) });
        double zl = 0.4 * OhmPerPu;
        yield return (Bind(T2(),
            Earth("E1", "50N", At(0, 1, 2, "from"), "\"instantaneous\": { \"pickupA\": 1.0, \"delayS\": 0.05 }"),
            Earth("E2", "51N", At(0, 1, 2, "from"), "\"shots\": [ { \"curve\": \"iec-si\", \"pickupA\": 0.2, \"timeDial\": 0.1 } ]"),
            Dir67("N1", At(0, 1, 2, "from"), "zero-sequence", -60.0), Dir67("N2", At(1, 2, 3, "from"), "zero-sequence", -60.0),
            Dir67("N3", At(0, 1, 2, "to"), "zero-sequence", -60.0), Dir67("Q2", At(0, 1, 2, "from"), "negative-sequence", -60.0),
            Distance("D1", At(0, 1, 2, "from"), "both", Zone(0.8 * zl, 90.0, 0.0), Zone(1.2 * zl, 90.0, 0.4), Zone(2.0 * zl, 90.0, 1.0)),
            Distance("D2", At(0, 1, 2, "to"), "earth", Zone(0.8 * zl, 90.0, 0.0))),
            new[] { FaultSpec.AtBus(2), FaultSpec.AtBus(3), FaultSpec.AlongBranch(0, 0.3), FaultSpec.AlongBranch(1, 0.6) });
        yield return (Bind(T3(), Diff), new[] { FaultSpec.AtBus(2), FaultSpec.AlongBranch(0, 0.5) });
        yield return (Bind(T4(),
            Distance("A", At(1, 2, 3, "from"), "both", Zone(0.8 * 0.4 * OhmPerPu, 90.0, 0.0), Zone(1.2 * 0.4 * OhmPerPu, 90.0, 0.4)),
            Distance("B", At(0, 1, 2, "from"), "both", Zone(0.8 * 0.2 * OhmPerPu, 90.0, 0.0), Zone(0.6 * OhmPerPu, 90.0, 0.5)),
            Earth("SA", "51N", At(1, 2, 3, "from"), "\"shots\": [ { \"curve\": \"iec-si\", \"pickupA\": 0.2, \"timeDial\": 0.05 } ]"),
            Earth("SB", "51N", At(0, 1, 2, "from"), "\"shots\": [ { \"curve\": \"iec-si\", \"pickupA\": 0.2, \"timeDial\": 0.2 } ]")),
            new[] { FaultSpec.AtBus(3), FaultSpec.AlongBranch(1, 0.5), FaultSpec.AtBus(2) });
    }

    private static readonly FaultType[] Types = { FaultType.ThreePhase, FaultType.SingleLineToGround, FaultType.LineToLine, FaultType.DoubleLineToGround };

    private static string CoreEvalText()
    {
        var sb = new StringBuilder();
        foreach ((BoundDeviceSet b, FaultSpec[] faults) in Fixtures())
            foreach (FaultSpec f in faults)
                foreach (FaultType t in Types)
                {
                    FaultPhasors ph = FaultPhasors.Build(b.Model, f, t);
                    sb.Append(f.Label).Append(' ').Append(FaultPhasors.TypeName(t)).Append('\n');
                    sb.Append(ph.NotEvaluated is null ? PlacedElementEvaluator.ToCsv(PlacedElementEvaluator.Evaluate(b, ph)) : "not evaluated: " + ph.NotEvaluated + "\n");
                }
        return sb.ToString();
    }

    private static string CoreGradeText()
    {
        var sb = new StringBuilder();
        foreach ((BoundDeviceSet b, FaultSpec[] _) in Fixtures())
        {
            PlacedCoordinationResult r = PlacedCoordination.Run(b, null, Types, 0.4);
            sb.Append(r.Verdict).Append('\n');
            foreach (PlacedPairRow p in r.Pairs)
                sb.Append(p.Fault).Append(',').Append(p.PrimaryId).Append(',').Append(p.BackupId).Append(',').Append(N(p.PrimaryTimeS)).Append(',').Append(N(p.BackupTimeS))
                  .Append(',').Append(N(p.MarginS)).Append(',').Append(p.Evaluated).Append(',').Append(p.Coordinated).Append(',').Append(p.Reason).Append('\n');
            foreach (PlacedCheck c in r.Checks)
                sb.Append(c.Check).Append(',').Append(c.DeviceId).Append(',').Append(c.Passed).Append(',').Append(N(c.Value)).Append(',').Append(c.Detail).Append('\n');
            foreach (NotEvaluatedCheck n in r.NotEvaluated) sb.Append(n.Subject).Append(',').Append(n.Check).Append(',').Append(n.Reason).Append('\n');
            foreach (PlacedFaultResult f in r.Faults)
                sb.Append(f.Label).Append('\n').Append(f.NotEvaluated is null ? PlacedElementEvaluator.ToCsv(f.Rows) : f.NotEvaluated + "\n");
        }
        return sb.ToString();
    }

    [Fact]
    public void Core_placed_evaluation_and_grading_are_byte_identical_to_2_1_112()
    {
        // ANA-02 (re-pinned, 2.1.161): the only rows that differ are D2's earth loops at its own bus (2.1.160 pinned an
        // apparent impedance of 2.4e-15 and 7.1e-15 ohm, rounding noise, the second of them inside zone 1, so a bus fault
        // behind the relay tripped it). A collapsed loop now takes the memory-polarised direction (reverse) and Z = 0. Every
        // other row is byte for byte the 2.1.160 text (the old hashes reproduce from the old evaluator). R5-01: asserted here:
        // with the "close-in fault" rows taken out, each text hashes to the 2.1.160 text with the same rows taken out (the
        // two "rows removed" hashes are constants recorded once from the 2.1.160 evaluator, not recomputed here).
        string eval = CoreEvalText(), grade = CoreGradeText();
        Pin("d65622340d61a28d48a1d6ed527e2270c120ceee3f89b13d823b1ede7b613375", Sha(eval));
        Pin("b3111f9e85d2defb7f187469e8959428c94032f5643a581635c380dfb526bfcf", Sha(grade));
        static string Rest(string s) => string.Join("\n", s.Split('\n').Where(l => !l.Contains("close-in fault", StringComparison.Ordinal)));
        Pin("1033c0c62bab1d4d6a278a70e987307d571e850f53e9a61a71e759bca91521ad", Sha(Rest(eval)));
        Pin("b4e87ca7577906ecd92d244e1502834bf37f53106c49afca210b199082135b56", Sha(Rest(grade)));
    }

    private const string Case9Devices = """
    { "manifestVersion": "gridsim-devices/1", "devices": [
      { "id": "Z45", "type": "relay", "function": "21", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
        "ct": { "primaryA": 1000, "secondaryA": 1 }, "vt": { "primaryV": 345000, "secondaryV": 110 },
        "distance": { "characteristic": "mho", "zones": [ { "reachOhms": 10, "angleDeg": 80, "timeS": 0 }, { "reachOhms": 20, "angleDeg": 80, "timeS": 0.4 } ] } },
      { "id": "D45", "type": "relay", "function": "87", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
        "ct": { "primaryA": 1000, "secondaryA": 1 }, "differential": { "is1A": 0.2, "k1": 0.3, "is2A": 2.0, "k2": 1.5 } },
      { "id": "O45", "type": "relay", "branch": { "index": 1, "from": 4, "to": 5, "end": "from" },
        "ct": { "primaryA": 1000, "secondaryA": 1 }, "shots": [ { "curve": "iec-si", "pickupA": 0.5, "timeDial": 0.1 } ] } ] }
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
    public void Cli_protection_verbs_are_byte_identical_to_2_1_112()
    {
        string dir = TestTemp.Under("gridsim-wp17-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dev = Path.Combine(dir, "devices.json");
        File.WriteAllText(dev, Case9Devices);
        string[][] runs =
        {
            new[] { "protection-eval", "case9", "--devices", dev, "--branch", "1", "--fraction", "0.5" },
            new[] { "protection-eval", "case9", "--devices", dev, "--bus", "5", "--type", "ll" },
            new[] { "protection-eval", "case9", "--devices", dev, "--bus", "5", "--type", "slg", "--xf", "0.01" },
            new[] { "protection-grade", "case9", "--devices", dev, "--types", "3ph,slg" },
            new[] { "sc-trace", "case9", "--devices", dev, "--bus", "5" },
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
        Pin("d93cee4ad29de0588016bc8a4d8bd34952562d7fddd9c035e3ea67719adaac18", Sha(console.ToString()));
        Pin("4f06cf9dbf94eea6453e069a982ff958ca8c7b51ad1cf1710e49c5d5e5806bf6", Sha(json.ToString()));
    }
}
