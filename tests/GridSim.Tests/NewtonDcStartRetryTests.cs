using System.Numerics;
using GridSim.Core.Abstractions;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The Newton engines' automatic retry (2.1.073): when the ordinary Newton solve does not converge, the case is solved
/// again with the same iteration and step rule from a DC power-flow angle start. A solve that converges ordinarily never
/// reaches the retry, so its bits are those it always had; only a failed solve can change outcome.
/// Residuals here are checked independently of the solver: the bus power mismatch is recomputed from the model's own
/// branch and bus data with the textbook pi model, not through the engine's admittance matrix or injections.
/// </summary>
public class NewtonDcStartRetryTests
{
    private static PowerFlowOptions Flat(int maxIterations) =>
        new() { FlatStart = true, EnforceQLimits = false, Tolerance = 1e-8, MaxIterations = maxIterations };

    private static IEnumerable<IPowerFlowSolver> Engines()
    {
        yield return new NewtonRaphsonPowerFlow();
        yield return new SparseNewtonRaphsonPowerFlow();
    }

    /// <summary>Largest |P| mismatch (non-slack buses) and |Q| mismatch (load buses, and PV buses with no generator) in
    /// pu, from the model data: S_i = V_i conj(sum_k Y_ik V_k) against scheduled generation minus load.</summary>
    internal static double IndependentMaxMismatch(GridModel m, PowerFlowResult r)
    {
        int n = m.BusCount;
        var at = new Dictionary<int, int>(n);
        for (int i = 0; i < n; i++) at[m.Buses[i].Id] = i;
        var v = new Complex[n];
        for (int i = 0; i < n; i++) v[i] = Complex.FromPolarCoordinates(r.Vm[i], r.VaDeg[i] * Math.PI / 180.0);
        var current = new Complex[n];
        foreach (Branch br in m.Branches)
        {
            if (!br.InService) continue;
            int f = at[br.From], t = at[br.To];
            Complex ys = 1.0 / new Complex(br.R, br.X), half = new Complex(0, br.B / 2.0);
            double a = br.TapRatio == 0 ? 1.0 : br.TapRatio;
            Complex tap = Complex.FromPolarCoordinates(a, br.PhaseShiftDeg * Math.PI / 180.0);
            current[f] += (ys + half) / (a * a) * v[f] - ys / Complex.Conjugate(tap) * v[t];
            current[t] += -ys / tap * v[f] + (ys + half) * v[t];
        }
        var sched = new Complex[n];
        var hasGen = new bool[n];
        foreach (Generator g in m.Generators) { int i = at[g.Bus]; sched[i] += new Complex(g.Pg, g.Qg); hasGen[i] = true; }
        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            Bus b = m.Buses[i];
            current[i] += new Complex(b.Gs, b.Bs) / m.BaseMva * v[i];
            Complex mis = v[i] * Complex.Conjugate(current[i]) - (sched[i] - new Complex(b.Pd, b.Qd)) / m.BaseMva;
            if (b.Type != BusType.Slack) worst = Math.Max(worst, Math.Abs(mis.Real));
            if (b.Type == BusType.Pq || (b.Type == BusType.Pv && !hasGen[i])) worst = Math.Max(worst, Math.Abs(mis.Imaginary));
        }
        return worst;
    }

    [Theory]
    [InlineData(9)]
    [InlineData(118)]
    [InlineData(300)]
    public void A_case_that_converges_ordinarily_never_enters_the_retry(int bus)
    {
        GridModel m = bus switch { 9 => IeeeCases.Case9(), 118 => IeeeCases.Case118(), _ => IeeeCases.Case300() };
        foreach (IPowerFlowSolver s in Engines())
            foreach (PowerFlowOptions o in new[] { new PowerFlowOptions(), Flat(50), new PowerFlowOptions { EnforceQLimits = true } })
            {
                PowerFlowResult r = s.Solve(m, o);
                Assert.True(r.Converged);
                Assert.Equal(NewtonRetryOutcome.NotTried, r.NewtonRetry);
                Assert.Null(r.NewtonRetryNote);
            }
    }

    /// <summary>A chain of <paramref name="buses"/> buses (bus 1 the slack, every other bus PV at 1.0 pu, only the last
    /// one generating), each section a lossless line of 0.1 pu reactance carrying the same transfer at
    /// <paramref name="sectionDeg"/> degrees, so the far end sits (buses - 1) x sectionDeg from the slack. From flat start
    /// the step damping (<see cref="NewtonShell.MaxUndampedStep"/>) lets the angles move only so far each step, so the
    /// ordinary solve creeps towards the root with a slowly falling mismatch (the mechanism case13659pegase shows at
    /// scale); the DC power flow gives almost the right angles at once.</summary>
    internal static GridModel Chain(int buses, double sectionDeg)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double p = Math.Sin(sectionDeg * Math.PI / 180.0) / 0.1 * 100.0;
        var sb = new System.Text.StringBuilder("function mpc = chain\nmpc.baseMVA = 100;\nmpc.bus = [\n");
        for (int i = 1; i <= buses; i++) sb.Append(inv, $" {i} {(i == 1 ? 3 : 2)} 0 0 0 0 1 1 0 230 1 1.1 0.9;\n");
        sb.Append("];\nmpc.gen = [\n 1 0 0 9000 -9000 1.0 100 1 9000 0;\n");
        for (int i = 2; i <= buses; i++) sb.Append(inv, $" {i} {(i == buses ? p : 0.0):F3} 0 9000 -9000 1.0 100 1 9000 0;\n");
        sb.Append("];\nmpc.branch = [\n");
        for (int i = 1; i < buses; i++) sb.Append(inv, $" {i} {i + 1} 0.0 0.1 0 0 0 0 0 0 1 -360 360;\n");
        sb.Append("];\n");
        return MatpowerParser.ParseWithDiagnostics(sb.ToString(), "chain").Model;
    }

    /// <summary>An 8-bus chain at 60 deg a section (420 deg end to end) takes 21 iterations from flat start. With a cap of
    /// 10 the ordinary solve stalls (its mismatch is still about 0.4 of where it began, far above the
    /// <see cref="NewtonShell.RetryStallFactor"/> line), and the retry from the DC-angle start converges within the same
    /// cap to the same root (angles equal modulo whole turns: the DC start is reduced to [-180, 180] deg).</summary>
    [Fact]
    public void A_stalled_ordinary_solve_converges_on_the_dc_start_retry_to_the_same_root()
    {
        GridModel m = Chain(8, 60.0);
        PowerFlowResult reference = new SparseNewtonRaphsonPowerFlow().Solve(m, Flat(50));
        Assert.True(reference.Converged);
        Assert.Equal(NewtonRetryOutcome.NotTried, reference.NewtonRetry);
        Assert.Equal(21, reference.Iterations);

        double startMismatch = new SparseNewtonRaphsonPowerFlow().Solve(m, Flat(0) with { RetryFromDcStart = false }).MaxMismatch;
        PowerFlowResult ordinary = new SparseNewtonRaphsonPowerFlow().Solve(m, Flat(10) with { RetryFromDcStart = false });
        Assert.False(ordinary.Converged);
        Assert.True(ordinary.MaxMismatch > 0.1 * startMismatch, "precondition: the ordinary solve stalls");

        var results = new List<PowerFlowResult>();
        foreach (IPowerFlowSolver s in Engines())
        {
            PowerFlowResult r = s.Solve(m, Flat(10));
            Assert.True(r.Converged);
            Assert.Equal(PowerFlowStatus.Converged, r.Status);
            Assert.Equal(NewtonRetryOutcome.Converged, r.NewtonRetry);
            Assert.Contains("converged on the automatic retry from a DC power-flow angle start", r.NewtonRetryNote, StringComparison.Ordinal);
            Assert.InRange(r.Iterations, 1, 10);
            Assert.True(r.MaxMismatch < 1e-8);
            Assert.True(IndependentMaxMismatch(m, r) <= 1e-8, $"independent mismatch {IndependentMaxMismatch(m, r):E3}");
            for (int i = 0; i < m.BusCount; i++)
            {
                Assert.True(Math.Abs(r.Vm[i] - reference.Vm[i]) < 1e-6);
                Assert.True(Math.Abs(Math.IEEERemainder(r.VaDeg[i] - reference.VaDeg[i], 360.0)) < 1e-4);
            }
            results.Add(r);
        }
        // Dense and sparse stay bit-identical on the retry path, and the retry is repeatable run to run.
        Assert.Equal(results[0].Vm, results[1].Vm);
        Assert.Equal(results[0].VaDeg, results[1].VaDeg);
        Assert.Equal(results[0].Iterations, results[1].Iterations);
        PowerFlowResult again = new SparseNewtonRaphsonPowerFlow().Solve(m, Flat(10));
        Assert.Equal(results[1].Vm, again.Vm);
        Assert.Equal(results[1].VaDeg, again.VaDeg);
    }

    private static void SameBits(PowerFlowResult a, PowerFlowResult b)
    {
        Assert.Equal(a.Converged, b.Converged);
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.MaxMismatch), BitConverter.DoubleToInt64Bits(b.MaxMismatch));
        Assert.Equal(a.Vm.Select(BitConverter.DoubleToInt64Bits), b.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.VaDeg.Select(BitConverter.DoubleToInt64Bits), b.VaDeg.Select(BitConverter.DoubleToInt64Bits));
    }

    /// <summary>The iteration cap is the caller's budget. A solve stopped by the cap one step short of converging (case9,
    /// case14, case118 from flat start: the IterationCapTests configuration) has cut its mismatch far below
    /// <see cref="NewtonShell.RetryStallFactor"/> times its start, so it is not retried: it is reported MaxIterations with
    /// the bits of the ordinary solve, exactly as with the retry switched off.</summary>
    [Fact]
    public void A_solve_stopped_by_the_cap_while_converging_never_enters_the_retry()
    {
        foreach (GridModel m in new[] { IeeeCases.Case9(), IeeeCases.Case14(), IeeeCases.Case118() })
            foreach (IPowerFlowSolver s in Engines())
            {
                int k = s.Solve(m, Flat(50)).Iterations;
                double startMismatch = s.Solve(m, Flat(0) with { RetryFromDcStart = false }).MaxMismatch;
                PowerFlowResult off = s.Solve(m, Flat(k - 1) with { RetryFromDcStart = false });
                Assert.False(NewtonShell.Stalled(startMismatch, off.MaxMismatch), $"{m.Name}: one step short reads stalled");
                Assert.True(off.MaxMismatch <= 1e-5 * startMismatch, $"{m.Name}: margin below the stall line");
                PowerFlowResult r = s.Solve(m, Flat(k - 1));
                Assert.False(r.Converged);
                Assert.Equal(PowerFlowStatus.MaxIterations, r.Status);
                Assert.Equal(k - 1, r.Iterations);
                Assert.Equal(NewtonRetryOutcome.NotTried, r.NewtonRetry);
                Assert.Null(r.NewtonRetryNote);
                SameBits(off, r);
            }
    }

    /// <summary><see cref="PowerFlowOptions.RetryFromDcStart"/> = false: a solve that would be retried is reported exactly
    /// as the ordinary solve ended, and no retry is recorded.</summary>
    [Fact]
    public void RetryFromDcStart_false_turns_the_retry_off()
    {
        Assert.True(new PowerFlowOptions().RetryFromDcStart);
        foreach (IPowerFlowSolver s in Engines())
        {
            foreach ((GridModel m, int cap) in new[] { (Chain(8, 60.0), 10) })
            {
                PowerFlowResult off = s.Solve(m, Flat(cap) with { RetryFromDcStart = false });
                Assert.False(off.Converged);
                Assert.Equal(NewtonRetryOutcome.NotTried, off.NewtonRetry);
                Assert.Null(off.NewtonRetryNote);
                Assert.Equal(cap, off.Iterations);
                Assert.NotEqual(NewtonRetryOutcome.NotTried, s.Solve(m, Flat(cap)).NewtonRetry);   // on (the default), it runs
            }
            // Approved by the maintainer (2.1.074): case9 at cap 1 was a retried solve here; a cap of 1 is never retried now.
            PowerFlowResult one = s.Solve(IeeeCases.Case9(), Flat(1));
            Assert.False(one.Converged);
            Assert.Equal(1, one.Iterations);
            Assert.Equal(NewtonRetryOutcome.NotTried, one.NewtonRetry);
        }
    }

    [Fact]
    public void The_stall_test_is_the_documented_ratio_and_treats_nan_as_stalled()
    {
        Assert.Equal(1e-3, NewtonShell.RetryStallFactor);
        Assert.True(NewtonShell.Stalled(1.0, 0.5));
        Assert.True(NewtonShell.Stalled(1.0, 1.0));
        Assert.True(NewtonShell.Stalled(1.0, 2.0));
        Assert.True(NewtonShell.Stalled(1.0, 1.0000001e-3));
        Assert.False(NewtonShell.Stalled(1.0, 1e-3));
        Assert.False(NewtonShell.Stalled(1.0, 1e-7));
        Assert.True(NewtonShell.Stalled(double.NaN, 1e-7));
        Assert.True(NewtonShell.Stalled(1.0, double.NaN));
        Assert.True(NewtonShell.Stalled(1.0, double.PositiveInfinity));
    }

    [Fact]
    public void When_the_retry_also_fails_the_ordinary_result_is_reported_with_a_note()
    {
        GridModel m = IeeeCases.Case9();
        foreach (IPowerFlowSolver s in Engines())
        {
            PowerFlowResult r = s.Solve(m, Flat(1));
            Assert.False(r.Converged);
            Assert.Equal(PowerFlowStatus.MaxIterations, r.Status);
            Assert.Equal(1, r.Iterations);
            // Approved by the maintainer (2.1.074): a cap of 1 is never retried, so case9 at cap 1 is not retried.
            Assert.Equal(NewtonRetryOutcome.NotTried, r.NewtonRetry);
            Assert.Null(r.NewtonRetryNote);
        }
    }

    private const string LightTwoBus = @"
function mpc = light_two_bus
mpc.baseMVA = 100;
mpc.bus = [
    1   3   0    0    0   0   1   1.0   0   230   1   1.1   0.9;
    2   1   0.5  0.1  0   0   1   1.0   0   230   1   1.1   0.9;
];
mpc.gen = [
    1   0   0   300   -300   1.0   100   1   300   0;
];
mpc.branch = [
    1   2   0.001   0.01   0.0   250   0   0   0   0   1   -360   360;
];
";

    /// <summary>A solve that converges in exactly one iteration at a cap of 1 is reported Converged by the ordinary solve,
    /// with no retry (a lightly loaded two-bus system from flat start: the start mismatch is 5e-3 pu and one Newton step
    /// takes it below 1e-4 pu).</summary>
    [Fact]
    public void A_solve_converging_in_one_iteration_at_cap_1_is_converged_with_no_retry()
    {
        GridModel m = MatpowerParser.ParseWithDiagnostics(LightTwoBus, "light_two_bus").Model;
        foreach (IPowerFlowSolver s in Engines())
        {
            PowerFlowResult r = s.Solve(m, Flat(1) with { Tolerance = 1e-4 });
            Assert.True(r.Converged);
            Assert.Equal(PowerFlowStatus.Converged, r.Status);
            Assert.Equal(1, r.Iterations);
            Assert.Equal(NewtonRetryOutcome.NotTried, r.NewtonRetry);
            Assert.Null(r.NewtonRetryNote);
        }
    }

    // Two separate two-bus systems, each with its own slack: the DC-angle start needs one slack and one island.
    private const string TwoIslands = @"
function mpc = two_islands
mpc.baseMVA = 100;
mpc.bus = [
    1   3   0    0    0   0   1   1.0   0   230   1   1.1   0.9;
    2   1   80   20   0   0   1   1.0   0   230   1   1.1   0.9;
    3   3   0    0    0   0   1   1.0   0   230   1   1.1   0.9;
    4   1   60   10   0   0   1   1.0   0   230   1   1.1   0.9;
];
mpc.gen = [
    1   0   0   300   -300   1.02   100   1   300   0;
    3   0   0   300   -300   1.01   100   1   300   0;
];
mpc.branch = [
    1   2   0.01   0.10   0.02   250   0   0   0   0   1   -360   360;
    3   4   0.01   0.10   0.02   250   0   0   0   0   1   -360   360;
];
";

    // The same two islands with the second one loaded far beyond its maximum transferable power (5000 MW through x = 0.1 pu
    // on 100 MVA, a limit of about 500 MW): Newton cannot converge there, so the first solve stalls at the default cap.
    private static readonly string TwoIslandsOverloaded = TwoIslands.Replace("4   1   60   10", "4   1   5000   10");

    [Fact]
    public void Without_a_single_slack_island_the_retry_is_not_run()
    {
        // Fixture changed (approved by the maintainer, 2.1.074): the test used a cap of 1 to make the first solve fail, and a
        // cap of 1 is never retried now. The overloaded island makes the first solve stall at the default cap instead.
        GridModel m = MatpowerParser.ParseWithDiagnostics(TwoIslandsOverloaded, "two_islands_overloaded").Model;
        foreach (IPowerFlowSolver s in Engines())
        {
            PowerFlowOptions o = Flat(50);
            PowerFlowResult off = s.Solve(m, o with { RetryFromDcStart = false });
            Assert.False(off.Converged);
            PowerFlowResult r = s.Solve(m, o);
            Assert.False(r.Converged);
            Assert.Equal(NewtonRetryOutcome.Unavailable, r.NewtonRetry);
            Assert.Contains("was not run", r.NewtonRetryNote, StringComparison.Ordinal);
            SameBits(off, r);   // the reported state is the first attempt's
        }
    }

    /// <summary>Two islands at a cap of 1: the first solve fails, and a cap of 1 is never retried, so the island guard is not
    /// reached either (2.1.074).</summary>
    [Fact]
    public void A_two_island_model_at_cap_1_is_not_retried()
    {
        GridModel m = MatpowerParser.ParseWithDiagnostics(TwoIslands, "two_islands").Model;
        foreach (IPowerFlowSolver s in Engines())
        {
            PowerFlowResult r = s.Solve(m, Flat(1));
            Assert.False(r.Converged);
            Assert.Equal(NewtonRetryOutcome.NotTried, r.NewtonRetry);
            Assert.Null(r.NewtonRetryNote);
        }
    }

    [Fact]
    public void Branch_angles_are_measured_net_of_phase_shift_and_wrapped()
    {
        GridModel m = MatpowerParser.ParseWithDiagnostics(TwoIslands, "two_islands").Model;
        Func<int, int> at = NewtonShell.ModelIndex(m);
        Assert.Equal(10.0, NewtonShell.MaxBranchAngleDeg(m, at, new[] { 0.0, -10.0, 0.0, 0.0 }).AngleDeg, 12);
        // 350 deg across a branch is 10 deg the other way.
        Assert.Equal(10.0, NewtonShell.MaxBranchAngleDeg(m, at, new[] { 0.0, 0.0, 350.0, 0.0 }).AngleDeg, 9);
        (double angle, int from, int to) = NewtonShell.MaxBranchAngleDeg(m, at, new[] { 0.0, 0.0, 170.0, 0.0 });
        Assert.Equal((170.0, 3, 4), (angle, from, to));
    }

    /// <summary>PGLib / MATPOWER case13659pegase (not committed; set GRIDSIM_CASE13659PEGASE to the file). From flat start
    /// the ordinary step rule stalls at a mismatch of 63.157 pu after 50 iterations; the DC-angle-start retry converges to
    /// the physical root (largest branch angle about 24.4 deg, losses about 9108.748 MW).</summary>
    [EnvironmentFact("needs case13659pegase.m", "GRIDSIM_CASE13659PEGASE")]
    public void Case13659pegase_converges_from_flat_start_on_the_retry()
    {
        GridModel m = MatpowerParser.ParseFile(Environment.GetEnvironmentVariable("GRIDSIM_CASE13659PEGASE")!);
        PowerFlowResult r = new SparseNewtonRaphsonPowerFlow().Solve(m, Flat(50));
        Assert.True(r.Converged);
        Assert.Equal(NewtonRetryOutcome.Converged, r.NewtonRetry);
        Assert.Equal(10, r.Iterations);
        double independent = IndependentMaxMismatch(m, r);
        Assert.True(independent <= 1e-8, $"independent mismatch {independent:E3}");
        Assert.InRange(NewtonShell.MaxBranchAngleDeg(m, NewtonShell.ModelIndex(m), r.VaDeg).AngleDeg, 24.0, 25.0);
        Assert.InRange(r.TotalLossMw, 9108.7, 9108.8);
        PowerFlowResult again = new SparseNewtonRaphsonPowerFlow().Solve(m, Flat(50));
        Assert.Equal(r.Vm, again.Vm);
        Assert.Equal(r.VaDeg, again.VaDeg);
    }
}
