using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection;
using GridSim.Core.Protection.Coordination;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// DOC-4 (October 2026): the documentation review's protection, fault, IEC 60909 and grading findings (stream 4 of
/// review-progress/2026-10/DOCS-FINDINGS-TRIAGE.md), one region per D-DOC id. Every test here is new.
/// </summary>
public class Doc4ProtectionIec60909Tests
{
    private readonly ITestOutputHelper _out;

    public Doc4ProtectionIec60909Tests(ITestOutputHelper output) => _out = output;

    private static readonly string Inv = "R";

    // Bus 1: a generator (X''d 0.2 on 100 MVA). Transformer 1-2 delta (bus 1) / grounded wye (bus 2), x 0.1. Line 2-3,
    // x 0.2. The same radial hand case as EarthFaultAndIec60909CompletionTests (no load, prefault 1.0 pu everywhere).
    private static GridModel Radial() => new()
    {
        Name = "radial",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 20), new Bus(2, BusType.Pq, BaseKv: 132), new Bus(3, BusType.Pq, BaseKv: 132) },
        Generators = new[] { new Generator(1, 0, Vg: 1.0, MBase: 100, SubtransientXdPu: 0.2) },
        Branches = new[]
        {
            new Branch(1, 2, 0, 0.1, Name: "T1", IsTransformer: true, RateAMva: 100,
                FromWinding: WindingConnection.Delta, ToWinding: WindingConnection.WyeGrounded),
            new Branch(2, 3, 0, 0.2, Name: "L23", RateAMva: 100),
        },
    };

    private static PowerFlowResult Solve(GridModel m)
    {
        PowerFlowResult r = SolverSelector.CreateNewton(m).Solve(m);
        Assert.True(r.Converged);
        return r;
    }

    // =================================================================================================================
    // D-DOC-088: 21N needs a residual current (earth-fault release); a line-to-line fault has none.
    // =================================================================================================================

    [Fact]
    public void D088_earth_fault_distance_does_not_operate_on_a_line_to_line_fault()
    {
        GridModel m = Radial();
        PowerFlowResult pre = Solve(m);
        (UnbalancedFaultResult ll, ProtectionReport llRep) = ProtectionAssessment.AssessUnbalancedFault(m, pre, 3, FaultType.LineToLine);
        Assert.Equal(0.0, ll.EarthCurrentPu);                       // no residual anywhere
        Assert.DoesNotContain(llRep.Operating, v => v.Device.StartsWith("21N", StringComparison.Ordinal));
        RelayVerdict line = Assert.Single(llRep.Verdicts, v => v.Device == "21N" && v.LocationName == "L23");
        Assert.Contains("earth loops not measured", line.Basis, StringComparison.Ordinal);

        // The phase-to-phase loops (21) are what see it: still in a zone on the faulted line.
        Assert.Contains(llRep.Operating, v => v.Device.StartsWith("21-Z", StringComparison.Ordinal) && v.LocationName == "L23");

        // An earth fault still releases 21N (the existing F21 behaviour is unchanged).
        (_, ProtectionReport slgRep) = ProtectionAssessment.AssessUnbalancedFault(m, pre, 3, FaultType.SingleLineToGround);
        Assert.Contains(slgRep.Operating, v => v.Device.StartsWith("21N-Z", StringComparison.Ordinal) && v.LocationName == "L23");
    }

    [Fact]
    public void D088_the_release_setting_defaults_to_ten_percent_and_a_release_of_one_blocks_a_double_line_to_ground_fault_only_when_residual_is_small()
    {
        Assert.Equal(0.10, new ProtectionSettings().EarthDistanceResidualReleaseFraction);
        // A release above any possible ratio (|3 I0| can never exceed 3 x the largest phase current) blocks every end.
        GridModel m = Radial();
        PowerFlowResult pre = Solve(m);
        (_, ProtectionReport blocked) = ProtectionAssessment.AssessUnbalancedFault(m, pre, 3, FaultType.SingleLineToGround,
            settings: new ProtectionSettings { EarthDistanceResidualReleaseFraction = 3.1 });
        Assert.DoesNotContain(blocked.Operating, v => v.Device.StartsWith("21N", StringComparison.Ordinal));
    }

    // =================================================================================================================
    // D-DOC-091: 37 watches in-service branches only.
    // =================================================================================================================

    [Fact]
    public void D091_undercurrent_gives_no_verdict_on_an_out_of_service_branch()
    {
        GridModel c9 = IeeeCases.Case9();
        var branches = c9.Branches.ToList();
        // Branch 4 out of service: case9's ring opens at one point and stays connected; the flow entry is the zero
        // placeholder that keeps the indices aligned.
        branches[4] = branches[4] with { InService = false };
        GridModel m = c9.CopyWith(branches: branches);
        PowerFlowResult r = Solve(m);
        Assert.Equal(0.0, r.LineFlows[4].PFromMw);
        ProtectionReport rep = ProtectionAssessment.Assess(m, r);
        Assert.DoesNotContain(rep.Verdicts, v => v.Device == "37" && v.LocationKind == ProtectionLocation.Branch && v.LocationId == 4);
        Assert.DoesNotContain(rep.Operating, v => v.Device == "37");
        var ctx = new ProtectionContext(m, r, new ProtectionSettings(), null);
        Assert.False(ctx.BranchInService[4]);
        Assert.True(ctx.BranchInService[3]);
    }

    // =================================================================================================================
    // D-DOC-224: ProtectionContext.PFromMw is read by no relay; the registry order is fixed and documented.
    // =================================================================================================================

    [Fact]
    public void D224_the_registry_order_is_pinned_and_no_relay_reads_the_from_end_power()
    {
        Assert.Equal(new[] { "27", "59", "32", "37", "49", "50", "51", "21", "81U", "81O", "81R", "78", "87", "50N/51N/21N/46" },
            ProtectionRegistry.All.Select(r => r.Device).ToArray());
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Core", "Protection", "ProtectionRelays.cs"));
        Assert.DoesNotContain("PFromMw", src, StringComparison.Ordinal);
        Assert.DoesNotContain("Ordered device-number ascending", src, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // D-DOC-158: operating elements fastest first; primary clearance = the fastest timed branch element.
    // =================================================================================================================

    private static RelayVerdict V(string device, ProtectionLocation kind, double margin, double pickup, double? t) =>
        new(device, device, kind, 0, device, true, 0, "-", pickup, margin, t, device, ProtectionConfidence.Generic);

    [Fact]
    public void D158_operating_list_is_fastest_first_and_primary_clearance_is_a_timed_branch_element()
    {
        // The uk bus 1 shape: a reverse-power margin of 145 MW, an instantaneous 50, a 51 at 0.33 s, an undervoltage.
        var rep = new ProtectionReport(new[]
        {
            V("51", ProtectionLocation.Branch, 8.2, 1.2, 0.33),
            V("32", ProtectionLocation.Generator, 145.0, -5.0, null),
            V("27", ProtectionLocation.Bus, 0.8, 0.8, null),
            V("50", ProtectionLocation.Branch, 6.9, 2.5, 0.0),
            V("87", ProtectionLocation.Bus, 4.7, 0.1, 0.0),
        }, new ProtectionSettings(), false);
        // Timed first (0, 0, 0.33; equal times by row index since N270: 50 is row 3, 87 row 4, the same order the old
        // raw-margin rule gave here), then 32 (145 / 5 = 29) before 27 (0.8 / 0.8 = 1) by the margin relative to the
        // pickup.
        Assert.Equal(new[] { "50", "87", "51", "32", "27" }, rep.Operating.Select(v => v.Device).ToArray());
        // The busbar differential of the faulted bus is the unit protection: it is the primary clearance.
        Assert.Equal("87", rep.PrimaryClearance!.Device);
        // Without it, the fastest timed branch element.
        var noDiff = new ProtectionReport(rep.Verdicts.Where(v => v.Device != "87").ToArray(), new ProtectionSettings(), false);
        Assert.Equal("50", noDiff.PrimaryClearance!.Device);

        var noTimed = new ProtectionReport(new[] { V("32", ProtectionLocation.Generator, 145.0, -5.0, null) }, new ProtectionSettings(), false);
        Assert.Null(noTimed.PrimaryClearance);
    }

    [Fact]
    public void D158_within_one_device_the_order_is_the_old_margin_order()
    {
        // Within one device (one setting), the larger margin is the faster time, so rows with DIFFERENT times are in
        // the old margin order.
        // Moved 2026-10 (LF1 N270): this test also pinned "equal times keep the margin order", which is the defect
        // N270 removes: the five 21-Z2 rows here have the same margin in exact arithmetic, so the old order among them
        // was decided by rounding (N233 moved it). Before: each device's rows equal the margin-descending order
        // (21-Z2: 4-9, 4-7, 4-5, 3-4, 2-4; 21-Z3: 1-5, 5-6, 2-5). After: each device's rows are in time order, equal
        // times by row index (21-Z2: 2-4, 3-4, 4-5, 4-7, 4-9; 21-Z3: 1-5, 2-5, 5-6), and the margin order still
        // holds between any two rows of one device whose times differ.
        GridModel m = IeeeCases.Case14();
        PowerFlowResult pre = Solve(m);
        (_, ProtectionReport rep) = ProtectionAssessment.AssessThreePhaseFault(m, pre, 4);
        var rowOf = new Dictionary<RelayVerdict, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < rep.Verdicts.Count; i++) rowOf[rep.Verdicts[i]] = i;
        foreach (IGrouping<string, RelayVerdict> g in rep.Operating.GroupBy(v => v.Device))
        {
            RelayVerdict[] inNew = g.ToArray();
            RelayVerdict[] expected = rep.Verdicts.Where(v => v.Operate && v.Device == g.Key)
                .OrderBy(v => v.TimeToTripS is null ? 1 : 0).ThenBy(v => v.TimeToTripS ?? 0.0)
                .ThenByDescending(v => v.TimeToTripS is null ? ProtectionReport.RelativeMargin(v) : 0.0)
                .ThenBy(v => rowOf[v]).ToArray();
            Assert.Equal(expected.Select(v => v.LocationId), inNew.Select(v => v.LocationId));
            // Between two rows of one device whose operating times differ, the faster one has the larger margin.
            for (int a = 0; a < inNew.Length; a++)
                for (int b = a + 1; b < inNew.Length; b++)
                    if (inNew[a].TimeToTripS is { } ta && inNew[b].TimeToTripS is { } tb && ta < tb)
                        Assert.True(inNew[a].Margin >= inNew[b].Margin, $"{g.Key}: {inNew[a].LocationName} before {inNew[b].LocationName}");
        }
    }

    // =================================================================================================================
    // D-DOC-090: the fault state's loading is taken on the heavier end.
    // =================================================================================================================

    [Fact]
    public void D090_fault_state_loading_is_the_heavier_end_over_the_rating()
    {
        GridModel m = IeeeCases.Case9();
        PowerFlowResult pre = Solve(m);
        FaultResult f = FaultAnalysis.ThreePhase(m, pre, 5);
        int changed = 0;
        for (int l = 0; l < f.State.LineFlows.Count; l++)
        {
            LineFlow lf = f.State.LineFlows[l];
            if (!(lf.RatingMva > 0)) { Assert.Equal(0.0, lf.LoadingPercent); continue; }
            double sFrom = new Complex(lf.PFromMw, lf.QFromMvar).Magnitude, sTo = new Complex(lf.PToMw, lf.QToMvar).Magnitude;
            double before = sFrom / lf.RatingMva * 100.0;
            double after = Math.Max(sFrom, sTo) / lf.RatingMva * 100.0;
            Assert.Equal(after, lf.LoadingPercent);
            if (before != after)
            {
                changed++;
                _out.WriteLine($"case9 bus 5 fault, branch {l} ({lf.From}-{lf.To}): LoadingPercent {before.ToString(Inv, CultureInfo.InvariantCulture)} -> {after.ToString(Inv, CultureInfo.InvariantCulture)}");
            }
        }
        Assert.True(changed > 0);   // the case exercises the fix: some faulted lines carry more at the to end
    }

    // =================================================================================================================
    // D-DOC-092 / 169 / 174 / 226: grading.
    // =================================================================================================================

    [Fact]
    public void D092_a_backup_that_never_operates_is_not_coordinated()
    {
        var primary = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 100.0, 0.10);
        var backup = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, 5000.0, 0.30);   // above the fault current
        GradingResult g = OvercurrentCoordination.Grade(primary, backup, faultCurrent: 1000.0);
        Assert.True(g.PrimaryOperates);
        Assert.False(g.BackupOperates);
        Assert.True(double.IsPositiveInfinity(g.MarginSeconds));   // the arithmetic stays honest
        Assert.False(g.Coordinated);                               // was true before D-DOC-092
    }

    [Fact]
    public void D226_the_ieee_and_iec_curve_constants_are_the_published_ones()
    {
        // IEEE C37.112-2018 Table 1 (A, B, p) and IEC 60255-151 (k, alpha); ABB 1MRS757285 Table 8.2.1 (docs/reference S317)
        // prints the IEC four. Pinned directly so a typo in the table cannot pass through a curve test.
        Assert.Equal((0.0515, 0.1140, 0.02), OvercurrentCoordination.IeeeConstants(OvercurrentCurve.IeeeModeratelyInverse));
        Assert.Equal((19.61, 0.491, 2.0), OvercurrentCoordination.IeeeConstants(OvercurrentCurve.IeeeVeryInverse));
        Assert.Equal((28.2, 0.1217, 2.0), OvercurrentCoordination.IeeeConstants(OvercurrentCurve.IeeeExtremelyInverse));
        Assert.Equal((0.14, 0.02), OvercurrentCoordination.IecConstants(OvercurrentCurve.IecStandardInverse));
        Assert.Equal((13.5, 1.0), OvercurrentCoordination.IecConstants(OvercurrentCurve.IecVeryInverse));
        Assert.Equal((80.0, 2.0), OvercurrentCoordination.IecConstants(OvercurrentCurve.IecExtremelyInverse));
        Assert.Equal((120.0, 1.0), OvercurrentCoordination.IecConstants(OvercurrentCurve.IecLongTimeInverse));
        // The IEEE very-inverse time at M = 2, TD = 1: 19.61 / 3 + 0.491.
        var e = new OvercurrentElement(OvercurrentCurve.IeeeVeryInverse, 100.0, 1.0);
        Assert.Equal(1.0 * (19.61 / (DetMath.Pow(2.0, 2.0) - 1.0) + 0.491), OvercurrentCoordination.OperateTimeSeconds(e, 200.0));
    }

    [Fact]
    public void D092_D169_cli_grade_says_the_backup_does_not_operate_prints_signed_infinities_and_exits_3()
    {
        CliProcess.Result r = CliProcess.RunBuilt("grade", "--chain", "iec-si:100:0.1,iec-si:5000:0.3", "--current", "1000");
        Assert.True(r.ExitCode == 3, r.ToString());
        Assert.Contains("| R1->R2 | 0.2971 | never | inf | NOT COORDINATED (backup does not operate) |", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("NOT GRADED", r.StdOut, StringComparison.Ordinal);

        // Primary never operates, backup does: the margin is minus infinity, printed with its sign.
        CliProcess.Result neg = CliProcess.RunBuilt("grade", "--chain", "iec-si:5000:0.1,iec-si:100:0.3", "--current", "1000");
        Assert.True(neg.ExitCode == 3, neg.ToString());
        Assert.Contains("| -inf | NOT COORDINATED (primary does not operate) |", neg.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2000,5")]       // a decimal comma
    [InlineData("1000,1000")]    // a repeat
    [InlineData("2000,1000")]    // out of order
    public void D174_cli_grade_refuses_a_current_list_that_is_not_strictly_ascending(string currents)
    {
        CliProcess.Result r = CliProcess.RunBuilt("grade", "--chain", "iec-si:100:0.1,iec-si:150:0.3", "--current", currents);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("write decimals with a point", r.StdErr, StringComparison.Ordinal);
        Assert.Equal("", r.StdOut);   // refused before anything is graded
    }

    [Fact]
    public void D226_cli_grade_chain_json_carries_every_pair_and_the_verdict()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-doc4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string json = Path.Combine(dir, "grade.json");
            CliProcess.Result r = CliProcess.RunBuilt("grade", "--chain", "iec-si:100:0.1,iec-si:150:0.3,iec-si:200:0.6",
                "--current", "1000,2000", "--json", json);
            Assert.True(r.ExitCode == 0, r.ToString());
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json));
            JsonElement s = doc.RootElement.GetProperty("summary");
            Assert.True(s.GetProperty("all_coordinated").GetBoolean());
            Assert.Equal(3, s.GetProperty("relays").GetInt32());
            JsonElement[] pairs = doc.RootElement.GetProperty("tables").GetProperty("pairs").EnumerateArray().ToArray();
            Assert.Equal(4, pairs.Length);   // 2 pairs x 2 currents, every one
            foreach (JsonElement p in pairs)
            {
                double i = p.GetProperty("fault_current").GetDouble();
                double tp = p.GetProperty("primary_s").GetDouble(), tb = p.GetProperty("backup_s").GetDouble();
                Assert.Equal(tb - tp, p.GetProperty("margin_s").GetDouble());   // the very values the console graded
                Assert.True(p.GetProperty("coordinated").GetBoolean());
                Assert.True(i is 1000.0 or 2000.0);
            }
            Assert.Equal(3, doc.RootElement.GetProperty("tables").GetProperty("relays").GetArrayLength());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // =================================================================================================================
    // D-DOC-089 / 166: fault --zf range check and the j in the header.
    // =================================================================================================================

    [Fact]
    public void D089_cli_fault_refuses_a_negative_or_infinite_fault_reactance()
    {
        CliProcess.Result r = CliProcess.RunBuilt("fault", "case9", "--bus", "5", "--zf", "-0.1");
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("--zf expected a finite fault reactance of 0 pu or more", r.StdErr, StringComparison.Ordinal);
        CliProcess.Result pc = CliProcess.RunBuilt("protection-check", "case9", "--fault-bus", "5", "--zf", "-0.1");
        Assert.True(pc.ExitCode == 2, pc.ToString());
        // A non-finite value never reaches the range check: the flag reader refuses it first (exit 2 as well).
        CliProcess.Result inf = CliProcess.RunBuilt("fault", "case9", "--bus", "5", "--zf", "Infinity");
        Assert.True(inf.ExitCode == 2, inf.ToString());
    }

    [Fact]
    public void D166_D158_cli_fault_prints_the_reactive_fault_impedance_and_the_primary_clearance()
    {
        CliProcess.Result r = CliProcess.RunBuilt("fault", "case9", "--bus", "5", "--zf", "0.05");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("Zf=j0.050 pu", r.StdOut, StringComparison.Ordinal);
        string last = r.StdOut.Replace("\r\n", "\n").TrimEnd().Split('\n')[^1];
        Assert.StartsWith("PROTECTION SIMULATION: a three-phase fault at bus 5", last, StringComparison.Ordinal);
        Assert.True(last.Contains("primary clearance by", StringComparison.Ordinal) || last.Contains("no branch element with an operating time", StringComparison.Ordinal), last);
    }

    // =================================================================================================================
    // D-DOC-224: real exit codes of grade and protection-check (the older tests only grep Program.cs).
    // =================================================================================================================

    [Theory]
    [InlineData(0, "protection-check case9")]
    [InlineData(2, "protection-check case9 --fault-bus 999")]
    [InlineData(2, "protection-check case9 --zf 0.1")]
    [InlineData(2, "protection-check case9 --fault-bus 5 --cascade")]
    [InlineData(2, "protection-check")]
    [InlineData(0, "grade --chain iec-si:100:0.1,iec-si:150:0.3 --current 1000")]
    [InlineData(3, "grade --chain iec-si:100:0.1,iec-si:150:0.1 --current 1000")]
    [InlineData(2, "grade --chain iec-si:100:0.1 --current 1000")]
    [InlineData(2, "grade --chain bogus:1:1,iec-si:150:0.3 --current 1000")]
    [InlineData(2, "grade --current 1000")]
    public void D224_cli_exit_codes_of_grade_and_protection_check_from_the_built_binary(int exit, string line)
    {
        CliProcess.Result r = CliProcess.RunBuilt(line.Split(' '));
        Assert.True(r.ExitCode == exit, r.ToString());
    }

    // =================================================================================================================
    // D-DOC-093 / 094 / 216: protection placement checks and the unused f0.
    // =================================================================================================================

    private static AssetClass Prot(string name, params (string Key, double Value)[] p) => new(name, AssetKind.Protection, "test",
        p.ToDictionary(x => x.Key, x => x.Value),
        new Dictionary<string, AssetClassProvenance> { [AssetClass.DefaultKey] = new(ProvenanceTag.CuratedPlaceholder, "[VERIFY]") });

    [Fact]
    public void D093_an_overload_stage_at_a_bus_with_no_rated_branch_is_refused()
    {
        GridModel c9 = IeeeCases.Case9();
        // Unrate every branch at bus 4 (1-4, 4-5, 4-6 on case9).
        GridModel m = c9.CopyWith(branches: c9.Branches.Select(b => b.From == 4 || b.To == 4 ? b with { RateAMva = 0 } : b).ToList());
        var lib = new AssetClassLibrary(new[] { Prot("ol", (AssetParams.OverloadTripFraction, 1.2)), Prot("uf", (AssetParams.UfTripHz, 49.0)) });
        var ex = Assert.Throws<InvalidDataException>(() => new ProtectionModel(lib, new[] { new ProtectionInstance("d1", "ol", 4, 10) }).ValidateAgainst(m));
        Assert.Contains("overload stage", ex.Message, StringComparison.Ordinal);
        Assert.Contains("monitor bus 4", ex.Message, StringComparison.Ordinal);
        // A rated branch at the bus, or a class with no overload stage, passes.
        new ProtectionModel(lib, new[] { new ProtectionInstance("d1", "ol", 5, 10) }).ValidateAgainst(m);
        new ProtectionModel(lib, new[] { new ProtectionInstance("d2", "uf", 4, 10) }).ValidateAgainst(m);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void D094_a_negative_or_non_finite_capacity_is_refused(double capacity)
    {
        GridModel m = IeeeCases.Case9();
        var lib = new AssetClassLibrary(new[] { Prot("uf", (AssetParams.UfTripHz, 49.0)) });
        var ex = Assert.Throws<InvalidDataException>(() => new ProtectionModel(lib, new[] { new ProtectionInstance("d1", "uf", 5, capacity) }).ValidateAgainst(m));
        Assert.Contains("capacityMw", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => new ProtectionController(Prot("uf", (AssetParams.UfTripHz, 49.0)), 5, capacity));
        // Moved 2026-10 (B4 R137, Ruling R1: Mark delegated EE calls 2026-10-04; source EREC G99 Issue 2 s.10.6.7.1 Table
        // 10.1): a protection class must author each stage's dwell. Before: Prot("uf", (UfTripHz, 49.0)) (dwell read as
        // 0 s). After: the same class with ufTripDwellS = 0.0 written out; the capacity check this test is about is
        // unchanged (it runs before the stages are built, so the refusal above is still the capacity one).
        _ = new ProtectionController(Prot("uf", (AssetParams.UfTripHz, 49.0), (AssetParams.UfTripDwellS, 0.0)), 5, 0.0);   // 0 MW is allowed
    }

    [Fact]
    public void D216_the_protection_controller_takes_no_unused_nominal_frequency()
    {
        foreach (ConstructorInfo c in typeof(ProtectionController).GetConstructors())
            Assert.DoesNotContain(c.GetParameters(), p => p.Name == "f0");
    }

    // =================================================================================================================
    // D-DOC-095: the dwell is counted in whole substeps.
    // =================================================================================================================

    private static int StepsToFire(double dwell, double dt, int maxSteps = 1000)
    {
        var e = new ProtectionEvaluator(new ProtectionStage(MonitoredQuantity.Frequency, ThresholdSense.Below, 49.0, dwell, ProtectionAction.TripInfeed));
        for (int n = 1; n <= maxSteps; n++)
        {
            var ctx = new AssetStepContext(48.0, 0.0, 0.0, dt, (n - 1) * dt);
            if (e.Step(in ctx, 48.0) == ProtectionEdge.Fired) return n;
        }
        return -1;
    }

    [Theory]
    [InlineData(0.2, 0.02, 10)]     // was 11: ten additions of 0.02 sum to 0.19999999999999998
    [InlineData(0.5, 0.02, 25)]
    [InlineData(0.5, 0.1, 5)]
    [InlineData(0.3, 0.1, 3)]
    [InlineData(1.0, 0.01, 100)]
    [InlineData(0.0, 0.02, 1)]      // no dwell: the first over-threshold substep
    [InlineData(0.21, 0.02, 11)]    // a dwell between steps waits for the next whole step
    public void D095_a_definite_time_stage_fires_after_the_whole_number_of_substeps(double dwell, double dt, int expected)
    {
        Assert.Equal(expected, StepsToFire(dwell, dt));
        // The old accumulation, for the record: repeated addition.
        double sum = 0; int old = 0;
        while (old < 1000) { sum += dt; old++; if (sum >= dwell) break; }
        _out.WriteLine($"dwell {dwell} s at {dt} s: fires after {expected} steps (repeated addition: {old})");
    }

    [Fact]
    public void D095_a_change_of_substep_mid_dwell_banks_the_steps_taken()
    {
        var e = new ProtectionEvaluator(new ProtectionStage(MonitoredQuantity.Frequency, ThresholdSense.Below, 49.0, 0.2, ProtectionAction.TripInfeed));
        // 5 steps of 0.02 (0.1 s), then 0.05 s steps: 0.15 after one, 0.2 after two.
        for (int n = 0; n < 5; n++) Assert.Equal(ProtectionEdge.None, e.Step(new AssetStepContext(48, 0, 0, 0.02, n * 0.02), 48.0));
        Assert.Equal(ProtectionEdge.None, e.Step(new AssetStepContext(48, 0, 0, 0.05, 0.10), 48.0));
        Assert.Equal(ProtectionEdge.Fired, e.Step(new AssetStepContext(48, 0, 0, 0.05, 0.15), 48.0));
    }

    // =================================================================================================================
    // D-DOC-096 / 231: the fault bus's island only; the sparse path solves where the dense path does.
    // =================================================================================================================

    // case9 plus a sourceless two-bus island (buses 101-102, one line, a load).
    private static GridModel Case9WithSourcelessIsland()
    {
        GridModel c9 = IeeeCases.Case9();
        var buses = c9.Buses.ToList();
        buses.Add(new Bus(101, BusType.Pq, Pd: 10, BaseKv: 230));
        buses.Add(new Bus(102, BusType.Pq, BaseKv: 230));
        var branches = c9.Branches.ToList();
        branches.Add(new Branch(101, 102, 0.01, 0.1, Name: "island"));
        return c9.CopyWith(buses: buses, branches: branches);
    }

    [Fact]
    public void D096_a_sourceless_island_elsewhere_changes_no_bit_and_the_sparse_path_now_solves()
    {
        // Moved 2026-10 (N233, Mark's determinism rule 2026-10-04): the fault networks are now the sparse engine at every
        // size, and the dense LU is a test oracle under FaultNetworkSystem.DenseReferenceScope (D-DOC-231's ForceSparseScope
        // is gone). Each check below is kept at its old strength: "dense" now means the oracle scope, "sparse" the default.
        GridModel c9 = IeeeCases.Case9();
        GridModel withIsland = Case9WithSourcelessIsland();
        foreach (int bus in new[] { 1, 5, 9 })
        {
            Iec60909.Duty plain = Iec60909.ThreePhase(c9, bus);
            Iec60909.Duty islanded = Iec60909.ThreePhase(withIsland, bus);
            Assert.Equal(plain, islanded);   // every field, bit for bit (the production engine, both)
            Iec60909.Duty dense;
            using (FaultNetworkSystem.DenseReferenceScope()) dense = Iec60909.ThreePhase(withIsland, bus);
            Iec60909.Duty densePlain;
            using (FaultNetworkSystem.DenseReferenceScope()) densePlain = Iec60909.ThreePhase(c9, bus);
            Assert.Equal(densePlain, dense);   // and on the dense oracle, as before N233

            // The pre-D-DOC-096 dense path: the WHOLE islanded matrix, factored densely (clamped pivots in the island).
            var shunts = new List<(int, Complex)>();
            var index = withIsland.Buses.Select((b, i) => (b.Id, i)).ToDictionary(x => x.Id, x => x.i);
            foreach (Generator g in withIsland.Generators)
                shunts.Add((index[g.Bus], Complex.One / new Complex(0.0, g.FaultSourceReactancePu(Iec60909.DefaultSubtransientXdPu) * withIsland.BaseMva / g.MBase)));
            var e = new Complex[withIsland.BusCount];
            e[index[bus]] = Complex.One;
            Complex oldZkk, denseZ1;
            using (FaultNetworkSystem.DenseReferenceScope())
            {
                oldZkk = FaultNetworkSystem.ForModel(withIsland, shunts, seriesOnly: true).Solve(e)[index[bus]];
                denseZ1 = Iec60909.PositiveSequenceThevenin(withIsland, bus).Z1Pu;
            }
            Assert.Equal(oldZkk, denseZ1);   // the same bits as before (on the dense LU the island cut changes nothing)
            // N233 (new check): on the sparse engine the island cut gives exactly plain case9's Z1.
            Assert.Equal(Iec60909.PositiveSequenceThevenin(c9, bus).Z1Pu, Iec60909.PositiveSequenceThevenin(withIsland, bus).Z1Pu);

            // Before: the sparse factor refused the whole matrix (singular island). Now it solves the same island.
            Assert.Throws<InvalidOperationException>(() => FaultNetworkSystem.ForModel(withIsland, shunts, seriesOnly: true));
            Iec60909.Duty sparse = islanded;
            double rel = Math.Abs(sparse.IkppKa - dense.IkppKa) / dense.IkppKa;
            _out.WriteLine($"case9+island bus {bus}: Ik'' dense {dense.IkppKa.ToString(Inv, CultureInfo.InvariantCulture)} sparse {sparse.IkppKa.ToString(Inv, CultureInfo.InvariantCulture)} rel {rel:E3}");
            Assert.True(rel <= 1e-9, $"bus {bus}: relative difference {rel}");
        }

        // A fault IN the sourceless island is refused by name, on both paths.
        var ex = Assert.Throws<InvalidOperationException>(() => Iec60909.ThreePhase(withIsland, 101));
        Assert.Contains("island of fault bus 101 (2 bus(es)", ex.Message, StringComparison.Ordinal);
        using (FaultNetworkSystem.DenseReferenceScope())
            Assert.Contains("island of fault bus 101", Assert.Throws<InvalidOperationException>(() => Iec60909.ThreePhase(withIsland, 101)).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("case9")]
    [InlineData("case30")]
    [InlineData("case118")]
    public void D231_iec60909_on_the_sparse_path_agrees_with_the_dense_path_at_every_bus(string name)
    {
        GridModel m = name switch { "case9" => IeeeCases.Case9(), "case30" => IeeeCases.Case30(), _ => IeeeCases.Case118() };
        double worst = 0; int worstBus = 0, studied = 0;
        foreach (Bus b in m.Buses)
        {
            if (!(b.BaseKv > 0)) continue;
            // Moved 2026-10 (N233, Mark's determinism rule 2026-10-04): the default is now the sparse engine; the dense LU
            // is the test oracle under DenseReferenceScope (was: dense default, sparse under ForceSparseScope). Same check.
            Iec60909.Duty dense;
            using (FaultNetworkSystem.DenseReferenceScope()) dense = Iec60909.ThreePhase(m, b.Id);
            Iec60909.Duty sparse = Iec60909.ThreePhase(m, b.Id);
            foreach ((double d, double s) in new[] { (dense.IkppKa, sparse.IkppKa), (dense.PeakIpKa, sparse.PeakIpKa),
                         (dense.BreakingIbKa, sparse.BreakingIbKa), (dense.ThermalIthKa, sparse.ThermalIthKa) })
            {
                double rel = Math.Abs(s - d) / Math.Abs(d);
                if (rel > worst) { worst = rel; worstBus = b.Id; }
            }
            Assert.Equal(dense.MeshedPeakFactor, sparse.MeshedPeakFactor);
            Assert.Equal(dense.SteadyBasis, sparse.SteadyBasis);
            studied++;
        }
        _out.WriteLine($"{name}: {studied} buses, largest dense/sparse relative difference {worst:E3} at bus {worstBus}");
        Assert.True(studied > 0);
        Assert.True(worst <= 1e-9, $"{name}: {worst} at bus {worstBus}");
    }

    // =================================================================================================================
    // D-DOC-097: the steady-state basis.
    // =================================================================================================================

    private static GridModel OneMachine(double lineX) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, BaseKv: 132) },
        Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0) },
        Branches = new[] { new Branch(1, 2, R: 0.0, X: lineX) },
    };

    [Fact]
    public void D097_the_steady_state_basis_says_exact_meshed_or_upper_bound()
    {
        // One machine, fault at its terminals: Ik''/IrG = 1.1 / 0.2 = 5.5, single-fed near: Ik'' is only an upper bound.
        Iec60909.Duty near = Iec60909.ThreePhase(OneMachine(0.3), 1);
        Assert.Equal(SteadyStateBasis.SingleFedUpperBound, near.SteadyBasis);
        Assert.True(near.SteadyIkKa == near.IkppKa);   // the number itself is unchanged
        // Behind 0.4 pu: 1.1 / 0.6 = 1.83 <= 2, far: exact.
        Assert.Equal(SteadyStateBasis.FarFromGenerator, Iec60909.ThreePhase(OneMachine(0.4), 2).SteadyBasis);
        // case9 bus 1 (machine 1 feeds 2.35 kA, more than twice its rating) with three machines: the meshed rule.
        Assert.Equal(SteadyStateBasis.MeshedMultipleFed, Iec60909.ThreePhase(IeeeCases.Case9(), 1).SteadyBasis);
        Assert.Equal(SteadyStateBasis.FarFromGenerator, Iec60909.ThreePhase(IeeeCases.Case9(), 5).SteadyBasis);
        // Unbalanced faults: no decay by the standard.
        Assert.Equal(SteadyStateBasis.UnbalancedNoDecay, Iec60909.LineToLine(IeeeCases.Case9(), 5).SteadyBasis);
    }

    // =================================================================================================================
    // D-DOC-098: kappa takes Method B's 1.15 in a meshed network.
    // =================================================================================================================

    [Fact]
    public void D098_meshed_kappa_is_one_point_one_five_times_kappa_capped()
    {
        Assert.Equal(2.0, Iec60909.PeakFactorMeshed(0.0, 132.0));       // 1.15 x 2 capped at 2.0
        Assert.Equal(1.8, Iec60909.PeakFactorMeshed(0.1, 0.4));         // at 1 kV and below the cap is 1.8 (1.15 x 1.746)
        Assert.Equal(2.0, Iec60909.PeakFactorMeshed(0.0, 0.4));         // but never below kappa(R/X) itself (2.0 here)
        Assert.Equal(1.15 * Iec60909.PeakFactor(0.3), Iec60909.PeakFactorMeshed(0.3, 132.0));

        GridModel c9 = IeeeCases.Case9();
        Assert.True(Iec60909.IsMeshed(c9, 3));
        Iec60909.Duty d = Iec60909.ThreePhase(c9, 5);
        Assert.True(d.MeshedPeakFactor);
        Assert.Equal(Iec60909.PeakFactorMeshed(d.RoverX, c9.Buses.First(b => b.Id == 5).BaseKv), d.Kappa);
        Assert.Equal(d.Kappa * Math.Sqrt(2.0) * d.IkppKa, d.PeakIpKa);
        Assert.Equal(Iec60909.ThermalDcFactor(Iec60909.PeakFactor(d.RoverX), 50.0, 1.0), d.ThermalM);   // m keeps kappa(R/X)
        double kv = c9.Buses.First(b => b.Id == 5).BaseKv;
        Iec60909.Duty pure = Iec60909.FromThevenin(1.1, kv, d.TheveninRPu, d.TheveninXPu, c9.BaseMva);
        Assert.Equal(pure.IkppKa, d.IkppKa);                             // Ik'' untouched
        Assert.Equal(pure.DcComponentKa, d.DcComponentKa);               // idc untouched
        Assert.Equal(pure.ThermalIthKa, d.ThermalIthKa);                 // Ith untouched (m on kappa(R/X))
        _out.WriteLine($"case9 bus 5: kappa {pure.Kappa.ToString(Inv, CultureInfo.InvariantCulture)} -> {d.Kappa.ToString(Inv, CultureInfo.InvariantCulture)}, "
            + $"ip {pure.PeakIpKa.ToString(Inv, CultureInfo.InvariantCulture)} -> {d.PeakIpKa.ToString(Inv, CultureInfo.InvariantCulture)} kA, "
            + $"Ith {pure.ThermalIthKa.ToString(Inv, CultureInfo.InvariantCulture)} -> {d.ThermalIthKa.ToString(Inv, CultureInfo.InvariantCulture)} kA");

        // A radial single source is not meshed: kappa(R/X) exactly, as before.
        Assert.False(Iec60909.IsMeshed(OneMachine(0.3), 1));
        Iec60909.Duty radial = Iec60909.ThreePhase(OneMachine(0.3), 2);
        Assert.False(radial.MeshedPeakFactor);
        Assert.Equal(Iec60909.PeakFactor(radial.RoverX), radial.Kappa);
        // Two sources on the radial line close a loop through earth: meshed.
        GridModel twoSources = OneMachine(0.3).CopyWith(generators: new[] { new Generator(1, 0.0, MBase: 100.0), new Generator(2, 0.0, MBase: 100.0) });
        Assert.True(Iec60909.IsMeshed(twoSources, 2));
    }

    // =================================================================================================================
    // D-DOC-220: a trajectory from a frequency run; 81R evaluable, 78 not checkable on a COI run.
    // =================================================================================================================

    [Fact]
    public void D220_a_frequency_run_gives_a_trajectory_the_system_elements_can_judge()
    {
        FrequencyResponse resp = FrequencyDynamics.Simulate(20000.0, 30000.0, new[] { new FrequencyEvent(1.0, -1800.0) });
        ProtectionTrajectory t = ProtectionTrajectory.FromFrequencyResponse(resp);
        Assert.Equal(Math.Abs(resp.InitialRocofHzPerS), t.MaxAbsRocofHzPerS);
        Assert.Equal(resp.NadirHz, t.MinFrequencyHz);
        Assert.Equal(resp.Points.Max(p => p.Hz), t.MaxFrequencyHz);
        Assert.True(double.IsNaN(t.MaxAbsVectorShiftDeg));

        ProtectionReport rep = ProtectionAssessment.AssessTrajectory(t);
        Assert.Equal(new[] { "81U", "81O", "81R", "78" }, rep.Verdicts.Select(v => v.Device).ToArray());
        RelayVerdict rocof = rep.Verdicts.Single(v => v.Device == "81R");
        Assert.NotEqual(ProtectionConfidence.NotCheckable, rocof.Confidence);
        Assert.Equal(t.MaxAbsRocofHzPerS > 1.0, rocof.Operate);
        Assert.Equal(ProtectionConfidence.NotCheckable, rep.Verdicts.Single(v => v.Device == "78").Confidence);

        // The same verdicts as the full assessment given the same trajectory (one shared function per element).
        // Moved 2026-10 (D2 N017, Ruling R1: Mark delegated EE calls 2026-10-04; source: Anderson and Fouad 2003, Example
        // 2.6, see IeeeCases.Wscc9NominalFrequencyHz): case9 is now a 60 Hz system, and the trajectory above is a 50 Hz
        // run judged against the G99 (50 Hz) settings. Comparing like with like, the full assessment uses case9 at the
        // trajectory's own 50 Hz nominal (the power flow is unchanged by the frequency). Before: IeeeCases.Case9() (then
        // 50 Hz). After: IeeeCases.Case9().CopyWith(frequencyHz: 50.0). The element-by-element equality is unchanged.
        GridModel c9 = IeeeCases.Case9().CopyWith(frequencyHz: 50.0);
        ProtectionReport full = ProtectionAssessment.Assess(c9, Solve(c9), trajectory: t);
        foreach (RelayVerdict v in rep.Verdicts)
            Assert.Equal(v, full.Verdicts.Single(x => x.Device == v.Device));
    }

    [Fact]
    public void D220_cli_trip_prints_the_frequency_protection_on_its_trajectory()
    {
        CliProcess.Result r = CliProcess.RunBuilt("trip", "uk");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("Frequency protection on this trajectory", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("81R", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("78   Vector shift / LoM   not evaluated", r.StdOut, StringComparison.Ordinal);
    }
}
