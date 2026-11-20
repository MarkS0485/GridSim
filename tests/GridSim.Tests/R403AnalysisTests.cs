using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Contingency;
using GridSim.Core.Analysis.Sensitivities;
using GridSim.Core.Analysis.VoltageStability;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-03: sensitivities (OTDF, PSDF, tap, dV/dQ, effectiveness), QV curves, per-contingency PV curves, the DC N-1
/// screen and the fault level under N-1. Every oracle is a closed form derived in the test comments, or a finite
/// difference of the existing solver; none is a remembered published number.
/// </summary>
public sealed class R403AnalysisTests
{
    // ---------------------------------------------------------------------------------------------------------------
    // Models
    // ---------------------------------------------------------------------------------------------------------------

    // Three buses, three branches of equal reactance X, slack at bus 1. Branch order [1-2, 2-3, 1-3].
    private static GridModel Triangle(double x = 0.1, double rateA = 0, double rateB = 0, double pd2 = 0, double pd3 = 0) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: pd2), new Bus(3, BusType.Pq, Pd: pd3) },
        Generators = new[] { new Generator(1, Pg: pd2 + pd3) },
        Branches = new[]
        {
            new Branch(1, 2, 0.0, x, RateAMva: rateA, RateBMva: rateB),
            new Branch(2, 3, 0.0, x, RateAMva: rateA, RateBMva: rateB),
            new Branch(1, 3, 0.0, x, RateAMva: rateA, RateBMva: rateB),
        },
    };

    // Slack (V = 1) behind a lossless reactance X to a load bus 2: P = pdMw, Q = qdMvar.
    private static GridModel TwoBus(double x, double pdMw, double qdMvar, double tap = 1.0) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: pdMw, Qd: qdMvar, BaseKv: 132) },
        Generators = new[] { new Generator(1, Pg: 0, Vg: 1.0) },
        Branches = new[] { new Branch(1, 2, 0.0, x, TapRatio: tap, IsTransformer: tap != 1.0) },
    };

    private static void Near(double expected, double actual, double tolerance) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected:R} +/- {tolerance:R} but was {actual:R}");

    private static PowerFlowOptions Tight => new() { Tolerance = 1e-12, MaxIterations = 100, EnforceQLimits = false };

    private static PowerFlowResult SolveTight(GridModel m)
    {
        PowerFlowResult r = ContingencyAnalysis.CreateStudySolver(m).Solve(m, Tight);
        Assert.True(r.Converged);
        return r;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // OTDF
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Otdf_of_the_equal_triangle_matches_the_closed_form_and_a_re_solve_with_the_branch_out()
    {
        // Closed form. With 1-3 out the network is the chain 1-2-3, so +1 MW at bus 2 (withdrawn at the slack) flows
        // entirely 2 -> 1 on branch 1-2 (-1 in from->to terms) and nothing reaches 2-3 or 1-3.
        GridModel m = Triangle();
        DistributionFactors df = DistributionFactors.Build(m);
        OtdfColumn c = OutageTransferFactors.Column(m, df, outageBranch: 2, busId: 2);
        Assert.False(c.Islanded);
        Assert.Equal(-1.0, c.Factors[0], 12);
        Assert.Equal(0.0, c.Factors[1], 12);
        Assert.Equal(0.0, c.Factors[2]);   // the outaged branch itself is exactly zero

        // The decomposition PTDF + LODF * PTDF[k] (the pre-outage factors are 2/3 and 1/3 by series-parallel reactance).
        double[] ptdf = df.PtdfColumn(2);
        Assert.Equal(-2.0 / 3.0, ptdf[0], 12);
        Assert.Equal(-1.0 / 3.0, ptdf[2], 12);

        // Re-solve: the same network built with the branch out of service. Both sides are sparse-LU solves of a 2x2
        // system with entries O(10); the residual of the factorisation is below 1e-13 relative, so 1e-9 is a safe bound.
        GridModel out2 = new()
        {
            BaseMva = 100, Buses = m.Buses, Generators = m.Generators,
            Branches = new[] { m.Branches[0], m.Branches[1], m.Branches[2] with { InService = false } },
        };
        double[] re = DistributionFactors.Build(out2).PtdfColumn(2);
        for (int l = 0; l < 3; l++) Assert.Equal(re[l], c.Factors[l], 9);
    }

    [Fact]
    public void Otdf_of_a_bridge_outage_is_islanded_and_not_a_number()
    {
        GridModel m = TwoBus(0.1, 50, 0);
        OtdfColumn c = OutageTransferFactors.Column(m, DistributionFactors.Build(m), 0, 2);
        Assert.True(c.Islanded);
        Assert.All(c.Factors, v => Assert.True(double.IsNaN(v)));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // PSDF
    // ---------------------------------------------------------------------------------------------------------------

    private static GridModel ParallelPair(double x1, double x2, double loadMw, double phaseDeg = 0.0) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: loadMw, BaseKv: 132) },
        Generators = new[] { new Generator(1, Pg: 0) },
        Branches = new[] { new Branch(1, 2, 0.0, x1, PhaseShiftDeg: phaseDeg), new Branch(1, 2, 0.0, x2) },
    };

    [Theory]
    [InlineData(0.2, 0.2)]
    [InlineData(0.2, 0.3)]
    public void Psdf_of_a_parallel_pair_is_minus_one_over_the_sum_of_reactances(double x1, double x2)
    {
        GridModel m = ParallelPair(x1, x2, 60);
        double[] s = PhaseShifterSensitivity.FlowSensitivityMwPerRad(m, DistributionFactors.Build(m), 0);
        // Derivation (solver convention, flow1 = (D - phi)/X1 from bus 1 to 2): flow1 = (D - phi)/X1, flow2 = D/X2,
        // flow1 + flow2 = T fixes D = (T + phi/X1)/(1/X1 + 1/X2); d flow1 / d phi = -1/X1 + (1/X1)/(1/X1 + 1/X2)/X1
        // = -1/(X1 + X2). Evaluated here by differencing that linear system (exact: it is linear in phi).
        static (double F1, double F2) Flows(double x1, double x2, double t, double phi)
        {
            double d = (t + phi / x1) / (1 / x1 + 1 / x2);
            return ((d - phi) / x1, d / x2);
        }
        const double h = 1e-3;
        double dF1 = (Flows(x1, x2, 0.6, h).F1 - Flows(x1, x2, 0.6, -h).F1) / (2 * h);
        double dF2 = (Flows(x1, x2, 0.6, h).F2 - Flows(x1, x2, 0.6, -h).F2) / (2 * h);
        Assert.Equal(-1.0 / (x1 + x2), dF1, 9);
        Assert.Equal(dF1 * 100.0, s[0], 6);          // MW per radian = pu per radian x base
        Assert.Equal(dF2 * 100.0, s[1], 6);
        if (x1 == 0.2 && x2 == 0.2) Assert.Equal(-250.0, s[0], 9);   // the plan's 2.5 pu/rad (sign: solver convention)
    }

    [Fact]
    public void Psdf_agrees_with_a_finite_difference_of_the_ac_solver_on_a_lossless_pair()
    {
        // AC solver, branch 0 shifted by +/- 0.05 degrees about zero; flows read at the From end. The DC value neglects
        // the cosine of the angle (about 1 per cent at the ~0.2 rad angle of this load) and the voltage sag, so 3 per cent.
        double d = 0.05, rad = d * Math.PI / 180.0;
        PowerFlowResult up = SolveTight(ParallelPair(0.2, 0.3, 60, +d)), dn = SolveTight(ParallelPair(0.2, 0.3, 60, -d));
        double fd = (up.LineFlows[0].PFromMw - dn.LineFlows[0].PFromMw) / (2 * rad);
        GridModel m = ParallelPair(0.2, 0.3, 60);
        double dc = PhaseShifterSensitivity.FlowSensitivityMwPerRad(m, DistributionFactors.Build(m), 0)[0];
        Assert.InRange(fd / dc, 0.97, 1.03);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // AC sensitivities
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Dvdq_on_two_buses_matches_the_closed_form_derivative()
    {
        // Lossless line, P = 0: Qd = (V - V^2)/X, V = (1 + s)/2 with s = sqrt(1 - 4 X Qd); dV/dQd = -X/s, so the voltage
        // rises by +X/s per pu of reactive GENERATION injected at the load bus.
        double x = 0.1, qd = 1.5, s = Math.Sqrt(1 - 4 * x * qd);
        GridModel m = TwoBus(x, 0, qd * 100);
        PowerFlowResult r = SolveTight(m);
        Near((1 + s) / 2, r.Vm[1], 1e-9);
        var engine = AcSensitivityEngine.Build(m, r);
        VoltageSensitivityColumn c = VoltageSensitivity.Column(engine, 2);
        Assert.True(c.Resolved);
        Near(x / s / 100.0, c.DVmPerMvar[1], 1e-10);
        Assert.Equal(0.0, c.DVmPerMvar[0]);    // the slack holds its voltage
    }

    [Fact]
    public void Tap_sensitivity_on_two_buses_matches_the_closed_form_derivative()
    {
        // Off-nominal ratio a on the From side, P = 0: Y22 = -j/X, Y21 = j/(a X), so Qd = V2 (V1/a - V2)/X and
        // V2 = (u + sqrt(u^2 - 4 X Qd))/2 with u = V1/a. At a = 1: dV2/da = (du/da)(1 + u/s)/2 = -(1 + 1/s)/2.
        double x = 0.1, qd = 1.5, s = Math.Sqrt(1 - 4 * x * qd);
        GridModel plain = TwoBus(x, 0, qd * 100);
        var m = new GridModel
        {
            BaseMva = 100, Buses = plain.Buses, Generators = plain.Generators,
            Branches = new[] { new Branch(1, 2, 0.0, x, IsTransformer: true) },
        };
        PowerFlowResult r = SolveTight(m);
        TapSensitivityColumn c = TapSensitivity.Column(AcSensitivityEngine.Build(m, r), 0);
        Near(-(1 + 1 / s) / 2, c.DVmPerUnitRatio[1], 1e-7);
    }

    [Fact]
    public void Ac_sensitivities_match_finite_differences_of_the_solver_on_case14()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case14.m").Model;
        PowerFlowResult r = SolveTight(m);
        var engine = AcSensitivityEngine.Build(m, r);

        // dV/dQ at every PQ bus, against a central difference of the solver in the bus's reactive load (1 MVAr each way).
        // Truncation error is O(delta^2 V''') ~ 1e-9 pu; the solver tolerance 1e-12 pu adds 1e-12; allow 2e-7 pu/MVAr
        // on columns whose entries are ~1e-3.
        int checkedBuses = 0;
        for (int b = 0; b < m.BusCount; b++)
        {
            if (engine.Types[b] != BusType.Pq) continue;
            PowerFlowResult hi = SolveTight(WithQd(m, b, +1.0)), lo = SolveTight(WithQd(m, b, -1.0));
            double[] dv = engine.InjectionResponse(m.Buses[b].Id, 0.0, 1.0).DVm;
            for (int i = 0; i < m.BusCount; i++)
            {
                double fd = -(hi.Vm[i] - lo.Vm[i]) / 2.0;   // load up = injection down
                Assert.True(Math.Abs(fd - dv[i]) < 2e-7, $"dV[{m.Buses[i].Id}]/dQ[{m.Buses[b].Id}] analytic {dv[i]:R} vs finite difference {fd:R}");
            }
            checkedBuses++;
        }
        Assert.True(checkedBuses >= 9);

        // dV/dtap of every transformer (branches 7-9 type entries with TapRatio != 1), central difference of the tap
        // +/- 1e-4 (truncation 1e-8, solver 1e-12/1e-4 = 1e-8).
        int taps = 0;
        for (int k = 0; k < m.Branches.Count; k++)
        {
            if (!(m.Branches[k].TapRatio != 1.0 && m.Branches[k].TapRatio != 0.0)) continue;
            PowerFlowResult hi = SolveTight(WithTap(m, k, +1e-4)), lo = SolveTight(WithTap(m, k, -1e-4));
            double[] dv = engine.TapResponse(k).DVm;
            for (int i = 0; i < m.BusCount; i++)
            {
                double fd = (hi.Vm[i] - lo.Vm[i]) / 2e-4;
                Assert.True(Math.Abs(fd - dv[i]) < 1e-5, $"dV[{m.Buses[i].Id}]/dtap[{k}] analytic {dv[i]:R} vs finite difference {fd:R}");
            }
            taps++;
        }
        Assert.True(taps >= 3);

        // dV/dVset of each regulated bus, against the generator set point +/- 1e-4 pu.
        for (int b = 0; b < m.BusCount; b++)
        {
            if (engine.Types[b] != BusType.Pv) continue;
            PowerFlowResult hi = SolveTight(WithVg(m, m.Buses[b].Id, +1e-4)), lo = SolveTight(WithVg(m, m.Buses[b].Id, -1e-4));
            double[] dv = engine.SetpointResponse(m.Buses[b].Id).DVm;
            for (int i = 0; i < m.BusCount; i++)
            {
                if (engine.Types[i] != BusType.Pq) continue;
                double fd = (hi.Vm[i] - lo.Vm[i]) / 2e-4;
                Assert.True(Math.Abs(fd - dv[i]) < 1e-5, $"dV[{m.Buses[i].Id}]/dVset[{m.Buses[b].Id}] analytic {dv[i]:R} vs finite difference {fd:R}");
            }
        }
    }

    [Fact]
    public void The_adjoint_effectiveness_equals_the_forward_responses()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case14.m").Model;
        var engine = AcSensitivityEngine.Build(m, SolveTight(m));
        int target = 14;
        IReadOnlyList<EffectivenessRow> rows = EffectivenessFactors.ForTarget(m, engine, target);
        int ti = m.Buses.ToList().FindIndex(b => b.Id == target);
        foreach (EffectivenessRow row in rows)
        {
            double forward = row.Kind switch
            {
                "reactive-injection" => engine.InjectionResponse(row.Element, 0, 1).DVm[ti],
                "tap" => engine.TapResponse(row.Element).DVm[ti],
                _ => engine.SetpointResponse(row.Element).DVm[ti],
            };
            Assert.Equal(forward, row.EffectPerUnit, 10);
        }
        Assert.Contains(rows, r => r.Kind == "reactive-injection");
        Assert.Contains(rows, r => r.Kind == "tap");
        Assert.Contains(rows, r => r.Kind == "voltage-setpoint");
    }

    [Fact]
    public void Ac_sensitivities_refuse_a_case_with_a_tap_changer_loop_and_a_non_converged_state()
    {
        GridModel m = TwoBus(0.1, 0, 50);
        PowerFlowResult r = SolveTight(m);
        GridModel withOltc = m.CopyWith(oltcs: new[] { new Oltc("T", 2) });
        var ex = Assert.Throws<InvalidOperationException>(() => AcSensitivityEngine.Build(withOltc, r));
        Assert.Contains("OUT OF SCOPE", ex.Message, StringComparison.Ordinal);
        PowerFlowResult bad = ContingencyAnalysis.CreateStudySolver(TwoBus(0.1, 0, 500)).Solve(TwoBus(0.1, 0, 500), Tight);
        Assert.False(bad.Converged);
        Assert.Throws<InvalidOperationException>(() => AcSensitivityEngine.Build(TwoBus(0.1, 0, 500), bad));
    }

    private static GridModel WithQd(GridModel m, int busIndex, double dQ)
    {
        var buses = m.Buses.ToList();
        buses[busIndex] = buses[busIndex] with { Qd = buses[busIndex].Qd + dQ };
        return m.CopyWith(buses: buses);
    }

    private static GridModel WithTap(GridModel m, int branch, double dTap)
    {
        var br = m.Branches.ToList();
        br[branch] = br[branch] with { TapRatio = br[branch].TapRatio + dTap };
        return m.CopyWith(branches: br);
    }

    private static GridModel WithVg(GridModel m, int busId, double dV)
    {
        var gens = m.Generators.Select(g => g.Bus == busId ? g with { Vg = g.Vg + dV } : g).ToList();
        return m.CopyWith(generators: gens);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // QV curve
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Qv_curve_on_two_buses_with_no_real_power_matches_the_closed_form()
    {
        // Compensator output Qc(V) = Qd - (V - V^2)/X (pu); X = 0.1, Qd = 1.5 pu: uncompensated V0 = (1 + sqrt(0.4))/2,
        // minimum at V = 0.5 equal to Qd - 1/(4X) = -1.0 pu = -100 MVAr, so the margin is 100 MVAr.
        double x = 0.1, qdPu = 1.5;
        GridModel m = TwoBus(x, 0, qdPu * 100);
        QvCurveResult r = QvCurve.Compute(m, 2, vMinPu: 0.3, vMaxPu: 1.2, stepPu: 0.01);
        Assert.Equal("OK", r.Status);
        Near((1 + Math.Sqrt(1 - 4 * x * qdPu)) / 2, r.BaseVPu, 1e-6);
        Near(0.0, r.BaseResidualMvar, 1e-3);
        foreach (QvPoint p in r.Points)
        {
            Assert.True(p.Converged);
            double expected = (qdPu - (p.VPu - p.VPu * p.VPu) / x) * 100.0;
            Near(expected, p.QMvar, 1e-3);   // solver tolerance 1e-8 pu of 100 MVA = 1e-6 MVAr, amplified by at most 1/X = 10 and the conditioning near V = 0.5
        }
        Assert.True(r.MinBracketed);
        // The parabola through three samples is exact for this quadratic Q(V).
        Near(0.5, r.NoseVPu, 1e-4);
        Near(-100.0, r.NoseQMvar, 1e-2);
        Near(100.0, r.MarginMvar, 1e-2);
        Assert.Equal("limit", r.DownEnd);
        Assert.Equal("limit", r.UpEnd);
        Near(-100.0, r.GridMinQMvar, 0.3);   // the grid minimum is within a step of the nose: Q is flat there
    }

    [Fact]
    public void Qv_curve_with_real_power_moves_the_nose_as_the_closed_form_says()
    {
        // Lossless line, load P + jQ at V: V^4 + (2 Q X - 1) V^2 + X^2 (P^2 + Q^2) = 0 for the net reactive demand Q the
        // line can serve, i.e. Q(V) = (sqrt(V^2 - X^2 P^2) - V^2)/X, and the compensator supplies Qc = Qd - Q(V).
        // Stationary at sqrt(V^2 - X^2 P^2) = 1/2: V* = sqrt(1/4 + X^2 P^2), Qc* = Qd - 1/(4X) + X P^2.
        double x = 0.1, pPu = 1.0, qdPu = 1.0;
        GridModel m = TwoBus(x, pPu * 100, qdPu * 100);
        QvCurveResult r = QvCurve.Compute(m, 2, 0.35, 1.2, 0.005);
        Assert.Equal("OK", r.Status);
        Assert.True(r.MinBracketed);
        foreach (QvPoint p in r.Points)
        {
            double expected = (qdPu - (Math.Sqrt(p.VPu * p.VPu - x * x * pPu * pPu) - p.VPu * p.VPu) / x) * 100.0;
            Near(expected, p.QMvar, 5e-3);
        }
        // Q(V) is not quadratic now, so the three-point parabola is an estimate: its error is the cubic term, bounded by
        // (step^2/6) |Q'''| ~ 1e-5 * |Q'''|/6 with |Q'''| of order 1/X x 10 here; 0.05 is generous and the V check 1e-3.
        Near(Math.Sqrt(0.25 + x * x * pPu * pPu), r.NoseVPu, 2e-3);
        double noseQ = (qdPu - 1 / (4 * x) + x * pPu * pPu) * 100.0;
        Near(noseQ, r.NoseQMvar, 0.1);
        Near(-noseQ, r.MarginMvar, 0.1);   // 140 MVAr
    }

    [Fact]
    public void Qv_margin_agrees_with_the_load_multiplier_walk_for_a_pure_reactive_load()
    {
        // With P = 0 scaling the load by lambda scales Qd, so collapse is at Qd*lambda = Qd + margin. The existing
        // walk brackets the nose from below (Newton fails slightly before it) to its minimum step 1e-3.
        GridModel m = TwoBus(0.1, 0, 150);
        QvCurveResult r = QvCurve.Compute(m, 2, 0.3, 1.2, 0.01);
        double lambdaStar = (150.0 + r.MarginMvar) / 150.0;
        PvCurve pv = ContinuationPowerFlow.TracePvCurve(m, ContingencyAnalysis.CreateStudySolver(m), ContingencyAnalysis.StudyOptions);
        Assert.InRange(pv.MaxLambda, lambdaStar - 0.01, lambdaStar + 1e-9);
    }

    [Fact]
    public void Qv_curve_refuses_the_slack_and_records_a_non_converged_end()
    {
        GridModel m = TwoBus(0.1, 0, 150);
        Assert.StartsWith("OUT OF SCOPE", QvCurve.Compute(m, 1).Status, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => QvCurve.Compute(m, 99));
        // A line whose PQ end cannot be held at very low voltage: with a huge real load the lower sweep must stop and say so.
        GridModel heavy = TwoBus(0.1, 400, 0);
        QvCurveResult r = QvCurve.Compute(heavy, 2, 0.05, 1.2, 0.05);
        Assert.Equal("OK", r.Status);
        Assert.Contains(r.Points, p => !p.Converged);
        Assert.Equal("non-converged", r.DownEnd);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // PV per contingency
    // ---------------------------------------------------------------------------------------------------------------

    // Two parallel X = 0.1... bus 2 carries a reactive load; a spur 2-3 (bridge) hangs off bus 2.
    private static GridModel ParallelWithSpur(double xEach, double qdMvar) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Qd: qdMvar, BaseKv: 132), new Bus(3, BusType.Pq, BaseKv: 132) },
        Generators = new[] { new Generator(1, Pg: 0) },
        Branches = new[] { new Branch(1, 2, 0.0, xEach), new Branch(1, 2, 0.0, xEach), new Branch(2, 3, 0.0, 0.05) },
    };

    [Fact]
    public void Pv_per_contingency_matches_the_parallel_line_closed_form_and_flags_the_bridge()
    {
        // Pd = 0: collapse at Qd*lambda = 1/(4 Xeq). Intact Xeq = 0.1 (two 0.2 lines): 2.5 pu, so with Qd = 0.5 pu
        // lambda* = 5; with one line out Xeq = 0.2: 1.25 pu, lambda* = 2.5. The walk is a lower bound within ~minStep
        // of the nose (it stops when Newton fails), so each is checked in [lambda* - 0.02, lambda*].
        GridModel m = ParallelWithSpur(0.2, 50);
        PvContingencyResult r = PvPerContingency.Run(m, minStep: 1e-3);
        Assert.Equal("OK", r.Base.Status);
        Assert.InRange(r.Base.MaxLambda, 5.0 - 0.02, 5.0 + 1e-9);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal(0, r.Rows[0].Outage);
        Assert.Equal("OK", r.Rows[0].Status);
        Assert.InRange(r.Rows[0].MaxLambda, 2.5 - 0.02, 2.5 + 1e-9);
        Assert.InRange(r.Rows[1].MaxLambda, 2.5 - 0.02, 2.5 + 1e-9);
        Assert.Equal("ISLANDED", r.Rows[2].Status);
        Assert.True(double.IsNaN(r.Rows[2].MaxLambda));
        Assert.Equal(0, r.Rows[2].Solves);
    }

    [Fact]
    public void Pv_per_contingency_records_an_outage_state_that_does_not_solve_at_the_base_load()
    {
        // 150 MVAr: intact Xeq = 0.1 supports 250 (lambda 1.67); one line out supports 125 < 150: NO SOLUTION.
        PvContingencyResult r = PvPerContingency.Run(ParallelWithSpur(0.2, 150));
        Assert.Equal("OK", r.Base.Status);
        Assert.Equal("NO SOLUTION", r.Rows[0].Status);
        Assert.Equal("NO SOLUTION", r.Rows[1].Status);
        // The state supports 1.25 pu of load against 1.5 pu: the walk finds the highest lower multiplier, 1.25/1.5 = 0.8333,
        // to within the bisection step (a negative margin), as the cpf verb does for a base case that does not solve.
        Assert.InRange(r.Rows[0].MaxLambda, 1.25 / 1.5 - 0.02, 1.25 / 1.5 + 1e-9);
        Assert.True(r.Rows[0].MarginPct < 0);
    }

    [Fact]
    public void Pv_per_contingency_parallel_equals_serial_bit_for_bit()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case9.m").Model;
        PvContingencyResult s = PvPerContingency.Run(m, maxDegreeOfParallelism: 1), p = PvPerContingency.Run(m, maxDegreeOfParallelism: 4);
        Assert.Equal(s.Rows.Count, p.Rows.Count);
        for (int i = 0; i < s.Rows.Count; i++)
        {
            Assert.Equal(s.Rows[i].Status, p.Rows[i].Status);
            Assert.Equal(BitConverter.DoubleToInt64Bits(s.Rows[i].MaxLambda), BitConverter.DoubleToInt64Bits(p.Rows[i].MaxLambda));
            Assert.Equal(s.Rows[i].Solves, p.Rows[i].Solves);
        }
        Assert.Equal(BitConverter.DoubleToInt64Bits(s.Base.MaxLambda), BitConverter.DoubleToInt64Bits(p.Base.MaxLambda));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // DC N-1
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Dc_contingency_on_the_loaded_triangle_matches_the_closed_form_flows()
    {
        // 100 MW at bus 2, slack at 1, X equal. Intact: 2/3 on 1-2, 1/3 round via 3. Outage of 1-2: all 100 MW goes
        // 1 -> 3 -> 2 (branch 1-3 = +100, branch 2-3 = -100 in from->to terms). Outage of 1-3: 1-2 = 100, 2-3 = 0.
        // Outage of 2-3: 1-2 = 100, 1-3 = 0.
        GridModel m = Triangle(rateA: 80, pd2: 100);
        DcContingencyResult r = DcContingencyScreen.Screen(m, keepFlows: true);
        Assert.Equal(66.6666666667, r.BaseFlowsMw[0], 8);
        Assert.Equal("SECURE", r.Base.Status);
        double[][] expected = { new[] { 0.0, -100.0, 100.0 }, new[] { 100.0, 0.0, 0.0 }, new[] { 100.0, 0.0, 0.0 } };
        for (int k = 0; k < 3; k++)
        {
            // Each factor is one 2x2 sparse-LU solve; bound the error by 1e-9 MW on 100 MW flows.
            for (int l = 0; l < 3; l++) Assert.Equal(expected[k][l], r.Rows[k].FlowsMw[l], 7);
        }
        Assert.Equal("VIOLATIONx2", r.Rows[0].Status);   // 1-3 = 100 and 2-3 = 100 against 80
        Assert.Equal("VIOLATIONx1", r.Rows[1].Status);
        Assert.Equal("VIOLATIONx1", r.Rows[2].Status);
        Assert.Equal(125.0, r.Rows[0].WorstLoadingPct, 6);

        // The independent path: the same DC problem with the branch removed from the model and re-solved.
        for (int k = 0; k < 3; k++)
        {
            GridModel out_ = new() { BaseMva = 100, Buses = m.Buses, Generators = m.Generators, Branches = m.Branches.Select((b, i) => i == k ? b with { InService = false } : b).ToArray() };
            double[] re = DistributionFactors.Build(out_).DcFlows(new Dictionary<int, double> { [1] = 100, [2] = -100 }, 100);
            for (int l = 0; l < 3; l++) Assert.Equal(re[l], r.Rows[k].FlowsMw[l], 7);
        }
    }

    [Fact]
    public void Dc_contingency_uses_the_effective_post_outage_rating_and_names_its_source()
    {
        GridModel m = Triangle(rateA: 80, rateB: 120, pd2: 100);
        DcContingencyResult a = DcContingencyScreen.Screen(m, PostFaultRating.RateA);
        DcContingencyResult b = DcContingencyScreen.Screen(m, PostFaultRating.RateB);
        DcContingencyResult c = DcContingencyScreen.Screen(m, PostFaultRating.RateC);   // no RateC: falls back to A
        Assert.All(a.Rows, r => Assert.NotEqual("SECURE", r.Status));
        Assert.All(b.Rows, r => Assert.Equal("SECURE", r.Status));   // 100 MW < 120 MW
        Assert.All(c.Rows, r => Assert.NotEqual("SECURE", r.Status));
        Assert.Equal("A", a.Rows[0].Violations[0].RatingSource);
        Assert.Equal("fallback A", c.Rows[0].Violations[0].RatingSource);
        Assert.Equal("SECURE", b.Base.Status);   // the intact state is always judged against A (66.7 < 80)
    }

    [Fact]
    public void Dc_contingency_reports_a_bridge_outage_as_islanded_without_algebra_and_refuses_bad_requests()
    {
        // Triangle plus a spur 3-4: its outage disconnects bus 4.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 50), new Bus(3, BusType.Pq), new Bus(4, BusType.Pq) },
            Generators = new[] { new Generator(1, Pg: 50) },
            Branches = new[] { new Branch(1, 2, 0, 0.1, RateAMva: 100), new Branch(2, 3, 0, 0.1, RateAMva: 100), new Branch(1, 3, 0, 0.1, RateAMva: 100), new Branch(3, 4, 0, 0.1, RateAMva: 0) },
        };
        DcContingencyResult r = DcContingencyScreen.Screen(m);
        Assert.Equal("ISLANDED", r.Rows[3].Status);
        Assert.Empty(r.Rows[3].Violations);
        Assert.Equal(1, r.UnratedBranches);
        Assert.Throws<ArgumentException>(() => DcContingencyScreen.Screen(m, outages: new[] { 0, 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => DcContingencyScreen.Screen(m, outages: new[] { 9 }));
        GridModel off = m.CopyWith(branches: m.Branches.Select((b, i) => i == 1 ? b with { InService = false } : b).ToArray());
        Assert.Throws<ArgumentException>(() => DcContingencyScreen.Screen(off, outages: new[] { 1 }));
        // Request order is kept.
        DcContingencyResult sub = DcContingencyScreen.Screen(m, outages: new[] { 2, 0 });
        Assert.Equal(new[] { 2, 0 }, sub.Rows.Select(x => x.Outage).ToArray());
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Fault level under N-1
    // ---------------------------------------------------------------------------------------------------------------

    // Source X''d = 0.1 pu at bus 1 (MBase 100 = system base), two parallel j0.2 lines to bus 2, a radial j0.3 line 2-3.
    private static GridModel FaultNetwork() => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, BaseKv: 132), new Bus(3, BusType.Pq, BaseKv: 132) },
        Generators = new[] { new Generator(1, Pg: 0, SubtransientXdPu: 0.1) },
        Branches = new[] { new Branch(1, 2, 0, 0.2), new Branch(1, 2, 0, 0.2), new Branch(2, 3, 0, 0.3) },
    };

    [Fact]
    public void Fault_level_under_n_minus_1_matches_the_closed_form_and_names_the_outage()
    {
        // Minimum voltage factor c = 1.00 at 132 kV, so Ik'' (pu) = 1/|Z|. Bus 2: intact Z = 0.1 + 0.2||0.2 = 0.2 -> 5;
        // one line out Z = 0.3 -> 3.3333. Bus 3 adds the 0.3 radial: 0.5 -> 2, one line out 0.6 -> 1.6667, and the
        // outage of 2-3 leaves it with no source. Bus 1 is the source bus: Z = 0.1 whatever the lines do -> 10.
        FaultLevelUnderOutageResult r = FaultLevelUnderOutage.Run(FaultNetwork(), objective: ShortCircuitObjective.Minimum);
        Assert.Equal(new[] { "intact", "branch 0 1-2", "branch 1 1-2", "branch 2 2-3" }, r.States.ToArray());
        FaultLevelBusRow b1 = r.Buses[0], b2 = r.Buses[1], b3 = r.Buses[2];
        Assert.Equal(10.0, b1.MaxIkppPu, 9); Assert.Equal(10.0, b1.MinIkppPu, 9);
        Assert.Equal(5.0, b2.IntactIkppPu, 9);
        Assert.Equal(5.0, b2.MaxIkppPu, 9); Assert.Equal("intact", b2.MaxState);
        Assert.Equal(1.0 / 0.3, b2.MinIkppPu, 9); Assert.Equal("branch 0 1-2", b2.MinState);   // first of two equal outages wins
        Assert.Equal(2.0, b3.MaxIkppPu, 9); Assert.Equal("intact", b3.MaxState);
        Assert.Equal(1.0 / 0.6, b3.MinIkppPu, 9); Assert.Equal("branch 0 1-2", b3.MinState);
        Assert.Equal(1, b3.StatesWithoutSource);
        Assert.Equal(3, b3.StatesEvaluated);
        // Ik'' in kA = pu * Sbase / (sqrt(3) Un).
        Assert.Equal(5.0 * 100.0 / (Math.Sqrt(3.0) * 132.0), b2.IntactIkppKa, 9);
        // Maximum voltage factor 1.10 scales every value by 1.1.
        FaultLevelUnderOutageResult hi = FaultLevelUnderOutage.Run(FaultNetwork(), objective: ShortCircuitObjective.Maximum);
        Assert.Equal(5.5, hi.Buses[1].MaxIkppPu, 9);
    }

    [Fact]
    public void Fault_level_under_n_minus_1_equals_the_incumbent_per_outage_calculation_and_parallel_equals_serial()
    {
        GridModel m = S10TestSupport.LoadMatpower("lib/case9.m").Model;
        FaultLevelUnderOutageResult s = FaultLevelUnderOutage.Run(m, maxDegreeOfParallelism: 1);
        FaultLevelUnderOutageResult p = FaultLevelUnderOutage.Run(m, maxDegreeOfParallelism: 4);
        for (int b = 0; b < s.Buses.Count; b++)
        {
            Assert.Equal(BitConverter.DoubleToInt64Bits(s.Buses[b].MaxIkppKa), BitConverter.DoubleToInt64Bits(p.Buses[b].MaxIkppKa));
            Assert.Equal(BitConverter.DoubleToInt64Bits(s.Buses[b].MinIkppKa), BitConverter.DoubleToInt64Bits(p.Buses[b].MinIkppKa));
            Assert.Equal(s.Buses[b].MaxState, p.Buses[b].MaxState);
            Assert.Equal(s.Buses[b].MinState, p.Buses[b].MinState);
        }
        // Independent path: remove each branch from the model here and call the incumbent at the bus.
        int bus = m.Buses[4].Id;
        double max = Iec60909.ThreePhase(m, bus).IkppKa, min = max;
        for (int k = 0; k < m.Branches.Count; k++)
        {
            GridModel o = m.CopyWith(branches: m.Branches.Where((_, i) => i != k).ToArray());
            double v = Iec60909.ThreePhase(o, bus).IkppKa;
            max = Math.Max(max, v); min = Math.Min(min, v);
        }
        Assert.Equal(max, s.Buses[4].MaxIkppKa);
        Assert.Equal(min, s.Buses[4].MinIkppKa);
        Assert.True(max > min);
    }

    [Fact]
    public void Fault_level_under_n_minus_1_refuses_an_unknown_bus_and_a_bad_degree()
    {
        Assert.Throws<ArgumentException>(() => FaultLevelUnderOutage.Run(FaultNetwork(), buses: new[] { 99 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => FaultLevelUnderOutage.Run(FaultNetwork(), maxDegreeOfParallelism: 0));
    }
}
