using System.Globalization;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Opf;
using GridSim.Core.Analysis.Reconfiguration;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP7: loss and reactive power OPF with continuous taps and shunts and the discrete rounding rule
/// (<see cref="AcOpfExtended"/>, <see cref="OpfRounding"/>), the AC-SCOPF (<see cref="AcScopf"/>) and feeder
/// reconfiguration (<see cref="FeederReconfiguration"/>).
///
/// Sources of the expected values, per test: hand derivations restated in each test (two-bus cases); SciPy 1.18 (SLSQP,
/// then an active-set KKT Newton polish to |g| &lt; 2e-14) for the loss-minimised case9 and case30 figures, which MATPOWER
/// does not publish; Goswami and Basu (IEEE Trans. PWRD 7(3), 1992) for the case33bw optimum. Each analytic figure was
/// recomputed with Python's math module before it was pinned here.
/// </summary>
public sealed class Rc5Wp7OptimisationTests
{
    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);
    private static GridModel Case(string rel) => S10TestSupport.LoadMatpower(rel).Model;

    // ---- T0: the MIPS copy reproduces AcOpfInteriorPoint bit for bit -------------------------------------------------

    [Theory]
    [InlineData("lib/case9.m")]
    [InlineData("lib/case30.m")]
    public void T0_copy_with_one_block_no_controls_and_the_cost_objective_gives_the_same_bits(string rel)
    {
        GridModel m = Case(rel);
        AcOpfIpmResult a = AcOpfInteriorPoint.Solve(m);
        AcOpfExtendedResult e = AcOpfExtended.Solve(m, new AcOpfExtendedOptions());
        AcOpfIpmResult b = e.Base;
        Assert.True(a.Converged);
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(a.Message, b.Message);
        Assert.Equal(Bits(a.ObjectivePerHour), Bits(b.ObjectivePerHour));
        Assert.Equal(Bits(a.TotalGenerationMw), Bits(b.TotalGenerationMw));
        Assert.Equal(Bits(a.TotalLoadMw), Bits(b.TotalLoadMw));
        Assert.Equal(a.Buses.SelectMany(x => new[] { x.VmPu, x.VaDeg, x.LmpP, x.LmpQ, x.MuVmin, x.MuVmax }).Select(Bits),
            b.Buses.SelectMany(x => new[] { x.VmPu, x.VaDeg, x.LmpP, x.LmpQ, x.MuVmin, x.MuVmax }).Select(Bits));
        Assert.Equal(a.Generators.SelectMany(x => new[] { x.PgMw, x.QgMvar, x.MuPmin, x.MuPmax, x.MuQmin, x.MuQmax }).Select(Bits),
            b.Generators.SelectMany(x => new[] { x.PgMw, x.QgMvar, x.MuPmin, x.MuPmax, x.MuQmin, x.MuQmax }).Select(Bits));
        Assert.Equal(a.Branches.Select(x => x.Index), b.Branches.Select(x => x.Index));
        Assert.Equal(a.Branches.SelectMany(x => new[] { x.PfMw, x.QfMvar, x.PtMw, x.QtMvar, x.RateMva, x.MuSf, x.MuSt }).Select(Bits),
            b.Branches.SelectMany(x => new[] { x.PfMw, x.QfMvar, x.PtMw, x.QtMvar, x.RateMva, x.MuSf, x.MuSt }).Select(Bits));
        Assert.Equal(a.Binding.Select(x => (x.Kind, x.Element, Bits(x.ShadowPrice))), b.Binding.Select(x => (x.Kind, x.Element, Bits(x.ShadowPrice))));
        Assert.Equal(a.Log.Count, b.Log.Count);
        for (int i = 0; i < a.Log.Count; i++)
        {
            AcOpfIpmIteration p = a.Log[i], q = b.Log[i];
            Assert.Equal(p.Iteration, q.Iteration);
            Assert.Equal(new[] { p.Objective, p.Feasibility, p.Gradient, p.Complementarity, p.CostChange, p.Barrier, p.StepPrimal, p.StepDual }.Select(Bits),
                new[] { q.Objective, q.Feasibility, q.Gradient, q.Complementarity, q.CostChange, q.Barrier, q.StepPrimal, q.StepDual }.Select(Bits));
        }
        Assert.Empty(e.Controls);
        Assert.Empty(e.Outages);
        if (rel.EndsWith("case9.m", StringComparison.Ordinal))
            Assert.Equal("5296.686523629809", b.ObjectivePerHour.ToString("R", CultureInfo.InvariantCulture));   // the pinned acopf-full figure
    }

    // ---- Two-bus cases ----------------------------------------------------------------------------------------------
    // Base 100 MVA, Z = 0.01 + j0.1 pu, B = 0, unrated, load 100 + j50 at bus 2, slack at bus 1. The losses are
    // R |I|^2 = R (P^2 + Qn^2) / V2^2 (pu), with Qn the net reactive draw at bus 2.

    private static GridModel TwoBus(double v1Min, double v1Max, double v2Max, IEnumerable<Generator>? extra = null, bool transformer = false)
    {
        var gens = new List<Generator> { new(1, 0, Pmin: 0, Pmax: 1000, Name: "slack") };
        if (extra is not null) gens.AddRange(extra);
        return new GridModel
        {
            Name = "two-bus", BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, VminPu: v1Min, VmaxPu: v1Max),
                new Bus(2, BusType.Pq, Pd: 100, Qd: 50, VminPu: 0.9, VmaxPu: v2Max),
            },
            Generators = gens,
            Branches = new[] { new Branch(1, 2, 0.01, 0.1, Name: "T1", IsTransformer: transformer) },
            Oltcs = transformer ? new[] { new Oltc("T1", 2, 1.0, 0.9, 1.1, 0.0125) } : Array.Empty<Oltc>(),
        };
    }

    [Fact]
    public void T1_losses_with_a_q_source_at_the_load_bus()
    {
        // A unit at bus 2 with P = 0 and Q in [0, 100] MVAr: the optimum cancels the reactive draw (Qn = 0) and puts V2 at
        // its 1.05 limit, so losses = 0.01 / 1.05^2 pu = 0.9070294784580499 MW, Pg1 = 100 + that, and
        // |V1| = |1.05 + Z (1 / 1.05)| = 1.0637955620007193 (inside 1.10).
        GridModel m = TwoBus(0.95, 1.10, 1.05, new[] { new Generator(2, 0, Qmin: 0, Qmax: 100, Pmin: 0, Pmax: 0, Name: "q") });
        AcOpfExtendedResult r = AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses });
        Assert.True(r.Converged, r.Base.Message);
        Assert.Equal(0.9070294784580499, r.LossesMw, 1e-7);
        Assert.Equal(100.90702947845804, r.Base.Generators[0].PgMw, 1e-7);
        Assert.Equal(50.0, r.Base.Generators[1].QgMvar, 1e-5);
        Assert.Equal(1.05, r.Base.Buses[1].VmPu, 1e-7);
        Assert.Equal(1.0637955620007193, r.Base.Buses[0].VmPu, 1e-7);
    }

    [Fact]
    public void T2_losses_with_the_q_source_at_its_limit()
    {
        // Qmax 30 MVAr: Qn = 0.5 - 0.3 = 0.2 pu, V2 = 1.05, losses = 0.01 (1 + 0.04) / 1.1025 = 0.943310657596372 MW and
        // |V1| = |1.05 + Z (1 - j0.2) / 1.05| = 1.0826021603718161.
        GridModel m = TwoBus(0.95, 1.10, 1.05, new[] { new Generator(2, 0, Qmin: 0, Qmax: 30, Pmin: 0, Pmax: 0, Name: "q") });
        AcOpfExtendedResult r = AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses });
        Assert.True(r.Converged, r.Base.Message);
        Assert.Equal(0.943310657596372, r.LossesMw, 1e-7);
        Assert.Equal(30.0, r.Base.Generators[1].QgMvar, 1e-5);
        Assert.Equal(1.0826021603718161, r.Base.Buses[0].VmPu, 1e-7);
        Assert.Contains(r.Base.Binding, b => b.Kind == "Qmax");
    }

    [Fact]
    public void T3_tap_continuous_optimum_then_the_rounding_flip()
    {
        // A transformer (OLTC 0.9 .. 1.1, step 0.0125); V1 fixed at 1.0; V2 <= 1.00; objective rpo. The losses
        // 0.01 (1 + 0.25) / V2^2 are least with V2 at 1.00, so tau* = 1 / |1 + Z (1 - j0.5)| = 0.939630116933986 and the
        // losses are 1.25 MW. Rounding: n = (tau* - 0.9) / 0.0125 = 3.1704, so tau = 0.9375; that puts V2 at
        // 1.002605763511089, above 1.00, so the re-solve fails, and the flip to tau = 0.95 gives V2 = 0.9874642578768138 and
        // losses 0.0125 / V2^2 pu = 1.2819386547547857 MW (V2 from |V2 + Z (1 - j0.5) / V2| = 1 / tau, solved by bisection).
        GridModel m = TwoBus(1.0, 1.0, 1.00, transformer: true);
        OpfControlSet c = OpfControlSet.Parse("{\"format\":\"gridsim-opf-controls/1\",\"taps\":\"oltcs\"}", m);
        Assert.Equal(new OpfTapControl(0, 0.9, 1.1, 0.0125), Assert.Single(c.Taps));
        OpfRoundingResult r = OpfRounding.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.ReactivePower, Controls = c });
        Assert.True(r.Continuous.Converged, r.Continuous.Base.Message);
        Assert.Equal(0.939630116933986, r.Continuous.Controls[0].Value, 1e-7);
        Assert.Equal(1.25, r.Continuous.LossesMw, 1e-7);
        Assert.Equal(3, r.Controls[0].Step);
        Assert.Equal(0.9375, r.Controls[0].Rounded, 1e-15);
        Assert.Equal(OpfRoundingStatus.Flipped, r.Status);
        Assert.Equal(0, r.FlippedControl);
        Assert.Contains("flipped", r.Message, StringComparison.Ordinal);
        Assert.Equal(0.95, r.FinalValues[0], 1e-15);
        Assert.NotNull(r.Final);
        Assert.Equal(0.9874642578768138, r.Final!.Base.Buses[1].VmPu, 1e-7);
        Assert.Equal(1.2819386547547857, r.Final.LossesMw, 1e-7);
    }

    [Fact]
    public void T4_shunt_continuous_optimum_and_rounding()
    {
        // A shunt at bus 2, B in [0, 100] MVAr, step 10. Continuous: Qn = 0 at V2 = 1.05, so B* = 0.5 / 1.05^2 pu =
        // 45.35147392290249 MVAr and the losses are those of T1. Rounded: n = 4.535 -> 5, B = 0.5 pu. With B fixed,
        // dLoss/dV2 = -0.0163 < 0 at 1.05, so V2 stays at 1.05: Qn = 0.5 - 0.5 x 1.1025 = -0.05125 and losses =
        // 0.01 (1 + 0.05125^2) / 1.1025 = 0.9094118480725623 MW.
        GridModel m = TwoBus(0.95, 1.10, 1.05);
        OpfControlSet c = OpfControlSet.Parse("{\"shunts\":[{\"bus\":2,\"bMinMvar\":0,\"bMaxMvar\":100,\"stepMvar\":10,\"initialMvar\":0}]}", m);
        OpfRoundingResult r = OpfRounding.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses, Controls = c });
        Assert.True(r.Continuous.Converged, r.Continuous.Base.Message);
        Assert.Equal(45.35147392290249, r.Continuous.Controls[0].Value, 1e-5);
        Assert.Equal(0.9070294784580499, r.Continuous.LossesMw, 1e-7);
        Assert.Equal(OpfRoundingStatus.Rounded, r.Status);
        Assert.Equal(5, r.Controls[0].Step);
        Assert.Equal(50.0, r.FinalValues[0]);
        Assert.Equal(1.05, r.Final!.Base.Buses[1].VmPu, 1e-7);
        Assert.Equal(0.9094118480725623, r.Final.LossesMw, 1e-7);
    }

    [Fact]
    public void T5_losses_accept_units_without_costs_and_the_cost_objective_still_refuses_them()
    {
        GridModel m = TwoBus(0.95, 1.10, 1.05);
        Assert.True(AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses }).Converged);
        Assert.True(AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.ReactivePower }).Converged);
        var ex = Assert.Throws<InvalidDataException>(() => AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Cost }));
        Assert.Contains("has no cost curve", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rounding_rule_ties_go_to_the_case_value_then_the_lower_point()
    {
        // Grid 0, 10, 20 ... ; 25 is a tie between 20 and 30.
        Assert.Equal((2, 3), OpfRounding.Nearest(25, 0, 0, 100, 10));     // case 0: nearer 20
        Assert.Equal((3, 2), OpfRounding.Nearest(25, 40, 0, 100, 10));    // case 40: nearer 30
        Assert.Equal((2, 3), OpfRounding.Nearest(25, 25, 0, 100, 10));    // equidistant: the lower
        Assert.Equal((3, 2), OpfRounding.Nearest(26, 0, 0, 100, 10));     // not a tie: nearest
        Assert.Equal((10, 9), OpfRounding.Nearest(100, 0, 0, 100, 10));   // at the top: the other is below
        Assert.Equal((0, 1), OpfRounding.Nearest(0, 0, 0, 100, 10));
    }

    [Fact]
    public void Tap_and_shunt_derivatives_match_central_finite_differences()
    {
        // A three-bus case with a rated tap-controlled transformer (so its flow rows carry u) and a shunt control.
        var m = new GridModel
        {
            Name = "fd", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 60, Qd: 20), new Bus(3, BusType.Pq, Pd: 40, Qd: 30, Bs: 5) },
            Generators = new[] { new Generator(1, 0, Pmin: 0, Pmax: 300, Qmin: -100, Qmax: 100) },
            Branches = new[]
            {
                new Branch(1, 2, 0.02, 0.08, 0.02, RateAMva: 80, Name: "L12"),
                new Branch(2, 3, 0.01, 0.12, TapRatio: 1.03, PhaseShiftDeg: 2.0, RateAMva: 60, Name: "T23", IsTransformer: true),
                new Branch(1, 3, 0.03, 0.2, 0.01, RateAMva: 50, Name: "L13"),
            },
        };
        var opt = new AcOpfExtendedOptions
        {
            Objective = AcOpfObjective.Losses,
            Controls = new OpfControlSet(new[] { new OpfTapControl(1, 0.9, 1.1, 0.0125) }, new[] { new OpfShuntControl(3, -20, 40, 5, 0) }),
        };
        (int nx, int neq, int niq) = AcOpfExtended.Dimensions(m, opt);
        var rnd = new Random(7);
        double[] x = AcOpfExtended.StartingPoint(m, opt);
        for (int j = 0; j < nx; j++) x[j] += 0.05 * (rnd.NextDouble() - 0.5);
        var lam = Enumerable.Range(0, neq).Select(_ => rnd.NextDouble() - 0.5).ToArray();
        var mu = Enumerable.Range(0, niq).Select(_ => rnd.NextDouble()).ToArray();
        double[] g = AcOpfExtended.LagrangianGradient(m, opt, x, lam, mu);
        double[,] h = AcOpfExtended.LagrangianHessian(m, opt, x, lam, mu);
        const double step = 1e-6;
        for (int j = 0; j < nx; j++)
        {
            double[] xp = (double[])x.Clone(), xm = (double[])x.Clone();
            xp[j] += step; xm[j] -= step;
            double fd = (AcOpfExtended.Lagrangian(m, opt, xp, lam, mu) - AcOpfExtended.Lagrangian(m, opt, xm, lam, mu)) / (2 * step);
            Assert.True(Math.Abs(fd - g[j]) <= 1e-6 * (1 + Math.Abs(g[j])), $"gradient {j}: analytic {g[j]:R}, finite difference {fd:R}");
            double[] gp = AcOpfExtended.LagrangianGradient(m, opt, xp, lam, mu), gm = AcOpfExtended.LagrangianGradient(m, opt, xm, lam, mu);
            for (int i = 0; i < nx; i++)
            {
                double fh = (gp[i] - gm[i]) / (2 * step);
                Assert.True(Math.Abs(fh - h[i, j]) <= 1e-5 * (1 + Math.Abs(h[i, j])), $"Hessian ({i},{j}): analytic {h[i, j]:R}, finite difference {fh:R}");
            }
        }
    }

    [Fact]
    public void A_tap_control_at_the_case_ratio_reproduces_the_fixed_tap_optimum()
    {
        // With the tap fixed (as a control) at the case ratio the problem is the fixed-tap problem, so the optimum agrees
        // with AcOpfInteriorPoint (to the IPM tolerance; the variable order and the Ybus accumulation differ).
        GridModel m = Case("lib/case14.m");   // case14 has off-nominal taps (case30's transformers are all at TAP 0)
        int k = Enumerable.Range(0, m.Branches.Count).First(i => m.Branches[i].TapRatio != 0 && m.Branches[i].TapRatio != 1);
        var ctl = new OpfControlSet(new[] { new OpfTapControl(k, 0.9, 1.1, 0.0125) }, Array.Empty<OpfShuntControl>());
        double tau = m.Branches[k].TapRatio;
        AcOpfExtendedResult r = AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Controls = ctl, FixedControlValues = new[] { tau } });
        AcOpfIpmResult a = AcOpfInteriorPoint.Solve(m);
        Assert.True(r.Converged, r.Base.Message);
        Assert.Equal(a.ObjectivePerHour, r.Base.ObjectivePerHour, 1e-4);
        for (int g = 0; g < a.Generators.Count; g++) Assert.Equal(a.Generators[g].PgMw, r.Base.Generators[g].PgMw, 1e-3);
    }

    // ---- Published or independent cases (SciPy reference; MATPOWER publishes no loss-minimised values) --------------

    private static double CostOf(GridModel m, AcOpfIpmResult r)
    {
        double c = 0;
        for (int g = 0; g < m.Generators.Count; g++)
        {
            double v = 0;
            foreach (double k in m.Generators[g].Cost!.Coefficients) v = v * r.Generators[g].PgMw + k;
            c += v;
        }
        return c;
    }

    [Fact]
    public void T6_case9_loss_minimum()
    {
        // SciPy: losses 2.3157971721609 MW, Pg = 157.65188, 88.09645, 71.56747 MW, Vm = 1.10 at buses 1, 6 and 8; the cost of
        // that dispatch is 6071.61 per hour against 5296.69 at the cost optimum, whose losses are 3.30669 MW.
        GridModel m = Case("lib/case9.m");
        AcOpfExtendedResult r = AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses });
        Assert.True(r.Converged, r.Base.Message);
        Assert.Equal(2.3157971721609, r.LossesMw, 1e-5);
        Assert.Equal(157.65188, r.Base.Generators[0].PgMw, 1e-3);
        Assert.Equal(88.09645, r.Base.Generators[1].PgMw, 1e-3);
        Assert.Equal(71.56747, r.Base.Generators[2].PgMw, 1e-3);
        foreach (int id in new[] { 1, 6, 8 }) Assert.Equal(1.10, r.Base.Buses.Single(b => b.BusId == id).VmPu, 1e-6);
        Assert.Equal(6071.61, CostOf(m, r.Base), 1e-2);
        // The cost optimum's losses, 3.30669 MW, are the exact optimum's: solved here with the cost objective at the
        // 1e-12 stopping tolerances. acopf-full stops at MATPOWER's 1e-6, which leaves its losses 1.5e-5 MW above that
        // (3.3067049), inside the duality gap its complementarity test allows, so it is checked to 1e-4.
        AcOpfExtendedResult cost = AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Ipm = AcOpfExtended.DefaultIpm(AcOpfObjective.Losses) });
        Assert.True(cost.Converged, cost.Base.Message);
        Assert.Equal(3.30669, cost.LossesMw, 1e-5);
        Assert.Equal(3.30669, AcOpfInteriorPoint.Solve(m).LossesMw, 1e-4);
        Assert.True(r.LossesMw < cost.LossesMw);
    }

    [Fact]
    public void T7_case30_loss_minimum()
    {
        // SciPy: losses 1.8910176834 MW; binding flow limits MATPOWER row 10 (6-8) at its from end and row 29 (21-22) at its
        // to end (GridSim indices 9 and 28).
        GridModel m = Case("lib/case30.m");
        AcOpfExtendedResult r = AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses });
        Assert.True(r.Converged, r.Base.Message);
        Assert.Equal(1.8910176834, r.LossesMw, 1e-5);
        Assert.Contains(r.Base.Binding, b => b.Kind == "Sf" && b.Element.StartsWith("branch 9 (", StringComparison.Ordinal));
        Assert.Contains(r.Base.Binding, b => b.Kind == "St" && b.Element.StartsWith("branch 28 (", StringComparison.Ordinal));
    }

    [Fact]
    public void T8_case9_reactive_power_optimisation()
    {
        // SciPy: units 2 and 3 held at the case's 163 and 85 MW; losses 4.0098943748 MW, slack Pg 71.0098944 MW,
        // Vm(bus 1) = 1.0997449 (inside its limit), Vm at buses 6 and 8 = 1.10.
        GridModel m = Case("lib/case9.m");
        AcOpfExtendedResult r = AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.ReactivePower });
        Assert.True(r.Converged, r.Base.Message);
        Assert.Equal(4.0098943748, r.LossesMw, 1e-5);
        Assert.Equal(163.0, r.Base.Generators[1].PgMw, 1e-9);
        Assert.Equal(85.0, r.Base.Generators[2].PgMw, 1e-9);
        Assert.Equal(71.0098944, r.Base.Generators[0].PgMw, 1e-5);
        Assert.Equal(1.0997449, r.Base.Buses[0].VmPu, 1e-6);
        foreach (int id in new[] { 6, 8 }) Assert.Equal(1.10, r.Base.Buses.Single(b => b.BusId == id).VmPu, 1e-6);
    }

    // ---- AC-SCOPF ---------------------------------------------------------------------------------------------------
    // Two buses, two parallel lines j0.1 (R = B = 0, RateA 100 MVA), V fixed at 1.0 at both. Unit A at bus 1 (slack)
    // costs 10/MWh, unit B at bus 2 costs 30/MWh, both 0..200 MW. Load 150 MW at bus 2. Contingency: line 0. On one lossless
    // line with |V1| = |V2| = 1, |S| = 2 sin(d/2) / X and P = sin(d) / X, so at |S| = 1 pu P = cos(asin(0.05)) pu =
    // 99.87492177719089 MW.

    private static GridModel ScopfPair() => new()
    {
        Name = "scopf-pair", BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, VminPu: 1.0, VmaxPu: 1.0), new Bus(2, BusType.Pv, Pd: 150, VminPu: 1.0, VmaxPu: 1.0) },
        Generators = new[]
        {
            new Generator(1, 0, Pmin: 0, Pmax: 200, Name: "A") { Cost = new GeneratorCost(GeneratorCostModel.Polynomial, 0, 0, new[] { 10.0, 0.0 }) },
            new Generator(2, 0, Pmin: 0, Pmax: 200, Name: "B") { Cost = new GeneratorCost(GeneratorCostModel.Polynomial, 0, 0, new[] { 30.0, 0.0 }) },
        },
        Branches = new[] { new Branch(1, 2, 0, 0.1, RateAMva: 100, Name: "L0"), new Branch(1, 2, 0, 0.1, RateAMva: 100, Name: "L1") },
    };

    [Fact]
    public void T9_preventive_scopf_two_bus()
    {
        // Preventive: B keeps its output after the outage, so A alone (through one line) is held at 99.87492177719089 MW;
        // PB = 150 - that; cost = 10 PA + 30 PB = 2502.501564456182. Before the outage each line carries 49.94 MW.
        AcScopfResult r = AcScopf.Solve(ScopfPair(), new AcScopfOptions { Contingencies = new[] { 0 } });
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(new[] { 0 }, r.Secured);
        Assert.Equal(2, r.Rounds.Count);
        AcOpfIpmResult b = r.Last!.Base;
        Assert.Equal(2502.501564456182, b.ObjectivePerHour, 2502.501564456182 * 1e-6);
        Assert.Equal(99.87492177719089, b.Generators[0].PgMw, 1e-4);
        Assert.Equal(50.12507822280911, b.Generators[1].PgMw, 1e-4);
        Assert.All(b.Branches, br => Assert.Equal(49.937, br.PfMw, 1e-2));
        AcOpfBlockResult c = Assert.Single(r.Last.Outages);
        Assert.Equal(b.Generators[1].PgMw, c.Generators[1].PgMw, 1e-9);
    }

    [Fact]
    public void T10_corrective_scopf_two_bus()
    {
        // Corrective, R = 30 MW: after the outage B may rise by 30, so B's base output need only be 50.125 - 30:
        // PA = 129.87492177719088, PB = 20.125078222809123, cost 1902.5015644561822; after the outage PA_c = 99.8749 and
        // PB_c = 50.1251.
        AcScopfResult r = AcScopf.Solve(ScopfPair(), new AcScopfOptions { Contingencies = new[] { 0 }, Mode = AcScopfMode.Corrective, RampMw = 30 });
        Assert.True(r.Optimal, r.Message);
        AcOpfIpmResult b = r.Last!.Base;
        Assert.Equal(1902.5015644561822, b.ObjectivePerHour, 1902.5015644561822 * 1e-6);
        Assert.Equal(129.87492177719088, b.Generators[0].PgMw, 1e-4);
        Assert.Equal(20.125078222809123, b.Generators[1].PgMw, 1e-4);
        AcOpfBlockResult c = Assert.Single(r.Last.Outages);
        Assert.Equal(99.8749, c.Generators[0].PgMw, 1e-4);
        Assert.Equal(50.1251, c.Generators[1].PgMw, 1e-4);
        Assert.Contains(r.Last.CouplingBinding, x => x.Kind == "RampUp");

        // R = 60 MW: B covers the outage entirely after it, so the base case is the unconstrained merit order: PA = 150,
        // cost 1500, 75 MW per line, |S| = 2 sin(asin(0.075) / 2) / 0.1 = 75.05286458281881 MVA (within 100).
        AcScopfResult r60 = AcScopf.Solve(ScopfPair(), new AcScopfOptions { Contingencies = new[] { 0 }, Mode = AcScopfMode.Corrective, RampMw = 60 });
        Assert.True(r60.Optimal, r60.Message);
        Assert.Equal(1500.0, r60.Last!.Base.ObjectivePerHour, 1500 * 1e-6);
        Assert.Equal(150.0, r60.Last.Base.Generators[0].PgMw, 1e-4);
        Assert.All(r60.Last.Base.Branches, br => Assert.Equal(75.05286458281881, Math.Sqrt(br.PfMw * br.PfMw + br.QfMvar * br.QfMvar), 1e-3));
    }

    [Fact]
    public void T11_constraint_generation_on_case9_with_ratings_scaled_by_0_75()
    {
        // The spec asked for 0.7. That set is AC-infeasible: after the outage of branch 5 (7-8) bus 7 is fed by branch 4
        // (6-7) alone, so the power entering branch 4 at bus 7 is exactly bus 7's load, |100 + j35| = 105.948 MVA, above
        // 0.7 x 150 = 105 MVA whatever the dispatch (the DC SCOPF, which sees only the 100 MW, is feasible). 0.75 gives
        // 112.5 MVA, which the outage can meet.
        GridModel src = Case("lib/case9.m");
        GridModel m = src.CopyWith(branches: src.Branches.Select(b => b with { RateAMva = b.RateAMva * 0.75 }).ToArray());
        var dcOptions = new ScopfOptions();
        ScopfResult dcBefore = DcScopf.Solve(m, dcOptions);

        AcScopfResult r = AcScopf.Solve(m, new AcScopfOptions());
        // 2.1.099 contract: the secured outages are screened again at the final dispatch. Each secured block lets the unit
        // voltages move after its outage, the screen holds them at the base case's values, and at this dispatch both
        // fail that screen (run: outage 7 leaves branch 2 (5-6) at 115.352 MVA against 0.75 x 150 = 112.5; outage 8 puts
        // bus 4 at 1.10209 pu above its 1.1 limit). So the run ends SecuredOutageFailsScreen, not Optimal; the rounds, the
        // secured set and the final OPF are unchanged, which is what the rest of this test pins.
        Assert.Equal(AcScopfStatus.SecuredOutageFailsScreen, r.Status);
        Assert.Equal(new[] { 7, 8 }, r.UnresolvedSecuredOutages);
        // The generator transformers (1-4, 3-6, 8-2) island a unit: listed, not secured.
        Assert.Equal(new[] { 0, 3, 6 }, r.Islanding);
        // The secured set is exactly the outages the screens added, round by round.
        Assert.Equal(r.Rounds.SelectMany(x => x.Added).OrderBy(k => k), r.Secured);
        // Pinned: the secured outages and the final objective (cost per hour, the interior point method's 1e-6 stop).
        Assert.Equal(new[] { 7, 8 }, r.Secured);
        Assert.Equal(5324.498337, r.Last!.Base.ObjectivePerHour, 4);
        Assert.Equal(new[] { 1, 2, 4, 5, 7, 8 }, r.Secured.Concat(r.ScreenedOk).OrderBy(k => k));
        // Round 1 is the plain OPF: its added set is every outage whose screen fails at the plain optimum.
        AcOpfExtendedResult plain = AcOpfExtended.Solve(m, new AcOpfExtendedOptions());
        var fails = new[] { 1, 2, 4, 5, 7, 8 }.Where(k => !AcScopf.Screen(m, plain.Base, k, PostFaultRating.RateA).Passes).ToArray();
        Assert.Equal(fails, r.Rounds[0].Added);
        // After the final round every unsecured outage passes the AC screen at the final dispatch.
        Assert.All(r.ScreenedOk, k => Assert.True(AcScopf.Screen(m, r.Last!.Base, k, PostFaultRating.RateA).Passes, $"outage {k}"));

        // The DC SCOPF (scopf) is not touched by this work: same bits before and after the AC-SCOPF ran.
        ScopfResult dcAfter = DcScopf.Solve(m, dcOptions);
        Assert.Equal(Bits(dcBefore.TotalCost), Bits(dcAfter.TotalCost));
        Assert.Equal(dcBefore.Dispatch.Select(d => Bits(d.Pmw)), dcAfter.Dispatch.Select(d => Bits(d.Pmw)));
    }

    [Fact]
    public void Scopf_refusals()
    {
        GridModel m = ScopfPair();
        Assert.Throws<ArgumentException>(() => AcScopf.Solve(m, new AcScopfOptions { Mode = AcScopfMode.Corrective }));            // no ramp
        Assert.Throws<ArgumentException>(() => AcScopf.Solve(m, new AcScopfOptions { Contingencies = new[] { 5 } }));                // no such branch
        Assert.Throws<ArgumentException>(() => AcScopf.Solve(m, new AcScopfOptions { Contingencies = new[] { 0, 0 } }));             // twice
        Assert.Throws<ArgumentException>(() => AcScopf.Solve(m, new AcScopfOptions { Objective = AcOpfObjective.ReactivePower }));
    }

    // ---- Reconfiguration, case33bw (ties 33-37 open). Branch numbers in comments are MATPOWER rows (1-based). ---------

    [Fact]
    public void T12_T13_case33bw_branch_exchange_reaches_the_published_optimum()
    {
        // The ties (MATPOWER rows 33-37) have status 0; the default reader drops them, so the feeder is read with them
        // kept out of service.
        GridSim.Core.IO.Matpower.MatpowerParseResult parsed =
            GridSim.Core.IO.Matpower.MatpowerParser.ParseWithOpenBranches(S10TestSupport.MatpowerText("dist/case33bw.m"), "case33bw");
        GridModel m = parsed.Model;
        Assert.Equal(37, m.Branches.Count);
        Assert.Equal(new[] { 32, 33, 34, 35, 36 }, Enumerable.Range(0, 37).Where(k => !m.Branches[k].InService));
        Assert.Equal(32, Case("dist/case33bw.m").Branches.Count);   // the default reader is unchanged
        ReconfigResult r = FeederReconfiguration.Run(m);
        // T12: base loss 202.67712645600 kW (Newton and an independent sweep agree to 6e-11 kW).
        Assert.Equal(202.67712645600, r.BaseLossMw * 1000.0, 1e-4);
        // T13: (close, open, loss kW) in MATPOWER rows: (35, 8, 153.4933140), (33, 6, 147.0251890), (8, 11, 145.0435155),
        // (36, 32, 144.4118829), (6, 7, 142.7588794), (34, 14, 141.2042493), (11, 9, 139.5513472).
        (int C, int O, double Kw)[] expected =
        {
            (35, 8, 153.4933140), (33, 6, 147.0251890), (8, 11, 145.0435155), (36, 32, 144.4118829),
            (6, 7, 142.7588794), (34, 14, 141.2042493), (11, 9, 139.5513472),
        };
        Assert.Equal(expected.Length, r.Swaps.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].C - 1, r.Swaps[i].Closed);
            Assert.Equal(expected[i].O - 1, r.Swaps[i].Opened);
            Assert.Equal(expected[i].Kw, r.Swaps[i].LossMw * 1000.0, 1e-4);
        }
        // Final open set {7, 9, 14, 32, 37}, loss 139.55134722 kW, Vmin 0.93782 pu: the published optimum (Goswami and Basu,
        // 1992: 139.55 kW, open 7, 9, 14, 32, 37), which an exhaustive check over all 50 751 radial configurations confirmed
        // is the global minimum.
        Assert.Equal(new[] { 6, 8, 13, 31, 36 }, r.OpenBranches);
        Assert.Equal(139.55134722, r.LossMw * 1000.0, 1e-4);
        Assert.Equal(0.93782, r.VminPu, 1e-5);
        Assert.Equal(ReconfigStatus.LocalOptimum, r.Status);
    }

    /// <summary>A source (bus 1) with a long main feeder 1-2-3 (heavy load at 3) and two identical light side feeders 1-4
    /// and 1-5, each with a short open tie to bus 3 (3-4 and 3-5, mirror images of each other).</summary>
    private static GridModel MirrorFeeder(bool swapTieOrder)
    {
        var ties = new[] { new Branch(3, 4, 0.002, 0.002, InService: false, Name: "tie34"), new Branch(3, 5, 0.002, 0.002, InService: false, Name: "tie35") };
        if (swapTieOrder) Array.Reverse(ties);
        var branches = new List<Branch>
        {
            new(1, 2, 0.01, 0.01, Name: "m12"), new(2, 3, 0.05, 0.05, Name: "m23"),
            new(1, 4, 0.01, 0.01, Name: "s14"), new(1, 5, 0.01, 0.01, Name: "s15"),
        };
        branches.AddRange(ties);
        return new GridModel
        {
            Name = "mirror", BaseMva = 10,
            Buses = new[]
            {
                new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 0.5, Qd: 0.2), new Bus(3, BusType.Pq, Pd: 2.0, Qd: 1.0),
                new Bus(4, BusType.Pq, Pd: 0.3, Qd: 0.1), new Bus(5, BusType.Pq, Pd: 0.3, Qd: 0.1),
            },
            Generators = new[] { new Generator(1, 0) },
            Branches = branches,
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void T14_a_tie_between_mirror_swaps_goes_to_the_lower_closed_index(bool swapTieOrder)
    {
        // Closing 3-4 and opening 2-3, or closing 3-5 and opening 2-3, give mirror-image networks and equal losses (to within
        // 1e-9 MW), so the swap with the lower closed index wins, whichever tie that is. After it the other tie gives no gain.
        GridModel m = MirrorFeeder(swapTieOrder);
        ReconfigResult r = FeederReconfiguration.Run(m);
        ReconfigSwap first = r.Swaps[0];
        Assert.Equal(4, first.Closed);   // the lower of the two tie indices (4 and 5)
        Assert.Equal(1, first.Opened);   // m23
        Assert.Single(r.Swaps);
        Assert.True(r.LossMw < r.BaseLossMw);
    }

    [Fact]
    public void T15_refusals()
    {
        GridModel m = MirrorFeeder(false);
        GridModel meshed = m.CopyWith(branches: m.Branches.Select((b, i) => i == 4 ? b with { InService = true } : b).ToArray());
        Assert.Contains("meshed", Assert.Throws<InvalidDataException>(() => FeederReconfiguration.Run(meshed)).Message, StringComparison.Ordinal);
        GridModel split = m.CopyWith(branches: m.Branches.Select((b, i) => i == 2 ? b with { InService = false } : b).ToArray());
        Assert.Contains("not connected", Assert.Throws<InvalidDataException>(() => FeederReconfiguration.Run(split)).Message, StringComparison.Ordinal);
        GridModel withXf = m.CopyWith(branches: m.Branches.Select((b, i) => i == 0 ? b with { IsTransformer = true } : b).ToArray());
        Assert.Contains("transformer", Assert.Throws<ArgumentException>(() => FeederReconfiguration.Run(withXf, new ReconfigOptions { Switchable = new[] { 0, 1, 4 } })).Message, StringComparison.Ordinal);
    }

    // ---- T16: determinism ----------------------------------------------------------------------------------------------

    private static void WithCulture(string name, Action body)
    {
        CultureInfo old = CultureInfo.CurrentCulture, oldUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
            body();
        }
        finally { CultureInfo.CurrentCulture = old; CultureInfo.CurrentUICulture = oldUi; }
    }

    [Fact]
    public void T16_result_files_are_byte_identical_across_runs_and_cultures()
    {
        GridModel m = TwoBus(0.95, 1.10, 1.05);
        var opt = new AcOpfExtendedOptions
        {
            Objective = AcOpfObjective.Losses,
            Controls = new OpfControlSet(Array.Empty<OpfTapControl>(), new[] { new OpfShuntControl(2, 0, 100, 10, 0) }),
        };
        string csv1 = OptimisationReport.Csv(OptimisationReport.Rows(OpfRounding.Solve(m, opt)));
        string json1 = OptimisationReport.Json("acopf-opt", OptimisationReport.Rows(OpfRounding.Solve(m, opt)));
        string scopf1 = OptimisationReport.Csv(OptimisationReport.Rows(AcScopf.Solve(ScopfPair(), new AcScopfOptions { Contingencies = new[] { 0 } })));
        string rec1 = OptimisationReport.Json("reconfig", OptimisationReport.Rows(FeederReconfiguration.Run(MirrorFeeder(false))));
        string? csv2 = null, json2 = null, scopf2 = null, rec2 = null;
        WithCulture("de-DE", () =>
        {
            csv2 = OptimisationReport.Csv(OptimisationReport.Rows(OpfRounding.Solve(m, opt)));
            json2 = OptimisationReport.Json("acopf-opt", OptimisationReport.Rows(OpfRounding.Solve(m, opt)));
            scopf2 = OptimisationReport.Csv(OptimisationReport.Rows(AcScopf.Solve(ScopfPair(), new AcScopfOptions { Contingencies = new[] { 0 } })));
            rec2 = OptimisationReport.Json("reconfig", OptimisationReport.Rows(FeederReconfiguration.Run(MirrorFeeder(false))));
        });
        Assert.Equal(csv1, csv2);
        Assert.Equal(json1, json2);
        Assert.Equal(scopf1, scopf2);
        Assert.Equal(rec1, rec2);
        // A decimal point, never a comma (the losses agree with T1's 0.9070294784580499 to 1e-9).
        Assert.Contains("losses_mw,0.90702947", csv1, StringComparison.Ordinal);
        Assert.DoesNotContain("0,90702947", csv1, StringComparison.Ordinal);
    }

    [Fact]
    public void T16_cli_verbs_run_twice_give_the_same_bytes()
    {
        string dir = TestTemp.Under("gridsim-wp7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach ((string verb, string[] args, string ext) in new[]
            {
                ("acopf-opt", new[] { "case9", "--objective", "losses" }, ".csv"),
                ("acscopf", new[] { "case9", "--contingencies", "1,2" }, ".json"),
                ("reconfig", new[] { Path.Combine(S10TestSupport.DataDir, "matpower", "dist", "case33bw.m"), "--max-swaps", "2" }, ".csv"),
            })
            {
                string a = Path.Combine(dir, verb + "-a" + ext), b = Path.Combine(dir, verb + "-b" + ext);
                CliProcess.Result r1 = CliProcess.RunBuilt(new[] { verb }.Concat(args).Concat(new[] { "--out", a }).ToArray());
                CliProcess.Result r2 = CliProcess.RunBuilt(new[] { verb }.Concat(args).Concat(new[] { "--out", b }).ToArray());
                Assert.True(r1.ExitCode == 0, $"{verb}: exit {r1.ExitCode}: {r1.StdErr}");
                Assert.Equal(r1.StdOut.Replace(a, "X", StringComparison.Ordinal), r2.StdOut.Replace(b, "X", StringComparison.Ordinal));
                Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
                Assert.StartsWith(verb + " - ", r1.StdOut, StringComparison.Ordinal);
            }
            Assert.Equal(2, CliProcess.RunBuilt("acopf-opt", "case9", "--objective", "magic").ExitCode);
            Assert.Equal(2, CliProcess.RunBuilt("acscopf", "case9", "--mode", "corrective").ExitCode);           // no ramp
            Assert.Equal(2, CliProcess.RunBuilt("acscopf", "case9", "--ramp-mw", "10").ExitCode);                // ramp without corrective
            Assert.Equal(2, CliProcess.RunBuilt("reconfig", "case9", "--top", "3").ExitCode);                     // another verb's flag
            Assert.Equal(1, CliProcess.RunBuilt("reconfig", "case9").ExitCode);                                  // meshed start
            Assert.Contains("acopf-opt", CliProcess.RunBuilt("acopf-opt", "--help").StdOut, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }
}
