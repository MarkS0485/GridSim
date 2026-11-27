using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Analysis.MotorStart;

/// <summary>
/// The reduced-voltage starter of a motor start (<see cref="MotorStartMethod"/> other than direct-on-line). Only the
/// fields of the chosen method may be given; the others must be null, and a field of another method is refused by name.
/// Every time is a whole number of integration steps.
///
/// <list type="bullet">
/// <item><b>StarDelta</b>: <see cref="SwitchSpeedPu"/> (default 0.8), optional <see cref="SwitchTimeSeconds"/> (whichever
/// comes first, timed from the start event), <see cref="DeadTimeSeconds"/> (default 0: the open-transition gap during which
/// the motor is disconnected and coasts under its load).</item>
/// <item><b>Autotransformer</b>: <see cref="TapRatio"/> k (0 &lt; k &lt; 1), switch speed and time as star-delta. The
/// switch to full voltage is instantaneous (the Korndorfer closed transition approximated as a step).</item>
/// <item><b>Reactor</b>: <see cref="ReactorRPu"/> + j <see cref="ReactorXPu"/>, pu on the motor base (see
/// <see cref="ReactorFromOhms"/>), switch speed and time as star-delta.</item>
/// <item><b>SoftStarter</b>: <see cref="InitialVoltageFraction"/> V0, <see cref="RampSeconds"/>, optional
/// <see cref="CurrentLimitPu"/> (pu of the motor's rated current). Bypassed at the end of the ramp.</item>
/// <item><b>Vfd</b>: <see cref="InitialFrequencyHz"/> f0, <see cref="RatedFrequencyHz"/> fn, <see cref="RampSeconds"/>,
/// <see cref="DriveEfficiency"/> (default 0.97), optional <see cref="CurrentLimitPu"/> (the ramp pauses while the motor
/// current is above it).</item>
/// </list>
/// </summary>
public sealed record MotorStarter(
    MotorStartMethod Method,
    double? SwitchSpeedPu = null,
    double? SwitchTimeSeconds = null,
    double? DeadTimeSeconds = null,
    double? TapRatio = null,
    double? ReactorRPu = null,
    double? ReactorXPu = null,
    double? InitialVoltageFraction = null,
    double? RampSeconds = null,
    double? CurrentLimitPu = null,
    double? InitialFrequencyHz = null,
    double? RatedFrequencyHz = null,
    double? DriveEfficiency = null)
{
    /// <summary>The default switch speed of star-delta, autotransformer and reactor starters (pu of synchronous speed).</summary>
    public const double DefaultSwitchSpeedPu = 0.8;

    /// <summary>The default drive efficiency of a VFD.</summary>
    public const double DefaultDriveEfficiency = 0.97;

    /// <summary>A series reactor given in ohms (per phase, star equivalent) as pu on the motor base
    /// Z_base = RatedKv^2 / RatedMva.</summary>
    public static (double RPu, double XPu) ReactorFromOhms(PlacedMotor motor, double rOhm, double xOhm)
    {
        ArgumentNullException.ThrowIfNull(motor);
        double zBase = motor.RatedKv * motor.RatedKv / motor.RatedMva;
        return (rOhm / zBase, xOhm / zBase);
    }
}

/// <summary>
/// The starter's own state at one reported instant (one per <see cref="MotorStartSample"/>, same time). Stage is
/// <c>off</c> (not yet started or in the open-transition dead time), <c>star</c>, <c>delta</c>, <c>autotransformer</c>,
/// <c>reactor</c>, <c>softStart</c>, <c>bypassed</c>, <c>full</c> (switched to full voltage) or <c>drive</c>.
/// <see cref="VoltageFactor"/> is the motor voltage over the supply voltage (star 1/sqrt(3), tap k, the soft-starter
/// fraction after any current limit, the drive's f/fn; 1 at full voltage). The motor current and voltage are on the
/// motor's own terminals; the line current is drawn from the bus. Both currents are pu of the motor's rated current.
/// </summary>
public sealed record MotorStarterSample(double TimeSeconds, string Stage, double VoltageFactor, double? FrequencyHz,
    double MotorTerminalVoltagePu, double MotorCurrentPu, double LineCurrentPu, double MotorInputMw);

/// <summary>A starter stage change: the time and speed, the stages, and the line current at that same speed just before
/// and just after (pu of rated; 0 when disconnected). <see cref="Note"/> is "transition current peak" when a star-delta
/// switch happens below the breakdown speed.</summary>
public sealed record MotorStarterTransition(double TimeSeconds, double SpeedPu, string FromStage, string ToStage,
    double LineCurrentBeforePu, double LineCurrentAfterPu, string? Note);

/// <summary>One motor of a group start: its own initial state, start time, method and starter.</summary>
public sealed record MotorGroupMember(string Motor, MotorStartInitialState Initial, double StartSeconds, MotorStartMethod Method,
    MotorStarter? Starter = null);

/// <summary>A group start: several placed motors, each with its own start time and method, on one network solved each
/// step with every connected motor. The step, termination criteria, pre-start tolerances and reporting are shared.</summary>
public sealed record MotorGroupStartRequest(
    IReadOnlyList<MotorGroupMember> Members,
    double StepSeconds,
    MotorStartTermination Termination,
    MotorStartPreStartTolerance PreStart,
    int SampleEverySteps = 1,
    double? MaxVoltageDipPercent = null);

/// <summary>The deepest voltage dip of a group start at any bus, relative to that bus's own pre-start voltage.</summary>
public sealed record MotorGroupVoltageDip(int WorstBus, double WorstPreStartVmPu, double WorstMinimumVmPu, double WorstMinimumAtSeconds,
    double WorstDipPercent, double? LimitPercent, bool? WithinLimit);

/// <summary>One motor's result in a group start, with the same meanings as <see cref="MotorStartResult"/>.</summary>
public sealed record MotorGroupMemberResult(
    string Status,
    MotorStartOutcome Outcome,
    string Termination,
    PlacedMotor Motor,
    MotorGroupMember Member,
    double? AccelerationTimeSeconds,
    double? DecisionTimeSeconds,
    MotorStartSample? StartingState,
    MotorStartSample? PeakCurrentState,
    MotorStartSample? FinalState,
    MotorStartVoltageDip? VoltageDip,
    IReadOnlyList<MotorStartSample> Samples,
    IReadOnlyList<MotorStarterSample> StarterSamples,
    IReadOnlyList<MotorStarterTransition> Transitions,
    IReadOnlyList<string> Reasons);

/// <summary>
/// The group start result. <see cref="Outcome"/> is RUNNING only when every motor is RUNNING; otherwise NO SOLUTION if a
/// network step failed, STALLED if any motor stalled, else HORIZON ENDED; INCOMPLETE when the pre-start point failed.
/// </summary>
public sealed record MotorGroupStartResult(
    string Status,
    MotorStartOutcome Outcome,
    string Termination,
    MotorGroupStartRequest Request,
    MotorStartLoadPolicy Policy,
    MotorStartMethodRecord Method,
    IReadOnlyList<MotorStartGeneratorState> Generators,
    MotorStartPreStart? PreStart,
    IReadOnlyList<MotorGroupMemberResult> Motors,
    MotorGroupVoltageDip? VoltageDip,
    string? FailedSolverStatus,
    int NetworkSolves,
    IReadOnlyList<string> Reasons);

/// <summary>The kind of a starter stage.</summary>
internal enum StarterStageKind
{
    /// <summary>Disconnected: no electrical torque, no current.</summary>
    Off,
    /// <summary>An ideal transformer of ratio <see cref="StarterStage.A"/> (full voltage at 1, star at 1/sqrt(3), autotransformer at k).</summary>
    Transformer,
    /// <summary>A series impedance between the bus and the motor.</summary>
    Reactor,
    /// <summary>A series voltage controller (soft starter) at fraction <see cref="StarterStage.A"/>.</summary>
    Series,
    /// <summary>A drive at frequency ratio <see cref="StarterStage.A"/> = f/fn.</summary>
    Drive,
}

/// <summary>One starter stage, held over a step (zero-order hold).</summary>
internal readonly record struct StarterStage(StarterStageKind Kind, string Name, double A, bool Final, double? CurrentLimitPu = null,
    double ReactorR = 0, double ReactorX = 0, double Efficiency = 1, double? FrequencyHz = null)
{
    public static readonly StarterStage Off = new(StarterStageKind.Off, "off", 0, false);
}

/// <summary>A motor's electrical state in a stage at a solved bus voltage.</summary>
internal readonly record struct StarterElectrical(double Slip, double TerminalPu, double MotorCurrentPu, double LineCurrentPu,
    double TorquePu, double PMw, double QMvar, double MotorInputMw);

/// <summary>
/// The starter arithmetic: the load a motor in a stage puts on its bus (a shunt admittance, or a constant active power for
/// a drive), and the motor's state at the solved bus voltage. Plain arithmetic and Math.Sqrt only.
///
/// <para>Closed forms on a supply of V (pu of the motor rating), with Y(s) = G + jB the motor's input admittance at 1 pu
/// (<see cref="InductionMachines.AtSlip"/>):</para>
/// <list type="bullet">
/// <item>Transformer of ratio a (star a = 1/sqrt(3); autotransformer a = k; full voltage a = 1): the motor sees aV, its
/// current is aV|Y|, the line current a times that, and the bus sees the admittance a^2 Y. Star-delta is this with
/// a = 1/sqrt(3): the winding sees V/sqrt(3), and the line current is a third of the delta (direct-on-line) current.</item>
/// <item>Reactor Zr: the bus sees 1 / (1/Y + Zr); the motor voltage is V |Y_eff| / |Y|; the line current is the motor
/// current.</item>
/// <item>Soft starter at fraction a: a series controller, so the line current is the motor current aV|Y|; it is lossless,
/// so the active power is the motor's, a^2 V^2 G; the rest of the apparent power V I is reactive (the phase-control
/// displacement). The bus therefore sees G' = a^2 G and B' = -sqrt(a^2 |Y|^2 - a^4 G^2).</item>
/// <item>Drive at ratio phi = f/fn: the motor sees phi (pu of rated voltage) at frequency f, its reactances scaled by phi
/// and its slip (phi - w)/phi; torque = air-gap power / phi. The line side draws P_motor / efficiency at unity
/// displacement power factor, a constant-power load at the bus.</item>
/// </list>
/// </summary>
internal static class StarterMath
{
    /// <summary>The soft-starter current limit is met to this relative tolerance: the fixed point a = I_lim / (V |Y|) is
    /// approached from above as the bus voltage recovers, and iterated until the line current is at most
    /// I_lim (1 + this).</summary>
    public const double CurrentLimitTolerance = 1e-9;

    /// <summary>The most network solves one evaluation may spend settling soft-starter current limits.</summary>
    public const int CurrentLimitMaxSolves = 100;

    /// <summary>The breakdown (pull-out torque) speed of the single-cage circuit at rated frequency, from the stator
    /// Thevenin equivalent: s_max = R1 / sqrt(Rth^2 + (Xth + X1)^2) (Chapman, Electric Machinery Fundamentals, ch. 6,
    /// slip at the pull-out torque), speed = 1 - s_max.</summary>
    public static double BreakdownSpeed(PlacedMotor m)
    {
        // Zth = jXm (Ra + jXa) / (Ra + j(Xa + Xm)).
        double nr = -m.XmPu * m.XaPu, ni = m.XmPu * m.RaPu;
        double dr = m.RaPu, di = m.XaPu + m.XmPu;
        double dd = dr * dr + di * di;
        double rth = (nr * dr + ni * di) / dd, xth = (ni * dr - nr * di) / dd;
        double x = xth + m.X1Pu;
        return 1.0 - m.R1Pu / Math.Sqrt(rth * rth + x * x);
    }

    /// <summary>The motor's circuit at frequency ratio <paramref name="phi"/>: reactances times phi.</summary>
    public static InductionMachine AtFrequency(InductionMachine c, double phi) =>
        c with { XaPu = c.XaPu * phi, XmPu = c.XmPu * phi, X1Pu = c.X1Pu * phi };

    /// <summary>The drive slip relative to the supply frequency: (phi - w) / phi.</summary>
    public static double DriveSlip(double phi, double speed) => (phi - speed) / phi;

    /// <summary>The load a motor in <paramref name="st"/> at <paramref name="speed"/> puts on its bus: shunt G and B in
    /// MW and MVAr at 1 pu bus voltage (<paramref name="scale"/> = ratedMva (busKv/ratedKv)^2), and a constant active
    /// demand in MW.</summary>
    public static (double G, double B, double Pd) Load(StarterStage st, InductionMachine circuit, double ratedMva, double scale, double speed)
    {
        double slip = 1.0 - speed;
        switch (st.Kind)
        {
            case StarterStageKind.Transformer:
            {
                InductionMachineSlipPoint u = InductionMachines.AtSlip(circuit, 1.0, slip);
                double y = st.A * st.A;
                return (scale * (y * u.ConductancePu), scale * (y * u.SusceptancePu), 0);
            }
            case StarterStageKind.Reactor:
            {
                (double g, double b) = ReactorAdmittance(circuit, slip, st.ReactorR, st.ReactorX);
                return (scale * g, scale * b, 0);
            }
            case StarterStageKind.Series:
            {
                InductionMachineSlipPoint u = InductionMachines.AtSlip(circuit, 1.0, slip);
                (double g, double b) = SeriesAdmittance(u.ConductancePu, u.SusceptancePu, st.A);
                return (scale * g, scale * b, 0);
            }
            case StarterStageKind.Drive:
            {
                InductionMachineSlipPoint p = InductionMachines.AtSlip(AtFrequency(circuit, st.A), st.A, DriveSlip(st.A, speed));
                return (0, 0, p.PPu / st.Efficiency * ratedMva);
            }
            default:
                return (0, 0, 0);
        }
    }

    /// <summary>1 / (1/(G + jB) + (R + jX)).</summary>
    public static (double G, double B) ReactorAdmittance(InductionMachine circuit, double slip, double r, double x)
    {
        InductionMachineSlipPoint u = InductionMachines.AtSlip(circuit, 1.0, slip);
        double g = u.ConductancePu, b = u.SusceptancePu;
        double y2 = g * g + b * b;
        double zr = g / y2 + r, zi = -b / y2 + x;
        double z2 = zr * zr + zi * zi;
        return (zr / z2, -zi / z2);
    }

    /// <summary>The soft starter's bus admittance at fraction <paramref name="a"/>: G' = a^2 G, B' = -sqrt(a^2 |Y|^2 - a^4 G^2).</summary>
    public static (double G, double B) SeriesAdmittance(double g, double b, double a)
    {
        double a2 = a * a;
        double gs = a2 * g;
        double s2 = a2 * (g * g + b * b);
        return (gs, -Math.Sqrt(Math.Max(0.0, s2 - gs * gs)));
    }

    /// <summary>The motor's state in <paramref name="st"/> at <paramref name="speed"/> with the bus solved at
    /// <paramref name="vBusPu"/> (pu of the bus base; <paramref name="kvRatio"/> = busKv / ratedKv).</summary>
    public static StarterElectrical Evaluate(StarterStage st, InductionMachine circuit, double ratedMva, double kvRatio, double vBusPu, double speed)
    {
        double slip = 1.0 - speed;
        double vt = vBusPu * kvRatio;
        switch (st.Kind)
        {
            case StarterStageKind.Transformer:
            {
                InductionMachineSlipPoint p = InductionMachines.AtSlip(circuit, st.A * vt, slip);
                return new StarterElectrical(slip, p.VoltagePu, p.CurrentPu, st.A * p.CurrentPu, p.TorquePu, p.PPu * ratedMva, p.QPu * ratedMva, p.PPu * ratedMva);
            }
            case StarterStageKind.Reactor:
            {
                InductionMachineSlipPoint u = InductionMachines.AtSlip(circuit, 1.0, slip);
                (double ge, double be) = ReactorAdmittance(circuit, slip, st.ReactorR, st.ReactorX);
                double ym = Math.Sqrt(u.ConductancePu * u.ConductancePu + u.SusceptancePu * u.SusceptancePu);
                double yeff = Math.Sqrt(ge * ge + be * be);
                InductionMachineSlipPoint p = InductionMachines.AtSlip(circuit, vt * yeff / ym, slip);
                double v2 = vt * vt;
                return new StarterElectrical(slip, p.VoltagePu, p.CurrentPu, p.CurrentPu, p.TorquePu, v2 * ge * ratedMva, -v2 * be * ratedMva, p.PPu * ratedMva);
            }
            case StarterStageKind.Series:
            {
                InductionMachineSlipPoint p = InductionMachines.AtSlip(circuit, st.A * vt, slip);
                double sMag = vt * p.CurrentPu;
                double q = Math.Sqrt(Math.Max(0.0, sMag * sMag - p.PPu * p.PPu));
                return new StarterElectrical(slip, p.VoltagePu, p.CurrentPu, p.CurrentPu, p.TorquePu, p.PPu * ratedMva, q * ratedMva, p.PPu * ratedMva);
            }
            case StarterStageKind.Drive:
            {
                double ds = DriveSlip(st.A, speed);
                InductionMachineSlipPoint p = InductionMachines.AtSlip(AtFrequency(circuit, st.A), st.A, ds);
                double pLine = p.PPu / st.Efficiency;
                return new StarterElectrical(ds, p.VoltagePu, p.CurrentPu, vt > 0 ? pLine / vt : 0.0, p.AirGapPu / st.A, pLine * ratedMva, 0.0, p.PPu * ratedMva);
            }
            default:
                return new StarterElectrical(slip, 0, 0, 0, 0, 0, 0, 0);
        }
    }
}
