using GridSim.Core.Model;
using GridSim.Core.PowerFlow;

namespace GridSim.Core.Protection;

/// <summary>
/// A passive protection-simulation overlay. Given a SOLVED state it evaluates a library of relay elements
/// (voltage 27/59, frequency 81U/81O, RoCoF 81R, loss-of-mains 78, over/under/thermal current 50/51/37/49,
/// distance 21, reverse power 32, busbar differential 87, and honestly-not-checkable earth/negative-sequence
/// 50N/51N/21N/46) and reports where each WOULD operate and why - so a run can conclude, without ever
/// altering the solve, "protection simulation suggests relay 51 on branch X would operate (2.1x rated,
/// trips in 1.3 s)".
///
/// It is deliberately not a certified protection study - real relay settings are unobtainable, so the
/// pickups are generic standard-practice values (grounded only for the statutory frequency band and the
/// modern RoCoF limit), and elements a positive-sequence model cannot evaluate are reported not-checkable
/// rather than fabricated. It never calls the solver or mutates the model/result.
/// </summary>
public static class ProtectionAssessment
{
    /// <summary>Assess a solved state. <paramref name="settings"/> overrides the generic pickups;
    /// <paramref name="trajectory"/> (from a dynamic run) enables the frequency-derivative elements;
    /// <paramref name="faultCurrentPuByBus"/> (from <see cref="FaultAnalysis"/>) lets the busbar
    /// differential see a fault within its zone.</summary>
    public static ProtectionReport Assess(
        GridModel model, PowerFlowResult result,
        ProtectionSettings? settings = null, ProtectionTrajectory? trajectory = null,
        IReadOnlyDictionary<int, double>? faultCurrentPuByBus = null, UnbalancedFaultResult? sequence = null)
    {
        result.RequireConverged("Protection assessment");   // the convergence contract (October 2026, WS4)
        ProtectionSettings s = settings ?? new ProtectionSettings();
        var ctx = new ProtectionContext(model, result, s, trajectory, faultCurrentPuByBus, sequence);
        var verdicts = new List<RelayVerdict>();
        foreach (IProtectionRelay relay in ProtectionRegistry.All)
            verdicts.AddRange(relay.Evaluate(ctx));
        return new ProtectionReport(verdicts, s, trajectory is not null);
    }

    /// <summary>
    /// D-DOC-220 (October 2026): assess the SYSTEM frequency elements - 81U, 81O, 81R and 78 - on a dynamic run's
    /// trajectory (for example <see cref="ProtectionTrajectory.FromFrequencyResponse"/> of a `trip` study). These four
    /// elements read nothing but the trajectory and the settings, so they need no solved network; the verdicts are the
    /// very ones <see cref="Assess"/> gives with the same trajectory (one shared function per element). 78 stays not
    /// checkable on a trajectory with no vector shift (a centre-of-inertia run).
    ///
    /// <para><paramref name="systemNominalHz"/> (D2 nominal frequency, October 2026): the nominal frequency the trajectory
    /// ran about (the GB 50 Hz by default, every existing caller). When it differs from the settings'
    /// <see cref="ProtectionSettings.SettingsNominalHz"/>, 81U and 81O are not checkable (their absolute pickups are for
    /// another system); 81R and 78 are judged as before.</para>
    /// </summary>
    public static ProtectionReport AssessTrajectory(ProtectionTrajectory trajectory, ProtectionSettings? settings = null,
        double systemNominalHz = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz)
    {
        ArgumentNullException.ThrowIfNull(trajectory);
        if (!double.IsFinite(trajectory.MinFrequencyHz) || !double.IsFinite(trajectory.MaxFrequencyHz) || !double.IsFinite(trajectory.MaxAbsRocofHzPerS))
            throw new ArgumentException(
                $"The trajectory must carry finite frequencies and RoCoF (got min {trajectory.MinFrequencyHz} Hz, max {trajectory.MaxFrequencyHz} Hz, peak RoCoF {trajectory.MaxAbsRocofHzPerS} Hz/s).",
                nameof(trajectory));
        ProtectionSettings s = settings ?? new ProtectionSettings();
        // The operating frequency is never read when a trajectory is given (each element takes the run's extreme).
        var verdicts = new List<RelayVerdict>
        {
            UnderFrequencyRelay.Verdict(trajectory, double.NaN, s, systemNominalHz),
            OverFrequencyRelay.Verdict(trajectory, double.NaN, s, systemNominalHz),
            RocofRelay.Verdict(trajectory, s),
            VectorShiftLomRelay.Verdict(trajectory, s),
        };
        return new ProtectionReport(verdicts, s, HadTrajectory: true);
    }

    /// <summary>Convenience: apply a balanced three-phase fault to the prefault state and assess the faulted
    /// network - so the current/voltage/distance/differential elements operate on a real fault.</summary>
    public static (FaultResult Fault, ProtectionReport Report) AssessThreePhaseFault(
        GridModel model, PowerFlowResult prefault, int faultBusId,
        double faultReactancePu = 0.0, ProtectionSettings? settings = null)
    {
        prefault.RequireConverged("Protection assessment (fault)");   // the convergence contract (October 2026, WS4)
        FaultResult fault = FaultAnalysis.ThreePhase(model, prefault, faultBusId, faultReactancePu);
        ProtectionReport report = Assess(model, fault.State, settings, null, fault.DifferentialCurrentPuByBus);
        return (fault, report);
    }

    /// <summary>F21: apply an unbalanced fault (<see cref="FaultAnalysis.Unbalanced"/>) and assess it: the phase
    /// elements on the phase currents and voltages built from the sequence study (N117, October 2026; they used to read
    /// the faulted positive-sequence state alone), the earth-fault and negative-sequence elements on the sequence
    /// currents, and the busbar differential with the largest phase fault current inside the faulted bus's zone.</summary>
    public static (UnbalancedFaultResult Fault, ProtectionReport Report) AssessUnbalancedFault(
        GridModel model, PowerFlowResult prefault, int faultBusId, FaultType type,
        double faultReactancePu = 0.0, ProtectionSettings? settings = null)
    {
        prefault.RequireConverged("Protection assessment (fault)");
        UnbalancedFaultResult fault = FaultAnalysis.Unbalanced(model, prefault, faultBusId, type, faultReactancePu);
        var diff = new Dictionary<int, double> { [faultBusId] = Math.Max(fault.PhaseAPu, Math.Max(fault.PhaseBPu, fault.PhaseCPu)) };
        ProtectionReport report = Assess(model, fault.PositiveSequenceState, settings, null, diff, fault);
        return (fault, report);
    }
}
