using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Dse;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// DOC-9 (docs-session stream 9, October 2026): real-time and distribution estimation, in process.
/// <list type="bullet">
/// <item>D-DOC-112: the three-phase WLS estimator refuses a state above its dense limit before allocating.</item>
/// <item>D-DOC-113: the ensemble Kalman filter samples the FULL process-noise covariance (deterministic semi-definite
/// Cholesky); with a diagonal Q it is bit-identical to the pre-change filter (a verbatim copy of the old step is the
/// reference here).</item>
/// <item>D-DOC-114: the branch-current estimator marks a branch phase with no current phasor unobservable (not converged).</item>
/// <item>D-DOC-115: the robust three-phase WLS reports hitting its IRLS pass cap.</item>
/// <item>D-DOC-116: every sequence fault type refuses a zero loop impedance instead of returning Infinity or NaN.</item>
/// <item>D-DOC-117: the EKF (and the UKF and EnKF, same defect) steps are transactional: a step that throws leaves the
/// filter exactly as it was.</item>
/// <item>D-DOC-165: the branch-current estimator accepts a current named in reversed orientation, as the WLS does.</item>
/// <item>D-DOC-217: the three-phase WLS reports per-measurement residuals and checks its iteration controls.</item>
/// <item>D-DOC-229: the UKF and EnKF constructors check the state size (and the EnKF its ensemble size).</item>
/// </list>
/// </summary>
public class Doc9RealtimeDistributionTests
{
    private readonly ITestOutputHelper _out;

    public Doc9RealtimeDistributionTests(ITestOutputHelper output) => _out = output;

    private static readonly PhaseImpedance Z = PhaseImpedance.Diagonal(new Complex(0.02, 0.06));

    private static PhaseImpedance Coupled() => new(new Complex[,]
    {
        { new(0.03, 0.09), new(0.01, 0.03), new(0.01, 0.028) },
        { new(0.01, 0.03), new(0.032, 0.088), new(0.01, 0.031) },
        { new(0.01, 0.028), new(0.01, 0.031), new(0.031, 0.091) },
    });

    // Source 0, buses 1 and 2 in a chain, unbalanced loads.
    private static MultiPhaseModel Radial() => new(
        new[] { new PhaseBus(0, true, MultiPhaseModel.BalancedNominal(1.0)), new PhaseBus(1, false), new PhaseBus(2, false) },
        new[] { new PhaseBranch(0, 1, Coupled()), new PhaseBranch(1, 2, Z) },
        new[]
        {
            new PhaseLoad(1, new[] { new Complex(0.5, 0.2), new Complex(0.3, 0.1), new Complex(0.4, 0.12) }),
            new PhaseLoad(2, new[] { new Complex(0.2, 0.05), new Complex(0.45, 0.15), new Complex(0.25, 0.08) }),
        });

    // Exact current phasors (into each From end) and per-phase magnitudes from the sweep truth.
    private static (MultiPhaseResult Truth, List<ThreePhaseCurrent> Currents, List<ThreePhaseVMag> VMags) Truth(MultiPhaseModel model, double sigma = 0.001)
    {
        MultiPhaseResult truth = new ThreePhaseSweep().Solve(model);
        Assert.True(truth.Converged);
        var index = new Dictionary<int, int>();
        for (int i = 0; i < truth.BusIds.Count; i++) index[truth.BusIds[i]] = i;
        var currents = new List<ThreePhaseCurrent>();
        foreach (PhaseBranch br in model.Branches)
        {
            Complex[] vf = truth.V[index[br.From]], vt = truth.V[index[br.To]];
            Complex[] i = br.Z.Invert().Multiply(new[] { vf[0] - vt[0], vf[1] - vt[1], vf[2] - vt[2] });
            for (int p = 0; p < 3; p++) currents.Add(new ThreePhaseCurrent(br.From, br.To, p, i[p], sigma));
        }
        var vmags = new List<ThreePhaseVMag>();
        foreach (PhaseBus b in model.Buses)
            if (!b.IsSource)
                for (int p = 0; p < 3; p++) vmags.Add(new ThreePhaseVMag(b.Id, p, truth.V[index[b.Id]][p].Magnitude, sigma));
        return (truth, currents, vmags);
    }

    private static void SameBits(Complex expected, Complex actual, string what)
    {
        Assert.True(BitConverter.DoubleToInt64Bits(expected.Real) == BitConverter.DoubleToInt64Bits(actual.Real), $"{what} real: {expected.Real:R} vs {actual.Real:R}");
        Assert.True(BitConverter.DoubleToInt64Bits(expected.Imaginary) == BitConverter.DoubleToInt64Bits(actual.Imaginary), $"{what} imag: {expected.Imaginary:R} vs {actual.Imaginary:R}");
    }

    private static void SameBits(double[] expected, double[] actual, string what)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(BitConverter.DoubleToInt64Bits(expected[i]) == BitConverter.DoubleToInt64Bits(actual[i]),
                $"{what}[{i}]: {expected[i]:R} vs {actual[i]:R}");
    }

    private static void SameEstimate(DseEstimate expected, DseEstimate actual)
    {
        SameBits(expected.Vm, actual.Vm, "Vm");
        SameBits(expected.VaDeg, actual.VaDeg, "VaDeg");
        SameBits(expected.StateVmSigma, actual.StateVmSigma, "Vm sigma");
        SameBits(expected.StateVaSigmaDeg, actual.StateVaSigmaDeg, "Va sigma");
        Assert.Equal(expected.Converged, actual.Converged);
    }

    // =================================================================================================================
    // D-DOC-112: the dense size guard
    // =================================================================================================================

    [Fact]
    public void D112_the_three_phase_wls_refuses_a_state_above_its_dense_limit_before_allocating()
    {
        Assert.Equal(DenseEstimationLimits.RecursiveFilterMaxState, ThreePhaseWlsEstimator.MaxState);
        Assert.Equal(2500, ThreePhaseWlsEstimator.MaxState);
        // 417 non-source buses = 2,502 states: one bus over the 416 that fit.
        int nonSource = ThreePhaseWlsEstimator.MaxState / 6 + 1;
        var buses = new List<PhaseBus> { new(0, true, MultiPhaseModel.BalancedNominal(1.0)) };
        var branches = new List<PhaseBranch>();
        for (int i = 1; i <= nonSource; i++) { buses.Add(new PhaseBus(i, false)); branches.Add(new PhaseBranch(i - 1, i, Z)); }
        var model = new MultiPhaseModel(buses, branches, Array.Empty<PhaseLoad>());
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            ThreePhaseWlsEstimator.Estimate(model, Array.Empty<ThreePhaseVMag>(), Array.Empty<ThreePhaseCurrent>()));
        Assert.Contains("limited to 2500 states (416 non-source buses)", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"{6 * nonSource} x {6 * nonSource} gain matrix", ex.Message, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // D-DOC-115 and D-DOC-217: the robust cap flag, the residual rows, the iteration controls
    // =================================================================================================================

    [Fact]
    public void D115_the_robust_estimate_reports_hitting_its_irls_cap_and_a_settled_one_does_not()
    {
        MultiPhaseModel model = Radial();
        (_, List<ThreePhaseCurrent> currents, List<ThreePhaseVMag> vmags) = Truth(model);
        vmags[0] = vmags[0] with { Value = vmags[0].Value + 0.2 };   // one gross error

        // One pass can never settle: its single re-weighting moves the weights away from 1.
        ThreePhaseWlsResult capped = ThreePhaseWlsEstimator.Estimate(model, vmags, currents, robust: true, maxRobustIterations: 1);
        Assert.True(capped.Converged);
        Assert.True(capped.IrlsCapReached);

        // Plain WLS never has the flag.
        Assert.False(ThreePhaseWlsEstimator.Estimate(model, vmags, currents).IrlsCapReached);

        // Exact data: the residuals are all at round-off, so every weight goes to 1 / floor on the first pass and stays
        // there on the second: settled, no flag.
        (_, List<ThreePhaseCurrent> c2, List<ThreePhaseVMag> v2) = Truth(model);
        ThreePhaseWlsResult settled = ThreePhaseWlsEstimator.Estimate(model, v2, c2, robust: true);
        Assert.True(settled.Converged);
        Assert.False(settled.IrlsCapReached);
    }

    [Fact]
    public void D115_the_flag_does_not_change_the_robust_arithmetic()
    {
        // The flag is written after the loop's own arithmetic and nothing inside the loop reads it, so a capped robust
        // estimate is the same computation as before; two identical capped calls are bit-identical.
        MultiPhaseModel model = Radial();
        (_, List<ThreePhaseCurrent> currents, List<ThreePhaseVMag> vmags) = Truth(model);
        vmags[0] = vmags[0] with { Value = vmags[0].Value + 0.2 };
        ThreePhaseWlsResult a = ThreePhaseWlsEstimator.Estimate(model, vmags, currents, robust: true, maxRobustIterations: 3);
        ThreePhaseWlsResult b = ThreePhaseWlsEstimator.Estimate(model, vmags, currents, robust: true, maxRobustIterations: 3);
        for (int i = 0; i < a.V.Length; i++) for (int p = 0; p < 3; p++) SameBits(a.V[i][p], b.V[i][p], $"bus {i} phase {p}");
        Assert.Equal(a.IrlsCapReached, b.IrlsCapReached);
    }

    [Fact]
    public void D217_each_measurement_in_the_fit_has_a_residual_row_in_the_orientation_it_was_given()
    {
        MultiPhaseModel model = Radial();
        (MultiPhaseResult truth, List<ThreePhaseCurrent> currents, List<ThreePhaseVMag> vmags) = Truth(model);
        // Author branch 1-2's currents from the other end, and add a magnitude on the source bus (not in the fit).
        List<ThreePhaseCurrent> mixed = currents
            .Select(c => c.FromBusId == 1 && c.ToBusId == 2 ? new ThreePhaseCurrent(2, 1, c.Phase, -c.Value, c.Sigma) : c).ToList();
        var withSource = new List<ThreePhaseVMag>(vmags) { new(0, 0, 1.0, 0.001) };

        ThreePhaseWlsResult r = ThreePhaseWlsEstimator.Estimate(model, withSource, mixed);
        Assert.True(r.Converged);
        Assert.Equal(vmags.Count + mixed.Count, r.Residuals.Count);   // the source-bus magnitude is not listed
        Assert.All(r.Residuals.Take(vmags.Count), row => Assert.Equal("vmag", row.Kind));
        Assert.All(r.Residuals.Skip(vmags.Count), row => Assert.Equal("current", row.Kind));
        for (int k = 0; k < mixed.Count; k++)
        {
            ThreePhaseMeasurementResidual row = r.Residuals[vmags.Count + k];
            Assert.Equal(mixed[k].FromBusId, row.FromBusId);
            Assert.Equal(mixed[k].ToBusId, row.ToBusId);
            SameBits(mixed[k].Value, row.Measured, $"current row {k} measured as given");
        }
        // Consistent data: every standardised residual is at round-off, and the sum of squares is the objective.
        Assert.All(r.Residuals, row => Assert.True(row.Standardised < 1e-6, $"{row.Kind} {row.FromBusId}-{row.ToBusId} {row.Phase}: {row.Standardised:R}"));
        double sumSq = r.Residuals.Sum(row => row.Standardised * row.Standardised);
        Assert.True(Math.Abs(sumSq - r.ObjectiveJ) <= 1e-9 * Math.Max(1e-30, r.ObjectiveJ) + 1e-24, $"sum {sumSq:R} vs J {r.ObjectiveJ:R}");

        // A gross error shows up in its own row.
        var bad = new List<ThreePhaseVMag>(vmags);
        bad[4] = bad[4] with { Value = bad[4].Value + 0.05 };
        ThreePhaseWlsResult rb = ThreePhaseWlsEstimator.Estimate(model, bad, currents);
        ThreePhaseMeasurementResidual worst = rb.Residuals.OrderByDescending(row => row.Standardised).First();
        Assert.Equal("vmag", worst.Kind);
        Assert.Equal(bad[4].BusId, worst.FromBusId);
        Assert.Equal(bad[4].Phase, worst.Phase);
        _ = truth;
    }

    [Theory]
    [InlineData(0.0, 50, 8, 1e-3, "tolerance")]
    [InlineData(double.NaN, 50, 8, 1e-3, "tolerance")]
    [InlineData(1e-10, 0, 8, 1e-3, "maxIterations")]
    [InlineData(1e-10, 50, 0, 1e-3, "maxRobustIterations")]
    [InlineData(1e-10, 50, 8, 0.0, "robustFloor")]
    [InlineData(1e-10, 50, 8, double.PositiveInfinity, "robustFloor")]
    public void D217_iteration_controls_that_cannot_mean_anything_are_refused_by_name(double tol, int maxIter, int passes, double floor, string param)
    {
        MultiPhaseModel model = Radial();
        (_, List<ThreePhaseCurrent> currents, List<ThreePhaseVMag> vmags) = Truth(model);
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ThreePhaseWlsEstimator.Estimate(model, vmags, currents, tol, maxIter, robust: true, maxRobustIterations: passes, robustFloor: floor));
        Assert.Equal(param, ex.ParamName);
    }

    // =================================================================================================================
    // D-DOC-114 and D-DOC-165: the branch-current estimator
    // =================================================================================================================

    [Fact]
    public void D114_a_branch_measured_on_some_phases_only_is_unobservable_not_converged()
    {
        MultiPhaseModel model = Radial();
        (_, List<ThreePhaseCurrent> currents, _) = Truth(model);
        // The reproduction: drop phase b of branch 1-2. Before: Converged = true with zero current on 1-2 b.
        List<ThreePhaseCurrent> partial = currents.Where(c => !(c.FromBusId == 1 && c.ToBusId == 2 && c.Phase == 1)).ToList();
        BranchCurrentEstimate est = BranchCurrentDsse.Estimate(model, partial);
        Assert.False(est.Converged);
        Assert.Equal(new[] { "1-2 b" }, est.UnobservableBranchPhases);

        // A branch with no phasor at all lists each of its phases.
        BranchCurrentEstimate none = BranchCurrentDsse.Estimate(model, currents.Where(c => c.FromBusId == 0).ToList());
        Assert.False(none.Converged);
        Assert.Equal(new[] { "1-2 a", "1-2 b", "1-2 c" }, none.UnobservableBranchPhases);

        // Fully measured: converged, nothing listed.
        BranchCurrentEstimate full = BranchCurrentDsse.Estimate(model, currents);
        Assert.True(full.Converged);
        Assert.Empty(full.UnobservableBranchPhases);
    }

    [Fact]
    public void D114_a_lateral_needs_phasors_only_on_the_phases_it_carries()
    {
        var za = new Complex[3, 3];
        za[0, 0] = new Complex(0.02, 0.06);
        var model = new MultiPhaseModel(
            new[] { new PhaseBus(0, true, MultiPhaseModel.BalancedNominal(1.0)), new PhaseBus(1, false), new PhaseBus(2, false) },
            new[] { new PhaseBranch(0, 1, Z), new PhaseBranch(1, 2, new PhaseImpedance(za), Phase.A) },
            Array.Empty<PhaseLoad>());
        var meas = new List<ThreePhaseCurrent>();
        for (int p = 0; p < 3; p++) meas.Add(new ThreePhaseCurrent(0, 1, p, new Complex(0.1, -0.05), 1e-3));
        meas.Add(new ThreePhaseCurrent(1, 2, 0, new Complex(0.1, -0.05), 1e-3));
        BranchCurrentEstimate est = BranchCurrentDsse.Estimate(model, meas);
        Assert.True(est.Converged);
        Assert.Empty(est.UnobservableBranchPhases);
        Assert.Equal(Complex.Zero, est.V[2][1]);   // the absent phases stay exactly zero
        Assert.Equal(Complex.Zero, est.V[2][2]);
    }

    [Fact]
    public void D165_a_current_named_in_reversed_orientation_gives_the_same_estimate_to_the_bit()
    {
        MultiPhaseModel model = Radial();
        (_, List<ThreePhaseCurrent> currents, _) = Truth(model);
        // The reproduction: branch 1-2's currents authored from bus 2 (the current into the To end is minus the From-end
        // current). Before: the branch read as unmeasured, not converged, and the CLI blamed a missing phasor.
        List<ThreePhaseCurrent> reversed = currents
            .Select(c => c.FromBusId == 1 && c.ToBusId == 2 ? new ThreePhaseCurrent(2, 1, c.Phase, -c.Value, c.Sigma) : c).ToList();
        BranchCurrentEstimate a = BranchCurrentDsse.Estimate(model, currents);
        BranchCurrentEstimate b = BranchCurrentDsse.Estimate(model, reversed);
        Assert.True(b.Converged);
        for (int i = 0; i < a.V.Length; i++) for (int p = 0; p < 3; p++) SameBits(a.V[i][p], b.V[i][p], $"bus {i} phase {p}");
        Assert.Equal(a.BranchCurrents.Keys.OrderBy(k => k), b.BranchCurrents.Keys.OrderBy(k => k));
    }

    [Fact]
    public void D165_a_reversed_current_across_a_regulator_and_a_current_on_no_branch_are_refused_by_name()
    {
        var model = new MultiPhaseModel(
            new[] { new PhaseBus(1, true, MultiPhaseModel.BalancedNominal(1.0)), new PhaseBus(2, false) },
            new[] { new PhaseBranch(1, 2, Z, RegulatorRatio: new[] { 1.05, 1.05, 1.05 }) }, Array.Empty<PhaseLoad>());
        var ex = Assert.Throws<ArgumentException>(() => BranchCurrentDsse.Estimate(model, new[] { new ThreePhaseCurrent(2, 1, 0, Complex.One, 1e-3) }));
        Assert.Contains("which has a regulator", ex.Message, StringComparison.Ordinal);
        Assert.Contains("give the measurement at the From end (from 1, to 2)", ex.Message, StringComparison.Ordinal);

        var ex2 = Assert.Throws<ArgumentException>(() => BranchCurrentDsse.Estimate(Radial(), new[] { new ThreePhaseCurrent(0, 2, 0, Complex.One, 1e-3) }));
        Assert.Contains("matches no branch of the model in either orientation", ex2.Message, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // D-DOC-116: every fault type refuses a zero loop impedance
    // =================================================================================================================

    private static MultiPhaseModel SwitchedBus() => new(
        new[] { new PhaseBus(1, true, MultiPhaseModel.BalancedNominal(1.0)), new PhaseBus(2, false), new PhaseBus(3, false) },
        new[] { new PhaseBranch(1, 2, new PhaseImpedance(new Complex[3, 3])), new PhaseBranch(2, 3, Coupled()) },   // 1-2 a closed switch
        Array.Empty<PhaseLoad>());

    [Theory]
    [InlineData(1)]   // the source itself
    [InlineData(2)]   // a bus joined to the source only by a closed switch
    public void D116_every_fault_type_refuses_a_bolted_fault_with_zero_loop_impedance(int bus)
    {
        MultiPhaseModel m = SwitchedBus();
        // Before: only the single-line-to-ground study refused; the others returned Infinity or NaN.
        Assert.Contains("zero loop impedance", Assert.Throws<ArgumentException>(() => SequenceFaultStudy.ThreePhaseFaultCurrentPu(m, bus)).Message, StringComparison.Ordinal);
        Assert.Contains("line-to-line", Assert.Throws<ArgumentException>(() => SequenceFaultStudy.LineToLineFaultCurrentPu(m, bus)).Message, StringComparison.Ordinal);
        Assert.Contains("double-line-to-ground", Assert.Throws<ArgumentException>(() => SequenceFaultStudy.LineToLineGroundFaultCurrentPu(m, bus)).Message, StringComparison.Ordinal);
        Assert.Contains("single-line-to-ground", Assert.Throws<ArgumentException>(() => SequenceFaultStudy.SingleLineToGround(m, bus)).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SequenceFaultStudy.AllFaults(m, bus));
    }

    [Fact]
    public void D116_a_non_zero_fault_impedance_at_the_same_bus_is_still_answered()
    {
        MultiPhaseModel m = SwitchedBus();
        var zf = new Complex(0.1, 0.0);
        Assert.Equal(1.0 / 0.1, SequenceFaultStudy.ThreePhaseFaultCurrentPu(m, 2, zf), 12);
        Assert.Equal(Math.Sqrt(3.0) / 0.1, SequenceFaultStudy.LineToLineFaultCurrentPu(m, 2, zf), 12);
        // A double-line-to-ground fault's Zf is the impedance to ground only; the two faulted phases are still bolted to
        // each other through Z1 + Z2 || (Z0 + 3 Zf) = 0 here, so it is refused (before: 1 / 0, a non-finite current).
        Assert.Throws<ArgumentException>(() => SequenceFaultStudy.LineToLineGroundFaultCurrentPu(m, 2, zf));
    }

    [Fact]
    public void D116_the_checks_leave_every_finite_fault_current_unchanged_to_the_bit()
    {
        // The pre-change expressions, verbatim, against the study at bus 3 (a real impedance path).
        MultiPhaseModel m = SwitchedBus();
        var zf = new Complex(0.01, 0.02);
        (Complex z0, Complex z1, Complex z2) = SequenceFaultStudy.Thevenin(m, 3);
        Complex e1 = Complex.One;
        double threePhase = (e1 / (z1 + zf)).Magnitude;
        double lineLine = Math.Sqrt(3.0) * (e1 / (z1 + z2 + zf)).Magnitude;
        Complex zg = z0 + 3.0 * zf;
        Complex zPar = z2 * zg / (z2 + zg);
        Complex i1 = e1 / (z1 + zPar);
        Complex i0 = -i1 * z2 / (z2 + zg);
        double llg = (3.0 * i0).Magnitude;
        Assert.Equal(BitConverter.DoubleToInt64Bits(threePhase), BitConverter.DoubleToInt64Bits(SequenceFaultStudy.ThreePhaseFaultCurrentPu(m, 3, zf)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(lineLine), BitConverter.DoubleToInt64Bits(SequenceFaultStudy.LineToLineFaultCurrentPu(m, 3, zf)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(llg), BitConverter.DoubleToInt64Bits(SequenceFaultStudy.LineToLineGroundFaultCurrentPu(m, 3, zf)));
    }

    // =================================================================================================================
    // D-DOC-117: transactional filter steps
    // =================================================================================================================

    /// <summary>A process model that is the random walk until it is told to fail; then its Predict moves the state by
    /// 0.1 (as a real non-persistence model would) and hands back a covariance the filter cannot use: F = 0 and Q = 0 (the
    /// EKF's prior inverse and the UKF's Cholesky factor then throw) or a non-symmetric Q (the EnKF's sampler throws).</summary>
    private sealed class FailingModel : IProcessModel
    {
        private readonly RandomWalkProcessModel _inner;
        private readonly bool _asymmetricQ;
        public bool Fail { get; set; }
        public FailingModel(int angleCount, bool asymmetricQ = false) { _inner = new RandomWalkProcessModel(angleCount); _asymmetricQ = asymmetricQ; }
        public string Name => "failing-test-model";
        public void Predict(double[] x, double dtSeconds, double[,] f, double[,] q)
        {
            _inner.Predict(x, dtSeconds, f, q);
            if (!Fail) return;
            for (int i = 0; i < x.Length; i++) x[i] += 0.1;
            if (_asymmetricQ) { q[0, 1] = 1e-4; q[1, 0] = 0.0; return; }
            for (int i = 0; i < x.Length; i++) for (int j = 0; j < x.Length; j++) { f[i, j] = 0.0; q[i, j] = 0.0; }
        }
    }

    private static (GridModel Model, MeasurementSet Window, int Angles) Case9Window()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet w = SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 9117 });
        return (model, w, model.Buses.Count(b => b.Type != BusType.Slack));
    }

    [Fact]
    public void D117_an_ekf_step_that_throws_leaves_the_filter_exactly_as_it_was()
    {
        (GridModel model, MeasurementSet w, int angles) = Case9Window();
        var failing = new FailingModel(angles);
        var a = new KalmanStateEstimator(model, failing);
        var b = new KalmanStateEstimator(model, new FailingModel(angles));
        a.Step(w, 1.0);
        b.Step(w, 1.0);
        failing.Fail = true;
        Assert.Throws<InvalidOperationException>(() => a.Step(w, 1.0));   // singular prior covariance
        failing.Fail = false;
        // Before: the failed Predict had already moved a's state by 0.1, so these differed.
        SameEstimate(b.Step(w, 2.0), a.Step(w, 2.0));
    }

    [Fact]
    public void D117_a_first_ekf_window_that_throws_leaves_the_filter_uninitialised()
    {
        (GridModel model, MeasurementSet w, int angles) = Case9Window();
        var failing = new FailingModel(angles) { Fail = true };
        var a = new KalmanStateEstimator(model, failing);
        Assert.Throws<InvalidOperationException>(() => a.Step(w, 1.0));
        Assert.False(a.IsInitialized);
        failing.Fail = false;
        SameEstimate(new KalmanStateEstimator(model, new FailingModel(angles)).Step(w, 1.0), a.Step(w, 1.0));
    }

    [Fact]
    public void D117_a_ukf_step_that_throws_leaves_the_filter_exactly_as_it_was()
    {
        (GridModel model, MeasurementSet w, int angles) = Case9Window();
        var failing = new FailingModel(angles);
        var a = new UnscentedKalmanStateEstimator(model, failing);
        var b = new UnscentedKalmanStateEstimator(model, new FailingModel(angles));
        a.Step(w, 1.0);
        b.Step(w, 1.0);
        failing.Fail = true;
        Assert.Throws<InvalidOperationException>(() => a.Step(w, 1.0));   // P^- = 0 has no Cholesky factor
        failing.Fail = false;
        SameEstimate(b.Step(w, 2.0), a.Step(w, 2.0));
    }

    [Fact]
    public void D117_an_enkf_step_that_throws_leaves_the_ensemble_exactly_as_it_was()
    {
        // The failure is raised for member 0 before any random draw of that step, so even the random stream is where it
        // was and the two filters must agree to the bit afterwards.
        (GridModel model, MeasurementSet w, int angles) = Case9Window();
        var failing = new FailingModel(angles, asymmetricQ: true);
        var a = new EnsembleKalmanStateEstimator(model, failing, ensembleSize: 30);
        var b = new EnsembleKalmanStateEstimator(model, new FailingModel(angles, asymmetricQ: true), ensembleSize: 30);
        a.Step(w, 1.0);
        b.Step(w, 1.0);
        failing.Fail = true;
        Assert.Contains("not symmetric", Assert.Throws<InvalidOperationException>(() => a.Step(w, 1.0)).Message, StringComparison.Ordinal);
        failing.Fail = false;
        SameEstimate(b.Step(w, 2.0), a.Step(w, 2.0));
    }

    // =================================================================================================================
    // D-DOC-113: the full process-noise covariance in the EnKF
    // =================================================================================================================

    /// <summary>The ensemble filter's Step as it was before D-DOC-113 / D-DOC-117, verbatim (diagonal-only sampling, in
    /// place). The reference the new filter must equal bit for bit on every diagonal Q.
    ///
    /// <para>Moved 2026-10 (LF4 N264, Ruling R1: Mark delegated EE calls 2026-10-04, and Mark's determinism rule): the one
    /// edit to the verbatim copy is the random stream. Before: one System.Random(seed) for the whole run. After: window k
    /// draws from System.Random(EnsembleKalmanStateEstimator.WindowSeed(seed, k)), the per-window stream the filter now
    /// uses so that a failed window cannot shift later ones (window 0's stream is the seed itself, so window 0 is
    /// unchanged). Every statement that forms a number is still the old one, so D113's two claims (diagonal Q: the new
    /// filter is the old statements to the bit; correlated Q: the old filter read only the diagonal) are tested exactly as
    /// before, on the new stream. WindowSeed itself is pinned against hand-computed SplitMix64 values in
    /// LF4LeftoversTests.N264.</para></summary>
    private sealed class LegacyEnkf
    {
        private readonly GridModel _model;
        private readonly int _n;
        private readonly double _slackRefRad;
        private readonly int _ensembleSize;
        private readonly double _initialStateStd;
        private readonly int _seed;
        private long _window;
        private Random _rng = null!;
        private IProcessModel? _process;
        private int _stateSize;
        private double[][] _members = Array.Empty<double[]>();
        private bool _initialized;

        public LegacyEnkf(GridModel model, IProcessModel? process, int ensembleSize, double initialStateStd = 0.5, int seed = 20260713)
        {
            _model = model;
            _n = model.BusCount;
            _process = process;
            _ensembleSize = ensembleSize;
            _initialStateStd = initialStateStd;
            _seed = seed;   // LF4 N264: the stream is now made per window, at the top of Step
            double refRad = 0;
            foreach (Bus bus in model.Buses)
                if (bus.Type == BusType.Slack) { refRad = bus.VaDeg * Math.PI / 180.0; break; }
            _slackRefRad = refRad;
        }

        public (double[] Vm, double[] VaDeg, double[] VmSigma, double[] VaSigmaDeg) Step(MeasurementSet window, double dtSeconds)
        {
            _rng = new Random(EnsembleKalmanStateEstimator.WindowSeed(_seed, _window++));   // LF4 N264 (see the type comment)
            window.Validate(_model);
            var mm = new MeasurementModel(_model, window);
            int s = mm.StateSize;
            if (!_initialized) Initialize(mm);
            _process ??= new RandomWalkProcessModel(s - _n);
            int m = mm.MeasurementCount;
            int ne = _ensembleSize;
            var f = new double[s, s];
            var q = new double[s, s];
            for (int e = 0; e < ne; e++)
            {
                _process.Predict(_members[e], dtSeconds, f, q);
                for (int i = 0; i < s; i++) _members[e][i] += Math.Sqrt(Math.Max(q[i, i], 0)) * Gaussian();
            }
            var hEns = new double[ne][];
            for (int e = 0; e < ne; e++)
            {
                Unpack(mm, _members[e], out double[] vm, out double[] va);
                var h = new double[m];
                mm.EvaluateH(vm, va, h);
                hEns[e] = h;
            }
            var xMean = Mean(_members, s);
            var hMean = Mean(hEns, m);
            var pyy = new double[m, m];
            var pxy = new double[s, m];
            double inv = 1.0 / (ne - 1);
            for (int e = 0; e < ne; e++)
            {
                var dh = new double[m];
                for (int r = 0; r < m; r++) dh[r] = hEns[e][r] - hMean[r];
                var dx = new double[s];
                for (int i = 0; i < s; i++) dx[i] = _members[e][i] - xMean[i];
                for (int r = 0; r < m; r++)
                {
                    for (int c = 0; c < m; c++) pyy[r, c] += inv * dh[r] * dh[c];
                    for (int i = 0; i < s; i++) pxy[i, r] += inv * dx[i] * dh[r];
                }
            }
            for (int r = 0; r < m; r++) pyy[r, r] += mm.SigmaPu[r] * mm.SigmaPu[r];
            double[,] pyyInv = DseLinAlg.Invert(pyy, m);
            var k = new double[s, m];
            for (int i = 0; i < s; i++)
                for (int r = 0; r < m; r++)
                {
                    double sum = 0;
                    for (int t = 0; t < m; t++) sum += pxy[i, t] * pyyInv[t, r];
                    k[i, r] = sum;
                }
            for (int e = 0; e < ne; e++)
            {
                var innov = new double[m];
                for (int r = 0; r < m; r++)
                {
                    double zPert = mm.ZPu[r] + mm.SigmaPu[r] * Gaussian();
                    innov[r] = zPert - hEns[e][r];
                }
                for (int i = 0; i < s; i++)
                {
                    double sum = 0;
                    for (int r = 0; r < m; r++) sum += k[i, r] * innov[r];
                    _members[e][i] += sum;
                }
            }
            // The estimate, as BuildEstimate forms it.
            double[] mean = Mean(_members, _stateSize);
            Unpack(mm, mean, out double[] vmOut, out double[] vaOut);
            var vaDeg = new double[_n];
            for (int i = 0; i < _n; i++) vaDeg[i] = vaOut[i] * 180.0 / Math.PI;
            var vmSigma = new double[_n];
            var vaSigmaRad = new double[_n];
            for (int c = 0; c < _stateSize; c++)
            {
                double var = 0;
                foreach (double[] mbr in _members) { double d = mbr[c] - mean[c]; var += d * d; }
                var /= _ensembleSize - 1;
                int bus = mm.StateBusIndex(c, out bool angle);
                double sigma = Math.Sqrt(var > 0 ? var : 0);
                if (angle) vaSigmaRad[bus] = sigma; else vmSigma[bus] = sigma;
            }
            var vaSigmaDeg = new double[_n];
            for (int i = 0; i < _n; i++) vaSigmaDeg[i] = vaSigmaRad[i] * 180.0 / Math.PI;
            return (vmOut, vaDeg, vmSigma, vaSigmaDeg);
        }

        private void Initialize(MeasurementModel mm)
        {
            int s = mm.StateSize;
            _stateSize = s;
            var baseState = new double[s];
            for (int c = 0; c < s; c++)
            {
                mm.StateBusIndex(c, out bool angle);
                baseState[c] = angle ? _slackRefRad : 1.0;
            }
            _members = new double[_ensembleSize][];
            for (int e = 0; e < _ensembleSize; e++)
            {
                _members[e] = new double[s];
                for (int c = 0; c < s; c++) _members[e][c] = baseState[c] + _initialStateStd * Gaussian();
            }
            _initialized = true;
        }

        private double Gaussian()
        {
            double u1 = 1.0 - _rng.NextDouble();
            double u2 = _rng.NextDouble();
            return Math.Sqrt(-2.0 * GridSim.Core.Numerics.DetMath.Log(u1)) * GridSim.Core.Numerics.DetMath.Cos(2.0 * Math.PI * u2);
        }

        private static double[] Mean(double[][] rows, int len)
        {
            var mean = new double[len];
            foreach (double[] row in rows) for (int i = 0; i < len; i++) mean[i] += row[i];
            for (int i = 0; i < len; i++) mean[i] /= rows.Length;
            return mean;
        }

        private void Unpack(MeasurementModel mm, double[] x, out double[] vm, out double[] va)
        {
            vm = new double[_n];
            va = new double[_n];
            for (int i = 0; i < _n; i++) va[i] = _slackRefRad;
            for (int c = 0; c < x.Length; c++)
            {
                int bus = mm.StateBusIndex(c, out bool angle);
                if (angle) va[bus] = x[c]; else vm[bus] = x[c];
            }
        }
    }

    /// <summary>The random walk plus a correlation between neighbouring angle states: q(i, i+1) = q(i+1, i) = 0.4 q(i, i)
    /// for consecutive angles (a tridiagonal block with off-diagonal 0.4 of the diagonal is positive definite: its smallest
    /// eigenvalue is above 0.2 of the diagonal). <paramref name="diagonalOnly"/> drops the correlation, giving exactly
    /// the diagonal the old filter sampled.</summary>
    private sealed class CorrelatedModel : IProcessModel
    {
        private readonly RandomWalkProcessModel _inner;
        private readonly int _angles;
        private readonly bool _diagonalOnly;
        public CorrelatedModel(int angles, bool diagonalOnly) { _inner = new RandomWalkProcessModel(angles); _angles = angles; _diagonalOnly = diagonalOnly; }
        public string Name => _diagonalOnly ? "correlated-diagonal-part" : "correlated";
        public void Predict(double[] x, double dtSeconds, double[,] f, double[,] q)
        {
            _inner.Predict(x, dtSeconds, f, q);
            if (_diagonalOnly) return;
            for (int i = 0; i + 1 < _angles; i++) { q[i, i + 1] = 0.4 * q[i, i]; q[i + 1, i] = 0.4 * q[i, i]; }
        }
    }

    private static List<MeasurementSet> Case9Windows(int count)
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        return Enumerable.Range(0, count)
            .Select(k => SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 9113 + k })).ToList();
    }

    [Fact]
    public void D113_with_the_default_diagonal_q_the_enkf_is_the_old_filter_to_the_bit()
    {
        GridModel model = IeeeCases.Case9();
        List<MeasurementSet> windows = Case9Windows(4);
        var now = new EnsembleKalmanStateEstimator(model, ensembleSize: 40);
        var old = new LegacyEnkf(model, null, 40);
        for (int k = 0; k < windows.Count; k++)
        {
            double dt = k == 2 ? 0.0 : 1.0 + k;
            DseEstimate e = now.Step(windows[k], dt);
            (double[] vm, double[] va, double[] vms, double[] vas) = old.Step(windows[k], dt);
            SameBits(vm, e.Vm, $"window {k} Vm");
            SameBits(va, e.VaDeg, $"window {k} VaDeg");
            SameBits(vms, e.StateVmSigma, $"window {k} Vm sigma");
            SameBits(vas, e.StateVaSigmaDeg, $"window {k} Va sigma");
        }
    }

    [Fact]
    public void D113_a_correlated_q_is_now_sampled_in_full_and_the_old_filter_was_its_diagonal_part()
    {
        GridModel model = IeeeCases.Case9();
        int angles = model.Buses.Count(b => b.Type != BusType.Slack);
        List<MeasurementSet> windows = Case9Windows(3);

        var oldFull = new LegacyEnkf(model, new CorrelatedModel(angles, diagonalOnly: false), 40);
        var newDiag = new EnsembleKalmanStateEstimator(model, new CorrelatedModel(angles, diagonalOnly: true), ensembleSize: 40);
        var newFull = new EnsembleKalmanStateEstimator(model, new CorrelatedModel(angles, diagonalOnly: false), ensembleSize: 40);
        for (int k = 0; k < windows.Count; k++)
        {
            (double[] vmOld, double[] vaOld, double[] vmsOld, double[] vasOld) = oldFull.Step(windows[k], 1.0);
            DseEstimate d = newDiag.Step(windows[k], 1.0);
            DseEstimate f = newFull.Step(windows[k], 1.0);
            // The defect, proved: the old filter given the correlated model gave exactly what the new one gives for the
            // correlated model's diagonal part (the off-diagonal entries were never read).
            SameBits(vmOld, d.Vm, $"window {k} Vm");
            SameBits(vaOld, d.VaDeg, $"window {k} VaDeg");
            SameBits(vmsOld, d.StateVmSigma, $"window {k} Vm sigma");
            SameBits(vasOld, d.StateVaSigmaDeg, $"window {k} Va sigma");
            // The fix: the full Q gives a different ensemble.
            Assert.False(f.VaDeg.Zip(vaOld).All(t => t.First == t.Second), $"window {k}: the correlated Q made no difference");
            // The exact before / after for the notes (window, bus 2 and bus 9 angle and Vm, and their sigmas).
            _out.WriteLine(FormattableString.Invariant(
                $"D113 window {k}: VaDeg[1] before {vaOld[1]:R} after {f.VaDeg[1]:R}; VaDeg[8] before {vaOld[8]:R} after {f.VaDeg[8]:R}; ") + FormattableString.Invariant(
                $"Vm[8] before {vmOld[8]:R} after {f.Vm[8]:R}; VaSigmaDeg[1] before {vasOld[1]:R} after {f.StateVaSigmaDeg[1]:R}"));
        }
    }

    [Fact]
    public void D113_the_sampler_takes_the_old_statement_for_a_diagonal_q_and_l_g_for_a_correlated_one()
    {
        // Diagonal: x_i += sqrt(max(q_ii, 0)) g_i, in state order.
        // Moved 2026-10 (LF4 N264, Ruling R1: fail loud): q_11 was -1.0 here, pinning that a negative variance reads as 0;
        // a negative diagonal entry is now refused by name (LF4LeftoversTests.N264). It is 0.0 now, which gives the same
        // expected value 2.0 + 0.0 * -1.2 = 2.0 bit for bit, so the statement under test is unchanged.
        var qd = new double[3, 3];
        qd[0, 0] = 4e-4; qd[1, 1] = 0.0; qd[2, 2] = 9e-6;
        var g = new Queue<double>(new[] { 0.3, -1.2, 2.5 });
        var x = new[] { 1.0, 2.0, 3.0 };
        new ProcessNoiseSampler("t").AddSample(qd, x, () => g.Dequeue());
        SameBits(new[] { 1.0 + Math.Sqrt(4e-4) * 0.3, 2.0 + 0.0 * -1.2, 3.0 + Math.Sqrt(9e-6) * 2.5 }, x, "diagonal");

        // Correlated 2 x 2: L = [[a, 0], [c / a, sqrt(d - c^2 / a^2)]], a = sqrt(q00).
        double q00 = 4.0, q01 = 1.2, q11 = 1.0;
        var qc = new double[,] { { q00, q01 }, { q01, q11 } };
        var g2 = new Queue<double>(new[] { 0.7, -0.4 });
        var y = new[] { 0.0, 0.0 };
        new ProcessNoiseSampler("t").AddSample(qc, y, () => g2.Dequeue());
        double l00 = Math.Sqrt(q00), l10 = q01 / l00, l11 = Math.Sqrt(q11 - l10 * l10);
        SameBits(new[] { 0.0 + (0.0 + l00 * 0.7), 0.0 + (0.0 + l10 * 0.7 + l11 * -0.4) }, y, "correlated");
    }

    [Fact]
    public void D113_the_sample_covariance_of_the_noise_is_q()
    {
        // 3 x 3 correlated Q, 40,000 draws from a fixed seed: the sample covariance must match Q to sampling accuracy
        // (standard error of a covariance about sqrt(2 / N) of the scale: 0.7 %; bound 3 %).
        var q = new double[,] { { 1.0, 0.6, -0.3 }, { 0.6, 2.0, 0.5 }, { -0.3, 0.5, 1.5 } };
        var rng = new Random(113);
        double Gauss() => Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble());
        var sampler = new ProcessNoiseSampler("t");
        const int n = 40_000;
        var sum = new double[3, 3];
        for (int k = 0; k < n; k++)
        {
            var w = new double[3];
            sampler.AddSample(q, w, Gauss);
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) sum[i, j] += w[i] * w[j];
        }
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                Assert.True(Math.Abs(sum[i, j] / n - q[i, j]) < 0.03 * Math.Sqrt(q[i, i] * q[j, j]), $"cov[{i},{j}] {sum[i, j] / n:R} vs {q[i, j]:R}");
    }

    [Fact]
    public void D113_the_semi_definite_cholesky_allows_a_zero_direction_and_refuses_an_indefinite_q()
    {
        // Rank one: perfectly correlated states. L = [[sqrt a, 0], [sqrt a, 0]].
        double[,] l = DseLinAlg.CholeskySemiDefinite(new double[,] { { 2.0, 2.0 }, { 2.0, 2.0 } }, 2);
        Assert.Equal(Math.Sqrt(2.0), l[0, 0]);
        Assert.Equal(Math.Sqrt(2.0), l[1, 0], 15);   // 2 / sqrt(2), within an ulp of sqrt(2)
        Assert.Equal(0.0, l[1, 1]);                   // no variance left: the column is exactly zero
        // L L^T reproduces a positive definite Q.
        var q = new double[,] { { 1.0, 0.6, -0.3 }, { 0.6, 2.0, 0.5 }, { -0.3, 0.5, 1.5 } };
        double[,] lq = DseLinAlg.CholeskySemiDefinite(q, 3);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += lq[i, k] * lq[j, k];
                Assert.True(Math.Abs(s - q[i, j]) < 1e-15, $"({i},{j}) {s:R} vs {q[i, j]:R}");
            }
        // Indefinite, a zero pivot still correlated, and NaN: refused.
        Assert.Throws<InvalidOperationException>(() => DseLinAlg.CholeskySemiDefinite(new double[,] { { 1.0, 2.0 }, { 2.0, 1.0 } }, 2));
        Assert.Throws<InvalidOperationException>(() => DseLinAlg.CholeskySemiDefinite(new double[,] { { 0.0, 1.0 }, { 1.0, 1.0 } }, 2));
        Assert.Throws<InvalidOperationException>(() => DseLinAlg.CholeskySemiDefinite(new double[,] { { double.NaN, 0.5 }, { 0.5, 1.0 } }, 2));
        // Through the sampler, a non-PSD Q names the process model.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ProcessNoiseSampler("my-model").AddSample(new double[,] { { 1.0, 2.0 }, { 2.0, 1.0 } }, new double[2], () => 0.0));
        Assert.Contains("process model 'my-model'", ex.Message, StringComparison.Ordinal);
    }

    // =================================================================================================================
    // D-DOC-229: constructor checks
    // =================================================================================================================

    private static GridModel Chain(int buses) => new()
    {
        Name = "doc9-cap-chain",
        BaseMva = 100,
        Buses = Enumerable.Range(1, buses).Select(i => new Bus(i, i == 1 ? BusType.Slack : BusType.Pq, BaseKv: 132)).ToList(),
        Generators = Array.Empty<Generator>(),
        Branches = Enumerable.Range(1, buses - 1).Select(i => new Branch(i, i + 1, 0.001, 0.01)).ToList(),
    };

    [Fact]
    public void D229_the_ukf_and_enkf_constructors_refuse_a_state_above_the_dense_limit()
    {
        int buses = DenseEstimationLimits.RecursiveFilterMaxState / 2 + 2;
        GridModel big = Chain(buses);
        Assert.Contains("2500 states", Assert.Throws<NotSupportedException>(() => new UnscentedKalmanStateEstimator(big)).Message, StringComparison.Ordinal);
        Assert.Contains("2500 states", Assert.Throws<NotSupportedException>(() => new EnsembleKalmanStateEstimator(big)).Message, StringComparison.Ordinal);
        int okBuses = (DenseEstimationLimits.RecursiveFilterMaxState + 1) / 2;
        _ = new UnscentedKalmanStateEstimator(Chain(okBuses));
        _ = new EnsembleKalmanStateEstimator(Chain(okBuses));
    }

    [Theory]
    [InlineData(1, 0.5, "ensembleSize")]
    [InlineData(0, 0.5, "ensembleSize")]
    [InlineData(40, -0.1, "initialStateStd")]
    [InlineData(40, double.NaN, "initialStateStd")]
    public void D229_the_enkf_refuses_an_ensemble_it_cannot_use(int size, double std, string param)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EnsembleKalmanStateEstimator(IeeeCases.Case9(), ensembleSize: size, initialStateStd: std));
        Assert.Equal(param, ex.ParamName);
    }
}
