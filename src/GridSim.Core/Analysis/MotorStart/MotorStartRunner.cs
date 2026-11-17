using System.Globalization;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.MotorStart;

/// <summary>
/// RC3 P8: the motor acceleration study, a dedicated study over the incumbent power flow and the incumbent single-cage
/// equivalent circuit. It never calls the steady-state induction machine solve (<see cref="InductionMachines.Solve"/>):
/// the slip is a state derived from rotor speed at every step (s = 1 - w, so s = 1 at standstill) and the circuit is
/// evaluated at that slip through <see cref="InductionMachines.AtSlip"/>.
///
/// <para><b>Order of operations</b> (each step's failure stops the later ones):</para>
/// <list type="number">
/// <item>Validate the request against the case: one motor of that name, its bus in the model, a disconnected initial
/// state below running speed, a direct-on-line start, a positive step dividing the event time and the horizon.</item>
/// <item>Authored against resolved: every authored in-service generator must be in the resolved model, by name, with
/// exactly its authored output. A loader that balanced the schedule fails here (INCOMPLETE).</item>
/// <item>Pre-start AC verification with the motor DISCONNECTED (it is not in the pre-start network at all): the
/// incumbent Newton engine with the contingency family's options (<see cref="ContingencyAnalysis.StudyOptions"/>: 100
/// iterations, reactive limits enforced); every non-slack generating bus at its schedule, every voltage-controlled bus at
/// its setpoint, every generating bus within its summed bounds, the slack movement within its tolerance.</item>
/// <item>Before the event the rotor coasts (Te = 0) under its load torque, held at standstill if at rest.</item>
/// <item>From the event, at each declared step: slip from speed; the motor as the constant-impedance load its circuit is
/// at that slip (exact for the quasi-static circuit: one network solve gives the coupled terminal voltage, no inner
/// iteration); solve and verify the network step; current, powers and torques at the solved terminal voltage; then
/// advance 2 H dw/dt = Te - Tl by Heun's method (explicit trapezoidal: a predictor network solve at the Euler speed,
/// then the averaged accelerating torque). The termination checks run on each step's evaluated state, before the
/// advance.</item>
/// </list>
/// <para>Every solve starts from the model's authored bus voltages (not the previous step), so each step is a pure
/// function of its inputs. Plain arithmetic and Math.Sqrt only: repeat runs are bit-identical.</para>
/// </summary>
public static class MotorStartRunner
{
    /// <summary>The electrical approximation recorded with every result.</summary>
    public const string ElectricalApproximation =
        "Quasi-static single-cage equivalent circuit (stator and rotor flux transients neglected): at each step the motor is the input impedance of its circuit at the slip derived from rotor speed.";

    /// <summary>The coupling policy recorded with every result.</summary>
    public const string Coupling =
        "Constant-impedance coupling: the motor's input admittance at the step's slip is added to its bus shunt and the network solved once; exact for the quasi-static circuit, so no inner iteration.";

    /// <summary>The integration order recorded with every result.</summary>
    public const string Integration =
        "Heun (explicit trapezoidal, second order): evaluate at (t, w); predictor w* = w + dt (Te - Tl) / 2H with its own network solve; w(t + dt) = w + dt ((Te - Tl) + (Te* - Tl*)) / 4H; speed never below 0 (a rotor at rest with Te <= Tl is held).";

    /// <summary>The torque convention recorded with every result.</summary>
    public const string TorqueConvention =
        "Torques in pu of the motor torque base (ratedMva / synchronous mechanical speed); electrical torque = air-gap power in pu; positive load torque opposes motoring; speed in pu of synchronous speed; 2 H dw/dt = Te - Tl.";

    /// <summary>Run the study. <paramref name="solverFactory"/> (tests only) replaces the incumbent Newton engine.</summary>
    public static MotorStartResult Run(MotorStartCaseInput input, MotorStartRequest request, Func<GridModel, IPowerFlowSolver>? solverFactory = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        PlacedMotor motor = Validate(input, request);
        GridModel model = input.Model;
        Func<GridModel, IPowerFlowSolver> factory = solverFactory ?? (m => SolverSelector.CreateNewton(m));
        var method = new MotorStartMethodRecord(ElectricalApproximation, Coupling, Integration, TorqueConvention, request.StepSeconds,
            "Incumbent Newton (SolverSelector.CreateNewton) with ContingencyAnalysis.StudyOptions (100 iterations, reactive limits enforced); no OLTC loop (taps held at their authored ratio over the seconds of a start); every solve starts from the authored bus voltages.",
            "Motor not represented in the pre-start network (disconnected; not static demand).");
        var reasons = new List<string>();

        // ---- Step 2: authored inputs against the resolved model. ----
        List<MotorStartGeneratorState> generators = CompareAuthored(input, reasons);
        if (reasons.Count > 0)
            return Ended(MotorStartOutcome.PreStartFailed, "resolved model does not hold the authored schedule", motor, request, input, method,
                generators, null, reasons);

        // ---- Step 3: the pre-start AC point, motor disconnected. ----
        MotorStartPreStart pre = VerifyPreStart(model, request.PreStart, factory, out PowerFlowResult? preSolved);
        if (!pre.Passed)
        {
            reasons.AddRange(pre.Discrepancies);
            return Ended(MotorStartOutcome.PreStartFailed, "pre-start operating point not reproduced", motor, request, input, method,
                generators, pre, reasons);
        }

        // ---- Steps 4 and 5: coast to the event, then the coupled start. ----
        var sim = new Simulation(model, motor, request, factory, preSolved!);
        return sim.Run(generators, pre, input, method);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------------------------------------------------

    private static PlacedMotor Validate(MotorStartCaseInput input, MotorStartRequest r)
    {
        ArgumentNullException.ThrowIfNull(input.Model);
        ArgumentNullException.ThrowIfNull(input.Motors);
        ArgumentNullException.ThrowIfNull(r.Initial);
        ArgumentNullException.ThrowIfNull(r.Termination);
        ArgumentNullException.ThrowIfNull(r.PreStart);
        if (string.IsNullOrWhiteSpace(r.Motor)) throw new InvalidDataException("motor: name the placed motor to start.");
        List<PlacedMotor> hits = input.Motors.Where(m => string.Equals(m.Name, r.Motor, StringComparison.Ordinal)).ToList();
        if (hits.Count == 0)
            throw new InvalidDataException($"motor: the case has no placed motor named '{r.Motor}'" + (input.Motors.Count == 0 ? " (it has no motor rows)." : $" (it has {string.Join(", ", input.Motors.Select(m => "'" + m.Name + "'"))})."));
        if (hits.Count > 1) throw new InvalidDataException($"motor: '{r.Motor}' names {hits.Count} motors; motor names must be unique.");
        PlacedMotor motor = hits[0];
        if (!input.Model.Buses.Any(b => b.Id == motor.Bus))
            throw new InvalidDataException($"motor '{motor.Name}': its bus {motor.Bus} is not a bus of the resolved model.");
        if (input.Model.InductionMachines.Any(m => m.Bus == motor.Bus && string.Equals(m.Name, motor.Name, StringComparison.Ordinal)))
            throw new InvalidDataException($"motor '{motor.Name}': the resolved model already holds an induction machine of that name at bus {motor.Bus}; the pre-start network would count the motor twice.");
        if (r.Method != MotorStartMethod.DirectOnLine) throw new InvalidDataException("start.method: only directOnLine is supported.");
        if (r.Initial.Connected)
            throw new InvalidDataException("initial.connected: a motor connected before the start event is not a start (a running restart or a post-start equilibrium would replace the pre-start point); give connected false, with the rotor's speed, and the start event connects it.");
        Finite(r.Initial.SpeedPu, "initial.speedPu");
        Finite(r.StartSeconds, "start.atSeconds");
        Finite(r.StepSeconds, "integration.stepSeconds");
        Finite(r.Termination.RunningSpeedPu, "termination.runningSpeedPu");
        Finite(r.Termination.HorizonSeconds, "termination.horizonSeconds");
        Finite(r.Termination.StallDwellSeconds, "termination.stallDwellSeconds");
        Finite(r.Termination.StallAcceleratingTorquePu, "termination.stallAcceleratingTorquePu");
        Finite(r.PreStart.PgToleranceMw, "preStart.pgToleranceMw");
        Finite(r.PreStart.VmTolerancePu, "preStart.vmTolerancePu");
        Finite(r.PreStart.MaxSlackMovementMw, "preStart.maxSlackMovementMw");
        if (r.Termination.RunningSpeedPu is not (> 0 and < 1)) throw new InvalidDataException("termination.runningSpeedPu must lie strictly between 0 and 1 (pu of synchronous speed).");
        if (r.Initial.SpeedPu < 0 || r.Initial.SpeedPu >= r.Termination.RunningSpeedPu)
            throw new InvalidDataException("initial.speedPu must be at least 0 and below termination.runningSpeedPu.");
        if (r.StepSeconds <= 0) throw new InvalidDataException("integration.stepSeconds must be positive.");
        if (r.StartSeconds < 0) throw new InvalidDataException("start.atSeconds must not be negative.");
        if (r.Termination.HorizonSeconds <= r.StartSeconds) throw new InvalidDataException("termination.horizonSeconds must be after start.atSeconds.");
        if (r.Termination.StallDwellSeconds < 0) throw new InvalidDataException("termination.stallDwellSeconds must not be negative.");
        if (r.PreStart.PgToleranceMw <= 0 || r.PreStart.VmTolerancePu <= 0 || r.PreStart.MaxSlackMovementMw < 0)
            throw new InvalidDataException("preStart: pgToleranceMw and vmTolerancePu must be positive and maxSlackMovementMw not negative.");
        if (r.SampleEverySteps < 1) throw new InvalidDataException("report.sampleEverySteps must be at least 1.");
        if (r.MaxVoltageDipPercent is double lim && (!double.IsFinite(lim) || lim <= 0 || lim >= 100))
            throw new InvalidDataException("report.maxVoltageDipPercent must be a percentage above 0 and below 100.");
        WholeSteps(r.StartSeconds, r.StepSeconds, "start.atSeconds");
        WholeSteps(r.Termination.HorizonSeconds, r.StepSeconds, "termination.horizonSeconds");
        if (r.Termination.HorizonSeconds / r.StepSeconds > 10_000_000) throw new InvalidDataException("termination.horizonSeconds / integration.stepSeconds exceeds 10 000 000 steps.");
        return motor;
    }

    private static void Finite(double v, string what)
    {
        if (!double.IsFinite(v)) throw new InvalidDataException($"{what} must be a finite number.");
    }

    private static long WholeSteps(double t, double dt, string what)
    {
        double n = t / dt;
        double k = Math.Round(n);
        if (Math.Abs(n - k) > 1e-9 * Math.Max(1.0, k))
            throw new InvalidDataException($"{what} ({t.ToString("R", CultureInfo.InvariantCulture)} s) must be a whole number of integration steps of {dt.ToString("R", CultureInfo.InvariantCulture)} s.");
        return (long)k;
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Authored against resolved
    // -------------------------------------------------------------------------------------------------------------------

    private static List<MotorStartGeneratorState> CompareAuthored(MotorStartCaseInput input, List<string> reasons)
    {
        var rows = new List<MotorStartGeneratorState>();
        IReadOnlyList<Generator> fleet = input.Model.AllGenerators;
        foreach (MotorStartAuthoredGenerator a in input.AuthoredGenerators)
        {
            List<Generator> hits = fleet.Where(g => string.Equals(g.Name, a.Name, StringComparison.Ordinal)).ToList();
            if (hits.Count != 1)
            {
                rows.Add(new MotorStartGeneratorState(a.Name, a.Bus, a.OutputMw, null, null, false));
                reasons.Add($"generator '{a.Name}': {(hits.Count == 0 ? "not in the resolved model" : $"{hits.Count} units of the resolved model carry that name")}; its identity cannot be resolved.");
                continue;
            }
            Generator g = hits[0];
            bool same = g.Bus == a.Bus && g.InService == a.InService
                && (!a.InService || BitConverter.DoubleToInt64Bits(g.Pg) == BitConverter.DoubleToInt64Bits(a.OutputMw));
            rows.Add(new MotorStartGeneratorState(a.Name, a.Bus, a.OutputMw, g.Pg, g.Pg - a.OutputMw, same));
            if (!same)
                reasons.Add($"generator '{a.Name}': authored {F(a.OutputMw)} MW at bus {a.Bus}{(a.InService ? "" : " (out of service)")}, resolved {F(g.Pg)} MW at bus {g.Bus}{(g.InService ? "" : " (out of service)")}: the loader moved the authored point (loader balancing ran, or the unit is not the authored one).");
        }
        foreach (Generator g in fleet)
            if (!input.AuthoredGenerators.Any(a => string.Equals(a.Name, g.Name, StringComparison.Ordinal)))
                reasons.Add($"generator '{g.Name ?? "(unnamed)"}' at bus {g.Bus}: in the resolved model but not among the authored rows.");
        return rows;
    }

    // -------------------------------------------------------------------------------------------------------------------
    // Pre-start verification
    // -------------------------------------------------------------------------------------------------------------------

    internal static PowerFlowResult? Solve(GridModel m, Func<GridModel, IPowerFlowSolver> factory, out string status)
    {
        PowerFlowResult solved;
        try
        {
            solved = factory(m).Solve(m, ContingencyAnalysis.StudyOptions);
        }
        catch (Exception ex) when (ex is ArithmeticException or InvalidOperationException or ArgumentException)
        {
            status = "Exception: " + ex.Message;
            return null;
        }
        bool complete = solved.BusIds.SequenceEqual(m.Buses.Select(b => b.Id)) && solved.Vm.Length == m.Buses.Count
            && solved.VaDeg.Length == m.Buses.Count && solved.PInjMw.Length == m.Buses.Count && solved.QInjMvar.Length == m.Buses.Count;
        bool finite = complete && solved.Vm.All(double.IsFinite) && solved.VaDeg.All(double.IsFinite)
            && solved.PInjMw.All(double.IsFinite) && solved.QInjMvar.All(double.IsFinite) && double.IsFinite(solved.MaxMismatch);
        status = solved.Status.ToString();
        if (!solved.Converged || solved.Status != PowerFlowStatus.Converged || !complete || !finite
            || solved.MaxMismatch > ContingencyAnalysis.StudyOptions.Tolerance)
        {
            if (solved.Status == PowerFlowStatus.Converged) status = complete ? "Converged (non-finite or out-of-tolerance evidence)" : "Converged (incomplete bus evidence)";
            return null;
        }
        return solved;
    }

    private static double DemandAt(Bus b, double v) => b.Zip is ZipLoad z && z.VariesWithVoltage ? b.Pd * (z.Ap * v * v + z.Bp * v + z.Cp) : b.Pd;

    private static double ReactiveDemandAt(Bus b, double v) => b.Zip is ZipLoad z && z.VariesWithVoltage ? b.Qd * (z.Aq * v * v + z.Bq * v + z.Cq) : b.Qd;

    private static MotorStartPreStart VerifyPreStart(GridModel model, MotorStartPreStartTolerance tol, Func<GridModel, IPowerFlowSolver> factory,
        out PowerFlowResult? solved)
    {
        solved = Solve(model, factory, out string status);
        if (solved is null)
            return new MotorStartPreStart(false, status, null, null, null, null, null, Array.Empty<MotorStartBusState>(),
                new[] { $"pre-start power flow (motor disconnected) has no converged solution: {status}." });
        var discrepancies = new List<string>();
        var rows = new List<MotorStartBusState>();
        int? slackBus = null;
        double? slackMove = null;
        for (int i = 0; i < model.Buses.Count; i++)
        {
            Bus b = model.Buses[i];
            double vm = solved.Vm[i];
            BusType solvedType = solved.SolvedBusTypes is { } t && t.Count == model.Buses.Count ? t[i] : b.Type;
            List<Generator> units = model.Generators.Where(g => g.Bus == b.Id).ToList();
            var failures = new List<string>();
            double? scheduled = null, realised = null, move = null, realisedQ = null, setpoint = null;
            if (units.Count > 0)
            {
                scheduled = units.Sum(g => g.Pg);
                // At convergence the injection is generation minus the modelled demand at the solved voltage (the bus
                // shunt is in the admittance matrix, so it is not added); this is the bus's aggregate realised output.
                realised = solved.PInjMw[i] + DemandAt(b, vm);
                realisedQ = solved.QInjMvar[i] + ReactiveDemandAt(b, vm);
                move = realised - scheduled;
                double pmin = units.Sum(g => g.Pmin), pmax = units.Sum(g => g.Pmax);
                if (b.Type == BusType.Slack)
                {
                    slackBus ??= b.Id;
                    slackMove ??= move;
                    if (Math.Abs(move.Value) > tol.MaxSlackMovementMw)
                        failures.Add($"slack bus {b.Id}: realised {F(realised.Value)} MW moved {F(move.Value)} MW from its schedule {F(scheduled.Value)} MW, beyond the {F(tol.MaxSlackMovementMw)} MW allowed.");
                }
                else if (Math.Abs(move.Value) > tol.PgToleranceMw)
                    failures.Add($"bus {b.Id}: realised {F(realised.Value)} MW differs from the scheduled {F(scheduled.Value)} MW by {F(move.Value)} MW, beyond the {F(tol.PgToleranceMw)} MW tolerance.");
                if (realised < pmin - tol.PgToleranceMw || realised > pmax + tol.PgToleranceMw)
                    failures.Add($"bus {b.Id}: realised {F(realised.Value)} MW is outside the summed unit bounds [{F(pmin)}, {F(pmax)}] MW.");
                if (b.Type is BusType.Pv or BusType.Slack)
                {
                    setpoint = units[0].Vg;
                    if (units.Any(g => BitConverter.DoubleToInt64Bits(g.Vg) != BitConverter.DoubleToInt64Bits(setpoint.Value)))
                        failures.Add($"bus {b.Id}: its units carry different voltage setpoints; the bus setpoint is ambiguous.");
                    else if (b.Type == BusType.Pv && solvedType != BusType.Pv)
                        failures.Add($"bus {b.Id}: voltage control was released at a reactive limit (solved as {solvedType}); the setpoint {F(setpoint.Value)} pu and the AC equations cannot both hold under the enforced limits.");
                    else if (Math.Abs(vm - setpoint.Value) > tol.VmTolerancePu)
                        failures.Add($"bus {b.Id}: solved {F(vm)} pu differs from the setpoint {F(setpoint.Value)} pu beyond the {F(tol.VmTolerancePu)} pu tolerance.");
                }
            }
            discrepancies.AddRange(failures);
            rows.Add(new MotorStartBusState(b.Id, b.Type.ToString(), solvedType.ToString(), vm, solved.VaDeg[i], scheduled, realised, move, realisedQ, setpoint, failures));
        }
        return new MotorStartPreStart(discrepancies.Count == 0, status, solved.MaxMismatch, solved.Iterations, solved.TotalLossMw,
            slackBus, slackMove, rows, discrepancies);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The start
    // -------------------------------------------------------------------------------------------------------------------

    private static MotorStartResult Ended(MotorStartOutcome outcome, string termination, PlacedMotor motor, MotorStartRequest request,
        MotorStartCaseInput input, MotorStartMethodRecord method, IReadOnlyList<MotorStartGeneratorState> generators, MotorStartPreStart? pre,
        IReadOnlyList<string> reasons)
        => new(Label(outcome), outcome, termination, motor, request, input.Policy, method, generators, pre, null, null, null, null, null, null,
            pre?.Passed == false && pre.Buses.Count == 0 ? pre.SolverStatus : null, 0, Array.Empty<MotorStartSample>(), reasons);

    /// <summary>The status label of an outcome.</summary>
    public static string Label(MotorStartOutcome o) => o switch
    {
        MotorStartOutcome.Running => "RUNNING",
        MotorStartOutcome.Stalled => "STALLED",
        MotorStartOutcome.NonConvergence => "NO SOLUTION",
        MotorStartOutcome.HorizonEnded => "HORIZON ENDED",
        _ => "INCOMPLETE",
    };

    private static string F(double v) => v.ToString("G9", CultureInfo.InvariantCulture);

    /// <summary>One electrical evaluation: the network solved with the motor at a slip, and the motor's state there.</summary>
    private sealed record Evaluation(double Slip, InductionMachineSlipPoint Point, double BusVm, double MinVm, int MinBus, double[] Vm);

    private sealed class Simulation
    {
        private readonly GridModel _model;
        private readonly PlacedMotor _motor;
        private readonly InductionMachine _circuit;
        private readonly MotorStartRequest _r;
        private readonly Func<GridModel, IPowerFlowSolver> _factory;
        private readonly PowerFlowResult _pre;
        private readonly int _busIndex;
        private readonly double _busKv;
        private readonly double _scale;      // motor-base admittance to bus shunt MW / MVAr at 1 pu: ratedMva (busKv / ratedKv)^2
        private readonly double _kvRatio;    // bus pu to motor terminal pu: busKv / ratedKv
        private readonly double _baseKa;     // rated current, kA
        private int _solves;
        private string? _failedStatus;

        public Simulation(GridModel model, PlacedMotor motor, MotorStartRequest r, Func<GridModel, IPowerFlowSolver> factory, PowerFlowResult pre)
        {
            _model = model;
            _motor = motor;
            _circuit = motor.Circuit();
            _r = r;
            _factory = factory;
            _pre = pre;
            _busIndex = model.Buses.ToList().FindIndex(b => b.Id == motor.Bus);
            _busKv = model.Buses[_busIndex].BaseKv;
            _kvRatio = _busKv > 0 ? _busKv / motor.RatedKv : 1.0;
            _scale = motor.RatedMva * _kvRatio * _kvRatio;
            _baseKa = motor.RatedMva / (Math.Sqrt(3.0) * motor.RatedKv);
        }

        private Evaluation? Evaluate(double speed)
        {
            double slip = 1.0 - speed;
            // The admittance at this slip (pu on the motor base at 1 pu terminal voltage), as a bus shunt.
            InductionMachineSlipPoint unit = InductionMachines.AtSlip(_circuit, 1.0, slip);
            Bus b = _model.Buses[_busIndex];
            var buses = _model.Buses.ToList();
            buses[_busIndex] = b with { Gs = b.Gs + _scale * unit.ConductancePu, Bs = b.Bs + _scale * unit.SusceptancePu };
            GridModel stepModel = _model.CopyWith(buses: buses);
            _solves++;
            PowerFlowResult? solved = Solve(stepModel, _factory, out string status);
            if (solved is null) { _failedStatus = status; return null; }
            double vBus = solved.Vm[_busIndex];
            InductionMachineSlipPoint point = InductionMachines.AtSlip(_circuit, vBus * _kvRatio, slip);
            int minIdx = 0;
            for (int i = 1; i < solved.Vm.Length; i++) if (solved.Vm[i] < solved.Vm[minIdx]) minIdx = i;
            return new Evaluation(slip, point, vBus, solved.Vm[minIdx], _model.Buses[minIdx].Id, solved.Vm);
        }

        private MotorStartSample Sample(double t, double speed, Evaluation? e)
        {
            double tl = _motor.LoadTorque.At(speed);
            if (e is null)
            {
                double v = _pre.Vm[_busIndex];
                int minIdx = 0;
                for (int i = 1; i < _pre.Vm.Length; i++) if (_pre.Vm[i] < _pre.Vm[minIdx]) minIdx = i;
                return new MotorStartSample(t, speed, 1.0 - speed, v, v * _kvRatio, 0, 0, 0, 0, 0, tl, -tl, _pre.Vm[minIdx], _model.Buses[minIdx].Id);
            }
            InductionMachineSlipPoint p = e.Point;
            return new MotorStartSample(t, speed, e.Slip, e.BusVm, p.VoltagePu, p.CurrentPu, p.CurrentPu * _baseKa, p.PPu * _motor.RatedMva,
                p.QPu * _motor.RatedMva, p.TorquePu, tl, p.TorquePu - tl, e.MinVm, e.MinBus);
        }

        public MotorStartResult Run(IReadOnlyList<MotorStartGeneratorState> generators, MotorStartPreStart pre, MotorStartCaseInput input, MotorStartMethodRecord method)
        {
            double dt = _r.StepSeconds, twoH = 2.0 * _motor.InertiaH;
            long startStep = (long)Math.Round(_r.StartSeconds / dt);
            long horizonStep = (long)Math.Round(_r.Termination.HorizonSeconds / dt);
            double speed = _r.Initial.SpeedPu;
            var samples = new List<MotorStartSample>();
            var reasons = new List<string>();

            // Before the event: the rotor coasts (no electrical torque) under its load torque; at rest it is held.
            for (long k = 0; k < startStep; k++)
            {
                double a = -_motor.LoadTorque.At(speed);
                if (speed <= 0 && a <= 0) { speed = 0; continue; }
                double pred = Math.Max(0.0, speed + dt * a / twoH);
                double a2 = -_motor.LoadTorque.At(pred);
                speed = Math.Max(0.0, speed + dt * (a + a2) / (2.0 * twoH));
            }
            double speedAtEvent = speed;
            samples.Add(Sample(_r.StartSeconds, speed, null) with { TimeSeconds = _r.StartSeconds });   // the instant before connection

            double preVm = _pre.Vm[_busIndex];
            double minMotorVm = double.PositiveInfinity, minMotorAt = _r.StartSeconds;
            var minByBus = (double[])_pre.Vm.Clone();
            MotorStartSample? starting = null, peak = null, last = null;
            double? stallSince = null;
            double prevSpeed = speed, prevT = _r.StartSeconds;
            MotorStartOutcome? outcome = null;
            string termination = "";
            double? accel = null, decision = null;

            for (long k = startStep; ; k++)
            {
                double t = k * dt;
                Evaluation? e = Evaluate(speed);
                if (e is null)
                {
                    outcome = MotorStartOutcome.NonConvergence;
                    decision = t;
                    termination = $"network step at {F(t)} s (speed {F(speed)} pu) has no converged solution: {_failedStatus}";
                    reasons.Add(termination + "; a failed network step is a failed evaluation, not a motor verdict.");
                    break;
                }
                MotorStartSample s = Sample(t, speed, e);
                last = s;
                starting ??= s;
                if (peak is null || s.CurrentPu > peak.CurrentPu) peak = s;
                if (s.BusVoltagePu < minMotorVm) { minMotorVm = s.BusVoltagePu; minMotorAt = t; }
                for (int i = 0; i < e.Vm.Length; i++) if (e.Vm[i] < minByBus[i]) minByBus[i] = e.Vm[i];
                bool report = (k - startStep) % _r.SampleEverySteps == 0;

                if (speed >= _r.Termination.RunningSpeedPu)
                {
                    // Linear interpolation of the crossing between the last two evaluated steps (second order, as Heun).
                    double tc = speed == prevSpeed ? t : prevT + (t - prevT) * (_r.Termination.RunningSpeedPu - prevSpeed) / (speed - prevSpeed);
                    outcome = MotorStartOutcome.Running;
                    accel = tc - _r.StartSeconds;
                    decision = t;
                    termination = $"speed reached the running speed {F(_r.Termination.RunningSpeedPu)} pu (crossing interpolated at {F(tc)} s, first step at or above it {F(t)} s)";
                    samples.Add(s);
                    break;
                }
                double ta = s.AcceleratingTorquePu;
                if (ta <= _r.Termination.StallAcceleratingTorquePu)
                {
                    stallSince ??= t;
                    if (t - stallSince.Value >= _r.Termination.StallDwellSeconds - 1e-12)
                    {
                        outcome = MotorStartOutcome.Stalled;
                        decision = t;
                        termination = $"accelerating torque at or below {F(_r.Termination.StallAcceleratingTorquePu)} pu from {F(stallSince.Value)} s to {F(t)} s (dwell {F(_r.Termination.StallDwellSeconds)} s) at speed {F(speed)} pu, below the running speed {F(_r.Termination.RunningSpeedPu)} pu";
                        samples.Add(s);
                        break;
                    }
                }
                else stallSince = null;
                if (k >= horizonStep)
                {
                    outcome = MotorStartOutcome.HorizonEnded;
                    decision = t;
                    termination = $"time horizon {F(_r.Termination.HorizonSeconds)} s ended at speed {F(speed)} pu, below the running speed {F(_r.Termination.RunningSpeedPu)} pu, without meeting the stall criterion";
                    samples.Add(s);
                    break;
                }
                if (report) samples.Add(s);

                // Heun: predictor at the Euler speed, with its own network solve; then the averaged accelerating torque.
                double next;
                if (speed <= 0 && ta <= 0) next = 0;   // held at rest: the predictor would be this same state
                else
                {
                    double pred = Math.Max(0.0, speed + dt * ta / twoH);
                    Evaluation? ep = Evaluate(pred);
                    if (ep is null)
                    {
                        outcome = MotorStartOutcome.NonConvergence;
                        decision = t + dt;
                        termination = $"predictor network step for {F(t + dt)} s (speed {F(pred)} pu) has no converged solution: {_failedStatus}";
                        reasons.Add(termination + "; a failed network step is a failed evaluation, not a motor verdict.");
                        break;
                    }
                    double tap = ep.Point.TorquePu - _motor.LoadTorque.At(pred);
                    next = Math.Max(0.0, speed + dt * (ta + tap) / (2.0 * twoH));
                }
                prevSpeed = speed;
                prevT = t;
                speed = next;
            }

            MotorStartVoltageDip? dip = null;
            if (!double.IsPositiveInfinity(minMotorVm))
            {
                int worst = 0;
                double worstDip = double.NegativeInfinity;
                for (int i = 0; i < minByBus.Length; i++)
                {
                    double d = (_pre.Vm[i] - minByBus[i]) / _pre.Vm[i] * 100.0;
                    if (d > worstDip) { worstDip = d; worst = i; }
                }
                double motorDip = (preVm - minMotorVm) / preVm * 100.0;
                bool? within = _r.MaxVoltageDipPercent is double lim ? worstDip <= lim : null;
                dip = new MotorStartVoltageDip(_motor.Bus, preVm, minMotorVm, minMotorAt, motorDip, _model.Buses[worst].Id, _pre.Vm[worst],
                    minByBus[worst], worstDip, _r.MaxVoltageDipPercent, within);
                if (within == false) reasons.Add($"the deepest voltage dip, {F(worstDip)} % at bus {_model.Buses[worst].Id}, exceeds the declared {F(_r.MaxVoltageDipPercent!.Value)} % (reported; the outcome is the motor's).");
            }
            if (speedAtEvent != _r.Initial.SpeedPu)
                reasons.Add($"the rotor coasted from {F(_r.Initial.SpeedPu)} pu to {F(speedAtEvent)} pu before the start event.");
            MotorStartOutcome final = outcome!.Value;
            return new MotorStartResult(Label(final), final, termination, _motor, _r, input.Policy, method, generators, pre,
                accel, decision, starting, peak, last, dip, final == MotorStartOutcome.NonConvergence ? _failedStatus : null, _solves, samples, reasons);
        }
    }
}
