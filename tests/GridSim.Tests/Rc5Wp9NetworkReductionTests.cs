using System.Globalization;
using System.Numerics;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using GridSim.Core.PowerFlow.Equivalents;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP9: the Ward, extended Ward and REI network equivalents (<see cref="NetworkReduction"/>, <see cref="WardMath"/>)
/// and the <c>reduce</c> verb.
///
/// Sources of the expected values: the exactness algebra restated in each test (every reduced equation is an original one
/// rearranged, so the base case solves the reduced case up to round-off), a hand derivation for the three-bus case, and an
/// independent NumPy 2.5 implementation written for this package (dense MATPOWER-model Y-bus, numpy.linalg.solve for the
/// Schur complements, its own full Newton to 1e-14 pu). Every pinned figure below was recomputed with that script before it
/// was pinned. The common case is data/matpower/lib/case14.m with reactive limits off, keeping buses 1 to 5; the tie
/// branches 4-7, 4-9 and 5-6 make B = {4, 5}, and E = {6, ..., 14} holds the synchronous condensers at 6 and 8.
/// </summary>
public sealed class Rc5Wp9NetworkReductionTests
{
    private static readonly int[] Kept = { 1, 2, 3, 4, 5 };
    private const double Eps = 2.220446049250313e-16;

    /// <summary>Both solves: reactive limits off (PV buses hold their set-points), and a tolerance tight enough that the
    /// Newton stop sits below every bound asserted here.</summary>
    private static readonly PowerFlowOptions Tight = new() { EnforceQLimits = false, Tolerance = 1e-12 };

    private static GridModel Case14() => S10TestSupport.LoadMatpower("lib/case14.m").Model;

    private static PowerFlowResult Solve(GridModel m, PowerFlowOptions? o = null)
    {
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, o ?? Tight);
        Assert.True(r.Converged, $"solve of {m.Name} did not converge: {r.Status}");
        return r;
    }

    private static ReducedNetwork Reduce(GridModel m, PowerFlowResult r, EquivalentMethod method, ReiGrouping g = ReiGrouping.Generators)
        => NetworkReduction.Reduce(m, r, new ReductionSpec(Kept, method, g));

    private static Complex V(PowerFlowResult r, int id)
    {
        int i = r.BusIds.ToList().IndexOf(id);
        return DetMath.FromPolar(r.Vm[i], r.VaDeg[i] * Math.PI / 180.0);
    }

    /// <summary>The mismatch S_calc - S_spec (pu) of every bus of <paramref name="m"/> at the voltages
    /// <paramref name="v"/> (by bus id), with the model's own injections: generation minus load.</summary>
    private static Dictionary<int, Complex> Mismatch(GridModel m, Func<int, Complex> v)
    {
        YBus y = YBus.Build(m);
        int n = m.BusCount;
        var vv = new Complex[n];
        for (int i = 0; i < n; i++) vv[y.IndexOf(m.Buses[i].Id)] = v(m.Buses[i].Id);
        var spec = new Complex[n];
        foreach (Generator g in m.Generators) spec[y.IndexOf(g.Bus)] += new Complex(g.Pg, g.Qg) / m.BaseMva;
        foreach (Bus b in m.Buses) spec[y.IndexOf(b.Id)] -= new Complex(b.Pd, b.Qd) / m.BaseMva;
        var mis = new Dictionary<int, Complex>();
        foreach (Bus b in m.Buses)
        {
            int i = y.IndexOf(b.Id);
            Complex yi = Complex.Zero;
            for (int k = 0; k < n; k++) yi += y.Y[i, k] * vv[k];
            mis[b.Id] = vv[i] * Complex.Conjugate(yi) - spec[i];
        }
        return mis;
    }

    /// <summary>The largest mismatch at the PQ buses (|dS|) and at the PV buses (|dP|; Q is free there) among
    /// <paramref name="ids"/>.</summary>
    private static (double Pq, double Pv) Worst(GridModel m, Dictionary<int, Complex> mis, IEnumerable<int> ids)
    {
        var gen = new HashSet<int>(m.Generators.Select(g => g.Bus));
        double pq = 0, pv = 0;
        foreach (int id in ids)
        {
            Bus b = m.Buses.First(x => x.Id == id);
            if (b.Type == BusType.Slack) continue;
            if (b.Type == BusType.Pv && gen.Contains(id)) pv = Math.Max(pv, Math.Abs(mis[id].Real));
            else pq = Math.Max(pq, mis[id].Magnitude);
        }
        return (pq, pv);
    }

    private static Complex Element(ReducedNetwork r, EquivalentElementKind kind, string name) =>
        r.Elements.Single(e => e.Kind == kind && e.Name == name).Value;

    private static void Near(double expected, double actual, double tol, string what) =>
        Assert.True(Math.Abs(actual - expected) <= tol, $"{what}: {actual:R} vs {expected:R} (|diff| {Math.Abs(actual - expected):R} > {tol:R})");

    // ---- 1. Ward: Y_eq and the equivalent injections ------------------------------------------------------------------

    [Fact]
    public void T01_ward_equivalent_admittance_and_injections_match_the_independent_values()
    {
        GridModel m = Case14();
        PowerFlowResult r = Solve(m);
        ReducedNetwork red = Reduce(m, r, EquivalentMethod.Ward);
        Assert.Equal(new[] { 4, 5 }, red.BoundaryBusIds);
        Assert.Equal(Enumerable.Range(6, 9), red.ExternalBusIds);

        // Y_eq,45 is the negative of the branch admittance; Y_eq,44 and Y_eq,55 are the shunt plus the branch y.
        Complex y45 = Element(red, EquivalentElementKind.Branch, "EQ:4-5");
        Complex s4 = Element(red, EquivalentElementKind.Shunt, "EQ-SH:4"), s5 = Element(red, EquivalentElementKind.Shunt, "EQ-SH:5");
        // NumPy: Y_44 = 0.25905345615597986 - j1.2657303680855607, Y_45 = -0.26048389726089655 + j1.481770265926228,
        // Y_55 = 0.2619222369740192 - j1.4899523034403201 (the spec's figures; NumPy here agrees to 1e-15).
        Near(-0.26048389726089655, -y45.Real, 1e-12, "Y45 re");
        Near(1.481770265926228, -y45.Imaginary, 1e-12, "Y45 im");
        Near(0.25905345615597986, (s4 + y45).Real, 1e-12, "Y44 re");
        Near(-1.2657303680855607, (s4 + y45).Imaginary, 1e-12, "Y44 im");
        Near(0.2619222369740192, (s5 + y45).Real, 1e-12, "Y55 re");
        Near(-1.4899523034403201, (s5 + y45).Imaginary, 1e-12, "Y55 im");

        // dS_b = V_b conj(dI_b), dI_B = -Y_BE Y_EE^-1 I_E. NumPy: -48.47031257646785 - j11.762167926061588 MVA and
        // -39.749996184250456 - j12.012220427951647 MVA (to 4e-13 MVA).
        Complex d4 = Element(red, EquivalentElementKind.Injection, "EQ-S:4") * 100, d5 = Element(red, EquivalentElementKind.Injection, "EQ-S:5") * 100;
        Near(-48.47031257646785, d4.Real, 1e-9, "dS4 P");
        Near(-11.762167926061588, d4.Imaginary, 1e-9, "dS4 Q");
        Near(-39.749996184250456, d5.Real, 1e-9, "dS5 P");
        Near(-12.012220427951647, d5.Imaginary, 1e-9, "dS5 Q");

        // The reduced case: buses 1..5, the five internal branches plus EQ:4-5, the three internal generators.
        Assert.Equal(Kept, red.Model.Buses.Select(b => b.Id));
        Assert.Equal(8, red.Model.Branches.Count);
        Assert.Equal(3, red.Model.Generators.Count);
        Assert.Contains(red.Notes, n => n.StartsWith("removed 9 external bus(es), 13 branch(es) (external and tie), 2 generator(s) and 7 load bus(es)", StringComparison.Ordinal));
    }

    // ---- 2. Ward: the reduced equations at the full model's solved voltages (machine epsilon) -------------------------

    [Fact]
    public void T02_ward_reduced_mismatch_at_the_full_voltages_is_the_full_mismatch_to_round_off()
    {
        GridModel m = Case14();
        PowerFlowResult r = Solve(m);
        ReducedNetwork red = Reduce(m, r, EquivalentMethod.Ward);

        // In exact arithmetic the reduced residual at every kept bus equals the full model's residual there (the internal
        // rows are the same rows; the boundary rows are the full rows with the E rows substituted, and I_E was taken from
        // the solved state, so the E rows hold exactly). So the comparison is round-off only, whatever the Newton stop:
        // the difference is bounded by a few eps times the row's largest term (|Y_ij V_j| <= 40 pu here), about 1e-14.
        Dictionary<int, Complex> full = Mismatch(m, id => V(r, id));
        Dictionary<int, Complex> reduced = Mismatch(red.Model, id => V(r, id));
        double diff = Kept.Where(id => id != 1).Max(id => (reduced[id] - full[id]).Magnitude);
        Assert.True(diff <= 1e-13, $"reduced - full residual {diff:R}");

        // And absolutely, with the full solve at its 1e-12 stop: the spec's 1e-12 at the PQ and PV buses.
        (double pq, double pv) = Worst(red.Model, reduced, Kept);
        Assert.True(pq <= 1e-12 && pv <= 1e-12, $"PQ {pq:R}, PV {pv:R}");
    }

    // ---- 3. Ward: the reduced case solved by Newton from a flat start ---------------------------------------------------

    [Fact]
    public void T03_ward_reduced_case_solves_to_the_full_voltages_flows_and_slack()
    {
        GridModel m = Case14();
        PowerFlowResult r = Solve(m);
        ReducedNetwork red = Reduce(m, r, EquivalentMethod.Ward);
        PowerFlowResult s = Solve(red.Model, Tight with { FlatStart = true });

        // Both solves stop with a residual below 1e-12 pu and (T2) the two residual maps agree to round-off at the full
        // state, so the voltages differ by at most ||J^-1|| times about 2e-12; on case14 the observed figure is 1e-15
        // (NumPy: 6.8e-16).
        double dv = NetworkReduction.MaxVoltageDifference(r, s, Kept);
        Assert.True(dv <= 1e-12, $"max |dV| {dv:R}");

        // Every internal branch flow (both ends, MVA) and the slack P and Q agree to 1e-9.
        for (int k = 0; k < m.Branches.Count; k++)
        {
            Branch br = m.Branches[k];
            if (br.From > 5 || br.To > 5) continue;
            int j = red.Model.Branches.ToList().FindIndex(b => b.From == br.From && b.To == br.To);
            LineFlow a = r.LineFlows[k], b = s.LineFlows[j];
            Assert.True(Math.Abs(a.PFromMw - b.PFromMw) <= 1e-9 && Math.Abs(a.QFromMvar - b.QFromMvar) <= 1e-9
                && Math.Abs(a.PToMw - b.PToMw) <= 1e-9 && Math.Abs(a.QToMvar - b.QToMvar) <= 1e-9, $"branch {br.From}-{br.To}");
        }
        Near(r.PInjMw[0], s.PInjMw[0], 1e-9, "slack P");
        Near(r.QInjMvar[0], s.QInjMvar[0], 1e-9, "slack Q");

        // The full model's boundary |V| (NumPy: 1.017670853691765 and 1.0195138598190605).
        Near(1.017670853691765, r.Vm[3], 1e-10, "|V4|");
        Near(1.0195138598190605, r.Vm[4], 1e-10, "|V5|");
    }

    // ---- 4. Extended Ward ---------------------------------------------------------------------------------------------

    [Fact]
    public void T04_extended_ward_sources_match_and_the_base_case_stays_exact()
    {
        GridModel m = Case14();
        PowerFlowResult r = Solve(m);
        ReducedNetwork red = Reduce(m, r, EquivalentMethod.ExtendedWard);

        // d_b = sum_j (Y_g - Y_eq)_bj, Y_g with the PV buses 6 and 8 grounded. NumPy: d_4 = 0.25452509094986836 -
        // j4.120194623205634, d_5 = -0.0014383397131226272 - j4.559892356534604.
        Complex d4 = Element(red, EquivalentElementKind.ExtendedWardSource, "XW:4");
        Complex d5 = Element(red, EquivalentElementKind.ExtendedWardSource, "XW:5");
        Near(0.25452509094986836, d4.Real, 1e-12, "d4 re");
        Near(-4.120194623205634, d4.Imaginary, 1e-12, "d4 im");
        Near(-0.0014383397131226272, d5.Real, 1e-12, "d5 re");
        Near(-4.559892356534604, d5.Imaginary, 1e-12, "d5 im");

        // Fictitious ids max(id) + 1, + 2 in boundary order, PV at the base |V_b|, angle V_b, P = 0, Q unlimited.
        Bus x4 = red.Model.Buses.Single(b => b.Id == 15), x5 = red.Model.Buses.Single(b => b.Id == 16);
        Assert.Equal(("XW:4", "XW:5"), (x4.Name, x5.Name));
        Generator g15 = red.Model.Generators.Single(g => g.Bus == 15);
        Assert.True(g15.PowerFlowOnly && g15.Pg == 0 && double.IsNegativeInfinity(g15.Qmin) && double.IsPositiveInfinity(g15.Qmax));
        Assert.Equal(r.Vm[3], g15.Vg);

        // At the full state with each XW bus at V_b, the XW branch carries d (V_b - V_b) = 0 exactly, and the mismatch at
        // buses 1 to 5 and at the XW buses (P only: they are PV) is the Ward one.
        Complex VX(int id) => id == 15 ? V(r, 4) : id == 16 ? V(r, 5) : V(r, id);
        Dictionary<int, Complex> mis = Mismatch(red.Model, VX);
        (double pq, double pv) = Worst(red.Model, mis, Kept.Concat(new[] { 15, 16 }));
        Assert.True(pq <= 1e-12 && pv <= 1e-12, $"PQ {pq:R}, PV {pv:R}");
        Assert.Equal(Complex.Zero, d4 * (VX(4) - VX(15)));

        PowerFlowResult s = Solve(red.Model, Tight with { FlatStart = true });
        double dv = NetworkReduction.MaxVoltageDifference(r, s, Kept);
        Assert.True(dv <= 1e-12, $"max |dV| {dv:R}");
        // The solved XW branch current is round-off: |d| |V_X - V_b| with both solved to 1e-12.
        double ix = (d4 * (V(s, 15) - V(s, 4))).Magnitude;
        Assert.True(ix <= 1e-11, $"XW:4 current {ix:R}");
    }

    // ---- 5. REI --------------------------------------------------------------------------------------------------------

    [Fact]
    public void T05_rei_bus_and_zpb_network_match_and_the_base_case_stays_exact()
    {
        GridModel m = Case14();
        PowerFlowResult r = Solve(m);
        ReducedNetwork red = Reduce(m, r, EquivalentMethod.Rei);

        // S_R = S_6 + S_8 (the condensers' output at the solved state). NumPy: S_R = 0 + j30.354395775362498 Mvar,
        // |V_R| = 1.0815513178991354, arg V_R = -13.724762010059429 deg.
        Complex sr = Element(red, EquivalentElementKind.ReiBus, "REI:gen") * 100;
        Near(0.0, sr.Real, 1e-9, "P_R");
        Near(30.354395775362498, sr.Imaginary, 1e-9, "Q_R");
        Bus rb = red.Model.Buses.Single(b => b.Name == "REI:gen");
        Assert.Equal(15, rb.Id);
        Assert.Equal(BusType.Pv, rb.Type);
        Near(1.0815513178991354, rb.Vm, 1e-12, "|V_R|");
        Near(-13.724762010059429, rb.VaDeg, 1e-10, "arg V_R");
        Generator gr = red.Model.Generators.Single(g => g.Bus == 15);
        Assert.Equal(-12.0, gr.Qmin);   // the summed limits of the units at 6 and 8: -6 + -6 and 24 + 24
        Assert.Equal(48.0, gr.Qmax);

        // The ZPB node voltage. V_0 = (sum y_k V_k + y_R V_R) / (sum y_k + y_R) is 0 in exact arithmetic, but the
        // denominator cancels: y_k = j Q_k / |V_k|^2 and y_R = -j Q_R / |V_R|^2 nearly balance (|sum| = 3.6e-5 pu
        // against terms of 0.1 to 0.3 pu), so the spec's 1e-15 is not reachable.
        //
        // The bound, counted from the operations of WardMath.ZeroPowerBalance and ZpbNodeVoltage (standard model, unit
        // roundoff u = eps/2, first order in u; T = sum|y_k V_k| + |y_R V_R|, n = 2 terms):
        //  - complex product: within 2 sqrt2 u |x||y| (Higham, Accuracy and Stability of Numerical Algorithms, Lemma 3.5);
        //  - |z|^2 then a real division of each component: 3u;
        //  - System.Numerics complex division is Smith's algorithm: its denominator c + d(d/c) has no cancellation (3u), each
        //    numerator a + b(d/c) adds 2u|b| <= 2u|x| over a denominator >= |y|, and the last division adds u: within 7u.
        //  - In exact arithmetic y_k V_k = -I_k and y_R V_R = I_R with I_R the computed sum, so the rounding of S_R and
        //    I_R cancels out. Left: each y_k V_k term (3 + 2 sqrt2)u |I_k|; the quotients S_k/V_k in I_R 7u |I_k|; the
        //    n - 1 additions of I_R (n - 1)u sum|I_k|; the y_R V_R term (7 + 3 + 2 sqrt2)u |I_R| (V_R's quotient, the norm
        //    and division, the product); the n additions of the numerator n u T.
        //  - |num| <= (n + max(9 + n + 2 sqrt2, 10 + 2 sqrt2)) u T = 15.83 u T for n = 2. The last division and the test's own
        //    T and |sum y| add O(u) relative, covered by the factor (1 + 32u).
        // So |V_0| <= 15.83 u T / |sum y| = 2.7e-11 V on this case. NumPy's |V_0| is 3.1e-12 (its own rounding), well inside.
        YBus y = YBus.Build(m);
        var s = new List<Complex>();
        var vk = new List<Complex>();
        foreach (int k in new[] { 6, 8 })
        {
            int i = y.IndexOf(k);
            Complex yi = Complex.Zero;
            for (int j = 0; j < m.BusCount; j++) yi += y.Y[i, j] * V(r, m.Buses[j].Id);
            Bus b = m.Buses[i];
            s.Add(V(r, k) * Complex.Conjugate(yi) + new Complex(b.Pd, b.Qd) / 100);
            vk.Add(V(r, k));
        }
        WardMath.Zpb z = WardMath.ZeroPowerBalance(s, vk);
        Complex v0 = WardMath.ZpbNodeVoltage(z, vk);
        double terms = z.Yk.Select((yk, q) => (yk * vk[q]).Magnitude).Sum() + (z.Yr * z.Vr).Magnitude;
        double den = (z.Yk[0] + z.Yk[1] + z.Yr).Magnitude;
        const double u = Eps / 2;
        const int n = 2;
        double c = n + Math.Max(9 + n + 2 * Math.Sqrt(2), 10 + 2 * Math.Sqrt(2));
        Assert.True(Math.Abs(c - 15.828427124746192) < 1e-12, $"c {c:R}");
        double bound = c * u * terms / den * (1 + 32 * u);
        Assert.True(v0.Magnitude <= bound, $"|V_0| {v0.Magnitude:R} > bound {bound:R}");
        Assert.True(bound > 2.7e-11 && bound < 2.8e-11, $"bound {bound:R}");
        Assert.True(den > 3e-5 && den < 4e-5, $"|sum y| {den:R}");
        Near(1.0815513178991354, Math.Sqrt(WardMath.Norm(z.Vr)), 1e-12, "|V_R| from WardMath");

        // The reduced mismatch at the full state (R at V_R) and the solve.
        Complex VR(int id) => id == 15 ? z.Vr : V(r, id);
        Dictionary<int, Complex> mis = Mismatch(red.Model, VR);
        (double pq, double pv) = Worst(red.Model, mis, Kept.Concat(new[] { 15 }));
        Assert.True(pq <= 1e-12 && pv <= 1e-12, $"PQ {pq:R}, PV {pv:R}");
        PowerFlowResult sol = Solve(red.Model, Tight with { FlatStart = true });
        double dv = NetworkReduction.MaxVoltageDifference(r, sol, Kept);
        Assert.True(dv <= 1e-12, $"max |dV| {dv:R}");
    }

    // ---- 6. Hand case ------------------------------------------------------------------------------------------------

    [Fact]
    public void T06_three_bus_hand_case_has_no_equivalent_branch_and_the_load_plus_tie_loss()
    {
        // Slack 1 - j0.1 - bus 2 - j0.1 - bus 3, 50 MW at bus 3, keep {1, 2}. Y^x on {2, 3} is the tie alone:
        // [[y, -y], [-y, y]], y = -j10, so Y_eq = y - y y / y = 0: no branch and no shunt (exactly, here). dI_2 =
        // -(-y) y^-1 I_3 = I_3, and V_2 = V_3 - jX I_3, so dS_2 = V_2 conj(I_3) = S_3 - jX |I_3|^2 with S_3 = -0.5 pu and
        // |I_3| = 0.5 / |V_3|.
        var m = new GridModel
        {
            BaseMva = 100,
            Name = "hand3",
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq), new Bus(3, BusType.Pq, Pd: 50) },
            Generators = new[] { new Generator(1, 50) },
            Branches = new[] { new Branch(1, 2, 0, 0.1), new Branch(2, 3, 0, 0.1) },
        };
        PowerFlowResult r = Solve(m);
        ReducedNetwork red = NetworkReduction.Reduce(m, r, new ReductionSpec(new[] { 1, 2 }, EquivalentMethod.Ward));
        Assert.DoesNotContain(red.Elements, e => e.Kind is EquivalentElementKind.Branch or EquivalentElementKind.Shunt);
        Complex dS = Element(red, EquivalentElementKind.Injection, "EQ-S:2");
        double i3sq = 0.25 / (r.Vm[2] * r.Vm[2]);
        var expected = new Complex(-0.5, -0.1 * i3sq);
        Assert.True((dS - expected).Magnitude <= 1e-13, $"dS_2 {dS} vs {expected}");
        Assert.Single(red.Model.Branches);   // 1-2 and nothing else
        // Bus 2 carries -dS as load: 50 MW and X |I_3|^2 Sbase = 10 |I_3|^2 Mvar.
        Near(50, red.Model.Buses[1].Pd, 1e-11, "bus 2 Pd");
        Near(10 * i3sq, red.Model.Buses[1].Qd, 1e-11, "bus 2 Qd");
    }

    // ---- Perturbation: the stated error ---------------------------------------------------------------------------------

    /// <summary>The loads at buses 3 and 4 scaled by 1.2 in P and Q (the original loads only: the equivalent injection on
    /// bus 4 stays frozen).</summary>
    private static GridModel Scale(GridModel model, GridModel original)
    {
        var buses = model.Buses.Select(b =>
        {
            if (b.Id is not (3 or 4)) return b;
            Bus o = original.Buses.Single(x => x.Id == b.Id);
            return b with { Pd = b.Pd + 0.2 * o.Pd, Qd = b.Qd + 0.2 * o.Qd };
        }).ToList();
        return model.CopyWith(buses: buses);
    }

    [Theory]
    // NumPy (the spec's figures, which NumPy here reproduces to 1e-15 pu and 4e-13 MVA): the largest boundary
    // | |V_red| - |V_full| | and the largest |dS| (MVA, either end) over the internal branches, with its branch.
    [InlineData(EquivalentMethod.Ward, 6.329073012851794e-4, 0.36739575085202064, 3, 4)]
    [InlineData(EquivalentMethod.ExtendedWard, 3.942355790220731e-5, 0.07973906095373332, 4, 5)]
    [InlineData(EquivalentMethod.Rei, 6.969321457273026e-5, 0.05701400270564336, 4, 5)]
    public void T07_perturbation_error_is_the_stated_figure(EquivalentMethod method, double dvExpected, double dsExpected, int from, int to)
    {
        GridModel m = Case14();
        PowerFlowResult r = Solve(m);
        ReducedNetwork red = Reduce(m, r, method);
        PowerFlowResult full = Solve(Scale(m, m));
        PowerFlowResult s = Solve(Scale(red.Model, m));

        double dv = red.BoundaryBusIds.Max(id => Math.Abs(s.Vm[s.BusIds.ToList().IndexOf(id)] - full.Vm[full.BusIds.ToList().IndexOf(id)]));
        Near(dvExpected, dv, 1e-9, $"{method} boundary |V| error");

        double worst = 0; (int, int) at = (0, 0);
        for (int k = 0; k < m.Branches.Count; k++)
        {
            Branch br = m.Branches[k];
            if (br.From > 5 || br.To > 5) continue;
            int j = red.Model.Branches.ToList().FindIndex(b => b.From == br.From && b.To == br.To);
            LineFlow a = full.LineFlows[k], b = s.LineFlows[j];
            double e = Math.Max(new Complex(a.PFromMw - b.PFromMw, a.QFromMvar - b.QFromMvar).Magnitude,
                new Complex(a.PToMw - b.PToMw, a.QToMvar - b.QToMvar).Magnitude);
            if (e > worst) { worst = e; at = (br.From, br.To); }
        }
        Near(dsExpected, worst, 1e-6, $"{method} worst internal flow error");
        Assert.Equal((from, to), at);

        // The full model's boundary |V| under the change (NumPy: 1.0160091460480294 and 1.0180274127640931).
        Near(1.0160091460480294, full.Vm[3], 1e-10, "|V4| perturbed");
        Near(1.0180274127640931, full.Vm[4], 1e-10, "|V5| perturbed");
    }

    [Fact]
    public void T08_extended_ward_and_rei_cut_the_ward_error()
    {
        GridModel m = Case14();
        PowerFlowResult r = Solve(m);
        PowerFlowResult full = Solve(Scale(m, m));
        double Err(EquivalentMethod method)
        {
            PowerFlowResult s = Solve(Scale(Reduce(m, r, method).Model, m));
            return new[] { 4, 5 }.Max(id => Math.Abs(s.Vm[s.BusIds.ToList().IndexOf(id)] - full.Vm[full.BusIds.ToList().IndexOf(id)]));
        }
        double w = Err(EquivalentMethod.Ward), x = Err(EquivalentMethod.ExtendedWard), rei = Err(EquivalentMethod.Rei);
        // Only the order is asserted. The ratios on this case (16.05 and 9.08, from the NumPy figures in technical chapter 0040) are exact for this case
        // and this change only, and no threshold is fitted to them.
        Assert.True(x < w && rei < w, $"ward {w:R}, xward {x:R}, rei {rei:R}");
    }

    // ---- REI with two groups ------------------------------------------------------------------------------------------

    [Fact]
    public void T09_rei_generator_and_load_groups_keep_the_base_case_exact()
    {
        // With gen,load the external loads leave E for a second fictitious bus (PQ, the aggregated load plus its own Ward
        // injection). The ZPB algebra is the same, so the base case is still a solution of the reduced case.
        GridModel m = Case14();
        PowerFlowResult r = Solve(m);
        ReducedNetwork red = Reduce(m, r, EquivalentMethod.Rei, ReiGrouping.GeneratorsAndLoads);
        Bus rg = red.Model.Buses.Single(b => b.Name == "REI:gen"), rl = red.Model.Buses.Single(b => b.Name == "REI:load");
        Assert.Equal((15, 16), (rg.Id, rl.Id));
        Assert.Equal(BusType.Pq, rl.Type);
        // The load group's S_R is minus the summed external load: 11.2+29.5+9+3.5+6.1+13.5+14.9 = 87.7 MW and
        // 7.5+16.6+5.8+1.8+1.6+5.8+5 = 44.1 Mvar.
        Complex sl = Element(red, EquivalentElementKind.ReiBus, "REI:load") * 100;
        Near(-87.7, sl.Real, 1e-12, "load P");
        Near(-44.1, sl.Imaginary, 1e-12, "load Q");
        PowerFlowResult s = Solve(red.Model, Tight with { FlatStart = true });
        double dv = NetworkReduction.MaxVoltageDifference(r, s, Kept);
        Assert.True(dv <= 1e-12, $"max |dV| {dv:R}");
    }

    // ---- Guards ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void T10_refusals_name_the_element()
    {
        GridModel m = Case14();
        PowerFlowResult r = Solve(m);
        string Refused(GridModel model, PowerFlowResult res, IReadOnlyList<int> keep, EquivalentMethod method = EquivalentMethod.Ward) =>
            Assert.Throws<InvalidOperationException>(() => NetworkReduction.Reduce(model, res, new ReductionSpec(keep, method))).Message;

        Assert.Contains("slack bus 1 is external", Refused(m, r, new[] { 2, 3, 4, 5 }), StringComparison.Ordinal);
        Assert.Contains("is empty", Refused(m, r, Array.Empty<int>()), StringComparison.Ordinal);
        Assert.Contains("covers every bus", Refused(m, r, Enumerable.Range(1, 14).ToArray()), StringComparison.Ordinal);
        Assert.Contains("bus 99 is not a bus", Refused(m, r, new[] { 1, 99 }), StringComparison.Ordinal);

        // A phase shifter in E (7-9 at 10 degrees) makes Y_eq non-reciprocal.
        GridModel pst = m.CopyWith(branches: m.Branches.Select(b => b.From == 7 && b.To == 9 ? b with { PhaseShiftDeg = 10 } : b).ToList());
        Assert.Contains("not reciprocal between buses 4 and 5", Refused(pst, Solve(pst), Kept), StringComparison.Ordinal);

        // A ZIP load in E.
        GridModel zip = m.CopyWith(buses: m.Buses.Select(b => b.Id == 9 ? b with { Zip = new ZipLoad(Ap: 1, Cp: 0, Aq: 1, Cq: 0) } : b).ToList());
        Assert.Contains("bus 9 has a ZIP", Refused(zip, Solve(zip), Kept), StringComparison.Ordinal);

        // An OLTC on a tie transformer (4-9) controlling an external bus.
        GridModel named = m.CopyWith(branches: m.Branches.Select(b => b.From == 4 && b.To == 9 ? b with { Name = "T49" } : b).ToList());
        GridModel oltc = named.CopyWith(oltcs: new[] { new Oltc("T49", 9) });
        Assert.Contains("OLTC on 'T49'", Refused(oltc, r, Kept), StringComparison.Ordinal);

        // A base case that did not converge.
        PowerFlowResult bad = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = false, MaxIterations = 1, FlatStart = true, RetryFromDcStart = false });
        Assert.False(bad.Converged);
        Assert.Contains("not converged", Refused(m, bad, Kept), StringComparison.Ordinal);
    }

    [Fact]
    public void T11_bordered_reduction_keeps_the_kron_block_bit_for_bit_and_solves_the_currents()
    {
        // WardMath.TransferredCurrents borders the matrix with a column z holding I_E. Row z is zero off the diagonal, so
        // the kept block must be the plain reduction's, bit for bit, and column z must equal -Y_KE Y_EE^-1 I_E (checked
        // against a dense solve of Y_EE x = I_E, to 1e-14 relative).
        GridModel m = Case14();
        SparseYBus yb = YBus.BuildSparse(m);
        var keep = new[] { 0, 3, 4 };
        Complex[,] plain = SparseKronReduction.Reduce(14, yb.Cells, keep);
        var inj = new Complex[14];
        for (int e = 0; e < 14; e++) if (Array.IndexOf(keep, e) < 0) inj[e] = new Complex(0.01 * (e + 1), -0.02 * e);
        var bordered = new Dictionary<(int Row, int Col), Complex>(yb.Cells);
        for (int e = 0; e < 14; e++) if (inj[e] != Complex.Zero) bordered[(e, 14)] = Complex.Zero + inj[e];
        bordered[(14, 14)] = Complex.One;
        Complex[,] big = SparseKronReduction.Reduce(15, bordered, keep.Append(14).ToArray());
        for (int a = 0; a < 3; a++)
            for (int b = 0; b < 3; b++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(plain[a, b].Real), BitConverter.DoubleToInt64Bits(big[a, b].Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(plain[a, b].Imaginary), BitConverter.DoubleToInt64Bits(big[a, b].Imaginary));
            }
        Complex[] dI = WardMath.TransferredCurrents(14, yb.Cells, keep, inj);

        int[] ext = Enumerable.Range(0, 14).Where(i => Array.IndexOf(keep, i) < 0).ToArray();
        var yee = new Complex[ext.Length, ext.Length];
        var dense = new Complex[14, 14];
        foreach (var c in yb.Cells) dense[c.Key.Row, c.Key.Col] = c.Value;
        for (int a = 0; a < ext.Length; a++) for (int b = 0; b < ext.Length; b++) yee[a, b] = dense[ext[a], ext[b]];
        Complex[] x = DenseComplexLuSolver.SolveDense(yee, ext.Select(e => inj[e]).ToArray(), ext.Length);
        for (int a = 0; a < 3; a++)
        {
            Complex want = Complex.Zero;
            for (int q = 0; q < ext.Length; q++) want -= dense[keep[a], ext[q]] * x[q];
            Assert.True((dI[a] - want).Magnitude <= 1e-14 * (1 + want.Magnitude), $"node {keep[a]}: {dI[a]} vs {want}");
            Assert.Equal(dI[a], big[a, 3]);
        }
    }

    [Fact]
    public void T12_the_written_case_is_byte_identical_across_runs_and_cultures_and_round_trips()
    {
        GridModel m = Case14();
        string Write(EquivalentMethod method) => MatpowerWriter.Write(Reduce(m, Solve(m), method).Model).Text;
        foreach (EquivalentMethod method in new[] { EquivalentMethod.Ward, EquivalentMethod.ExtendedWard, EquivalentMethod.Rei })
        {
            string a = Write(method), b = Write(method), c = "";
            CultureInfo old = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); c = Write(method); }
            finally { CultureInfo.CurrentCulture = old; }
            Assert.Equal(a, b);
            Assert.Equal(a, c);
        }

        // Round trip: the Ward case written as text, read back and solved (as `pf` reads a .m file) gives the boundary
        // voltages of T3 to 1e-10 ("R" formatting makes the text exact, so the read-back case is the same case).
        PowerFlowResult r = Solve(m);
        GridModel back = MatpowerParser.Parse(Write(EquivalentMethod.Ward), "back");
        PowerFlowResult s = Solve(back, Tight with { FlatStart = true });
        Assert.True(NetworkReduction.MaxVoltageDifference(r, s, new[] { 4, 5 }) <= 1e-10);
    }

    [Fact]
    public void T13_cli_reduce_writes_a_case_pf_solves_to_the_full_boundary_voltages()
    {
        string dir = TestTemp.Under("gridsim-wp9-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string casePath = Path.Combine(S10TestSupport.DataDir, "matpower", "lib", "case14.m");
            string outA = Path.Combine(dir, "a.m"), outB = Path.Combine(dir, "b.m"), json = Path.Combine(dir, "pf.json");
            CliProcess.Result r1 = CliProcess.RunBuilt("reduce", casePath, "--keep", "1,2,3,4,5", "--method", "ward", "--out", outA);
            Assert.True(r1.ExitCode == 0, r1.ToString());
            Assert.StartsWith("reduce - ward equivalent of case14: 14 buses -> 5 buses", r1.StdOut, StringComparison.Ordinal);
            Assert.Contains("boundary buses: 4, 5", r1.StdOut, StringComparison.Ordinal);
            Assert.Contains("base-case check: max |V_reduced - V_full| at the boundary", r1.StdOut, StringComparison.Ordinal);
            CliProcess.Result r2 = CliProcess.RunBuilt("reduce", casePath, "--keep", "1,2,3,4,5", "--method", "ward", "--out", outB);
            Assert.Equal(r1.StdOut.Replace(outA, "X", StringComparison.Ordinal), r2.StdOut.Replace(outB, "X", StringComparison.Ordinal));
            Assert.Equal(File.ReadAllBytes(outA), File.ReadAllBytes(outB));

            CliProcess.Result pf = CliProcess.RunBuilt("pf", outA, "--json", json);
            Assert.True(pf.ExitCode == 0, pf.ToString());
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            PowerFlowResult full = Solve(Case14());
            foreach (System.Text.Json.JsonElement row in doc.RootElement.GetProperty("tables").GetProperty("buses").EnumerateArray())
            {
                int id = row.GetProperty("bus").GetInt32();
                double vm = double.Parse(row.GetProperty("vm_pu").GetRawText(), CultureInfo.InvariantCulture);
                double va = double.Parse(row.GetProperty("va_deg").GetRawText(), CultureInfo.InvariantCulture);
                if (id is 4 or 5)
                    Assert.True((DetMath.FromPolar(vm, va * Math.PI / 180) - V(full, id)).Magnitude <= 1e-10, $"bus {id}");
            }

            // Usage and refusals.
            Assert.Equal(2, CliProcess.RunBuilt("reduce", casePath, "--keep", "1,2", "--method", "magic").ExitCode);
            Assert.Equal(2, CliProcess.RunBuilt("reduce", casePath, "--method", "ward").ExitCode);
            Assert.Equal(2, CliProcess.RunBuilt("reduce", casePath, "--keep", "1,2", "--method", "ward", "--rei-groups", "gen").ExitCode);
            Assert.Equal(2, CliProcess.RunBuilt("reduce", casePath, "--keep", "1,2", "--method", "ward", "--top", "3").ExitCode);
            Assert.Equal(1, CliProcess.RunBuilt("reduce", casePath, "--keep", "2,3,4,5", "--method", "ward").ExitCode);   // slack external
            CliProcess.Result rei = CliProcess.RunBuilt("reduce", casePath, "--keep", "1,2,3,4,5", "--method", "rei", "--rei-groups", "gen,load",
                "--out", Path.Combine(dir, "rei.m"));
            Assert.True(rei.ExitCode == 0, rei.ToString());
            Assert.True(File.Exists(Path.Combine(dir, "rei.m")));
            // The v2 JSON writer keeps its own rules: case14 has baseKV 0, which a v2 bus may not carry, so it is refused
            // (exit 1).
            string j = Path.Combine(dir, "w.json");
            Assert.Equal(1, CliProcess.RunBuilt("reduce", casePath, "--keep", "1,2,3,4,5", "--method", "ward", "--out", j).ExitCode);
            Assert.Contains("reduce", CliProcess.RunBuilt("reduce", "--help").StdOut, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }
}
