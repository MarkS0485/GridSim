using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using GridSim.Core.Analysis;
using GridSim.Core.Diagnostics;
using GridSim.Core.Events;
using GridSim.Core.IO.Matpower;
using GridSim.Core.IO.Psse;
using GridSim.Core.IO.Ucte;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Post-RC3 audit batch H (IO-05, AN-04, AN-07, AN-Q1, AN-Q2 culture half; same class as store bugs 93, 106, 193):
/// refusal and result text is written the same on every machine. Each case runs the formatting under de-DE (decimal
/// comma) and under the invariant culture, requires the two texts to be identical, and requires the '.' decimal.
/// </summary>
public sealed class PostRc3CultureHTests
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static T Under<T>(CultureInfo culture, Func<T> body)
    {
        CultureInfo old = CultureInfo.CurrentCulture, oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            return body();
        }
        finally { CultureInfo.CurrentCulture = old; CultureInfo.CurrentUICulture = oldUi; }
    }

    /// <summary>Runs <paramref name="text"/> under de-DE and the invariant culture; the two must match and match <paramref name="decimalPattern"/>.</summary>
    private static string Same(Func<string> text, string decimalPattern)
    {
        string german = Under(German, text);
        string invariant = Under(CultureInfo.InvariantCulture, text);
        Assert.Equal(invariant, german);
        Assert.Matches(decimalPattern, german);
        return german;
    }

    private static string Message(Action act)
    {
        var ex = Assert.ThrowsAny<Exception>(act);
        return ex.Message;
    }

    // ---- IO-05 -------------------------------------------------------------------------------------------------------

    [Fact]
    public void IO05_matpower_refusals_and_the_mbase_note_use_the_invariant_culture()
    {
        const string head = "function mpc = t\nmpc.version = '2';\n";
        string Body(string baseMva, string mBase, string tap = "0") =>
            head + $"mpc.baseMVA = {baseMva};\n" +
            "mpc.bus = [\n 1 3 0 0 0 0 1 1 0 100 1 1.1 0.9;\n 2 1 10 0 0 0 1 1 0 100 1 1.1 0.9;\n];\n" +
            $"mpc.gen = [\n 1 5 0 100 -100 1 {mBase} 1 100 0;\n];\n" +
            $"mpc.branch = [\n 1 2 0.01 0.1 0 100 100 100 {tap} 0 1 -360 360;\n];\n";

        Same(() => Message(() => MatpowerParser.Parse(Body("-1.5", "100"), "t")), @"mpc\.baseMVA is -1\.5;");
        Same(() => Message(() => MatpowerParser.Parse(Body("100", "-2.5"), "t")), @"has mBase -2\.5;");
        Same(() => Message(() => MatpowerParser.Parse(Body("100", "100", "-0.5"), "t")), @"has TAP -0\.5;");

        string note = Same(() =>
        {
            MatpowerParseResult r = MatpowerParser.ParseWithDiagnostics(Body("50/3", "0"), "t");
            return string.Join("|", r.Notes);
        }, @"system base \(16\.66\d+ MVA\)");
        Assert.Contains("1 generator(s) give mBase 0; their machine base is the system base (", note, StringComparison.Ordinal);
    }

    [Fact]
    public void IO05_psse_refusals_use_the_invariant_culture()
    {
        string raw = File.ReadAllText(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "case14.raw"));
        Assert.Contains("0, 100, 33", raw, StringComparison.Ordinal);
        string bad = raw.Replace("0, 100, 33", "0, -0.5, 33", StringComparison.Ordinal);
        Same(() => Message(() => PsseRawParser.Parse(bad, "c14")), @"SBASE is -0\.5;");
    }

    [Fact]
    public void IO05_ucte_refusals_use_the_invariant_culture()
    {
        string text = File.ReadAllText(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "ucte", "small.uct"));
        Assert.Contains(" 0.00000 -1000.0 500.000", text, StringComparison.Ordinal);
        string bad = text.Replace(" 0.00000 -1000.0 500.000", " -100.50 -50.250 500.000", StringComparison.Ordinal);
        Same(() => Message(() => UcteParser.Parse(bad, "small")), @"minimum 100\.5 MW above the maximum 50\.25 MW");
    }

    // ---- AN-04 -------------------------------------------------------------------------------------------------------

    private static PowerFlowResult Crafted(int[] ids, double[] vm, IReadOnlyList<LineFlow> flows) => new()
    {
        Converged = true, MaxMismatch = 1e-10,
        Iterations = 1,
        BusIds = ids,
        Vm = vm,
        VaDeg = new double[ids.Length], PInjMw = new double[ids.Length], QInjMvar = new double[ids.Length], TotalLossMw = 0, TotalLossMvar = 0,
        LineFlows = flows,
    };

    [Fact]
    public void AN04_watch_list_voltage_text_uses_the_invariant_culture()
    {
        var m = new GridModel
        {
            Name = "watch-culture",
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, Name: "plant"), new Bus(2, BusType.Pq, Name: "load") },
            Generators = new[] { new Generator(1, 0, Vg: 1.05) },
            Branches = new[] { new Branch(1, 2, 0.02, 0.2) },
        };
        PowerFlowResult r = Crafted(new[] { 1, 2 }, new[] { 0.99, 1.09 }, Array.Empty<LineFlow>());
        string text = Same(() => string.Join("|", WatchList.Assess(m, r, new WatchListOptions { TopN = 10 }).Items
            .Where(i => i.Category == WatchCategory.VoltageLimit).Select(i => i.Description).OrderBy(d => d, StringComparer.Ordinal)),
            @"at 0\.990 pu \(low against its 1\.050 pu setpoint\)");
        Assert.Contains("load at 1.090 pu (high)", text, StringComparison.Ordinal);
    }

    // ---- AN-07 -------------------------------------------------------------------------------------------------------

    [Fact]
    public void AN07_qsts_failure_reason_uses_the_invariant_culture()
    {
        GridModel m = QstsEngineTests.Feeder(oltc: false);
        var study = new QstsStudy
        {
            StepSeconds = 60, Steps = 3,
            Profiles = new[] { new QstsProfile("p", 60, new[] { 1.0, 40.0, 1.0 }) },
            Loads = new[] { new QstsLoadBinding(null, "p") },
        };
        Same(() => new QstsEngine(new NewtonRaphsonPowerFlow(), QstsEngineTests.Opt).Run(m, study).FailureReason!, @"mismatch \d\.\d{3}E[+-]\d{3} pu");
    }

    [Fact]
    public void AN07_hosting_violation_details_use_the_invariant_culture()
    {
        MethodInfo violations = typeof(HostingCapacity).GetMethod("Violations", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(violations);
        QstsResult Run(int taps, bool violating) => new()
        {
            Completed = true, FailedStep = -1, Mode = default, StepSeconds = 60,
            BusIds = new[] { 1 }, Steps = Array.Empty<QstsStepRecord>(), Operations = Array.Empty<QstsOperation>(),
            OltcNames = Array.Empty<string>(), TapOperations = new[] { taps },
            CapacitorNames = Array.Empty<string>(), CapacitorOperations = Array.Empty<int>(),
            PvNames = Array.Empty<string>(), StorageNames = Array.Empty<string>(), FinalSoc = Array.Empty<double>(),
            UnsettledSteps = 0, Energy = new QstsEnergy(0, 0, 0, 0, 0, 0, 0, 0),
            BusViolations = violating ? new[] { new QstsBusViolation(7, 1800, 0, 10, 0, 1.0612, 1.0) } : Array.Empty<QstsBusViolation>(),
            BranchViolations = violating ? new[] { new QstsBranchViolation(0, "L12", 600, 5, 112.5) } : Array.Empty<QstsBranchViolation>(),
        };
        var study = new HostingStudy { MaxMw = 10, ToleranceMw = 0.1, TapIncreasePercent = 12.5, Base = new QstsStudy { StepSeconds = 60, Steps = 1 } };
        string text = Same(() =>
        {
            var found = (List<HostingViolation>)violations.Invoke(null, new object?[] { study, Run(20, true), Run(2, false) })!;
            return string.Join("|", found.Select(v => v.Detail));
        }, @"bus 7 at 1\.0612 pu for 1800 s");
        Assert.Contains("branch L12 at 112.5 % for 600 s", text, StringComparison.Ordinal);
        Assert.Contains("(allowance 12.5 %)", text, StringComparison.Ordinal);
    }

    // ---- AN-Q1 -------------------------------------------------------------------------------------------------------

    [Fact]
    public void ANQ1_analysis_refusals_use_the_invariant_culture()
    {
        GridModel feeder = QstsEngineTests.Feeder(oltc: false);

        // Boundary limit below 0.
        var boundary = new Boundary("B1", -1.5, new[] { new BoundaryBranch("L23") });
        GridModel withBoundary = feeder.CopyWith(boundaries: new[] { boundary });
        PowerFlowResult solved = new NewtonRaphsonPowerFlow().Solve(feeder);
        Same(() => Message(() => BoundaryAnalysis.EvaluateAfterOutage(withBoundary, solved)), @"transfer limit -1\.5 MW");

        // DC-OPF: Pmin above Pmax.
        GridModel badRange = feeder.CopyWith(generators: feeder.Generators.Select(g => g with { Pmin = 50.5, Pmax = 10.5 }).ToList());
        Same(() => Message(() => DcOptimalPowerFlow.Solve(badRange)), @"Pmin 50\.5 MW above Pmax 10\.5 MW");

        // Unit commitment: a negative demand scale.
        Same(() => Message(() => UnitCommitment.Solve(feeder, new UcOptions { Periods = 2, DemandScale = new[] { 1.0, -1.5 } })),
            @"demand scale -1\.5 must be");
    }

    // ---- AN-Q2 (culture half) ---------------------------------------------------------------------------------------

    [Fact]
    public void ANQ2_event_refusals_use_the_invariant_culture()
    {
        GridEvent ev = EventCatalog.Builtin()[0];

        Same(() => (ev with { DemandEffect = new RecordedDemandEffect(-1.5, "x", ProvenanceTag.Recorded) }).DemandEffectProblem()!,
            @"mwPerHz = -1\.5 must be");
        Same(() => (ev with { DemandEffect = new RecordedDemandEffect(10.5, "x", ProvenanceTag.Recorded), PreEventDemandMw = -2.5 }).DemandEffectProblem()!,
            @"\(got -2\.5\)");
        Same(() => Message(() => EventCatalog.Validate(new[] { ev with { PreEventInertiaMwS = -1.5 } })), @"preEventInertiaMwS -1\.5 must be");
        Same(() => Message(() => EventCatalog.Validate(new[] { ev with { Losses = new[] { new PowerStep(-2.5, 100.5, "loss", ProvenanceTag.Recorded) } } })),
            @"at -2\.5 s of 100\.5 MW");
    }
}
