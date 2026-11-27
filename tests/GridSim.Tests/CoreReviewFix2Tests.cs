using System.Globalization;
using System.Numerics;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Opf;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Adaptive;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Protection.Devices;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Regression tests for the 2.1.099 core fixes (second round of the core review). Each expected value comes from outside
/// the code under test: a finite difference of re-solved optima, a hand derivation stated in the test, a published model
/// description quoted in the test, or an identity of the estimator's linear theory.
/// </summary>
public sealed class CoreReviewFix2Tests
{
    private static void Rel(double want, double got, double rel, string what)
        => Assert.True(Math.Abs(want - got) <= rel * Math.Abs(want), $"{what}: want {want:R}, got {got:R}");

    // ---- OPF tap shadow prices are per unit of the ratio ------------------------------------------------------------

    // A transformer (Z = 0.01 + j0.1 pu), V1 held at 1.0, V2 <= 1.00, load 100 + j50 MW/MVAr, tap range [minTap, 1.1].
    // The loss optimum wants tau = 0.9396 (Rc5Wp7 T3), so with minTap = 0.95 the tap sits on its lower limit.
    private static GridModel TapCase(double minTap) => new()
    {
        Name = "tap-limit", BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, VminPu: 1.0, VmaxPu: 1.0), new Bus(2, BusType.Pq, Pd: 100, Qd: 50, VminPu: 0.9, VmaxPu: 1.0) },
        Generators = new[] { new Generator(1, 0, Pmin: 0, Pmax: 1000, Name: "slack") },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1, Name: "T1", IsTransformer: true) },
        Oltcs = new[] { new Oltc("T1", 2, 1.0, minTap, 1.1, 0.0125) },
    };

    private static AcOpfExtendedResult TapSolve(double minTap) => AcOpfExtended.Solve(TapCase(minTap), new AcOpfExtendedOptions
    {
        Objective = AcOpfObjective.Losses,
        Controls = new OpfControlSet(new[] { new OpfTapControl(0, minTap, 1.1, 0.0125) }, Array.Empty<OpfShuntControl>()),
    });

    [Fact]
    public void A_tap_shadow_price_is_the_derivative_of_the_optimum_with_respect_to_its_limit()
    {
        // Independent value: the central difference of the optimum objective in the limit, from two re-solves,
        // (f(MinTap + h) - f(MinTap - h)) / 2h with h = 1e-5. The interior point method's multiplier is on u = 1/tau;
        // d(1/tau)/dtau = -1/tau^2, so the price per unit of tau is mu_u u^2 (at tau = 0.95, u^2 = 1.108: the old figure,
        // mu_u alone, was 9.8 % low).
        AcOpfExtendedResult r = TapSolve(0.95);
        Assert.True(r.Converged, r.Base.Message);
        OpfControlValue c = Assert.Single(r.Controls);
        Assert.Equal(0.95, c.Value, 1e-8);
        const double h = 1e-5;
        AcOpfExtendedResult up = TapSolve(0.95 + h), down = TapSolve(0.95 - h);
        Assert.True(up.Converged && down.Converged);
        double fd = (up.Base.ObjectivePerHour - down.Base.ObjectivePerHour) / (2 * h);
        Assert.True(fd > 3.0, $"finite difference {fd:R}");
        Rel(fd, c.MuMin, 1e-4, "MuMin");
        Assert.Equal(0.0, c.MuMax);
    }

    // ---- AC-SCOPF: secured outages are re-screened ----------------------------------------------------------------

    // Two buses, base 100 MVA. Bus 1: slack, one unit at 10 per MWh, Vm 0.9..1.1, Q unbounded. Bus 2: 60 MW + 50 MVAr,
    // Vm 0.95..1.05. Branch 0: R 0.015, X 0.15, B 0. Branch 1 (a cable): R 0.01, X 0.10, B 1.5. Both unrated.
    // Contingency: branch 1.
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

    [Fact]
    public void A_secured_outage_that_still_fails_its_own_screen_is_not_reported_optimal()
    {
        // The cost is 10 x Pg, so the optimum is the loss minimum: SciPy (SLSQP on the same equations) puts it inside the
        // limits at V1 = 1.008153, V2 = 1.020472 (the cable's charging stops V rising). The interior point method stops at
        // its 1e-6 cost tolerance on a flat optimum, so V1 is checked to 2e-3. Round 1 adds outage 1 (V2 falls below 0.95).
        // Round 2's block of outage 1 raises V1 (the block lets the unit voltage move after the outage), so nothing more is
        // added; the base case is the same (the only unit is at the slack, so the preventive coupling is empty).
        // The screen holds V1 at the base case's value. With the cable out, the line alone carries S = 0.6 + j0.5 pu:
        // V2^4 + (2 (R P + X Q) - V1^2) V2^2 + (R^2 + X^2)(P^2 + Q^2) = 0, which gives V2 = 0.911979 at V1 = 1.008153, below
        // 0.95. That is not a secured optimum.
        GridModel m = CablePair();
        AcScopfResult r = AcScopf.Solve(m, new AcScopfOptions { Contingencies = new[] { 1 } });
        Assert.Equal(AcScopfStatus.SecuredOutageFailsScreen, r.Status);
        Assert.False(r.Optimal);
        Assert.Equal(new[] { 1 }, r.Secured);
        Assert.Equal(new[] { 1 }, r.UnresolvedSecuredOutages);
        Assert.Equal(new[] { 1 }, r.Rounds[0].Added);
        Assert.Empty(r.Rounds[1].Added);
        Assert.Contains("still fail the AC screen", r.Message, StringComparison.Ordinal);
        double v1 = r.Last!.Base.Buses[0].VmPu;
        Assert.Equal(1.008153, v1, 2e-3);
        Assert.True(r.Last.Outages[0].Buses[0].VmPu > v1 + 0.01, "the block moved V1 after the outage");

        static double V2(double v1) =>
            Math.Sqrt((v1 * v1 - 2 * (0.015 * 0.6 + 0.15 * 0.5) + Math.Sqrt(Math.Pow(v1 * v1 - 2 * (0.015 * 0.6 + 0.15 * 0.5), 2) - 4 * (0.015 * 0.015 + 0.15 * 0.15) * (0.6 * 0.6 + 0.5 * 0.5))) / 2);
        Assert.Equal(0.911979, V2(1.008153), 1e-6);
        AcScopfScreen s = AcScopf.Screen(m, r.Last.Base, 1, PostFaultRating.RateA);
        Assert.False(s.Passes);
        Assert.StartsWith("bus 2 at ", s.Reason, StringComparison.Ordinal);
        string reason = s.Reason!;
        double screenV2 = double.Parse(reason["bus 2 at ".Length..reason.IndexOf(" pu", StringComparison.Ordinal)], CultureInfo.InvariantCulture);
        Assert.Equal(V2(v1), screenV2, 1e-8);
        Assert.True(screenV2 < 0.95);
    }

    [Fact]
    public void The_acscopf_verb_exits_with_the_verdict_code_when_a_secured_outage_fails_its_screen()
    {
        // case9 with every RateA x 0.75 (Rc5Wp7 T11): outages 7 and 8 are secured, and at the final dispatch outage 7
        // still overloads branch 2 (5-6) and outage 8 puts bus 4 above 1.1 pu (the screen holds the unit voltages at
        // their base-case values; the blocks did not).
        GridModel src = S10TestSupport.LoadMatpower("lib/case9.m").Model;
        GridModel m = src.CopyWith(branches: src.Branches.Select(b => b with { RateAMva = b.RateAMva * 0.75 }).ToArray());
        string dir = TestTemp.Under("gridsim-crf2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "case9_075.m");
            File.WriteAllText(path, MatpowerWriter.Write(m).Text);
            CliProcess.Result run = CliProcess.RunBuilt("acscopf", path);
            Assert.True(run.ExitCode == 3, run.ToString());
            Assert.Contains("SecuredOutageFailsScreen", run.StdErr, StringComparison.Ordinal);
            Assert.Contains("[7,8]", run.StdErr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- MIPS barrier floor ----------------------------------------------------------------------------------------

    [Fact]
    public void A_complementarity_tolerance_below_the_barrier_floor_converges_at_the_floor()
    {
        // case9, loss objective, complementarity tolerance 1e-15. Convergence needs z'mu < 1e-15 (1 + |x|inf), about
        // 2.6e-15; the barrier gamma = 0.1 z'mu / 48 falls below machine epsilon once z'mu < 1.07e-13. Before, that was a
        // breakdown (NumericalFailure at iteration 18, barrier 3.9e-17, with feasibility 9.4e-16 and gradient 2.0e-16).
        // Now it ends there as converged. Losses: SciPy 2.3157971721609 MW (Rc5Wp7 T6).
        GridModel m = S10TestSupport.LoadMatpower("lib/case9.m").Model;
        AcOpfExtendedResult r = AcOpfExtended.Solve(m, new AcOpfExtendedOptions
        {
            Objective = AcOpfObjective.Losses,
            Ipm = AcOpfExtended.DefaultIpm(AcOpfObjective.Losses) with { ComplementarityTolerance = 1e-15 },
        });
        Assert.True(r.Converged, r.Base.Message);
        Assert.Equal(AcOpfIpmStatus.Converged, r.Base.Status);
        Assert.Contains("at the barrier floor", r.Base.Message, StringComparison.Ordinal);
        Assert.Equal(2.3157971721609, r.LossesMw, 1e-5);
        // The default settings still converge on the ordinary test, with the ordinary message.
        AcOpfExtendedResult d = AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses });
        Assert.True(d.Converged);
        Assert.DoesNotContain("barrier floor", d.Base.Message, StringComparison.Ordinal);
    }

    // ---- OPF refuses a tap whose impedance follows the tap -------------------------------------------------------------

    [Fact]
    public void An_opf_tap_control_on_a_transformer_with_an_impedance_correction_table_is_refused()
    {
        GridModel src = TapCase(0.9);
        var table = new TapImpedanceCorrection(0.01, 0.1, 1.0, new[] { new ImpedanceCorrectionPoint(0.9, 0.9, 0.0), new ImpedanceCorrectionPoint(1.1, 1.1, 0.0) });
        GridModel m = src.CopyWith(oltcs: new[] { src.Oltcs[0] with { ImpedanceCorrection = table } });
        var opt = new AcOpfExtendedOptions
        {
            Objective = AcOpfObjective.Losses,
            Controls = new OpfControlSet(new[] { new OpfTapControl(0, 0.9, 1.1, 0.0125) }, Array.Empty<OpfShuntControl>()),
        };
        var ex = Assert.Throws<InvalidDataException>(() => AcOpfExtended.Solve(m, opt));
        Assert.Contains("impedance correction table", ex.Message, StringComparison.Ordinal);
        // Without the table the same control solves.
        Assert.True(AcOpfExtended.Solve(src, opt).Converged);
    }

    // ---- State estimation: opt-in largest normalised residual detection --------------------------------------------

    [Fact]
    public void Lnr_detection_finds_a_gross_error_the_chi_square_test_passes_and_is_off_by_default()
    {
        // case14, the exact full measurement set (WlsEstimatorTests.ExactFullSet), one active flow wrong by b = 8 sigma.
        // With m - n large the chi-square test misses it. Linear WLS theory: with r = S e and S = I - H G^-1 H' W, a lone
        // error b on row i gives r = b S e_i, so J = b^2 S_ii / sigma_i^2 and rN_i = |b| sqrt(S_ii) / sigma_i: J = rN_max^2.
        // At the nonlinear estimate the identity holds to the curvature of h over the state shift the error causes (well
        // below 1e-3 here), so it is checked to 1e-3 relative.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(model, truth);
        var ms = set.Measurements.ToList();
        int victim = ms.FindIndex(x => x.Kind == MeasurementKind.PFlowMw);
        Measurement bad = ms[victim] with { Value = ms[victim].Value + 8 * ms[victim].Sigma };
        ms[victim] = bad;
        set = set with { Measurements = ms };

        EstimationResult plain = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(EstimationStatus.Converged, plain.Status);
        Assert.True(plain.ChiSquarePassed, $"J {plain.ObjectiveJ:R}, threshold {plain.ChiSquareThreshold:R}");
        Assert.Empty(plain.Removed);
        Assert.Equal(bad, plain.MaxNormalizedResidualMeasurement);
        Assert.True(plain.MaxNormalizedResidual > 3.0, $"rN max {plain.MaxNormalizedResidual:R}");
        Rel(Math.Sqrt(plain.ObjectiveJ), plain.MaxNormalizedResidual, 1e-3, "rN max against sqrt(J)");

        EstimationResult lnr = new WlsStateEstimator().Estimate(model, set, new EstimationOptions { LnrDetection = true });
        Assert.Equal(EstimationStatus.Converged, lnr.Status);
        BadDataRemoval removal = Assert.Single(lnr.Removed);
        Assert.Equal(bad, removal.Measurement);
        for (int i = 0; i < model.BusCount; i++)
        {
            Assert.True(Math.Abs(lnr.Vm[i] - truth.Vm[i]) < 1e-8, $"Vm[{i}] off by {lnr.Vm[i] - truth.Vm[i]}");
            Assert.True(Math.Abs(lnr.VaDeg[i] - truth.VaDeg[i]) < 1e-7);
        }
        // A clean set: nothing is removed with LNR detection on either (the exact set has every rN at rounding level).
        EstimationResult clean = new WlsStateEstimator().Estimate(model, WlsEstimatorTests.ExactFullSet(model, truth), new EstimationOptions { LnrDetection = true });
        Assert.Empty(clean.Removed);
        // Off by default, and the default run's state is the same bits as a run that names the default.
        EstimationResult named = new WlsStateEstimator().Estimate(model, set, new EstimationOptions { LnrDetection = false });
        Assert.Equal(plain.Vm.Select(BitConverter.DoubleToInt64Bits), named.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(plain.VaDeg.Select(BitConverter.DoubleToInt64Bits), named.VaDeg.Select(BitConverter.DoubleToInt64Bits));
    }

    // ---- QSTS: two self-consumption units on one bus --------------------------------------------------------------

    [Theory]
    [InlineData(1.0, 1.0, 0.5, 0.5)]
    [InlineData(1.0, 3.0, 0.25, 0.75)]
    public void Two_self_consumption_units_on_one_bus_share_its_net_demand_by_rating(double r1, double r2, double p1, double p2)
    {
        // Bus 4 of the QSTS feeder draws 1 MW and has no PV. Two self-consumption units there, with ample energy, should
        // cover it once between them, in proportion to rating: 1 x r1 / (r1 + r2) and 1 x r2 / (r1 + r2) (both exact in
        // binary here). Before, each asked for the whole 1 MW, so the bus exported 1 MW.
        GridModel m = QstsEngineTests.Feeder(oltc: false);
        var a = new QstsStorageUnit("B4a", 4, r1, 10.0, 0.5, 0.0, 1.0, 1.0, 1.0, StorageMode.SelfConsumption);
        var b = new QstsStorageUnit("B4b", 4, r2, 10.0, 0.5, 0.0, 1.0, 1.0, 1.0, StorageMode.SelfConsumption);
        QstsResult q = new QstsEngine(new BackwardForwardSweep(), QstsEngineTests.Opt).Run(m, new QstsStudy { StepSeconds = 900, Steps = 1, Storage = new[] { a, b } });
        Assert.Equal(p1, q.Steps[0].StorageP[0]);
        Assert.Equal(p2, q.Steps[0].StorageP[1]);
        Assert.Equal(1.0, q.Steps[0].StorageP[0] + q.Steps[0].StorageP[1]);
    }

    // ---- VSC1: the DC side carries the exchanged power ------------------------------------------------------------------

    [Fact]
    public void A_current_limited_vsc_power_converter_drives_the_dc_side_with_the_power_it_exchanges()
    {
        // Link: power converter at bus 1, DC voltage converter at bus 2 holding 300 kV, R = 2 ohm, losses 0.1 MW + 0.5 MW/kA
        // (at least 0.05 MW) each; base 100 MVA, Imax 1.1 pu. Order 100 MW with the power converter's bus at |V| = 0.1:
        // it exchanges |V| Imax = 0.11 pu = 11 MW. The DC side balances that: (300 + 2 I) I + 0.1 + 0.5 I = 11, so
        // 2 I^2 + 300.5 I - 10.9 = 0, I = 0.036264125878034 kA, and the DC voltage converter delivers
        // 300 I - (0.1 + 0.5 I) = 10.761105700471 MW. Before, it delivered the 100 MW order less losses, 99.248 MW.
        var conv1 = new VscConverter(1, VscDcControl.Power, VscAcControl.PowerFactor, 80, 1.0, 100, 0.5, 50, 0, 0, 1, 500, -500);
        var conv2 = new VscConverter(2, VscDcControl.DcVoltage, VscAcControl.PowerFactor, 300, 1.0, 100, 0.5, 50, 0, 0, 1, 500, -500);
        var link = new VscDcLink("VV", 2, conv1, conv2);
        var dc = VscDcLinks.SolveDc(link);
        var pf = new VscLinkResult("VV", dc.CurrentKa * 1000, new VscConverterResult(1, dc.FirstMw, 0, dc.FirstDcKv, dc.FirstLossMw, 80, false),
            new VscConverterResult(2, dc.SecondMw, 0, dc.SecondDcKv, dc.SecondLossMw, double.NaN, false));
        var plan = new VscLinkPlan(link, pf, new Vsc1Data(0.05, 0.02, 1.1, 100) { OrderSteps = new[] { new RmsOrderStep(0.0, 100) } },
            100, Complex.One, Complex.One);
        var s = new VscLinkState(plan, null);
        const double dt = 1e-3;
        for (int n = 0; n < 1000; n++) s.Advance(n * dt, new Complex(0.1, 0), Complex.One, dt);   // 1 s = 20 Tp
        double c = -10.9, i = -2 * c / (300.5 + Math.Sqrt(300.5 * 300.5 - 8 * c));
        Assert.Equal(0.036264125878034, i, 1e-14);
        Assert.Equal(i, s.DcCurrentKa, 1e-12);
        Assert.Equal(300 * i - (0.1 + 0.5 * i), s.DcTerminalPower * 100, 1e-9);
        Assert.Equal(10.761105700471, s.DcTerminalPower * 100, 1e-9);
        // The converter itself injects the clipped current: |Id| = Imax.
        Assert.Equal(-1.1, s.TrialCurrent(0, new Complex(0.1, 0)).Real, 14);
    }

    // ---- Adaptive step: a NaN anywhere rejects the step -----------------------------------------------------------------

    private sealed class NanFirst : IAdaptiveOde
    {
        public int Dimension => 2;
        public void Derivative(double t, double[] y, double[] dydt) { dydt[0] = double.NaN; dydt[1] = 0.0; }
    }

    [Fact]
    public void A_nan_in_any_state_makes_the_error_norm_infinite()
    {
        // IEEE comparisons with NaN are false, so a running maximum that is followed by a finite value forgets the NaN
        // (max(NaN, 0) came out 0). The norm must be infinite whatever the position of the NaN.
        var ctl = new AdaptiveStepController(new AdaptiveOptions());
        double[] y = { 1.0, 1.0 }, yNew = new double[2];
        Assert.Equal(double.PositiveInfinity, ctl.Step(new NanFirst(), 0.0, y, 1e-3, yNew));
    }

    private const double E = 1.2, Xs = 0.5, H = 5.0, F0 = 50.0;
    private static readonly double Pm = 0.5 * E / Xs, Delta0 = Math.Asin(0.5);

    /// <summary>One machine behind jX to an infinite bus of 1 pu.</summary>
    private sealed class Smib : IRmsNetwork
    {
        public int Machines => 1;
        public int Converters => 0;
        public (Complex[] MachineCurrents, Complex[] ConverterVoltages) Solve(Complex[] emf, Complex[] converterCurrents) =>
            ([(emf[0] - 1.0) * new Complex(0.0, -1.0 / Xs)], []);
    }

    /// <summary>A network whose solution is not a number (as a singular or corrupted network gives).</summary>
    private sealed class NanNetwork : IRmsNetwork
    {
        public int Machines => 1;
        public int Converters => 0;
        public (Complex[] MachineCurrents, Complex[] ConverterVoltages) Solve(Complex[] emf, Complex[] converterCurrents) =>
            ([new Complex(double.NaN, double.NaN)], []);
    }

    private sealed class SteadyCurrent : IRelayCurrentSource
    {
        public void PrimaryCurrentsA(IRmsNetwork network, Complex[] emf, double[] amps) => amps[0] = 100.0;
    }

    [Fact]
    public void An_adaptive_run_whose_state_goes_non_finite_stops_with_the_step_error()
    {
        // From t = 0.1 s the network gives NaN currents, so the angle and speed derivatives are NaN. A relay (pick-up
        // 400 A against a steady 100 A) adds a state after them whose derivative stays 0. Before, the error norm forgot the
        // NaN in front of that 0, accepted the step and the run ended as loss of synchronism; now every trial step is
        // rejected down to the minimum step, and the run stops with the non-finite state named.
        var relays = new RelayInLoop([new PlacedDevice("R1", DeviceType.Relay, "51", new DeviceLocation(0, 1, 2, "from"), 1.0, 1.0, 0.0, 0.0,
            [new ShotSetting("iec-si", 400.0, 0.1, 0.0)], null, [], null, null)], new CurveLibrary(), 1e-6);
        var pre = new Smib();
        var p = new AdaptiveSwingProblem
        {
            H = [H], D = [0.0], Pm = [Pm], Eqpp = [E], Edpp = [0.0], Delta0 = [Delta0], F0 = F0, DurationSeconds = 0.5,
            Topology = new StageListTopology(pre, [(0.1, new NanNetwork(), "corrupt network")], 1, _ => pre),
            Options = new AdaptiveOptions(MinStepSeconds: 1e-6, MaxStepSeconds: 0.01),
            Relays = relays, CurrentSource = new SteadyCurrent(),
        };
        var ex = Assert.Throws<AdaptiveStepException>(() => AdaptiveSwingKernel.Run(p));
        Assert.Contains("non-finite", ex.Message, StringComparison.Ordinal);
    }

    // ---- REEC_A/B/C: linked s2/s3 non-windup and the Tpord = 0 bypass ----------------------------------------------

    private static ReecParameters ReecA() => new()
    {
        Variant = ReecVariant.A, PfFlag = 0, VFlag = 1, QFlag = 1, PFlag = 0, PqFlag = 0,
        Vdip = 0.9, Vup = 1.1, Trv = 0.02, Dbd1 = -0.05, Dbd2 = 0.05, Kqv = 2.0, Iqh1 = 1.05, Iql1 = -1.05, Vref0 = 0.0,
        Tp = 0.05, Qmax = 0.5, Qmin = -0.5, Vmax = 1.1, Vmin = 0.9, Kqp = 0.0, Kqi = 0.1, Kvp = 0.0, Kvi = 40.0, Tiq = 0.02,
        DPmax = 99.0, DPmin = -99.0, Pmax = 1.0, Pmin = 0.0, Imax = 1.2, Tpord = 0.02,
        Vq = new[] { 0.0, 0.2, 0.5, 1.0 }, Iq = new[] { 1.2, 1.2, 1.2, 1.2 }, Vp = new[] { 0.0, 0.2, 0.5, 1.0 }, Ip = new[] { 1.2, 1.2, 1.2, 1.2 },
    };

    [Theory]
    [InlineData(ReecVariant.A)]
    [InlineData(ReecVariant.B)]
    public void Reec_s2_stops_when_s3_is_at_its_limit_and_both_push_the_same_way(ReecVariant variant)
    {
        // WECC, Report on WT3 Model Description (2013), footnote 1 to Figure 3-3: "if s3 hits its maximum limit and ds3 is
        // positive, then ds3 is set to 0; if ds2 is also positive, then it is also set to 0".
        // V held at 0.92 (outside the dip), Qgen held at 0, Qext stepped from 0 to 0.2, Kqp = Kvp = 0, Kqi = 1, Kvi = 40,
        // Imax = 0.3 (Q priority, so Iqmax = 0.3). Q error 0.2 throughout, so s2 = 0.92 + 0.2 k dt (forward Euler of a
        // constant) and s3 = 40 sum (s2 - 0.92) dt, about 4 t^2, reaching 0.3 near t = 0.27 s, long before s2 could reach
        // Vmax = 1.1 at t = 0.9 s. From the step s3 reaches 0.3 on, Vsel = s2 is frozen at 0.92 + 0.2 k* dt.
        const double dt = 1e-3, v = 0.92;
        ReecParameters rp = ReecA() with { Variant = variant, Kqi = 1.0, Imax = 0.3, Trv = 0.0 };
        var reec = new Reec(rp, v, 0.2, 0.0, 0.2 / v, 0.0);
        var u = new ReecInputs(v, 0.2, 0.0, reec.PrefInitial, reec.QextInitial + 0.2, 1.0);
        int kStar = -1;
        double frozenVerr = double.NaN;
        for (int k = 0; k < 600; k++)
        {
            reec.UpdateDipState(k * dt, v);
            ReecOutputs o = reec.Evaluate(u, k * dt, dt);
            Assert.False(o.Dip);
            if (kStar < 0 && o.Iqcmd == 0.3)
            {
                kStar = k;   // k advances have been made, the last of which put s3 on its limit
                frozenVerr = o.VErr;
                Assert.Equal(0.92 + 0.2 * k * dt - v, o.VErr, 1e-12);
            }
            if (kStar >= 0) Assert.Equal(frozenVerr, o.VErr);   // s2 held, bit for bit
            reec.Advance(u, o, dt);
        }
        Assert.InRange(kStar, 250, 300);
    }

    [Fact]
    public void Reec_with_tpord_zero_follows_the_power_reference_in_a_dip()
    {
        // Same footnote: "for s5, if Tpord is zero then the time constant and freezing of the state are by-passed, however,
        // the Pmax/Pmin limits are still in effect." Start at P0 = 0.8, V = 1, Tpord = 0, dPmax/dPmin = +-99 per s with
        // dt = 0.01 (so the rate limit allows 0.99 per step); the voltage dips to 0.5 and Pref steps to 0.6. Step 1: Psel
        // is the rate limiter's state, still 0.8, so Pord = 0.8, and the limiter moves to 0.6. Step 2: Pord = 0.6 exactly.
        // Before, Pord stayed frozen at 0.8 for the whole dip.
        const double dt = 0.01;
        ReecParameters rp = ReecA() with { Tpord = 0.0 };
        var reec = new Reec(rp, 1.0, 0.8, 0.0, 0.8, 0.0);
        var u = new ReecInputs(0.5, 0.8, 0.0, 0.6, reec.QextInitial, 1.0);
        for (int k = 0; k < 2; k++)
        {
            reec.UpdateDipState(k * dt, 0.5);
            Assert.True(reec.InDip);
            ReecOutputs o = reec.Evaluate(u, k * dt, dt);
            reec.Advance(u, o, dt);
            Assert.Equal(k == 0 ? 0.8 : 0.6, reec.Pord);
        }
        // Pmax still applies: Pref 1.5 gives Pord = Pmax = 1.0.
        var high = new Reec(rp, 1.0, 0.8, 0.0, 0.8, 0.0);
        var uh = new ReecInputs(0.5, 0.8, 0.0, 1.5, high.QextInitial, 1.0);
        for (int k = 0; k < 3; k++)
        {
            high.UpdateDipState(k * dt, 0.5);
            ReecOutputs o = high.Evaluate(uh, k * dt, dt);
            high.Advance(uh, o, dt);
        }
        Assert.Equal(1.0, high.Pord);
    }
}
