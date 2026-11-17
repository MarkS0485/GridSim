using GridSim.Core.Model;

namespace GridSim.Core.Analysis.MotorStart;

/// <summary>RC3 P8: the starting method. Direct-on-line only in this revision (the motor's terminals are connected to
/// its bus at the start event, with no starter impedance, autotransformer or soft-starter).</summary>
public enum MotorStartMethod
{
    /// <summary>Direct-on-line: full network voltage at the start event.</summary>
    DirectOnLine,
}

/// <summary>RC3 P8: how a motor start ended. A converged electrical state does not by itself establish a successful
/// acceleration: only <see cref="Running"/> does.</summary>
public enum MotorStartOutcome
{
    /// <summary>The rotor reached the declared running speed (RUNNING).</summary>
    Running,
    /// <summary>The accelerating torque stayed at or below the declared stall margin for the declared dwell while the rotor
    /// was below running speed (STALLED): locked at standstill, decelerating, or crawling at an equilibrium below the
    /// running speed.</summary>
    Stalled,
    /// <summary>A network step had no converged solution (NO SOLUTION): numerical non-convergence, reported with the
    /// incumbent solver's exact status. It says nothing about the motor.</summary>
    NonConvergence,
    /// <summary>The time horizon ended before the motor reached running speed or met the stall criterion (HORIZON ENDED):
    /// no decision.</summary>
    HorizonEnded,
    /// <summary>The pre-start operating point was not reproduced (or the resolved model does not hold the authored point),
    /// so no event was run (INCOMPLETE).</summary>
    PreStartFailed,
}

/// <summary>RC3 P8: the motor's state before the start event. A start from rest is speed 0, disconnected. A restart of a
/// coasting motor gives its speed (below the running speed) and is also disconnected until the event. A motor already
/// connected before the event (a running restart or a post-start equilibrium) is not a start and is refused.</summary>
public sealed record MotorStartInitialState(double SpeedPu, bool Connected);

/// <summary>RC3 P8: the termination criteria. RUNNING when the speed reaches <paramref name="RunningSpeedPu"/>; STALLED
/// when the accelerating torque Te - Tl stays at or below <paramref name="StallAcceleratingTorquePu"/> for
/// <paramref name="StallDwellSeconds"/> while below running speed (a margin of 0 catches only a locked or decelerating
/// rotor; a small positive margin also catches a crawl that approaches an equilibrium below running speed); HORIZON ENDED
/// at <paramref name="HorizonSeconds"/> (simulation time from 0) without either.</summary>
public sealed record MotorStartTermination(double RunningSpeedPu, double HorizonSeconds, double StallDwellSeconds, double StallAcceleratingTorquePu);

/// <summary>RC3 P8: the pre-start acceptance tolerances. Each non-slack generating bus's realised active output must equal
/// the resolved schedule within <paramref name="PgToleranceMw"/>, each voltage-controlled bus must hold its setpoint
/// within <paramref name="VmTolerancePu"/>, and the slack bus's movement from its scheduled output (losses and mismatch)
/// must be within <paramref name="MaxSlackMovementMw"/>.</summary>
public sealed record MotorStartPreStartTolerance(double PgToleranceMw, double VmTolerancePu, double MaxSlackMovementMw);

/// <summary>
/// RC3 P8: the run-specific setup of one motor start (the native model holds the motor itself): which motor, its initial
/// state, the start event time and method, the integration step, the termination criteria and the pre-start
/// tolerances. <paramref name="StartSeconds"/> and <paramref name="Termination"/>'s horizon must be whole multiples of
/// <paramref name="StepSeconds"/>. <paramref name="SampleEverySteps"/> thins the reported trajectory (the first connected
/// step and the terminal step are always reported). <paramref name="MaxVoltageDipPercent"/>, when given, is a reported
/// acceptance flag on the largest voltage dip; it does not change the outcome, which is the motor's.
/// </summary>
public sealed record MotorStartRequest(
    string Motor,
    MotorStartInitialState Initial,
    double StartSeconds,
    MotorStartMethod Method,
    double StepSeconds,
    MotorStartTermination Termination,
    MotorStartPreStartTolerance PreStart,
    int SampleEverySteps = 1,
    double? MaxVoltageDipPercent = null);

/// <summary>RC3 P8: one generator as AUTHORED in the native case file (before any loader decision): the explicit output
/// and bounds the fixed-point study requires.</summary>
public sealed record MotorStartAuthoredGenerator(string Name, int Bus, double OutputMw, double PMinMw, double PMaxMw, bool InService);

/// <summary>RC3 P8: how the case was loaded. <paramref name="AuthoredSystemDispatch"/> is the case's own
/// <c>system.dispatch</c> (null when absent); <paramref name="LoaderDispatch"/> the dispatch argument passed at the loader
/// seam; <paramref name="EffectiveDispatch"/> whether loader balancing ran (both must allow it).</summary>
public sealed record MotorStartLoadPolicy(string Format, bool? AuthoredSystemDispatch, bool LoaderDispatch, bool EffectiveDispatch, IReadOnlyList<string> Notes);

/// <summary>RC3 P8: a loaded native case for the motor study: the resolved model, its placed motors (carried beside the
/// model), the authored generator schedule and the load policy.</summary>
public sealed record MotorStartCaseInput(GridModel Model, IReadOnlyList<PlacedMotor> Motors,
    IReadOnlyList<MotorStartAuthoredGenerator> AuthoredGenerators, MotorStartLoadPolicy Policy);

/// <summary>RC3 P8: one generator across the first two states: authored output and the resolved model's output.</summary>
public sealed record MotorStartGeneratorState(string Name, int Bus, double AuthoredMw, double? ResolvedMw, double? DifferenceMw, bool Matches);

/// <summary>RC3 P8: one bus of the realised pre-start AC solution: its solved voltage and type, the resolved schedule of
/// its generators, the realised active output and its movement, and the verdict of each check that applies.</summary>
public sealed record MotorStartBusState(int Bus, string ModelType, string SolvedType, double VmPu, double VaDeg,
    double? ScheduledPgMw, double? RealisedPgMw, double? MovementMw, double? RealisedQgMvar, double? SetpointVmPu, IReadOnlyList<string> Failures);

/// <summary>RC3 P8: the pre-start verification (motor disconnected): the solver status and residual, the per-bus
/// realised state, the slack movement and every discrepancy.</summary>
public sealed record MotorStartPreStart(bool Passed, string SolverStatus, double? MaxMismatchPu, int? Iterations,
    double? LossMw, int? SlackBus, double? SlackMovementMw, IReadOnlyList<MotorStartBusState> Buses, IReadOnlyList<string> Discrepancies);

/// <summary>RC3 P8: one reported instant of the start. Voltages: the bus in pu of its base, the motor terminal in pu of
/// the motor's rated voltage. Current in pu of the motor's rated current and in kA. P and Q drawn by the motor (MW,
/// MVAr, Q positive absorbed). Torques in pu of the motor torque base; accelerating = electrical - load.</summary>
public sealed record MotorStartSample(double TimeSeconds, double SpeedPu, double Slip, double BusVoltagePu, double TerminalVoltagePu,
    double CurrentPu, double CurrentKa, double PMw, double QMvar, double ElectricalTorquePu, double LoadTorquePu, double AcceleratingTorquePu,
    double MinBusVoltagePu, int MinVoltageBus);

/// <summary>RC3 P8: the voltage dip over the start: at the motor bus and the deepest at any bus, each relative to that
/// bus's own pre-start voltage (percent of the pre-start value).</summary>
public sealed record MotorStartVoltageDip(int MotorBus, double PreStartVmPu, double MinimumVmPu, double MinimumAtSeconds, double DipPercent,
    int WorstBus, double WorstPreStartVmPu, double WorstMinimumVmPu, double WorstDipPercent, double? LimitPercent, bool? WithinLimit);

/// <summary>RC3 P8: the declared method of the study, recorded with every result.</summary>
public sealed record MotorStartMethodRecord(string ElectricalApproximation, string Coupling, string Integration, string TorqueConvention,
    double StepSeconds, string Solver, string PreStartRepresentation);

/// <summary>
/// RC3 P8: the motor start result. <see cref="Status"/> is RUNNING, STALLED, NO SOLUTION, HORIZON ENDED or INCOMPLETE
/// (<see cref="Outcome"/>), with the termination criterion met in <see cref="Termination"/>. The three states are kept
/// separately: <see cref="Generators"/> (authored against resolved) and <see cref="PreStart"/> (the realised AC
/// solution). Every measurement that was not taken is null, never zero.
/// </summary>
public sealed record MotorStartResult(
    string Status,
    MotorStartOutcome Outcome,
    string Termination,
    PlacedMotor Motor,
    MotorStartRequest Request,
    MotorStartLoadPolicy Policy,
    MotorStartMethodRecord Method,
    IReadOnlyList<MotorStartGeneratorState> Generators,
    MotorStartPreStart? PreStart,
    double? AccelerationTimeSeconds,
    double? DecisionTimeSeconds,
    MotorStartSample? StartingState,
    MotorStartSample? PeakCurrentState,
    MotorStartSample? FinalState,
    MotorStartVoltageDip? VoltageDip,
    string? FailedSolverStatus,
    int NetworkSolves,
    IReadOnlyList<MotorStartSample> Samples,
    IReadOnlyList<string> Reasons);
