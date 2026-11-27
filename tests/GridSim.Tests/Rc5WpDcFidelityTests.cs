using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Contingency;
using GridSim.Core.Analysis.Sensitivities;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// B33 (RC5 WP-DC): the DC model follows MATPOWER's makeBdc and rundcpf. Branch susceptance b = (1/x)/tap, a phase shift
/// is the injection pair Pfinj = -b phi (flow b (theta_f - theta_t - phi)), and bus shunt conductance Gs is a
/// constant-power load (Pbus -= Gs/base). Sources, per test:
/// <list type="bullet">
/// <item>An independent NumPy transcription of MATPOWER makeBdc + rundcpf, <c>tools/crossval/pf/dcpf_fidelity.py</c>, run
/// outside the build; its values are pinned at 17 significant digits in
/// <c>fixtures/dcpf-fidelity/dcpf-fidelity-pins.json</c> with the script hash and the NumPy/SciPy versions. Octave and
/// MATLAB were not available, so MATPOWER's rundcpf is represented by that line-for-line transcription; pandapower was not
/// installed, so its rundcpp was not run.</item>
/// <item>Hand derivations (closed forms written in the tests).</item>
/// <item>Internal consistency of the one DC model: PTDF/LODF against re-solves, PSDF against a finite re-solve.</item>
/// </list>
/// Tolerance (derived, not fitted): 4096 machine epsilon relative to the infinity norm of the reference vector, with a floor
/// of 1e-3 pu (1e-3 rad for angles). The two codes differ legitimately in operation order: MATPOWER divides the net
/// (Pg - Pd) by base once where GridSim sums mw/base per entry, and the sparse LU's pivot order differs from LAPACK's;
/// both are backward-stable, so the forward error of each entry is bounded by a small multiple of eps times the condition
/// number times the vector norm (the B' condition numbers here are below 1.1e5). The norm is the honest scale: a near-zero
/// flow on a stiff branch is a difference of two large angle products and has no relative accuracy of its own in either
/// code (two NumPy solvers with different orderings already differ by up to 5e4 eps element-wise on case300, and by at most
/// 190 eps norm-wise on every pinned case).
/// </summary>
public class Rc5WpDcFidelityTests
{
    private const double Eps = 2.220446049250313e-16;
    private const double Allowance = 4096 * Eps;

    // ---- shared helpers -------------------------------------------------------------------------------------------

    private static JsonElement Pins()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "dcpf-fidelity", "dcpf-fidelity-pins.json");
        Assert.True(File.Exists(path), $"pin file missing: {path}");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    private static double[] Doubles(JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    private static int[] Ints(JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    /// <summary>|got - want| &lt;= 4096 eps max(||want||_inf, floor) for every entry.</summary>
    private static void Near(string label, IReadOnlyList<double> got, IReadOnlyList<double> want, double floor)
    {
        Assert.True(got.Count == want.Count, $"{label}: {got.Count} values against {want.Count}");
        double scale = floor;
        foreach (double w in want) scale = Math.Max(scale, Math.Abs(w));
        double tol = Allowance * scale;
        for (int i = 0; i < want.Count; i++)
        {
            double d = Math.Abs(got[i] - want[i]);
            Assert.True(d <= tol, $"{label}[{i}]: {got[i]:R} against {want[i]:R}, |diff| {d:R} = {d / (Eps * scale):F1} eps of the norm (allowed 4096)");
        }
    }

    private static bool SameBits(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

    private static void Bits(string label, IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        Assert.True(a.Count == b.Count, $"{label}: length {a.Count} vs {b.Count}");
        for (int i = 0; i < a.Count; i++) Assert.True(SameBits(a[i], b[i]), $"{label}[{i}]: {a[i]:R} vs {b[i]:R}");
    }

    private static GridModel Load(string rel) => S10TestSupport.LoadMatpower(rel).Model;

    // The synthetic variants of the pin script (VARIANTS there): the same edits on the same files.
    private static GridModel Variant(string name) => name switch
    {
        "case14-tap105" => Edit(Load("lib/case14.m"), taps: [(0, 1.05)]),
        "case9-shift5" => Edit(Load("lib/case9.m"), shifts: [(5, 5.0)]),
        "case30-gs" => Edit(Load("lib/case30.m"), gs: [(3, 10.0), (7, 10.0), (21, 10.0)]),
        "case30-all" => Edit(Load("lib/case30.m"), taps: [(0, 1.05)], shifts: [(5, 5.0)], gs: [(3, 10.0), (7, 10.0), (21, 10.0)]),
        "three-bus-hand" => ThreeBus(5.0),
        _ => Load(name),
    };

    private static GridModel Edit(GridModel m, (int Branch, double Tap)[]? taps = null, (int Branch, double Deg)[]? shifts = null,
        (int Bus, double Gs)[]? gs = null)
    {
        var branches = m.Branches.ToList();
        foreach ((int k, double t) in taps ?? []) branches[k] = branches[k] with { TapRatio = t };
        foreach ((int k, double s) in shifts ?? []) branches[k] = branches[k] with { PhaseShiftDeg = s };
        var buses = m.Buses.ToList();
        foreach ((int id, double g) in gs ?? [])
        {
            int i = buses.FindIndex(b => b.Id == id);
            Assert.True(i >= 0, $"bus {id} not in {m.Name}");
            buses[i] = buses[i] with { Gs = g };
        }
        return m.CopyWith(buses: buses, branches: branches);
    }

    /// <summary>The spec's hand case: base 100 MVA; 1-2 x 0.1 with a shift of <paramref name="shiftDeg"/>, 2-3 x 0.2, 1-3 x 0.2;
    /// Pd 60 MW at bus 3, Gs 10 MW at bus 2; one generator at the slack bus 1 (Pg 0, so the reference supplies everything).</summary>
    private static GridModel ThreeBus(double shiftDeg, double gs2 = 10.0, double rate = 0.0) => new()
    {
        BaseMva = 100,
        Buses = [new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Gs: gs2), new Bus(3, BusType.Pq, Pd: 60)],
        Generators = [new Generator(1, Pg: 0, Pmax: 200)],
        Branches =
        [
            new Branch(1, 2, 0.0, 0.1, PhaseShiftDeg: shiftDeg, RateAMva: rate),
            new Branch(2, 3, 0.0, 0.2, RateAMva: rate),
            new Branch(1, 3, 0.0, 0.2, RateAMva: rate),
        ],
    };

    private static Dictionary<int, double> NetInjections(GridModel m)
    {
        var net = new Dictionary<int, double>();
        foreach (Bus b in m.Buses) net[b.Id] = net.GetValueOrDefault(b.Id) - b.Pd;
        foreach (Generator g in m.Generators) if (net.ContainsKey(g.Bus)) net[g.Bus] += g.Pg;
        return net;
    }

    // ---- 1. census: which bundled cases carry taps, shifts or Gs (so the tap-free list cannot rot) ---------------

    public static IEnumerable<object[]> CensusRows() =>
    [
        // file, taps other than 0/1, non-zero shifts, buses with Gs (counted on the model the reader builds)
        ["lib/case9.m", 0, 0, 0], ["lib/case14.m", 3, 0, 0], ["lib/case30.m", 0, 0, 0], ["lib/case57.m", 15, 0, 0],
        ["lib/case118.m", 9, 0, 0], ["lib/case300.m", 62, 0, 17], ["lib/case145.m", 52, 0, 85], ["lib/case24_ieee_rts.m", 5, 0, 0],
        ["benchmarks/cigre_mv.m", 0, 2, 0], ["gb/GBreducednetwork.m", 0, 4, 0], ["nesta/pglib_opf_case30_as.m", 0, 0, 0],
        ["nesta/pglib_opf_case5_pjm.m", 0, 0, 0], ["nesta/pglib_opf_case3_lmbd.m", 0, 0, 0], ["nesta/pglib_opf_case240_pserc.m", 0, 0, 0],
        ["nesta/pglib_opf_case162_ieee_dtc.m", 31, 0, 0], ["rts/pglib_opf_case73_ieee_rts.m", 15, 0, 0],
        ["dist/case18.m", 0, 0, 0], ["dist/case22.m", 0, 0, 0], ["dist/case33bw.m", 0, 0, 0], ["dist/case69.m", 0, 0, 0], ["dist/case85.m", 0, 0, 0],
    ];

    [Theory]
    [MemberData(nameof(CensusRows))]
    public void Census_of_taps_shifts_and_shunt_conductance_in_the_bundled_cases(string rel, int taps, int shifts, int gs)
    {
        // Source: the pin script's census (and data/matpower read directly); a case with all three zero is tap-free and its
        // DC results are bit-identical to the reactance-only model.
        GridModel m = Load(rel);
        Assert.Equal(taps, m.Branches.Count(b => b.InService && b.TapRatio != 1.0 && b.TapRatio != 0.0));
        Assert.Equal(shifts, m.Branches.Count(b => b.InService && b.PhaseShiftDeg != 0.0));
        Assert.Equal(gs, m.Buses.Count(b => b.Gs != 0.0));
    }

    [Theory]
    [InlineData("gb-full")]
    [InlineData("uk")]
    public void The_bundled_gb_json_cases_are_tap_shift_and_gs_free(string dir)
    {
        GridModel m = GridSim.Core.IO.Json.JsonGridLoader.LoadDirectory(Path.Combine(S10TestSupport.DataDir, dir));
        Assert.Equal(0, m.Branches.Count(b => b.TapRatio != 1.0 && b.TapRatio != 0.0));
        Assert.Equal(0, m.Branches.Count(b => b.PhaseShiftDeg != 0.0));
        Assert.Equal(0, m.Buses.Count(b => b.Gs != 0.0));
    }

    // ---- 2. cross-check against the NumPy makeBdc / rundcpf transcription ------------------------------------------

    public static IEnumerable<object[]> PinnedCases() =>
    [
        ["lib/case9.m"], ["lib/case14.m"], ["lib/case30.m"], ["lib/case57.m"], ["lib/case118.m"], ["lib/case300.m"],
        ["benchmarks/cigre_mv.m"], ["gb/GBreducednetwork.m"],
        ["case14-tap105"], ["case9-shift5"], ["case30-gs"], ["case30-all"], ["three-bus-hand"],
    ];

    [Theory]
    [MemberData(nameof(PinnedCases))]
    public void Dc_power_flow_matches_the_numpy_makeBdc_transcription(string name)
    {
        JsonElement pin = Pins().GetProperty("cases").GetProperty(name);
        GridModel m = Variant(name);
        DcPowerFlowResult r = DcPowerFlow.Solve(m);
        double baseMva = pin.GetProperty("base_mva").GetDouble();
        Assert.Equal(baseMva, m.BaseMva);
        Assert.Equal(Ints(pin.GetProperty("bus_ids")), r.BusIds.ToArray());
        Assert.Equal(Ints(pin.GetProperty("reference_bus_ids")).OrderBy(x => x), r.Islands.Select(i => i.ReferenceBusId).OrderBy(x => x));

        // The reference angle is 0 on every pinned case (the script subtracts the file's VA(ref), as GridSim sets it to 0).
        foreach (DcIslandBalance isl in r.Islands) Assert.Equal(0.0, r.VaDeg[r.BusIds.ToList().IndexOf(isl.ReferenceBusId)]);

        Near($"{name} angle (rad)", r.VaDeg.Select(v => v * Math.PI / 180.0).ToArray(), Doubles(pin.GetProperty("va_rad")), 1e-3);
        Near($"{name} flow (pu)", r.FlowMw.Select(f => f / baseMva).ToArray(), Doubles(pin.GetProperty("pf_pu")), 1e-3);
        Near($"{name} net injection (MW)", r.PInjMw, Doubles(pin.GetProperty("p_inj_mw")), 1e-3 * baseMva);
    }

    [Theory]
    [MemberData(nameof(PinnedCases))]
    public void Batched_dc_power_flow_is_the_single_solve_bit_for_bit_with_the_new_terms(string name)
    {
        // The batch contract of DcPowerFlow.SolveBatch holds in the new model: the shift and Gs terms live in the shared
        // right-hand-side and flow code, before and after the lane solve.
        GridModel m = Variant(name);
        DcPowerFlowResult single = DcPowerFlow.Solve(m);
        DcPowerFlowResult batch = DcPowerFlow.SolveBatch([m, m], maxDegreeOfParallelism: 1)[1];
        Bits($"{name} angles", single.VaDeg, batch.VaDeg);
        Bits($"{name} flows", single.FlowMw, batch.FlowMw);
        Bits($"{name} injections", single.PInjMw, batch.PInjMw);
    }

    [Fact]
    public void Ptdf_and_lodf_of_tapped_case14_match_the_numpy_transcription()
    {
        JsonElement pin = Pins().GetProperty("cases").GetProperty("lib/case14.m");
        DistributionFactors df = DistributionFactors.Build(Load("lib/case14.m"));
        Near("case14 PTDF bus 9", df.PtdfColumn(9), Doubles(pin.GetProperty("ptdf_bus9")), 1e-3);
        Near("case14 LODF branch 3", df.LodfColumn(3), Doubles(pin.GetProperty("lodf_branch3")), 1e-3);
    }

    // ---- 3. hand-derived cases ---------------------------------------------------------------------------------------

    [Fact]
    public void Three_bus_phase_shifter_and_shunt_conductance_match_the_hand_derivation()
    {
        // b12 = 10, b23 = 5, b13 = 5. Reduced B' over buses 2, 3 = [[15, -5], [-5, 10]], determinant 125. Right-hand side
        // [-10 phi - 0.1, -0.6]: bus 2 receives -b12 phi (the shift's to-bus term) and -Gs/base. So
        //   theta2 = -(100 phi + 4) / 125,  theta3 = -(50 phi + 9.5) / 125,
        //   P12 = 10 (0 - theta2 - phi) 100 = 32 - 200 phi,  P23 = 5 (theta2 - theta3) 100,  P13 = 5 (0 - theta3) 100,
        // and the reference supplies Pd + Gs = 70 MW.
        double phi = 5.0 * Math.PI / 180.0;
        DcPowerFlowResult r = DcPowerFlow.Solve(ThreeBus(5.0));
        double th2 = -(100 * phi + 4) / 125, th3 = -(50 * phi + 9.5) / 125;
        Near("theta", r.VaDeg.Select(v => v * Math.PI / 180.0).ToArray(), [0.0, th2, th3], 1e-3);
        Near("flows", r.FlowMw, [10 * (0 - th2 - phi) * 100, 5 * (th2 - th3) * 100, 5 * (0 - th3) * 100], 0.1);
        Near("P12 closed form", [r.FlowMw[0]], [32 - 200 * phi], 0.1);
        // KCL with the shunt: bus 2 takes P12 in, sends P23 on and draws its 10 MW of Gs; bus 3 takes P23 + P13 = 60.
        Near("bus 2 balance", [r.FlowMw[0] - r.FlowMw[1]], [10.0], 0.1);
        Near("bus 3 balance", [r.FlowMw[1] + r.FlowMw[2]], [60.0], 0.1);
        DcIslandBalance isl = Assert.Single(r.Islands);
        Near("reference injection", [isl.ReferenceInjectionMw], [70.0], 0.1);
        Assert.Equal(10.0, isl.ShuntLoadMw);
        Assert.Equal(60.0, isl.LoadMw);   // LoadMw stays the Pd sum
        Assert.Equal(-60.0, r.PInjMw[2]);   // scheduled Pg - Pd, as before
    }

    [Fact]
    public void Reversing_the_shifter_moves_the_flow_by_the_same_amount_the_other_way()
    {
        // From the closed form P12 = 32 - 200 phi: the shift-only change is -200 phi = -(b12 phi base) x 0.2, the ring
        // factor 1 - PTDF of the 1-2 transfer through the 2-3-1 path (0.2 = (5 x 5 / 10) / (10 + 2.5)).
        double phi = 5.0 * Math.PI / 180.0;
        double plus = DcPowerFlow.Solve(ThreeBus(5.0)).FlowMw[0];
        double none = DcPowerFlow.Solve(ThreeBus(0.0)).FlowMw[0];
        double minus = DcPowerFlow.Solve(ThreeBus(-5.0)).FlowMw[0];
        Near("no shift", [none], [32.0], 0.1);
        Near("+5 deg change", [plus - none], [-200 * phi], 0.1);
        Near("-5 deg change", [minus - none], [200 * phi], 0.1);
        Near("ring factor", [(plus - none) / (-10 * phi * 100)], [0.2], 1e-3);
    }

    [Fact]
    public void A_single_tapped_branch_has_susceptance_one_over_x_over_tap()
    {
        // One branch x = 0.1, tap 1.05, 50 MW load: the flow is the load, and theta2 = -0.5 / ((1 / 0.1) / 1.05), MATPOWER's
        // order (1/x)/tap; 1/(x tap) is the same number to 1 ulp.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = [new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 50)],
            Generators = [new Generator(1, Pg: 0)],
            Branches = [new Branch(1, 2, 0.0, 0.1, TapRatio: 1.05)],
        };
        DcPowerFlowResult r = DcPowerFlow.Solve(m);
        double theta2 = r.VaDeg[1] * Math.PI / 180.0;
        double expected = -0.5 / ((1.0 / 0.1) / 1.05);
        Assert.True(Math.Abs(theta2 - expected) <= 4 * Math.Abs(Math.BitIncrement(expected) - expected),
            $"theta2 {theta2:R} vs {expected:R}");   // the degree round trip costs at most a few ulp
        Assert.True(Math.Abs(theta2 - (-0.5 * (0.1 * 1.05))) <= Allowance * 0.0525, $"1/(x tap) form: {theta2:R}");
        Near("flow", [r.FlowMw[0]], [50.0], 0.1);
    }

    // ---- 4. tap 1 / tap 0 / shift 0 / Gs 0 take the old path ---------------------------------------------------------

    [Theory]
    [InlineData("lib/case9.m")]
    [InlineData("lib/case30.m")]
    [InlineData("lib/case14.m")]
    public void A_tap_of_zero_a_negative_zero_shift_and_a_negative_zero_gs_give_the_same_bits(string rel)
    {
        // Tap 0 is MATPOWER's "a line" and is read as 1; -0.0 equals 0.0, so neither builds a shift or Gs table. On the
        // tap-free cases every tap becomes 0; on case14 only the untapped branches do (its three transformers keep theirs).
        GridModel m = Load(rel);
        GridModel z = m.CopyWith(
            buses: m.Buses.Select(b => b.Gs == 0.0 ? b with { Gs = -0.0 } : b).ToList(),
            branches: m.Branches.Select(b => b with { TapRatio = b.TapRatio == 1.0 ? 0.0 : b.TapRatio, PhaseShiftDeg = b.PhaseShiftDeg == 0.0 ? -0.0 : b.PhaseShiftDeg }).ToList());
        DcPowerFlowResult ra = DcPowerFlow.Solve(m), rz = DcPowerFlow.Solve(z);
        Bits($"{rel} angles", ra.VaDeg, rz.VaDeg);
        Bits($"{rel} flows", ra.FlowMw, rz.FlowMw);
        Bits($"{rel} injections", ra.PInjMw, rz.PInjMw);
        DistributionFactors da = DistributionFactors.Build(m), db = DistributionFactors.Build(z);
        foreach (int bus in m.Buses.Select(x => x.Id)) Bits($"{rel} PTDF {bus}", da.PtdfColumn(bus), db.PtdfColumn(bus));
    }

    [Fact]
    public void The_legacy_view_clears_taps_shifts_and_gs_and_returns_a_clean_model_itself()
    {
        GridModel clean = Load("lib/case30.m");
        Assert.Same(clean, DcDemand.LegacyView(clean));
        GridModel all = Variant("case30-all");
        GridModel legacy = DcDemand.LegacyView(all);
        Assert.All(legacy.Branches, b => Assert.True(b.TapRatio == 1.0 && b.PhaseShiftDeg == 0.0));
        Assert.All(legacy.Buses, b => Assert.Equal(0.0, b.Gs));
        // Everything else is carried: the legacy view of case30-all is case30 itself, so its DC flows are case30's bits.
        Bits("legacy flows", DcPowerFlow.Solve(legacy).FlowMw, DcPowerFlow.Solve(clean).FlowMw);
        Assert.Equal(clean.Generators.Select(g => g.Pg), legacy.Generators.Select(g => g.Pg));
    }

    // ---- 5. PTDF / LODF / PSDF consistency with the new B --------------------------------------------------------------

    public static IEnumerable<object[]> ConsistencyCases() =>
        [["lib/case14.m"], ["lib/case118.m"], ["case9-shift5"], ["case30-gs"], ["case30-all"], ["three-bus-hand"]];

    [Theory]
    [MemberData(nameof(ConsistencyCases))]
    public void Flows_are_the_ptdf_map_of_the_injections_plus_the_zero_injection_offset(string name)
    {
        // f(inj) = PTDF inj + f(0): affine, the offset being the shifts and the Gs loads. Random injections, fixed seed.
        GridModel m = Variant(name);
        DistributionFactors df = DistributionFactors.Build(m);
        double[] offset = df.DcFlows(new Dictionary<int, double>(), m.BaseMva);
        var rng = new Random(20261124);
        var inj = new Dictionary<int, double>();
        foreach (Bus b in m.Buses) inj[b.Id] = (rng.NextDouble() - 0.5) * 200.0;
        double[] direct = df.DcFlows(inj, m.BaseMva);
        var composed = (double[])offset.Clone();
        foreach ((int bus, double mw) in inj)
        {
            double[] col = df.PtdfColumn(bus);
            for (int l = 0; l < composed.Length; l++) composed[l] += col[l] * mw;
        }
        Near($"{name} PTDF composition", composed, direct, 1e-3 * m.BaseMva);
    }

    [Theory]
    [MemberData(nameof(ConsistencyCases))]
    public void Dc_power_flow_equals_the_ptdf_composed_flow_of_the_schedule(string name)
    {
        // The consistency that two DC definitions broke: dcpf's flows from ptdf columns and the scheduled injections.
        GridModel m = Variant(name);
        DistributionFactors df = DistributionFactors.Build(m);
        double[] composed = df.DcFlows(new Dictionary<int, double>(), m.BaseMva);
        foreach ((int bus, double mw) in NetInjections(m))
        {
            double[] col = df.PtdfColumn(bus);
            for (int l = 0; l < composed.Length; l++) composed[l] += col[l] * mw;
        }
        Near($"{name} dcpf vs PTDF", composed, DcPowerFlow.Solve(m).FlowMw, 1e-3 * m.BaseMva);
    }

    public static IEnumerable<object[]> LodfCases() => [["lib/case14.m"], ["lib/case30.m"], ["case9-shift5"], ["case30-all"]];

    [Theory]
    [MemberData(nameof(LodfCases))]
    public void Lodf_post_outage_flows_equal_a_re_solve_without_the_branch(string name)
    {
        // f_l + LODF[l,k] f_k against the DC solve of the model with k out of service, for every non-bridge k. With a
        // shifter outaged, its own shift injection must leave with it (the pre-outage f_k includes the shift).
        GridModel m = Variant(name);
        DistributionFactors df = DistributionFactors.Build(m);
        Dictionary<int, double> net = NetInjections(m);
        double[] f = df.DcFlows(net, m.BaseMva);
        int checkedOutages = 0;
        for (int k = 0; k < m.Branches.Count; k++)
        {
            if (df.OutageIslands(k)) continue;
            double[] lodf = df.LodfColumn(k);
            var post = new double[f.Length];
            for (int l = 0; l < f.Length; l++) post[l] = l == k ? 0.0 : f[l] + lodf[l] * f[k];
            GridModel without = m.CopyWith(branches: m.Branches.Select((b, j) => j == k ? b with { InService = false } : b).ToList());
            double[] resolved = DistributionFactors.Build(without).DcFlows(net, m.BaseMva);
            Near($"{name} outage {k}", post, resolved, 1e-3 * m.BaseMva);
            checkedOutages++;
        }
        Assert.True(checkedOutages > 0);
    }

    [Theory]
    [InlineData("case9-shift5", 5)]
    [InlineData("case30-all", 5)]
    [InlineData("lib/case14.m", 7)]   // a tapped transformer (4-7, tap 0.978) with no shift yet
    public void Phase_shifter_sensitivity_equals_a_finite_re_solve(string name, int branch)
    {
        // The model is linear in phi, so (f(phi + d) - f(phi)) / d is the PSDF exactly in real arithmetic; d = 0.1 rad.
        // Rounding: each flow carries at most 4096 eps of its vector's norm, so the quotient carries 2 x that / d.
        GridModel m = Variant(name);
        const double d = 0.1;
        DistributionFactors df = DistributionFactors.Build(m);
        double[] psdf = PhaseShifterSensitivity.FlowSensitivityMwPerRad(m, df, branch);
        double[] f0 = DcPowerFlow.Solve(m).FlowMw;
        var shifted = m.Branches.ToList();
        shifted[branch] = shifted[branch] with { PhaseShiftDeg = shifted[branch].PhaseShiftDeg + d * 180.0 / Math.PI };
        double[] f1 = DcPowerFlow.Solve(m.CopyWith(branches: shifted)).FlowMw;
        double norm = Math.Max(f0.Max(Math.Abs), f1.Max(Math.Abs));
        double tol = 2 * Allowance * norm / d + Allowance * psdf.Max(Math.Abs);
        for (int l = 0; l < psdf.Length; l++)
        {
            double fd = (f1[l] - f0[l]) / d;
            Assert.True(Math.Abs(fd - psdf[l]) <= tol, $"{name} branch {l}: finite {fd:R} vs PSDF {psdf[l]:R} (tol {tol:R})");
        }
    }

    // ---- 6. consumers ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("lib/case14.m")]
    [InlineData("case30-all")]
    public void Dc_contingency_screen_post_outage_flows_equal_a_dc_re_solve(string name)
    {
        GridModel m = Variant(name);
        DcContingencyResult res = DcContingencyScreen.Screen(m, keepFlows: true);
        Near($"{name} base", res.BaseFlowsMw, DcPowerFlow.Solve(m).FlowMw, 1e-3 * m.BaseMva);
        Dictionary<int, double> net = NetInjections(m);
        int compared = 0;
        foreach (DcContingencyRow row in res.Rows)
        {
            if (row.Status == "ISLANDED") continue;
            int k = row.Outage;
            GridModel without = m.CopyWith(branches: m.Branches.Select((b, j) => j == k ? b with { InService = false } : b).ToList());
            Near($"{name} outage {k}", row.FlowsMw, DistributionFactors.Build(without).DcFlows(net, m.BaseMva), 1e-3 * m.BaseMva);
            compared++;
        }
        Assert.True(compared > 0);
    }

    [Fact]
    public void Dc_opf_meets_demand_plus_shunt_conductance_and_its_flows_are_the_dc_power_flow()
    {
        // case30-all has 30 MW of Gs. The DC-OPF balance is sum Pg = sum Pd + sum Gs (MATPOWER Pg = Pinj + Pload + Gs).
        GridModel m = Variant("case30-all");
        DcOptimalPowerFlow.DcOpfResult r = DcOptimalPowerFlow.Solve(m);
        Assert.True(r.Feasible);
        double demand = m.Buses.Sum(b => b.Pd) + 30.0;
        Assert.True(Math.Abs(r.TotalLoadMw - demand) <= Allowance * demand, $"load {r.TotalLoadMw:R} vs {demand:R}");
        Assert.True(Math.Abs(r.TotalGenMw - demand) <= DcOptimalPowerFlow.BalanceToleranceMw, $"generation {r.TotalGenMw:R} vs {demand:R}");

        // Flows of the optimal dispatch: the DC-OPF's own flow helper against DcPowerFlow on the dispatched model.
        double[] pg = r.Dispatch.Select(x => x.Pg).ToArray();
        GridModel dispatched = m.CopyWith(generators: m.Generators.Select((g, i) => g with { Pg = pg[i] }).ToList());
        Near("dcopf flows", DcOptimalPowerFlow.FlowsFrom(m, DistributionFactors.Build(m), pg), DcPowerFlow.Solve(dispatched).FlowMw, 1e-3 * m.BaseMva);
        foreach (DcOptimalPowerFlow.LineBinding b in r.BindingLines)
            Near($"binding {b.Branch}", [b.FlowMw], [DcPowerFlow.Solve(dispatched).FlowMw[b.Branch]], 1e-3 * m.BaseMva);
    }

    [Fact]
    public void Dc_scopf_flow_rows_carry_the_shift_and_its_balance_carries_the_gs()
    {
        // The three-bus hand case with one unit: the dispatch is forced to Pd + Gs = 70 MW and the LP's flows must be the
        // hand flows (P12 = 32 - 200 phi), not the unshifted ones (32 MW) DcScopf's own flow rows used to give. Tolerance:
        // the sparse simplex accepts each row to 1e-6 x max(1, row scale); every row scale here is below 1000 MW (flows
        // under 70 MW, b psi under 10 x 11.1), so 1e-6 x 6 rows x 1000 MW bounds the flow error.
        double phi = 5.0 * Math.PI / 180.0;
        ScopfResult r = DcScopf.Solve(ThreeBus(5.0, rate: 500), new ScopfOptions { Contingencies = [] });
        Assert.Equal(ScopfStatus.Optimal, r.Status);
        const double tol = 1e-6 * 6 * 1000;
        Assert.True(Math.Abs(r.DemandMw - 70.0) <= Allowance * 70.0, $"demand {r.DemandMw:R}");
        Assert.True(Math.Abs(r.Dispatch[0].Pmw - 70.0) <= tol, $"dispatch {r.Dispatch[0].Pmw:R}");
        double[] hand = DcPowerFlow.Solve(ThreeBus(5.0)).FlowMw;
        for (int l = 0; l < 3; l++) Assert.True(Math.Abs(r.BaseFlowsMw[l] - hand[l]) <= tol, $"branch {l}: {r.BaseFlowsMw[l]:R} vs {hand[l]:R}");
        Assert.True(Math.Abs(r.BaseFlowsMw[0] - (32 - 200 * phi)) <= tol, $"P12 {r.BaseFlowsMw[0]:R}");
    }

    [Fact]
    public void Unit_commitment_period_demand_adds_the_unscaled_shunt_conductance()
    {
        // Gs is a shunt, not load: the period scale applies to Pd only (60 MW x s), and the 10 MW of Gs is added as is.
        var o = new UcOptions { Periods = 2, DemandScale = [1.0, 0.5], EnforceLineLimits = false };
        UcResult r = UnitCommitment.Solve(ThreeBus(5.0), o);
        Assert.Equal(GridSim.Core.Numerics.MilpStatus.Optimal, r.Status);
        Assert.Equal(70.0, r.Periods[0].DemandMw);
        Assert.Equal(40.0, r.Periods[1].DemandMw);
        for (int t = 0; t < 2; t++)
            Assert.True(Math.Abs(r.Periods[t].GenerationMw - r.Periods[t].DemandMw) <= DcOptimalPowerFlow.BalanceToleranceMw,
                $"period {t + 1}: generation {r.Periods[t].GenerationMw:R} vs demand {r.Periods[t].DemandMw:R}");
    }

    // ---- 7. islands and Gs ----------------------------------------------------------------------------------------

    [Fact]
    public void Gs_at_an_island_reference_changes_only_its_reference_injection()
    {
        // Gs at the reference bus 1 sits on the dropped row of B': angles and flows are those without it, bit for bit,
        // and the reference supplies 10 MW more.
        GridModel without = ThreeBus(5.0, gs2: 0.0);
        GridModel withGs = without.CopyWith(buses: without.Buses.Select(b => b.Id == 1 ? b with { Gs = 10.0 } : b).ToList());
        DcPowerFlowResult r0 = DcPowerFlow.Solve(without), r1 = DcPowerFlow.Solve(withGs);
        Bits("angles", r0.VaDeg, r1.VaDeg);
        Bits("flows", r0.FlowMw, r1.FlowMw);
        Near("reference injection", [r1.Islands[0].ReferenceInjectionMw - r0.Islands[0].ReferenceInjectionMw], [10.0], 0.1);
        Assert.Equal(10.0, r1.Islands[0].ShuntLoadMw);
    }

    [Fact]
    public void A_dead_island_with_only_shunt_conductance_reports_it_and_cannot_be_dispatched()
    {
        GridModel tri = ThreeBus(0.0, gs2: 0.0, rate: 0.0);
        var m = tri.CopyWith(
            buses: [.. tri.Buses, new Bus(21, BusType.Pq), new Bus(22, BusType.Pq, Gs: 5.0)],
            branches: [.. tri.Branches, new Branch(21, 22, 0.0, 0.2)]);
        DcPowerFlowResult r = DcPowerFlow.Solve(m);
        DcIslandBalance dead = Assert.Single(r.Islands, i => !i.HasGeneration);
        Assert.Equal(0.0, dead.LoadMw);
        Assert.Equal(5.0, dead.ShuntLoadMw);
        Assert.Equal(5.0, dead.ReferenceInjectionMw);   // the reference (bus 21) would have to inject the 5 MW
        DcOptimalPowerFlow.DcOpfResult opf = DcOptimalPowerFlow.Solve(m);
        Assert.False(opf.Feasible);
        DcOptimalPowerFlow.IslandDispatch d = Assert.Single(opf.Islands!, i => !i.HasGeneration);
        Assert.Equal(5.0, d.LoadMw);
        Assert.False(d.Feasible);
    }

    // ---- 8. refusals ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void A_negative_or_non_finite_tap_is_refused_by_branch(double tap)
    {
        GridModel m = ThreeBus(0.0);
        GridModel bad = m.CopyWith(branches: m.Branches.Select((b, j) => j == 1 ? b with { TapRatio = tap } : b).ToList());
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => DcPowerFlow.Solve(bad));
        Assert.Contains("branch 1 ('2-3', 2->3)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tap ratio", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_out_of_service_branch_with_a_negative_tap_is_not_refused()
    {
        GridModel m = ThreeBus(0.0);
        GridModel off = m.CopyWith(branches: m.Branches.Select((b, j) => j == 1 ? b with { TapRatio = -1.0, InService = false } : b).ToList());
        Assert.Equal(0.0, DcPowerFlow.Solve(off).FlowMw[1]);
    }
}
