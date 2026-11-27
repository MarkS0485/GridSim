using System.Numerics;
using System.Text.Json;
using GridSim.Core.Abstractions;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.MotorStart;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using static GridSim.Tests.MotorStartTestSupport;

namespace GridSim.Tests;

/// <summary>
/// RC3 P8: the motor acceleration study from a native v2 operating point. Fixtures under fixtures/motor-start:
/// prestart (a 33/11 kV network, non-merit-order schedule, motor M1 at bus 4), control (the same network file with the
/// loader default), stiff (M1 on the slack bus, 1.0 pu held), weak (a 0.3 + j1.2 pu feeder; M1 fan load, M2 constant
/// torque above its dipped locked-rotor torque). The references are independent (MotorStartTestSupport).
/// </summary>
public sealed class MotorStartStudyTests
{
    private static readonly Complex WeakFeeder = new(0.3, 1.2);

    // ------------------------------------------------------------------------------------------------------------
    // The authored point through every native loader, and the pre-start AC point
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_authored_schedule_survives_every_native_loader()
    {
        // Authored: Grid 8 MW (slack), G2 (ocgt) 20 MW, G3 (ccgt) 6 MW of 40: not merit order.
        double[] authored = { 8, 20, 6 };
        GridModel v2Default = V2CaseLoader.Load(Fixture("prestart.case.json"), Schemas, DataRoot);
        GridModel v2Suppressed = V2CaseLoader.Load(Fixture("prestart.case.json"), Schemas, DataRoot, dispatch: false);
        GridModel v1Default = JsonGridLoader.LoadDirectory(Fixture("prestart-v1"));
        GridModel v1Suppressed = JsonGridLoader.LoadDirectory(Fixture("prestart-v1"), dispatch: false);
        foreach (GridModel m in new[] { v2Default, v2Suppressed, v1Default, v1Suppressed })
            Assert.Equal(authored, m.AllGenerators.Select(g => g.Pg));
        // The v1 folder and the v2 case are the same network: the same solve, bit for bit.
        PowerFlowResult a = SolverSelector.CreateNewton(v2Suppressed).Solve(v2Suppressed, ContingencyAnalysis.StudyOptions);
        PowerFlowResult b = SolverSelector.CreateNewton(v1Suppressed).Solve(v1Suppressed, ContingencyAnalysis.StudyOptions);
        Assert.Equal(a.Vm.Select(BitConverter.DoubleToInt64Bits), b.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.VaDeg.Select(BitConverter.DoubleToInt64Bits), b.VaDeg.Select(BitConverter.DoubleToInt64Bits));
    }

    [Fact]
    public void The_pre_start_point_is_verified_with_the_motor_disconnected_and_reproduced_exactly()
    {
        MotorStartCaseInput input = Load("prestart.case.json");
        Assert.Equal(new MotorStartLoadPolicy("gridsim-case/2", false, false, false, input.Policy.Notes), input.Policy);
        Assert.Contains(input.Policy.Notes, n => n.StartsWith("motors: 1 placed motor row", StringComparison.Ordinal));
        MotorStartResult r = MotorStartRunner.Run(input, Request());
        Assert.All(r.Generators, g => Assert.True(g.Matches, g.Name));
        MotorStartPreStart pre = r.PreStart!;
        Assert.True(pre.Passed, string.Join("; ", pre.Discrepancies));
        Assert.Equal("Converged", pre.SolverStatus);
        // Motor not represented: the pre-start network is exactly the resolved model, solved by the incumbent engine.
        PowerFlowResult direct = SolverSelector.CreateNewton(input.Model).Solve(input.Model, ContingencyAnalysis.StudyOptions);
        Assert.Equal(direct.Vm.Select(BitConverter.DoubleToInt64Bits), pre.Buses.Select(x => BitConverter.DoubleToInt64Bits(x.VmPu)));
        // Scheduled units hold their schedule; the slack carries exactly the losses (scheduled generation = demand).
        MotorStartBusState g2 = pre.Buses.Single(x => x.Bus == 2), g3 = pre.Buses.Single(x => x.Bus == 3);
        Assert.Equal(20.0, g2.RealisedPgMw!.Value, 6);
        Assert.Equal(6.0, g3.RealisedPgMw!.Value, 6);
        Assert.Equal(1.0, g2.VmPu, 9);
        Assert.Equal(1, pre.SlackBus);
        Assert.Equal(pre.LossMw!.Value, pre.SlackMovementMw!.Value, 6);
        Assert.True(pre.SlackMovementMw > 0);
        Assert.Equal("Motor not represented in the pre-start network (disconnected; not static demand).", r.Method.PreStartRepresentation);
    }

    [Fact]
    public void The_loader_default_control_moves_the_authored_slack_and_the_study_refuses_it()
    {
        // The loader default balances the slack: 34 MW x 1.02 - 26 MW = 8.68 MW, not the authored 8 MW.
        GridModel defaulted = V2CaseLoader.Load(Fixture("control.case.json"), Schemas, DataRoot);
        Assert.Equal(34 * 1.02 - 26, defaulted.Generators[0].Pg, 9);
        MotorStartCaseInput control = Load("control.case.json", dispatch: true);
        Assert.True(control.Policy.EffectiveDispatch);
        Assert.Null(control.Policy.AuthoredSystemDispatch);
        MotorStartResult refused = MotorStartRunner.Run(control, Request());
        Assert.Equal(MotorStartOutcome.PreStartFailed, refused.Outcome);
        Assert.Equal("INCOMPLETE", refused.Status);
        Assert.Null(refused.PreStart);
        Assert.Empty(refused.Samples);
        Assert.False(refused.Generators.Single(g => g.Name == "Grid").Matches);
        Assert.Contains(refused.Reasons, x => x.Contains("generator 'Grid': authored 8 MW", StringComparison.Ordinal));

        // The same control case through the study's own seam (dispatch suppressed) reproduces the authored point.
        MotorStartResult suppressed = MotorStartRunner.Run(Load("control.case.json"), Request(horizon: 0.2));
        MotorStartResult authored = MotorStartRunner.Run(Load("prestart.case.json"), Request(horizon: 0.2));
        Assert.True(suppressed.PreStart!.Passed);
        Assert.Equal(authored.PreStart!.Buses.Select(b => b.VmPu), suppressed.PreStart.Buses.Select(b => b.VmPu));
    }

    [Fact]
    public void A_scheduled_point_the_ac_equations_cannot_hold_fails_the_initial_state_check()
    {
        MotorStartCaseInput input = Load("prestart.case.json");
        // G3 limited to +-0.1 MVAr cannot hold 1.0 pu under the works load: the solver releases bus 3, and the check fails with its status kept.
        var gens = input.Model.AllGenerators.Select(g => g.Name == "G3" ? g with { Qmin = -0.1, Qmax = 0.1 } : g).ToList();
        MotorStartCaseInput tight = input with { Model = input.Model.CopyWith(generators: gens, generatorsAreWholeFleet: true) };
        MotorStartResult r = MotorStartRunner.Run(tight, Request());
        Assert.Equal(MotorStartOutcome.PreStartFailed, r.Outcome);
        Assert.Equal("Converged", r.PreStart!.SolverStatus);
        Assert.Contains(r.PreStart.Discrepancies, d => d.StartsWith("bus 3: voltage control was released", StringComparison.Ordinal));
        Assert.Null(r.FinalState);
    }

    // ------------------------------------------------------------------------------------------------------------
    // The independent stiff-source reference
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Stiff_source_current_and_torque_match_the_closed_form_locked_rotor_values()
    {
        MotorStartCaseInput input = Load("stiff.case.json");
        PlacedMotor m = input.Motors.Single();
        MotorStartResult r = MotorStartRunner.Run(input, Request(horizon: 0.2));
        MotorStartSample s = r.StartingState!;
        Assert.Equal(0.1, s.TimeSeconds, 12);
        Assert.Equal(1.0, s.Slip);
        Assert.Equal(1.0, s.BusVoltagePu, 12);   // the slack holds 1.0 pu
        double iRef = Current(m, 1.0, 1.0);
        Assert.Equal(iRef, s.CurrentPu, 10);
        Assert.Equal(iRef * 5.0 / (Math.Sqrt(3.0) * 11.0), s.CurrentKa, 10);
        Complex sRef = 1.0 / Complex.Conjugate(Circuit(m, 1.0).Zin) * 5.0;   // MVA drawn at 1 pu
        Assert.Equal(sRef.Real, s.PMw, 9);
        Assert.Equal(sRef.Imaginary, s.QMvar, 9);
        Assert.Equal(Torque(m, 1.0, 1.0), s.ElectricalTorquePu, 10);
        Assert.Equal(0.0, s.LoadTorquePu);
    }

    [Fact]
    public void Stiff_source_acceleration_time_matches_the_quadrature_reference_and_converges_at_second_order()
    {
        // Stated tolerance: at a 1 ms step the acceleration time is within 1e-4 s of the reference, and each halving of
        // the step cuts the error by at least 3 (Heun is second order; observed about 4).
        MotorStartCaseInput input = Load("stiff.case.json");
        PlacedMotor m = input.Motors.Single();
        double reference = AccelerationTime(m, _ => 1.0);
        var errors = new List<double>();
        foreach (double dt in new[] { 0.004, 0.002, 0.001 })
        {
            MotorStartResult r = MotorStartRunner.Run(input, Request(step: dt, sampleEvery: 1000));
            Assert.Equal(MotorStartOutcome.Running, r.Outcome);
            Assert.Equal("RUNNING", r.Status);
            errors.Add(Math.Abs(r.AccelerationTimeSeconds!.Value - reference));
        }
        Assert.True(errors[2] <= 1e-4, $"1 ms error {errors[2]} s against {reference} s");
        Assert.True(errors[0] / errors[1] >= 3 && errors[1] / errors[2] >= 3, string.Join(", ", errors));
    }

    // ------------------------------------------------------------------------------------------------------------
    // The weak feeder: voltage dip, acceleration through a dipped supply, stall
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Weak_feeder_voltage_dip_matches_the_closed_form_divider()
    {
        MotorStartCaseInput input = Load("weak.case.json");
        PlacedMotor m = input.Motors.Single(x => x.Name == "M1");
        MotorStartResult r = MotorStartRunner.Run(input, Request(dipLimit: 20));
        Assert.Equal(MotorStartOutcome.Running, r.Outcome);
        double vLocked = Divider(m, 1.0, WeakFeeder);
        // The network step converges to the Newton bus mismatch, so the solved voltage matches the closed form to within
        // a small multiple of that tolerance (a fixed decimal-place rounding can split values ~1e-10 apart).
        double vTol = 10 * new GridSim.Core.PowerFlow.PowerFlowOptions().Tolerance;
        Assert.InRange(Math.Abs(r.StartingState!.BusVoltagePu - vLocked), 0, vTol);
        MotorStartVoltageDip dip = r.VoltageDip!;
        Assert.Equal(2, dip.MotorBus);
        Assert.Equal(1.0, dip.PreStartVmPu, 12);
        Assert.InRange(Math.Abs(dip.MinimumVmPu - vLocked), 0, vTol);
        Assert.Equal(0.1, dip.MinimumAtSeconds, 12);
        Assert.InRange(Math.Abs(dip.DipPercent - (1 - vLocked) * 100), 0, 100 * vTol);
        Assert.Equal(2, dip.WorstBus);
        Assert.False(dip.WithinLimit);   // about 25.5 %, beyond the declared 20 %: reported, the outcome stays the motor's
        Assert.Contains(r.Reasons, x => x.Contains("exceeds the declared 20 %", StringComparison.Ordinal));
        // The starting current through the divider.
        double iLocked = Current(m, vLocked, 1.0);   // proportional to V through the divider: error scales by I/V
        Assert.InRange(Math.Abs(r.StartingState.CurrentPu - iLocked), 0, vTol * Math.Max(1.0, iLocked / vLocked));
    }

    [Fact]
    public void Weak_feeder_acceleration_time_matches_an_independent_quadrature_through_the_divider()
    {
        MotorStartCaseInput input = Load("weak.case.json");
        PlacedMotor m = input.Motors.Single(x => x.Name == "M1");
        double reference = AccelerationTime(m, s => Divider(m, s, WeakFeeder));
        MotorStartResult r = MotorStartRunner.Run(input, Request(sampleEvery: 100));
        Assert.Equal(MotorStartOutcome.Running, r.Outcome);
        Assert.True(Math.Abs(r.AccelerationTimeSeconds!.Value - reference) <= 2e-4, $"{r.AccelerationTimeSeconds} s against {reference} s");
        MotorStartResult stiff = MotorStartRunner.Run(Load("stiff.case.json"), Request(sampleEvery: 100));
        Assert.True(r.AccelerationTimeSeconds > stiff.AccelerationTimeSeconds + 1.0, "the dipped supply must slow the start");
    }

    [Fact]
    public void A_load_torque_above_the_dipped_locked_rotor_torque_stalls_at_standstill()
    {
        MotorStartCaseInput input = Load("weak.case.json");
        PlacedMotor m2 = input.Motors.Single(x => x.Name == "M2");
        Assert.True(Torque(m2, Divider(m2, 1.0, WeakFeeder), 1.0) < 0.4);
        MotorStartResult r = MotorStartRunner.Run(input, Request(motor: "M2"));
        Assert.Equal(MotorStartOutcome.Stalled, r.Outcome);
        Assert.Equal("STALLED", r.Status);
        Assert.Null(r.AccelerationTimeSeconds);
        Assert.Equal(0.6, r.DecisionTimeSeconds!.Value, 9);   // 0.1 s start + 0.5 s dwell
        Assert.Equal(0.0, r.FinalState!.SpeedPu);
        Assert.True(r.FinalState.AcceleratingTorquePu < 0);
        Assert.Null(r.FailedSolverStatus);
    }

    [Fact]
    public void A_crawl_to_an_equilibrium_below_running_speed_is_a_stall_with_a_margin()
    {
        MotorStartCaseInput input = Load("weak.case.json");
        PlacedMotor heavy = input.Motors.Single(x => x.Name == "M1") with { LoadTorque = new MotorLoadTorque(0.15, 0, 1.0) };
        MotorStartCaseInput crawl = input with { Motors = new[] { heavy } };
        // Independent: the first speed where the torque through the divider falls to the load torque.
        double lo = 0, hi = 0.98;
        Assert.True(Torque(heavy, Divider(heavy, 1.0, WeakFeeder), 1.0) > heavy.LoadTorque.At(0));
        for (int k = 0; k < 60; k++)
        {
            double mid = (lo + hi) / 2, s = 1 - mid;
            if (Torque(heavy, Divider(heavy, s, WeakFeeder), s) > heavy.LoadTorque.At(mid)) lo = mid; else hi = mid;
        }
        MotorStartResult r = MotorStartRunner.Run(crawl, Request(horizon: 30, stallMargin: 0.005, sampleEvery: 1000));
        Assert.Equal(MotorStartOutcome.Stalled, r.Outcome);
        Assert.InRange(r.FinalState!.SpeedPu, lo - 0.05, lo + 1e-6);
        Assert.InRange(r.FinalState.AcceleratingTorquePu, 0.0, 0.005);
        // Without a margin the crawl never decides: the horizon ends first.
        MotorStartResult undecided = MotorStartRunner.Run(crawl, Request(horizon: 5, sampleEvery: 1000));
        Assert.Equal(MotorStartOutcome.HorizonEnded, undecided.Outcome);
        Assert.Equal("HORIZON ENDED", undecided.Status);
    }

    // ------------------------------------------------------------------------------------------------------------
    // The pre-start fixture: running, horizon, non-convergence, refinement, repeatability
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_pre_start_fixture_accelerates_and_reports_every_quantity()
    {
        MotorStartResult r = MotorStartRunner.Run(Load("prestart.case.json"), Request(sampleEvery: 50, dipLimit: 15));
        Assert.Equal(MotorStartOutcome.Running, r.Outcome);
        Assert.StartsWith("speed reached the running speed 0.98 pu", r.Termination, StringComparison.Ordinal);
        Assert.InRange(r.AccelerationTimeSeconds!.Value, 1.0, 5.0);
        Assert.True(r.FinalState!.SpeedPu >= 0.98);
        Assert.Equal(r.StartingState!, r.Samples[1]);   // samples[0] is the instant before connection
        Assert.Equal(0.0, r.Samples[0].CurrentPu);
        Assert.True(r.PeakCurrentState!.CurrentPu >= r.StartingState!.CurrentPu);
        Assert.True(r.VoltageDip!.DipPercent > 0 && r.VoltageDip.WithinLimit == true);
        Assert.Equal(4, r.VoltageDip.MotorBus);
        Assert.True(r.NetworkSolves > 2 * 1000);
        Assert.Equal(MotorStartRunner.Integration, r.Method.Integration);
        Assert.Equal(0.001, r.Method.StepSeconds);
        // Signs: motoring draws P and Q; torque positive.
        Assert.All(r.Samples.Skip(1), s => Assert.True(s.PMw > 0 && s.QMvar > 0 && s.ElectricalTorquePu > 0));
    }

    [Fact]
    public void Step_refinement_supports_the_stated_tolerance_on_the_pre_start_fixture()
    {
        MotorStartCaseInput input = Load("prestart.case.json");
        double t2 = MotorStartRunner.Run(input, Request(step: 0.002, sampleEvery: 1000)).AccelerationTimeSeconds!.Value;
        double t1 = MotorStartRunner.Run(input, Request(step: 0.001, sampleEvery: 1000)).AccelerationTimeSeconds!.Value;
        double t05 = MotorStartRunner.Run(input, Request(step: 0.0005, sampleEvery: 1000)).AccelerationTimeSeconds!.Value;
        Assert.True(Math.Abs(t1 - t05) <= 1e-4, $"{t1} vs {t05}");
        Assert.True(Math.Abs(t2 - t1) >= 3 * Math.Abs(t1 - t05), $"{t2}, {t1}, {t05}");
    }

    [Fact]
    public void A_short_horizon_ends_without_a_decision()
    {
        MotorStartResult r = MotorStartRunner.Run(Load("prestart.case.json"), Request(horizon: 0.5));
        Assert.Equal(MotorStartOutcome.HorizonEnded, r.Outcome);
        Assert.Null(r.AccelerationTimeSeconds);
        Assert.Equal(0.5, r.DecisionTimeSeconds!.Value, 9);
        Assert.InRange(r.FinalState!.SpeedPu, 0.01, 0.98);
    }

    [Fact]
    public void A_failed_network_step_is_non_convergence_with_the_exact_solver_status()
    {
        int calls = 0;
        IPowerFlowSolver Factory(GridModel m) => new FailingAfter(() => ++calls > 5);
        MotorStartResult r = MotorStartRunner.Run(Load("prestart.case.json"), Request(), Factory);
        Assert.Equal(MotorStartOutcome.NonConvergence, r.Outcome);
        Assert.Equal("NO SOLUTION", r.Status);
        Assert.Equal("MaxIterations", r.FailedSolverStatus);
        Assert.Null(r.AccelerationTimeSeconds);
        Assert.Contains(r.Reasons, x => x.Contains("not a motor verdict", StringComparison.Ordinal));
        Assert.True(r.PreStart!.Passed);
    }

    private sealed class FailingAfter(Func<bool> fail) : IPowerFlowSolver
    {
        public PowerFlowResult Solve(GridModel model, PowerFlowOptions? options = null)
        {
            PowerFlowResult ok = SolverSelector.CreateNewton(model).Solve(model, options);
            if (!fail()) return ok;
            return new PowerFlowResult
            {
                Converged = false, Iterations = 100, MaxMismatch = 0.5, BusIds = ok.BusIds, Vm = ok.Vm, VaDeg = ok.VaDeg,
                PInjMw = ok.PInjMw, QInjMvar = ok.QInjMvar, TotalLossMw = ok.TotalLossMw, TotalLossMvar = ok.TotalLossMvar, LineFlows = ok.LineFlows,
            };
        }
    }

    [Fact]
    public void Repeat_runs_are_bit_identical()
    {
        MotorStartCaseInput input = Load("prestart.case.json");
        var options = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        string a = JsonSerializer.Serialize(MotorStartRunner.Run(input, Request(sampleEvery: 10)), options);
        string b = JsonSerializer.Serialize(MotorStartRunner.Run(Load("prestart.case.json"), Request(sampleEvery: 10)), options);
        Assert.Equal(a, b);
    }

    [Fact]
    public void A_v1_folder_serves_as_the_same_pre_start_network_with_the_motor_given_by_the_caller()
    {
        MotorStartCaseInput v2 = Load("prestart.case.json");
        MotorStartCaseInput v1 = MotorStartCase.LoadV1Directory(Fixture("prestart-v1"), v2.Motors);
        Assert.True(v1.Policy.AuthoredSystemDispatch == false);
        MotorStartResult a = MotorStartRunner.Run(v2, Request(sampleEvery: 1000));
        MotorStartResult b = MotorStartRunner.Run(v1, Request(sampleEvery: 1000));
        Assert.True(b.PreStart!.Passed);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.AccelerationTimeSeconds!.Value), BitConverter.DoubleToInt64Bits(b.AccelerationTimeSeconds!.Value));
    }

    [Fact]
    public void A_coasting_restart_starts_from_its_declared_speed()
    {
        MotorStartCaseInput input = Load("stiff.case.json");
        MotorStartResult rest = MotorStartRunner.Run(input, Request(sampleEvery: 1000));
        MotorStartResult coast = MotorStartRunner.Run(input, Request(speed: 0.5, sampleEvery: 1000));
        Assert.Equal(MotorStartOutcome.Running, coast.Outcome);
        Assert.True(coast.StartingState!.SpeedPu < 0.5 && coast.StartingState.SpeedPu > 0.45);   // the fan slowed it before the event
        Assert.Contains(coast.Reasons, x => x.StartsWith("the rotor coasted from 0.5 pu", StringComparison.Ordinal));
        Assert.True(coast.AccelerationTimeSeconds < rest.AccelerationTimeSeconds);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Refusals
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Invalid_requests_are_refused_before_any_solve()
    {
        MotorStartCaseInput input = Load("prestart.case.json");
        void Refused(MotorStartRequest q, string text)
        {
            var ex = Assert.Throws<InvalidDataException>(() => MotorStartRunner.Run(input, q));
            Assert.Contains(text, ex.Message, StringComparison.Ordinal);
        }
        Refused(Request(motor: "M9"), "no placed motor named 'M9'");
        Refused(Request() with { Initial = new MotorStartInitialState(0, true) }, "a motor connected before the start event is not a start");
        Refused(Request(start: 0.1005), "whole number of integration steps");
        Refused(Request(speed: 0.99), "initial.speedPu must be at least 0 and below");
        Refused(Request(step: 0), "integration.stepSeconds must be positive");
        Refused(Request(horizon: 0.05), "must be after start.atSeconds");
        Refused(Request() with { StepSeconds = double.NaN }, "must be a finite number");
    }

    [Fact]
    public void A_case_without_explicit_generator_outputs_is_refused_by_the_adapter()
    {
        string dir = TestTemp.Under("gridsim-motor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.Copy(Fixture("stiff.case.json"), Path.Combine(dir, "stiff.case.json"));
            File.WriteAllText(Path.Combine(dir, "stiff.network.json"), File.ReadAllText(Fixture("stiff.network.json")).Replace("\"outputMw\": 2, ", "", StringComparison.Ordinal));
            var ex = Assert.Throws<InvalidDataException>(() => MotorStartCase.LoadV2(Path.Combine(dir, "stiff.case.json"), Schemas, DataRoot));
            Assert.Contains("must give outputMw, pMinMw and pMaxMw explicitly", ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }
}
