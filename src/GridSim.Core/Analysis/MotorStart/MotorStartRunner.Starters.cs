using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.MotorStart;

/// <summary>
/// Reduced-voltage starters (<see cref="MotorStarter"/>) and group starting (<see cref="MotorGroupStartRequest"/>). These
/// run on their own engine below. A direct-on-line request without a starter never reaches it: it keeps the original
/// single-motor path, bit for bit.
///
/// <para>The engine follows the direct-on-line study step for step: slip from speed, every connected motor as the load
/// its starter stage puts on its bus (<see cref="StarterMath"/>), one network solve, the motor states at the solved
/// voltages, the termination checks, then Heun's method with a predictor solve. Starter inputs (stage, voltage
/// fraction, drive frequency) are held over each step (zero-order hold), so the predictor uses the step's stage. Every
/// time is a whole number of steps: switch times, dead times, ramps and the stall dwell are counted in steps. A motor in
/// a star-delta dead time is disconnected (no torque) and coasts under its load; the stall dwell does not run while it is
/// disconnected. RUNNING needs the starter in its final stage (switched, bypassed, or the drive at rated frequency): a
/// motor already above the running speed at that moment is RUNNING at that step.</para>
/// </summary>
public static partial class MotorStartRunner
{
    /// <summary>The coupling text recorded for a starter or group run.</summary>
    public const string StarterCoupling =
        "Each connected motor is the load its starter stage puts on its bus at the step's slip (transformer a^2 Y, reactor 1/(1/Y + Zr), soft starter a^2 G - j sqrt(a^2|Y|^2 - a^4 G^2), drive P/efficiency at unity power factor), and the network is solved once; a soft-starter current limit is settled by repeated solves to 1e-9 relative. Starter inputs are held over each step.";

    // -------------------------------------------------------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------------------------------------------------------

    private static string MethodName(MotorStartMethod m) => m switch
    {
        MotorStartMethod.DirectOnLine => "directOnLine",
        MotorStartMethod.StarDelta => "starDelta",
        MotorStartMethod.Autotransformer => "autotransformer",
        MotorStartMethod.Reactor => "reactor",
        MotorStartMethod.SoftStarter => "softStarter",
        MotorStartMethod.Vfd => "vfd",
        _ => ((int)m).ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static void ValidateStarter(MotorStartMethod method, MotorStarter? s, double dt, double runningSpeed, double initialSpeed, GridModel model, PlacedMotor motor)
    {
        if (!Enum.IsDefined(method)) throw new InvalidDataException($"start.method: {MethodName(method)} is not a starting method.");
        if (method == MotorStartMethod.DirectOnLine)
            throw new InvalidDataException("start.starter: a starter goes with a reduced-voltage method; directOnLine takes none.");
        if (s is null) throw new InvalidDataException($"start.starter: method {MethodName(method)} needs a starter object.");
        if (s.Method != method)
            throw new InvalidDataException($"start.starter: the starter is for {MethodName(s.Method)}, but start.method is {MethodName(method)}.");
        string[] allowed = method switch
        {
            MotorStartMethod.StarDelta => new[] { "switchSpeedPu", "switchTimeSeconds", "deadTimeSeconds" },
            MotorStartMethod.Autotransformer => new[] { "switchSpeedPu", "switchTimeSeconds", "tapRatio" },
            MotorStartMethod.Reactor => new[] { "switchSpeedPu", "switchTimeSeconds", "reactorRPu", "reactorXPu" },
            MotorStartMethod.SoftStarter => new[] { "initialVoltageFraction", "rampSeconds", "currentLimitPu" },
            _ => new[] { "initialFrequencyHz", "ratedFrequencyHz", "rampSeconds", "currentLimitPu", "driveEfficiency" },
        };
        (string Name, double? Value)[] fields =
        {
            ("switchSpeedPu", s.SwitchSpeedPu), ("switchTimeSeconds", s.SwitchTimeSeconds), ("deadTimeSeconds", s.DeadTimeSeconds),
            ("tapRatio", s.TapRatio), ("reactorRPu", s.ReactorRPu), ("reactorXPu", s.ReactorXPu),
            ("initialVoltageFraction", s.InitialVoltageFraction), ("rampSeconds", s.RampSeconds), ("currentLimitPu", s.CurrentLimitPu),
            ("initialFrequencyHz", s.InitialFrequencyHz), ("ratedFrequencyHz", s.RatedFrequencyHz), ("driveEfficiency", s.DriveEfficiency),
        };
        foreach ((string name, double? value) in fields)
        {
            if (value is null) continue;
            if (!allowed.Contains(name)) throw new InvalidDataException($"start.starter.{name}: not a field of a {MethodName(method)} starter.");
            Finite(value.Value, "start.starter." + name);
        }
        if (method is MotorStartMethod.StarDelta or MotorStartMethod.Autotransformer or MotorStartMethod.Reactor)
        {
            double sw = s.SwitchSpeedPu ?? MotorStarter.DefaultSwitchSpeedPu;
            if (sw <= 0 || sw >= runningSpeed)
                throw new InvalidDataException("start.starter.switchSpeedPu must lie above 0 and below termination.runningSpeedPu.");
            // A rotor already at or above the switch speed would switch at once, so the start would really be direct-on-line.
            if (initialSpeed >= sw)
                throw new InvalidDataException($"initial.speedPu {F(initialSpeed)} is at or above start.starter.switchSpeedPu {F(sw)}: the {MethodName(method)} starter would switch to full voltage at the first step, so the start would be direct-on-line; refused (start below the switch speed, or use directOnLine).");
            if (s.SwitchTimeSeconds is double ts)
            {
                if (ts <= 0) throw new InvalidDataException("start.starter.switchTimeSeconds must be positive.");
                WholeSteps(ts, dt, "start.starter.switchTimeSeconds");
            }
        }
        switch (method)
        {
            case MotorStartMethod.StarDelta:
                if (s.DeadTimeSeconds is double dead)
                {
                    if (dead < 0) throw new InvalidDataException("start.starter.deadTimeSeconds must not be negative.");
                    WholeSteps(dead, dt, "start.starter.deadTimeSeconds");
                }
                break;
            case MotorStartMethod.Autotransformer:
                if (s.TapRatio is not (> 0 and < 1)) throw new InvalidDataException("start.starter.tapRatio is required and must lie strictly between 0 and 1.");
                break;
            case MotorStartMethod.Reactor:
                if (s.ReactorRPu is not double rr || s.ReactorXPu is not double xr)
                    throw new InvalidDataException("start.starter: a reactor starter needs reactorRPu and reactorXPu (pu on the motor base).");
                if (rr < 0 || xr < 0 || rr + xr <= 0)
                    throw new InvalidDataException("start.starter: reactorRPu and reactorXPu must not be negative, and not both zero.");
                break;
            case MotorStartMethod.SoftStarter:
                if (s.InitialVoltageFraction is not (> 0 and < 1))
                    throw new InvalidDataException("start.starter.initialVoltageFraction is required and must lie strictly between 0 and 1.");
                RampSteps(s, dt);
                if (s.CurrentLimitPu is <= 0) throw new InvalidDataException("start.starter.currentLimitPu must be positive.");
                break;
            case MotorStartMethod.Vfd:
                if (s.InitialFrequencyHz is not double f0 || s.RatedFrequencyHz is not double fn)
                    throw new InvalidDataException("start.starter: a vfd starter needs initialFrequencyHz and ratedFrequencyHz.");
                if (fn <= 0 || f0 <= 0 || f0 >= fn)
                    throw new InvalidDataException("start.starter: initialFrequencyHz must be above 0 and below ratedFrequencyHz.");
                // Above f0/fn the slip against the drive's first frequency is negative: the drive would regenerate (a flying
                // restart), which the model does not represent.
                if (initialSpeed > f0 / fn)
                    throw new InvalidDataException($"initial.speedPu {F(initialSpeed)} is above the drive's starting frequency ratio initialFrequencyHz / ratedFrequencyHz = {F(f0 / fn)}: the slip would be negative and the drive would regenerate (a flying restart), which is not modelled; refused.");
                RampSteps(s, dt);
                if (s.CurrentLimitPu is <= 0) throw new InvalidDataException("start.starter.currentLimitPu must be positive.");
                if (s.DriveEfficiency is double eta && eta is not (> 0 and <= 1))
                    throw new InvalidDataException("start.starter.driveEfficiency must lie above 0 and at most 1.");
                Bus bus = model.Buses.First(b => b.Id == motor.Bus);
                if (bus.Zip is ZipLoad z && z.VariesWithVoltage)
                    throw new InvalidDataException($"start.starter: motor '{motor.Name}' is on bus {motor.Bus}, whose demand varies with voltage; the drive's constant-power line draw is added to that demand, so a vfd start there is refused.");
                break;
        }
    }

    private static long RampSteps(MotorStarter s, double dt)
    {
        if (s.RampSeconds is not double ramp || ramp <= 0) throw new InvalidDataException("start.starter.rampSeconds is required and must be positive.");
        return WholeSteps(ramp, dt, "start.starter.rampSeconds");
    }

    /// <summary>Whole steps in <paramref name="t"/>, rounded up unless already whole (to the same 1e-9 relative test as
    /// <see cref="WholeSteps"/>).</summary>
    private static long CeilSteps(double t, double dt)
    {
        double n = t / dt;
        double k = Math.Round(n);
        return Math.Abs(n - k) <= 1e-9 * Math.Max(1.0, k) ? (long)k : (long)Math.Ceiling(n);
    }

    private static MotorStartRequest MemberRequest(MotorGroupMember m, MotorGroupStartRequest r) =>
        new(m.Motor, m.Initial, m.StartSeconds, m.Method, r.StepSeconds, r.Termination, r.PreStart, r.SampleEverySteps, r.MaxVoltageDipPercent, m.Starter);

    // -------------------------------------------------------------------------------------------------------------------
    // Entry points
    // -------------------------------------------------------------------------------------------------------------------

    /// <summary>Run a group start: each member motor with its own start time and method, on one network.
    /// <paramref name="solverFactory"/> (tests only) replaces the incumbent Newton engine.</summary>
    public static MotorGroupStartResult RunGroup(MotorStartCaseInput input, MotorGroupStartRequest request, Func<GridModel, IPowerFlowSolver>? solverFactory = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Members);
        if (request.Members.Count == 0) throw new InvalidDataException("group: name at least one motor.");
        var motors = new List<PlacedMotor>();
        foreach (MotorGroupMember m in request.Members)
        {
            ArgumentNullException.ThrowIfNull(m);
            if (request.Members.Count(x => string.Equals(x.Motor, m.Motor, StringComparison.Ordinal)) > 1)
                throw new InvalidDataException($"group: motor '{m.Motor}' is listed more than once.");
            motors.Add(Validate(input, MemberRequest(m, request)));
        }
        Func<GridModel, IPowerFlowSolver> factory = solverFactory ?? (m => SolverSelector.CreateNewton(m));
        var method = new MotorStartMethodRecord(ElectricalApproximation, StarterCoupling, Integration, TorqueConvention, request.StepSeconds,
            "Incumbent Newton (SolverSelector.CreateNewton) with ContingencyAnalysis.StudyOptions (100 iterations, reactive limits enforced); no OLTC loop (taps held at their authored ratio over the seconds of a start); every solve starts from the authored bus voltages.",
            "Motors not represented in the pre-start network (disconnected; not static demand).");
        var reasons = new List<string>();
        List<MotorStartGeneratorState> generators = CompareAuthored(input, reasons);
        if (reasons.Count > 0)
            return new MotorGroupStartResult(Label(MotorStartOutcome.PreStartFailed), MotorStartOutcome.PreStartFailed, "resolved model does not hold the authored schedule",
                request, input.Policy, method, generators, null, Array.Empty<MotorGroupMemberResult>(), null, null, 0, reasons);
        MotorStartPreStart pre = VerifyPreStart(input.Model, request.PreStart, factory, out PowerFlowResult? preSolved);
        if (!pre.Passed)
        {
            reasons.AddRange(pre.Discrepancies);
            return new MotorGroupStartResult(Label(MotorStartOutcome.PreStartFailed), MotorStartOutcome.PreStartFailed, "pre-start operating point not reproduced",
                request, input.Policy, method, generators, pre, Array.Empty<MotorGroupMemberResult>(), null, pre.Buses.Count == 0 ? pre.SolverStatus : null, 0, reasons);
        }
        var engine = new StarterEngine(input.Model, motors, request.Members, request.StepSeconds, request.Termination, request.SampleEverySteps,
            request.MaxVoltageDipPercent, factory, preSolved!);
        engine.Run();
        MotorStartOutcome outcome = engine.Members.All(x => x.Outcome == MotorStartOutcome.Running) ? MotorStartOutcome.Running
            : engine.Members.Any(x => x.Outcome == MotorStartOutcome.NonConvergence) ? MotorStartOutcome.NonConvergence
            : engine.Members.Any(x => x.Outcome == MotorStartOutcome.Stalled) ? MotorStartOutcome.Stalled
            : MotorStartOutcome.HorizonEnded;
        reasons.AddRange(engine.GroupReasons);
        if (engine.GroupDip() is { WithinLimit: false } gd)
            reasons.Add($"the deepest voltage dip, {F(gd.WorstDipPercent)} % at bus {gd.WorstBus}, exceeds the declared {F(gd.LimitPercent!.Value)} % (reported; the outcomes are the motors').");
        string termination = string.Join("; ", engine.Members.Select(x => $"{x.Motor.Name}: {Label(x.Outcome!.Value)}"));
        return new MotorGroupStartResult(Label(outcome), outcome, termination, request, input.Policy, method, generators, pre,
            engine.Members.Select(x => x.ToResult(engine)).ToList(), engine.GroupDip(), outcome == MotorStartOutcome.NonConvergence ? engine.FailedStatus : null,
            engine.Solves, reasons);
    }

    private static MotorStartResult RunWithStarter(MotorStartCaseInput input, MotorStartRequest request, PlacedMotor motor, Func<GridModel, IPowerFlowSolver> factory,
        MotorStartMethodRecord method, IReadOnlyList<MotorStartGeneratorState> generators, MotorStartPreStart pre, PowerFlowResult preSolved)
    {
        var member = new MotorGroupMember(request.Motor, request.Initial, request.StartSeconds, request.Method, request.Starter);
        var engine = new StarterEngine(input.Model, new[] { motor }, new[] { member }, request.StepSeconds, request.Termination, request.SampleEverySteps,
            request.MaxVoltageDipPercent, factory, preSolved);
        engine.Run();
        MemberState m = engine.Members[0];
        MotorStartOutcome final = m.Outcome!.Value;
        return new MotorStartResult(Label(final), final, m.Termination, motor, request, input.Policy, method with { Coupling = StarterCoupling }, generators, pre,
            m.Accel, m.Decision, m.Starting, m.Peak, m.Last, m.MotorDip(engine), final == MotorStartOutcome.NonConvergence ? engine.FailedStatus : null,
            engine.Solves, m.Samples, m.AllReasons(engine), m.StarterSamples, m.Transitions);
    }

    // -------------------------------------------------------------------------------------------------------------------
    // The engine
    // -------------------------------------------------------------------------------------------------------------------

    /// <summary>One network evaluation: the solved bus voltages and each member's electrical state.</summary>
    private sealed record GroupEvaluation(double[] Vm, StarterElectrical[] Motors, double[] Factors, double MinVm, int MinBus);

    private sealed class MemberState
    {
        public required PlacedMotor Motor { get; init; }
        public required MotorGroupMember Spec { get; init; }
        public required InductionMachine Circuit { get; init; }
        public required int BusIndex { get; init; }
        public required double KvRatio { get; init; }
        public required double Scale { get; init; }
        public required double BaseKa { get; init; }
        public required long StartStep { get; init; }
        public long SwitchSteps { get; init; } = -1;
        public long DeadSteps { get; init; }
        public long RampSteps { get; init; }
        public double SwitchSpeed { get; init; }
        public double BreakdownSpeed { get; init; }

        public double Speed;
        public bool Switched;
        public long SwitchedAt = -1;
        public long DriveCount;
        public long DrivePausedSteps;
        public StarterStage? Previous;
        public double SpeedAtEvent;
        public double PrevSpeed, PrevT;
        public long? StallSince;
        public MotorStartOutcome? Outcome;
        public string Termination = "";
        public double? Accel, Decision;
        public MotorStartSample? Starting, Peak, Last;
        public double MinMotorVm = double.PositiveInfinity, MinMotorAt;
        public readonly List<MotorStartSample> Samples = new();
        public readonly List<MotorStarterSample> StarterSamples = new();
        public readonly List<MotorStarterTransition> Transitions = new();
        public readonly List<string> Reasons = new();

        public bool Decided => Outcome is not null;

        public List<string> AllReasons(StarterEngine e, bool withDip = true)
        {
            var all = new List<string>(Reasons);
            if (withDip && MotorDip(e) is { WithinLimit: false } d)
                all.Add($"the deepest voltage dip, {F(d.WorstDipPercent)} % at bus {d.WorstBus}, exceeds the declared {F(d.LimitPercent!.Value)} % (reported; the outcome is the motor's).");
            if (SpeedAtEvent != Spec.Initial.SpeedPu)
                all.Add($"the rotor coasted from {F(Spec.Initial.SpeedPu)} pu to {F(SpeedAtEvent)} pu before the start event.");
            if (DrivePausedSteps > 0)
                all.Add($"the drive's frequency ramp paused for {DrivePausedSteps} steps with the motor current above the {F(Spec.Starter!.CurrentLimitPu!.Value)} pu limit.");
            return all;
        }

        public MotorStartVoltageDip? MotorDip(StarterEngine e)
        {
            if (double.IsPositiveInfinity(MinMotorVm)) return null;
            double preVm = e.Pre.Vm[BusIndex];
            (int worst, double worstDip) = e.Worst();
            bool? within = e.DipLimit is double lim ? worstDip <= lim : null;
            return new MotorStartVoltageDip(Motor.Bus, preVm, MinMotorVm, MinMotorAt, (preVm - MinMotorVm) / preVm * 100.0, e.Model.Buses[worst].Id,
                e.Pre.Vm[worst], e.MinByBus[worst], worstDip, e.DipLimit, within);
        }

        public MotorGroupMemberResult ToResult(StarterEngine e) => new(Label(Outcome!.Value), Outcome.Value, Termination, Motor, Spec, Accel, Decision, Starting,
            Peak, Last, MotorDip(e), Samples, StarterSamples, Transitions, AllReasons(e, withDip: false));
    }

    private sealed class StarterEngine
    {
        public GridModel Model { get; }
        public PowerFlowResult Pre { get; }
        public List<MemberState> Members { get; } = new();
        public double[] MinByBus { get; }
        public double? DipLimit { get; }
        public int Solves { get; private set; }
        public string? FailedStatus { get; private set; }
        public List<string> GroupReasons { get; } = new();

        private readonly double[] _minByBusAt;
        private readonly double _dt;
        private readonly MotorStartTermination _term;
        private readonly int _every;
        private readonly long _dwellSteps;
        private readonly Func<GridModel, IPowerFlowSolver> _factory;
        private bool _anyStarted;

        public StarterEngine(GridModel model, IReadOnlyList<PlacedMotor> motors, IReadOnlyList<MotorGroupMember> members, double dt, MotorStartTermination term,
            int every, double? dipLimit, Func<GridModel, IPowerFlowSolver> factory, PowerFlowResult pre)
        {
            Model = model;
            Pre = pre;
            _dt = dt;
            _term = term;
            _every = every;
            DipLimit = dipLimit;
            _factory = factory;
            _dwellSteps = CeilSteps(term.StallDwellSeconds, dt);
            MinByBus = (double[])pre.Vm.Clone();
            _minByBusAt = new double[pre.Vm.Length];
            List<Bus> buses = model.Buses.ToList();
            for (int i = 0; i < members.Count; i++)
            {
                PlacedMotor m = motors[i];
                MotorGroupMember spec = members[i];
                int busIndex = buses.FindIndex(b => b.Id == m.Bus);
                double busKv = buses[busIndex].BaseKv;
                double kvRatio = busKv > 0 ? busKv / m.RatedKv : 1.0;
                MotorStarter? s = spec.Starter;
                Members.Add(new MemberState
                {
                    Motor = m,
                    Spec = spec,
                    Circuit = m.Circuit(),
                    BusIndex = busIndex,
                    KvRatio = kvRatio,
                    Scale = m.RatedMva * kvRatio * kvRatio,
                    BaseKa = m.RatedMva / (Math.Sqrt(3.0) * m.RatedKv),
                    StartStep = (long)Math.Round(spec.StartSeconds / dt),
                    SwitchSteps = s?.SwitchTimeSeconds is double ts ? (long)Math.Round(ts / dt) : -1,
                    DeadSteps = s?.DeadTimeSeconds is double dead ? (long)Math.Round(dead / dt) : 0,
                    RampSteps = s?.RampSeconds is double ramp ? (long)Math.Round(ramp / dt) : 0,
                    SwitchSpeed = s?.SwitchSpeedPu ?? MotorStarter.DefaultSwitchSpeedPu,
                    BreakdownSpeed = StarterMath.BreakdownSpeed(m),
                    Speed = spec.Initial.SpeedPu,
                });
            }
        }

        /// <summary>The bus with the deepest dip so far, relative to its pre-start voltage.</summary>
        public (int Index, double DipPercent) Worst()
        {
            int worst = 0;
            double worstDip = double.NegativeInfinity;
            for (int i = 0; i < MinByBus.Length; i++)
            {
                double d = (Pre.Vm[i] - MinByBus[i]) / Pre.Vm[i] * 100.0;
                if (d > worstDip) { worstDip = d; worst = i; }
            }
            return (worst, worstDip);
        }

        public MotorGroupVoltageDip? GroupDip()
        {
            if (!_anyStarted) return null;
            (int w, double d) = Worst();
            return new MotorGroupVoltageDip(Model.Buses[w].Id, Pre.Vm[w], MinByBus[w], _minByBusAt[w], d, DipLimit, DipLimit is double lim ? d <= lim : null);
        }

        /// <summary>The member's stage at step <paramref name="k"/>; decides a speed- or time-triggered switch.</summary>
        private static StarterStage StageAt(MemberState m, long k)
        {
            if (k < m.StartStep) return StarterStage.Off;
            long n = k - m.StartStep;
            MotorStarter? s = m.Spec.Starter;
            switch (m.Spec.Method)
            {
                case MotorStartMethod.StarDelta:
                case MotorStartMethod.Autotransformer:
                case MotorStartMethod.Reactor:
                    if (!m.Switched && (m.Speed >= m.SwitchSpeed || (m.SwitchSteps > 0 && n >= m.SwitchSteps)))
                    {
                        m.Switched = true;
                        m.SwitchedAt = k;
                    }
                    if (!m.Switched)
                        return m.Spec.Method switch
                        {
                            MotorStartMethod.StarDelta => new StarterStage(StarterStageKind.Transformer, "star", 1.0 / Math.Sqrt(3.0), false),
                            MotorStartMethod.Autotransformer => new StarterStage(StarterStageKind.Transformer, "autotransformer", s!.TapRatio!.Value, false),
                            _ => new StarterStage(StarterStageKind.Reactor, "reactor", 1.0, false, null, s!.ReactorRPu!.Value, s.ReactorXPu!.Value),
                        };
                    if (k < m.SwitchedAt + m.DeadSteps) return StarterStage.Off;
                    return new StarterStage(StarterStageKind.Transformer, m.Spec.Method == MotorStartMethod.StarDelta ? "delta" : "full", 1.0, true);
                case MotorStartMethod.SoftStarter:
                    if (n >= m.RampSteps) return new StarterStage(StarterStageKind.Transformer, "bypassed", 1.0, true);
                    double v0 = s!.InitialVoltageFraction!.Value;
                    return new StarterStage(StarterStageKind.Series, "softStart", v0 + (1.0 - v0) * ((double)n / m.RampSteps), false, s.CurrentLimitPu);
                case MotorStartMethod.Vfd:
                    double f0 = s!.InitialFrequencyHz!.Value, fn = s.RatedFrequencyHz!.Value;
                    double f = m.DriveCount >= m.RampSteps ? fn : f0 + (fn - f0) * ((double)m.DriveCount / m.RampSteps);
                    return new StarterStage(StarterStageKind.Drive, "drive", f / fn, m.DriveCount >= m.RampSteps, s.CurrentLimitPu, 0, 0,
                        s.DriveEfficiency ?? MotorStarter.DefaultDriveEfficiency, f);
                default:
                    return new StarterStage(StarterStageKind.Transformer, "full", 1.0, true);
            }
        }

        /// <summary>Solve the network with every member in its stage at its speed; null when a solve fails.</summary>
        private GroupEvaluation? Evaluate(StarterStage[] stages, double[] speeds)
        {
            int n = Members.Count;
            var factors = new double[n];
            bool connected = false;
            for (int i = 0; i < n; i++)
            {
                factors[i] = stages[i].A;
                connected |= stages[i].Kind != StarterStageKind.Off;
            }
            var motors = new StarterElectrical[n];
            if (!connected)
            {
                for (int i = 0; i < n; i++) motors[i] = StarterMath.Evaluate(StarterStage.Off, Members[i].Circuit, Members[i].Motor.RatedMva, Members[i].KvRatio, Pre.Vm[Members[i].BusIndex], speeds[i]);
                return new GroupEvaluation(Pre.Vm, motors, factors, MinOf(Pre.Vm).Vm, Model.Buses[MinOf(Pre.Vm).Index].Id);
            }
            for (int pass = 0; ; pass++)
            {
                int nb = Model.Buses.Count;
                var dg = new double[nb];
                var db = new double[nb];
                var dp = new double[nb];
                var touched = new bool[nb];
                var hasPd = new bool[nb];
                for (int i = 0; i < n; i++)
                {
                    if (stages[i].Kind == StarterStageKind.Off) continue;
                    MemberState m = Members[i];
                    (double g, double b, double pd) = StarterMath.Load(stages[i] with { A = factors[i] }, m.Circuit, m.Motor.RatedMva, m.Scale, speeds[i]);
                    dg[m.BusIndex] += g;
                    db[m.BusIndex] += b;
                    touched[m.BusIndex] = true;
                    if (stages[i].Kind == StarterStageKind.Drive) { dp[m.BusIndex] += pd; hasPd[m.BusIndex] = true; }
                }
                List<Bus> buses = Model.Buses.ToList();
                for (int j = 0; j < nb; j++)
                {
                    if (!touched[j]) continue;
                    Bus b = buses[j];
                    buses[j] = hasPd[j] ? b with { Gs = b.Gs + dg[j], Bs = b.Bs + db[j], Pd = b.Pd + dp[j] } : b with { Gs = b.Gs + dg[j], Bs = b.Bs + db[j] };
                }
                GridModel stepModel = Model.CopyWith(buses: buses);
                Solves++;
                PowerFlowResult? solved = Solve(stepModel, _factory, out string status);
                if (solved is null) { FailedStatus = status; return null; }
                bool changed = false;
                for (int i = 0; i < n; i++)
                {
                    MemberState m = Members[i];
                    StarterStage st = stages[i] with { A = factors[i] };
                    double vBus = solved.Vm[m.BusIndex];
                    motors[i] = StarterMath.Evaluate(st, m.Circuit, m.Motor.RatedMva, m.KvRatio, vBus, speeds[i]);
                    if (st.Kind == StarterStageKind.Series && st.CurrentLimitPu is double lim && motors[i].LineCurrentPu > lim * (1.0 + StarterMath.CurrentLimitTolerance))
                    {
                        InductionMachineSlipPoint u = InductionMachines.AtSlip(m.Circuit, 1.0, 1.0 - speeds[i]);
                        double y = Math.Sqrt(u.ConductancePu * u.ConductancePu + u.SusceptancePu * u.SusceptancePu);
                        factors[i] = Math.Min(factors[i], lim / (vBus * m.KvRatio * y));
                        changed = true;
                    }
                }
                if (!changed)
                {
                    (double minVm, int minIdx) = MinOf(solved.Vm);
                    return new GroupEvaluation(solved.Vm, motors, factors, minVm, Model.Buses[minIdx].Id);
                }
                if (pass + 1 >= StarterMath.CurrentLimitMaxSolves)
                {
                    FailedStatus = $"soft-starter current limit not settled within {StarterMath.CurrentLimitMaxSolves} network solves";
                    return null;
                }
            }
        }

        private static (double Vm, int Index) MinOf(double[] vm)
        {
            int minIdx = 0;
            for (int i = 1; i < vm.Length; i++) if (vm[i] < vm[minIdx]) minIdx = i;
            return (vm[minIdx], minIdx);
        }

        private static MotorStartSample Sample(MemberState m, double t, double speed, StarterStage st, StarterElectrical e, GroupEvaluation ev)
        {
            double tl = m.Motor.LoadTorque.At(speed);
            double vBus = ev.Vm[m.BusIndex];
            if (st.Kind == StarterStageKind.Off)
                return new MotorStartSample(t, speed, 1.0 - speed, vBus, 0, 0, 0, 0, 0, 0, tl, -tl, ev.MinVm, ev.MinBus);
            return new MotorStartSample(t, speed, e.Slip, vBus, e.TerminalPu, e.LineCurrentPu, e.LineCurrentPu * m.BaseKa, e.PMw, e.QMvar, e.TorquePu, tl,
                e.TorquePu - tl, ev.MinVm, ev.MinBus);
        }

        private static MotorStarterSample StarterSample(MemberState m, double t, StarterStage st, double factor, StarterElectrical e, GroupEvaluation ev)
        {
            if (st.Kind == StarterStageKind.Off) return new MotorStarterSample(t, st.Name, 0, null, 0, 0, 0, 0);
            // A reactor's voltage factor is the motor voltage over the supply voltage it divides.
            double vt = ev.Vm[m.BusIndex] * m.KvRatio;
            double f = st.Kind == StarterStageKind.Reactor ? (vt > 0 ? e.TerminalPu / vt : 0.0) : factor;
            return new MotorStarterSample(t, st.Name, f, st.FrequencyHz, e.TerminalPu, e.MotorCurrentPu, e.LineCurrentPu, e.MotorInputMw);
        }

        public void Run()
        {
            double dt = _dt;
            long horizonStep = (long)Math.Round(_term.HorizonSeconds / dt);
            int n = Members.Count;
            var stages = new StarterStage[n];
            var speeds = new double[n];
            double[] lastVm = Pre.Vm;
            (double preMin, int preMinIdx) = MinOf(Pre.Vm);
            double lastMin = preMin;
            int lastMinBus = Model.Buses[preMinIdx].Id;

            for (long k = 0; ; k++)
            {
                double t = k * dt;
                var switching = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    MemberState m = Members[i];
                    speeds[i] = m.Speed;
                    stages[i] = StageAt(m, k);
                    if (k == m.StartStep)
                    {
                        // The instant before connection, as the direct-on-line study reports it.
                        m.SpeedAtEvent = m.Speed;
                        m.PrevSpeed = m.Speed;
                        m.PrevT = m.Spec.StartSeconds;
                        double v = lastVm[m.BusIndex], tl0 = m.Motor.LoadTorque.At(m.Speed);
                        m.Samples.Add(new MotorStartSample(m.Spec.StartSeconds, m.Speed, 1.0 - m.Speed, v, v * m.KvRatio, 0, 0, 0, 0, 0, tl0, -tl0, lastMin, lastMinBus));
                        m.StarterSamples.Add(new MotorStarterSample(m.Spec.StartSeconds, "off", 0, null, 0, 0, 0, 0));
                        m.MinMotorAt = m.Spec.StartSeconds;
                        _anyStarted = true;
                    }
                    // Recorded also after the motor's outcome is decided (a group runs on): the network still carries the
                    // new stage, so the change is reported, with a note.
                    else if (k > m.StartStep && m.Previous is StarterStage p && (p.Kind != stages[i].Kind || p.Name != stages[i].Name))
                        switching.Add(i);
                }

                // A stage change: the line current just before (the old stage at this same speed) and just after.
                double[]? before = null;
                if (switching.Count > 0)
                {
                    var old = (StarterStage[])stages.Clone();
                    foreach (int i in switching) old[i] = Members[i].Previous!.Value;
                    before = new double[n];
                    if (switching.Any(i => old[i].Kind != StarterStageKind.Off))
                    {
                        GroupEvaluation? eb = Evaluate(old, speeds);
                        if (eb is null) { Fail(t, "network step before a starter stage change", t); return; }
                        for (int i = 0; i < n; i++) before[i] = eb.Motors[i].LineCurrentPu;
                    }
                }

                GroupEvaluation? ev = Evaluate(stages, speeds);
                if (ev is null) { Fail(t, "network step", t); return; }
                if (_anyStarted)
                    for (int j = 0; j < ev.Vm.Length; j++)
                        if (ev.Vm[j] < MinByBus[j]) { MinByBus[j] = ev.Vm[j]; _minByBusAt[j] = t; }

                foreach (int i in switching)
                {
                    MemberState m = Members[i];
                    StarterStage from = m.Previous!.Value;
                    // The current peak is at the connection in delta: straight from star, or after an open-transition dead
                    // time (star -> off -> delta), at the reconnection.
                    string? note = m.Spec.Method == MotorStartMethod.StarDelta && stages[i].Name == "delta" && m.Speed < m.BreakdownSpeed
                        ? from.Name == "star"
                            ? $"transition current peak: switched from star at {F(m.Speed)} pu, below the breakdown speed {F(m.BreakdownSpeed)} pu"
                            : $"transition current peak: reconnected in delta after the dead time at {F(m.Speed)} pu, below the breakdown speed {F(m.BreakdownSpeed)} pu"
                        : null;
                    if (m.Decided)
                        note = (note is null ? "" : note + "; ") + $"after the motor's outcome ({Label(m.Outcome!.Value)}) was decided: the starter still changed stage, and the network carries the new stage";
                    m.Transitions.Add(new MotorStarterTransition(t, m.Speed, from.Name, stages[i].Name, before![i], ev.Motors[i].LineCurrentPu, note));
                    if (note is not null) m.Reasons.Add(note + ".");
                }

                bool allDecided = true;
                for (int i = 0; i < n; i++)
                {
                    MemberState m = Members[i];
                    if (k < m.StartStep) { allDecided = false; continue; }
                    m.Previous = stages[i];
                    if (m.Decided) continue;
                    StarterStage st = stages[i];
                    StarterElectrical e = ev.Motors[i];
                    MotorStartSample s = Sample(m, t, m.Speed, st, e, ev);
                    MotorStarterSample ss = StarterSample(m, t, st, ev.Factors[i], e, ev);
                    m.Last = s;
                    if (st.Kind != StarterStageKind.Off)
                    {
                        m.Starting ??= s;
                        if (m.Peak is null || s.CurrentPu > m.Peak.CurrentPu) m.Peak = s;
                    }
                    if (s.BusVoltagePu < m.MinMotorVm) { m.MinMotorVm = s.BusVoltagePu; m.MinMotorAt = t; }
                    bool report = (k - m.StartStep) % _every == 0;
                    double run = _term.RunningSpeedPu;
                    if (st.Final && m.Speed >= run)
                    {
                        double tc = m.PrevSpeed >= run || m.Speed == m.PrevSpeed ? t : m.PrevT + (t - m.PrevT) * (run - m.PrevSpeed) / (m.Speed - m.PrevSpeed);
                        m.Outcome = MotorStartOutcome.Running;
                        m.Accel = tc - m.Spec.StartSeconds;
                        m.Decision = t;
                        m.Termination = $"speed reached the running speed {F(run)} pu with the starter in its final stage (crossing at {F(tc)} s, first step at or above it {F(t)} s)";
                        m.Samples.Add(s);
                        m.StarterSamples.Add(ss);
                        continue;
                    }
                    double ta = s.AcceleratingTorquePu;
                    if (st.Kind == StarterStageKind.Off) m.StallSince = null;
                    else if (ta <= _term.StallAcceleratingTorquePu)
                    {
                        m.StallSince ??= k;
                        if (k - m.StallSince.Value >= _dwellSteps)
                        {
                            m.Outcome = MotorStartOutcome.Stalled;
                            m.Decision = t;
                            m.Termination = $"accelerating torque at or below {F(_term.StallAcceleratingTorquePu)} pu from {F(m.StallSince.Value * dt)} s to {F(t)} s (dwell {F(_term.StallDwellSeconds)} s, {_dwellSteps} steps) at speed {F(m.Speed)} pu, below the running speed {F(run)} pu";
                            m.Samples.Add(s);
                            m.StarterSamples.Add(ss);
                            continue;
                        }
                    }
                    else m.StallSince = null;
                    if (k >= horizonStep)
                    {
                        m.Outcome = MotorStartOutcome.HorizonEnded;
                        m.Decision = t;
                        m.Termination = $"time horizon {F(_term.HorizonSeconds)} s ended at speed {F(m.Speed)} pu (stage {st.Name}), without reaching the running speed {F(run)} pu in the final stage or meeting the stall criterion";
                        m.Samples.Add(s);
                        m.StarterSamples.Add(ss);
                        continue;
                    }
                    allDecided = false;
                    if (report)
                    {
                        m.Samples.Add(s);
                        m.StarterSamples.Add(ss);
                    }
                }
                if (allDecided) return;
                lastVm = ev.Vm;
                lastMin = ev.MinVm;
                lastMinBus = ev.MinBus;

                // Heun for every member (a disconnected one coasts under its load), the predictor solved with the same stages.
                var ta0 = new double[n];
                var pred = new double[n];
                var held = new bool[n];
                bool needPredictor = false;
                for (int i = 0; i < n; i++)
                {
                    MemberState m = Members[i];
                    double tl = m.Motor.LoadTorque.At(m.Speed);
                    ta0[i] = stages[i].Kind == StarterStageKind.Off ? -tl : ev.Motors[i].TorquePu - tl;
                    held[i] = m.Speed <= 0 && ta0[i] <= 0;
                    double twoH = 2.0 * m.Motor.InertiaH;
                    pred[i] = held[i] ? m.Speed : Math.Max(0.0, m.Speed + dt * ta0[i] / twoH);
                    needPredictor |= !held[i] && stages[i].Kind != StarterStageKind.Off;
                }
                GroupEvaluation? ep = null;
                if (needPredictor)
                {
                    ep = Evaluate(stages, pred);
                    if (ep is null) { Fail(t + dt, "predictor network step", t + dt); return; }
                }
                for (int i = 0; i < n; i++)
                {
                    MemberState m = Members[i];
                    double twoH = 2.0 * m.Motor.InertiaH;
                    double next;
                    if (held[i]) next = 0;
                    else
                    {
                        double tap = stages[i].Kind == StarterStageKind.Off ? -m.Motor.LoadTorque.At(pred[i]) : ep!.Motors[i].TorquePu - m.Motor.LoadTorque.At(pred[i]);
                        next = Math.Max(0.0, m.Speed + dt * (ta0[i] + tap) / (2.0 * twoH));
                    }
                    if (stages[i].Kind == StarterStageKind.Drive && !stages[i].Final)
                    {
                        if (stages[i].CurrentLimitPu is double lim && ev.Motors[i].MotorCurrentPu > lim) m.DrivePausedSteps++;
                        else m.DriveCount++;
                    }
                    if (k >= m.StartStep)
                    {
                        m.PrevSpeed = m.Speed;
                        m.PrevT = t;
                    }
                    m.Speed = next;
                }
            }
        }

        private void Fail(double t, string what, double decision)
        {
            foreach (MemberState m in Members)
            {
                if (m.Decided) continue;
                m.Outcome = MotorStartOutcome.NonConvergence;
                m.Decision = decision;
                m.Termination = $"{what} at {F(t)} s (speed {F(m.Speed)} pu) has no converged solution: {FailedStatus}";
                m.Reasons.Add(m.Termination + "; a failed network step is a failed evaluation, not a motor verdict.");
            }
            GroupReasons.Add($"{what} at {F(t)} s has no converged solution: {FailedStatus}.");
        }
    }
}
