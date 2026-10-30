using System.Security.Cryptography;

namespace GridSim.Core.Qsts;

/// <summary>One logged controller operation.</summary>
/// <param name="Step">The step it happened in.</param>
/// <param name="TimeSeconds">The instant (seconds from the start).</param>
/// <param name="Kind">"oltc" or "capacitor".</param>
/// <param name="Device">The transformer or bank name.</param>
/// <param name="From">Tap ratio or bank step before.</param>
/// <param name="To">Tap ratio or bank step after.</param>
/// <param name="Sensed">The quantity that triggered it (pu voltage, LDC-compensated voltage, MVAr, or the second of the day).</param>
/// <param name="ControlIteration">The discrete control iteration within the step (1 = after the first solve).</param>
public sealed record QstsOperation(int Step, long TimeSeconds, string Kind, string Device, double From, double To, double Sensed, int ControlIteration);

/// <summary>The recorded state of one step.</summary>
public sealed class QstsStepRecord
{
    public required int Step { get; init; }
    public required long TimeSeconds { get; init; }
    /// <summary>True when every control level settled within its iteration cap.</summary>
    public required bool Settled { get; init; }
    /// <summary>Power-flow solves at this step (1 + re-solves).</summary>
    public required int Solves { get; init; }
    /// <summary>Newton (or sweep) iterations summed over this step's solves.</summary>
    public required int Iterations { get; init; }
    public required int ContinuousIterations { get; init; }
    public required int DiscreteIterations { get; init; }
    public required double LossMw { get; init; }
    public required double LossMvar { get; init; }
    public required double LoadMw { get; init; }
    public required double SourceMw { get; init; }
    public required double PvAvailableMw { get; init; }
    public required double PvMw { get; init; }
    public required double PvMvar { get; init; }
    /// <summary>Net storage power (MW, discharge positive).</summary>
    public required double StorageMw { get; init; }
    public required double VminPu { get; init; }
    public required int VminBus { get; init; }
    public required double VmaxPu { get; init; }
    public required int VmaxBus { get; init; }
    public required double MaxLoadingPercent { get; init; }
    /// <summary>Index of the most loaded rated branch (-1 when none has a rating).</summary>
    public required int MaxLoadingBranch { get; init; }
    /// <summary>Tap ratio of each OLTC record (model order) at the end of the step.</summary>
    public required double[] Taps { get; init; }
    /// <summary>Step of each capacitor bank at the end of the step.</summary>
    public required int[] CapacitorSteps { get; init; }
    /// <summary>Each PV unit's delivered P (MW) and Q (MVAr, positive = injecting).</summary>
    public required double[] PvP { get; init; }
    public required double[] PvQ { get; init; }
    /// <summary>Each storage unit's power (MW, discharge positive), held over [t, t + dt).</summary>
    public required double[] StorageP { get; init; }
    /// <summary>Each storage unit's state of charge at the START of the step (fraction of capacity).</summary>
    public required double[] Soc { get; init; }
    /// <summary>Bus voltages (pu), model bus order; null when the study does not record states.</summary>
    public double[]? Vm { get; init; }
    /// <summary>Branch loading (% of RateA, 0 where unrated), model branch order; null when not recorded.</summary>
    public double[]? LoadingPercent { get; init; }
    /// <summary>Active power (MW) into each monitored branch at its From end (<see cref="QstsStudy.MonitorBranches"/>).</summary>
    public double[] MonitoredPMw { get; init; } = Array.Empty<double>();
    /// <summary>OLTCs whose tap is at a limit with the bus still outside the band at the end of the step.</summary>
    public IReadOnlyList<string> TapsAtLimit { get; init; } = Array.Empty<string>();
}

/// <summary>Time a bus spent outside its voltage limits.</summary>
public sealed record QstsBusViolation(int Bus, double SecondsAbove, double SecondsBelow, double PuSecondsAbove, double PuSecondsBelow, double MaxPu, double MinPu);

/// <summary>Time a branch spent above its loading limit.</summary>
public sealed record QstsBranchViolation(int Branch, string Name, double SecondsOver, double PercentSecondsOver, double MaxPercent);

/// <summary>Energy totals over the run (MWh); each step is held for its step length.</summary>
public sealed record QstsEnergy(double LoadMwh, double LossMwh, double SourceMwh, double PvAvailableMwh, double PvMwh,
    double PvCurtailedMwh, double StorageChargeMwh, double StorageDischargeMwh);

/// <summary>The outcome of a QSTS run.</summary>
public sealed class QstsResult
{
    /// <summary>True when every step converged (settled or not; see <see cref="UnsettledSteps"/>).</summary>
    public required bool Completed { get; init; }
    /// <summary>The step whose solve did not converge (-1 when none): the run stopped there.</summary>
    public required int FailedStep { get; init; }
    public string? FailureReason { get; init; }
    public required QstsControlMode Mode { get; init; }
    public required int StepSeconds { get; init; }
    public required IReadOnlyList<int> BusIds { get; init; }
    public required IReadOnlyList<QstsStepRecord> Steps { get; init; }
    public required IReadOnlyList<QstsOperation> Operations { get; init; }
    public required IReadOnlyList<string> OltcNames { get; init; }
    public required int[] TapOperations { get; init; }
    public required IReadOnlyList<string> CapacitorNames { get; init; }
    public required int[] CapacitorOperations { get; init; }
    public required IReadOnlyList<string> PvNames { get; init; }
    public required IReadOnlyList<string> StorageNames { get; init; }
    /// <summary>Each storage unit's state of charge after the last step's interval.</summary>
    public required double[] FinalSoc { get; init; }
    public required int UnsettledSteps { get; init; }
    public required QstsEnergy Energy { get; init; }
    public required IReadOnlyList<QstsBusViolation> BusViolations { get; init; }
    public required IReadOnlyList<QstsBranchViolation> BranchViolations { get; init; }
    /// <summary>The last converged power-flow result (null when step 0 failed).</summary>
    public PowerFlow.PowerFlowResult? LastResult { get; init; }
    /// <summary>Wall-clock seconds (not part of the deterministic result).</summary>
    public double ElapsedSeconds { get; init; }

    public int TotalTapOperations => TapOperations.Sum();
    public int TotalCapacitorOperations => CapacitorOperations.Sum();

    /// <summary>
    /// SHA-256 over the bits of every number of the result (steps, operations, counts, energy, violations), as lower-case
    /// hex. Two runs agree to the bit exactly when their digests agree; used by the cross-SIMD-tier tests.
    /// </summary>
    public string Digest()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Completed); w.Write(FailedStep); w.Write(UnsettledSteps);
            foreach (QstsStepRecord s in Steps)
            {
                w.Write(s.Step); w.Write(s.TimeSeconds); w.Write(s.Settled); w.Write(s.Solves); w.Write(s.Iterations);
                w.Write(s.ContinuousIterations); w.Write(s.DiscreteIterations);
                foreach (double d in new[] { s.LossMw, s.LossMvar, s.LoadMw, s.SourceMw, s.PvAvailableMw, s.PvMw, s.PvMvar, s.StorageMw, s.VminPu, s.VmaxPu, s.MaxLoadingPercent }) w.Write(d);
                w.Write(s.VminBus); w.Write(s.VmaxBus); w.Write(s.MaxLoadingBranch);
                foreach (double d in s.Taps) w.Write(d);
                foreach (int c in s.CapacitorSteps) w.Write(c);
                foreach (double d in s.PvP) w.Write(d);
                foreach (double d in s.PvQ) w.Write(d);
                foreach (double d in s.StorageP) w.Write(d);
                foreach (double d in s.Soc) w.Write(d);
                if (s.Vm is not null) foreach (double d in s.Vm) w.Write(d);
                if (s.LoadingPercent is not null) foreach (double d in s.LoadingPercent) w.Write(d);
                foreach (double d in s.MonitoredPMw) w.Write(d);
            }
            foreach (QstsOperation o in Operations)
            {
                w.Write(o.Step); w.Write(o.TimeSeconds); w.Write(o.Kind); w.Write(o.Device); w.Write(o.From); w.Write(o.To); w.Write(o.Sensed); w.Write(o.ControlIteration);
            }
            foreach (double d in FinalSoc) w.Write(d);
            QstsEnergy e = Energy;
            foreach (double d in new[] { e.LoadMwh, e.LossMwh, e.SourceMwh, e.PvAvailableMwh, e.PvMwh, e.PvCurtailedMwh, e.StorageChargeMwh, e.StorageDischargeMwh }) w.Write(d);
            foreach (QstsBusViolation v in BusViolations) { w.Write(v.Bus); w.Write(v.SecondsAbove); w.Write(v.SecondsBelow); w.Write(v.PuSecondsAbove); w.Write(v.PuSecondsBelow); w.Write(v.MaxPu); w.Write(v.MinPu); }
            foreach (QstsBranchViolation v in BranchViolations) { w.Write(v.Branch); w.Write(v.SecondsOver); w.Write(v.PercentSecondsOver); w.Write(v.MaxPercent); }
        }
        return Convert.ToHexStringLower(SHA256.HashData(ms.ToArray()));
    }
}
