using System.Numerics;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Opf;
using GridSim.Core.Model;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// The loss objective of the extended AC-OPF on a stacked (AC-SCOPF) problem whose outage block has a free direction.
///
/// <para>The case: two buses, base 100 MVA. Bus 1 slack, one unit, Vm 0.9..1.1, Q unbounded (+-9999 MVAr). Bus 2:
/// 60 MW + 50 MVAr, Vm 0.95..1.05. Branch 0: R 0.015, X 0.15, B 0. Branch 1 (a cable): R 0.01, X 0.10, B 1.5. Both
/// unrated. Contingency: branch 1. Round 1 secures outage 1; round 2 solves the base case and the outage block together.</para>
///
/// <para>Why round 2 used to stop short. The outage block has no objective, and with the unit at the slack its state has one
/// free direction (the unit's voltage after the outage): only the barrier holds it. In that direction the primal-dual step
/// moves sigma = 0.1 of the Newton step to the analytic centre. With z mu = gamma on every row and gamma+ = sigma gamma,
/// the linearised complementarity gives dmu = -(1 - sigma) mu + (mu a / z) dt, and the block's stationarity then gives
/// dt = -sigma (sum a mu) / (sum a^2 mu / z) = sigma times the Newton step of the barrier -sum log z. So the error there
/// falls by 0.9 per iteration (the run moved V1 of the block by 9.6e-4, 8.6e-4, 7.8e-4, 7.0e-4, 6.3e-4 pu), each full
/// step leaves an equality residual of order |Y| |dx|^2 (falling by 0.81: feasibility 1.64e-8, 1.32e-8, 1.07e-8,
/// 8.7e-9, 7.0e-9), and the barrier falls by 0.1 a step, reaching machine epsilon at iteration 18. The iteration stopped as
/// a numerical failure with 7e-9 against the 1e-12 tolerance. That residual (7e-7 pu before scaling) is eight orders above
/// the rounding of g (about 1e-16 pu after the 1 + |z|inf scaling), so the tolerance is reachable; the fix projects the
/// point onto g = 0 at the barrier floor and holds it to the same tests.</para>
/// </summary>
public sealed class LossOpfCableConvergenceTests(ITestOutputHelper output)
{
    private static GridModel CablePair() => new()
    {
        Name = "cable-pair", BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, VminPu: 0.9, VmaxPu: 1.1), new Bus(2, BusType.Pq, Pd: 60, Qd: 50, VminPu: 0.95, VmaxPu: 1.05) },
        Generators = new[]
        {
            new Generator(1, 0, Qmin: -9999, Qmax: 9999, Pmin: 0, Pmax: 1000, Name: "G") { Cost = new GeneratorCost(GeneratorCostModel.Polynomial, 0, 0, new[] { 10.0, 0.0 }) },
        },
        Branches = new[] { new Branch(1, 2, 0.015, 0.15, 0, Name: "L0"), new Branch(1, 2, 0.01, 0.10, 1.5, Name: "C1") },
    };

    // The independent optimum (SciPy 1.18.1, NumPy 2.5.3): for each V1, bus 2's two balance equations solved for (theta2,
    // V2) by scipy.optimize.fsolve plus Newton polish; Pg = P1 of the MATPOWER pi model (B/2 at each end of the cable);
    // the loss Pg - 60 MW minimised over V1 by minimize_scalar (bounded, xatol 1e-12). V1 = 1.0081549211, V2 = 1.0204737290,
    // theta2 = -2.0992901802 deg (inside every limit, so the bounds do not bind). The minimum is flat, so the 1e-12 error in
    // V1 moves the loss by about 1e-24 MW. SLSQP on the full equations agrees to 7e-12 MW.
    private const double SciPyLossMw = 0.25292247083157093;

    // The admittance of each branch and the cable's charging (pu).
    private static readonly Complex Y0 = 1 / new Complex(0.015, 0.15), Y1 = 1 / new Complex(0.01, 0.10);
    private const double HalfCharging = 1.5 / 2;

    /// <summary>The largest power-balance mismatch (pu) of a two-bus state, with or without the cable.</summary>
    private static double Mismatch(IReadOnlyList<AcOpfBusResult> buses, AcOpfGenResult unit, bool cableIn)
    {
        Complex ys = cableIn ? Y0 + Y1 : Y0;
        Complex yd = ys + (cableIn ? new Complex(0, HalfCharging) : Complex.Zero);
        Complex v1 = Complex.FromPolarCoordinates(buses[0].VmPu, buses[0].VaDeg * Math.PI / 180);
        Complex v2 = Complex.FromPolarCoordinates(buses[1].VmPu, buses[1].VaDeg * Math.PI / 180);
        Complex s1 = v1 * Complex.Conjugate(yd * v1 - ys * v2);
        Complex s2 = v2 * Complex.Conjugate(yd * v2 - ys * v1);
        Complex g1 = s1 - new Complex(unit.PgMw, unit.QgMvar) / 100;
        Complex g2 = s2 + new Complex(0.6, 0.5);
        return Math.Max(Math.Max(Math.Abs(g1.Real), Math.Abs(g1.Imaginary)), Math.Max(Math.Abs(g2.Real), Math.Abs(g2.Imaginary)));
    }

    /// <summary>|x|inf of a block in pu (voltages, angles in radians, unit outputs).</summary>
    private static double XInf(IReadOnlyList<AcOpfBusResult> buses, AcOpfGenResult unit) => new[]
    {
        buses[0].VmPu, buses[1].VmPu, Math.Abs(buses[0].VaDeg) * Math.PI / 180, Math.Abs(buses[1].VaDeg) * Math.PI / 180,
        Math.Abs(unit.PgMw) / 100, Math.Abs(unit.QgMvar) / 100,
    }.Max();

    [Fact]
    public void The_loss_objective_converges_in_round_two_of_the_cable_pair_scopf()
    {
        GridModel m = CablePair();
        AcScopfResult r = AcScopf.Solve(m, new AcScopfOptions { Contingencies = new[] { 1 }, Objective = AcOpfObjective.Losses });

        // Before the fix: NotConverged, round 2 a numerical failure at iteration 18 (barrier 4.3e-17, feasibility 7.0e-9).
        // Now round 2 converges, and the re-screen of the secured outage fails exactly as with the cost objective
        // (CoreReviewFix2Tests): V1 held at the base value leaves V2 = 0.912 pu after the outage.
        Assert.Equal(AcScopfStatus.SecuredOutageFailsScreen, r.Status);
        Assert.Equal(new[] { 1 }, r.Secured);
        Assert.Equal(2, r.Rounds.Count);
        AcOpfExtendedResult last = r.Last!;
        Assert.True(last.Converged, last.Base.Message);
        Assert.Contains("at the barrier floor, after a feasibility correction", last.Base.Message, StringComparison.Ordinal);
        Assert.True(last.Base.Log[^1].Complementarity < AcOpfExtended.LossTolerance);

        // Feasibility, checked outside the code: the reported states satisfy the power-flow equations. The stopping test is
        // |g|inf < 1e-12 (1 + max(|x|inf, |z|inf)); |z|inf is the slack of the unit's Q limits, 99.99 + 1.018 = 101.01, so
        // |g|inf < 1.0202e-10 pu. Before the fix the block's mismatch was 7e-7 pu.
        AcOpfBlockResult blk = Assert.Single(last.Outages);
        double zInf = 9999.0 / 100 + Math.Abs(last.Base.Generators[0].QgMvar) / 100;
        double gBound = AcOpfExtended.LossTolerance * (1 + zInf);
        Assert.True(Mismatch(last.Base.Buses, last.Base.Generators[0], cableIn: true) < gBound);
        Assert.True(Mismatch(blk.Buses, blk.Generators[0], cableIn: false) < gBound,
            $"block mismatch {Mismatch(blk.Buses, blk.Generators[0], cableIn: false):R}");
        Assert.InRange(blk.Buses[0].VmPu, 0.9, 1.1);
        Assert.InRange(blk.Buses[1].VmPu, 0.95, 1.05);

        // The loss against SciPy. The complementarity test bounds the duality gap by 1e-12 (1 + |x|inf) scaled, that is
        // 1e-8 (1 + |x|inf) MW at the 1e-4 objective scale; the cost-change test bounds what the correction moved the
        // objective by 1e-12 (1 + |f|) scaled, 1e-8 (1 + |f|) MW with |f| = 1e-4 x 60.25. |x|inf = 1.018 (the base Q).
        double xInf = Math.Max(XInf(last.Base.Buses, last.Base.Generators[0]), XInf(blk.Buses, blk.Generators[0]));
        double bound = 1e-8 * (1 + xInf) + 1e-8 * (1 + 1e-4 * last.Base.TotalGenerationMw);
        output.WriteLine($"{last.Base.Message}; losses {last.LossesMw:R} MW, SciPy {SciPyLossMw:R}, bound {bound:R}; block V1 {blk.Buses[0].VmPu:R} V2 {blk.Buses[1].VmPu:R}");
        Assert.True(Math.Abs(last.LossesMw - SciPyLossMw) <= bound, $"losses {last.LossesMw:R} MW, SciPy {SciPyLossMw:R}, bound {bound:R}");
    }

    [Fact]
    public void The_single_block_loss_optimum_matches_scipy()
    {
        // Round 1 alone (no outage block, no free direction): converges without the correction, to the same optimum.
        AcOpfExtendedResult b = AcOpfExtended.Solve(CablePair(), new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses });
        Assert.True(b.Converged, b.Base.Message);
        Assert.DoesNotContain("feasibility correction", b.Base.Message, StringComparison.Ordinal);
        double xInf = XInf(b.Base.Buses, b.Base.Generators[0]);
        Assert.True(Math.Abs(b.LossesMw - SciPyLossMw) <= 1e-8 * (1 + xInf), $"losses {b.LossesMw:R} MW, SciPy {SciPyLossMw:R}");
        Assert.Equal(1.0081549211, b.Base.Buses[0].VmPu, 1e-4);
    }

    [Fact]
    public void The_cost_objective_on_the_same_scopf_is_untouched()
    {
        // The correction is on the losses and reactive power path only. The cost objective stops at MIPS's 1e-6 tolerances
        // before the barrier floor, so its round 2 converges without it.
        AcScopfResult r = AcScopf.Solve(CablePair(), new AcScopfOptions { Contingencies = new[] { 1 } });
        Assert.True(r.Last!.Converged, r.Last.Base.Message);
        Assert.DoesNotContain("feasibility correction", r.Last.Base.Message, StringComparison.Ordinal);
    }
}
