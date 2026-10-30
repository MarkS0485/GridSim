namespace GridSim.Core.Assets.Protection;

/// <summary>
/// A free-standing protective device as an <see cref="IActiveAssetDynamics"/>: a set of definite-time
/// <see cref="ProtectionStage"/>s (built from the authored class parameters) sharing one measurement-delay
/// buffer. Contributes exactly 0 MW until a stage operates, then emits the mapped <see cref="AssetActuation"/>
/// channel - a trip removes <c>CapacityMw</c> of infeed, an LFDD stage sheds <c>lfddBlockMw</c> of demand
/// (a positive swing-power term), a recloser requests a branch open (Phase 3 topology; inert on the swing).
/// With no stages, or stages that never operate, it is the reduction-gate zero.
/// </summary>
public sealed class ProtectionController : IActiveAssetDynamics
{
    // N345 (CF3, October 2026): the fall-back value of each class parameter this code reads when the class does not
    // set it, named so the v2 kind specs (schemas/v2/kinds) cite it by symbol instead of quoting the source line.
    // Each constant is the literal it replaces, character for character, so no number moves.
    internal const double DefaultAutoRecloseDelayS = double.PositiveInfinity;   // AssetParams.AutoRecloseDelayS
    internal const double DefaultDetectionLatencyS = 0.0;   // AssetParams.DetectionLatencyS
    internal const double DefaultLocalRocofUpliftFactor = 1.0;   // AssetParams.LocalRocofUpliftFactor

    private readonly (ProtectionStage Stage, ProtectionEvaluator Eval)[] _stages;
    // N297 (B4): the empty history reads as the system's nominal (set in the constructor; the GB 50 Hz by default, so
    // every existing construction is bit-identical). Before, it was always 50 Hz: with the stages now about a 60 Hz
    // nominal, a 50 Hz first read would have operated a shifted under-frequency stage at once.
    private readonly MeasurementDelay _delay;
    private readonly double _capacityMw;
    private readonly double _detectionLatencyS;
    private readonly string? _targetBranch;
    // Reference-counted holds per branch: several stages (RoCoF, UF, ...) can open the SAME branch
    // at different times, each with its own reclose delay. The branch stays open until the LAST
    // holding stage recloses, so a plain HashSet + single bool would reclose onto a condition another
    // stage still requires open. Only branches with count > 0 are present.
    private readonly Dictionary<string, int> _openBranches = new();
    private double _shedMw;     // persistent ShedDemand/InjectStep injection (+ reduces the deficit)
    private bool _tripped;      // an infeed-trip or branch-open operated (NOT set by a demand-shed)
    // N297 (B4, October 2026): the demand now running as a fraction of the demand the device was set against (see
    // SetDemandScale). 1.0 until a caller says otherwise, and 1.0 x mw == mw exactly, so every batch study is unchanged.
    private double _demandScale = 1.0;
    // N297: the MW each ShedDemand stage actually shed when it fired (index = stage position), so an auto-restore
    // reconnects exactly the demand it disconnected even if the demand scale moved meanwhile.
    private readonly double[] _shedByStage;

    /// <summary>
    /// Build the device for class <paramref name="cls"/> at <paramref name="bus"/>, acting on <paramref name="capacityMw"/>.
    /// D-DOC-216 (October 2026): the constructor used to take a nominal frequency <c>f0</c> it never read (every stage
    /// compared the measured frequency with its own absolute threshold, in Hz); the unused parameter was removed. N297
    /// (B4, October 2026) gives it a real use (<paramref name="systemNominalHz"/>: frequency settings move with the
    /// system's nominal, and the measurement buffer's empty history reads as that nominal). D-DOC-094: a capacity that is negative or not finite is refused (it would
    /// add a negative or NaN infeed loss to the frequency study); 0 is allowed (a recloser opens a branch and trips no MW).
    /// </summary>
    /// <param name="systemNominalHz">N297 (B4, October 2026): the nominal frequency (Hz) of the system the device sits in
    /// (the fleet passes its f0, the model's nominal). The class's frequency settings are written for
    /// <see cref="AssetParams.SettingsNominalHz"/> (default the GB 50 Hz) and move with the system's nominal as hertz
    /// offsets; at 50 Hz with the default every threshold is used exactly as written. Default 50 Hz (a caller with no
    /// system, every existing direct construction).</param>
    public ProtectionController(AssetClass cls, int bus, double capacityMw, string? targetBranch = null,
        double systemNominalHz = Dynamics.FrequencyLimits.NominalHz)
    {
        if (!(capacityMw >= 0.0) || !double.IsFinite(capacityMw))
            throw new InvalidDataException(
                $"Protection class '{cls.Name}' at bus {bus}: the capacity {capacityMw} MW must be a finite number of 0 MW or more " +
                "(the infeed it trips or the demand it sheds).");
        // A recloser class (it has an auto-reclose delay) turns every stage into "open the target branch". With no
        // target it used to latch Tripped and open nothing, so the dashboard showed a trip on an untouched network
        // (R232, October 2026). Fail loud instead: the device cannot do what its class says.
        if (cls.Params.ContainsKey(AssetParams.AutoRecloseDelayS) && string.IsNullOrWhiteSpace(targetBranch))
            throw new InvalidDataException(
                $"Protection class '{cls.Name}' is a recloser (it sets {AssetParams.AutoRecloseDelayS}), so the device at bus {bus} " +
                "must name the branch it opens (TargetBranch), but none was given.");
        ClassName = cls.Name;
        Bus = bus;
        _capacityMw = capacityMw;
        _targetBranch = targetBranch;
        _detectionLatencyS = cls.Param(AssetParams.DetectionLatencyS, DefaultDetectionLatencyS);
        if (!double.IsFinite(systemNominalHz) || !(systemNominalHz > 0.0))
            throw new ArgumentOutOfRangeException(nameof(systemNominalHz), systemNominalHz,
                $"Protection class '{cls.Name}' at bus {bus}: the system nominal frequency must be a finite number of Hz above 0.");
        _delay = new MeasurementDelay(systemNominalHz);
        _stages = BuildStages(cls, targetBranch, systemNominalHz).Select(s => (s, new ProtectionEvaluator(s))).ToArray();
        _shedByStage = new double[_stages.Length];
    }

    /// <summary>
    /// N297 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04; source Grid Code OC6.6.2 (Issue 6 Revision 20,
    /// docs/reference S078): the demand subject to LFDD "will be split into discrete MW blocks" whose "size (% Demand)" is
    /// set in Table CC.A.5.5.1a, i.e. a block is a SHARE of the demand on the feeders it disconnects). Tell the device the
    /// demand now running as a fraction of the demand its blocks were set against (the base case). A demand-shed stage
    /// (LFDD) then disconnects its block times this factor: at 04:00 on the live loop (daily factor 0.6) an LFDD block of
    /// 400 MW at the base case sheds 240 MW, the demand its feeders actually carry. Before, it shed the base-case MW at
    /// every hour (B2 NF-3). An infeed trip keeps its capacity (it is a unit, not a share of demand). Fails loud on a
    /// factor that is not finite or is negative. Default 1.0 (no caller: every batch study), which is bit-identical.
    /// A stage that already operated keeps the MW it shed.
    /// </summary>
    public void SetDemandScale(double factor)
    {
        if (!double.IsFinite(factor) || factor < 0.0)
            throw new ArgumentOutOfRangeException(nameof(factor), factor,
                $"Protection class '{ClassName}' at bus {Bus}: the demand scale must be a finite fraction of 0 or more.");
        _demandScale = factor;
    }

    /// <summary>N297: the demand scale the demand-shed stages apply now (1.0 unless <see cref="SetDemandScale"/> was called).</summary>
    public double DemandScale => _demandScale;

    /// <summary>Branches this device currently holds OPEN (a recloser; empty until a topology stage operates).</summary>
    public IReadOnlyCollection<string> OpenBranches => _openBranches.Keys;

    public string ClassName { get; }
    public AssetKind Kind => AssetKind.Protection;
    public int Bus { get; }
    public bool Tripped => _tripped;
    public double CurrentMw => _shedMw;
    public double SocFraction => double.NaN;
    public int StageCount => _stages.Length;

    /// <summary>D-DOC-038: whether any stage reads the bus voltage or a branch loading, which only a network solve provides
    /// (on a path that solves none those stages see NaN and never operate; the caller reports them as not evaluated).</summary>
    public bool UsesNetworkState => _stages.Any(s => s.Stage.Quantity is MonitoredQuantity.Voltage or MonitoredQuantity.LoadingFraction);

    public void Reset()
    {
        foreach ((_, ProtectionEvaluator e) in _stages) e.Reset();
        _delay.Clear();
        _shedMw = 0;
        Array.Clear(_shedByStage);   // N297: nothing shed after a reset (the demand scale is the operating point's, kept)
        _tripped = false;
        _openBranches.Clear();
    }

    public AssetActuation Update(in AssetStepContext ctx)
    {
        _delay.Push(ctx.ClockSeconds, ctx.LocalHz, ctx.LocalRocofHzPerS, ctx.LocalPhaseDeg);
        // GR-620: a detection latency the full 512-sample history cannot hold is refused, not silently shortened.
        if (_delay.TruncatesLatency(ctx.ClockSeconds, _detectionLatencyS))
            throw new InvalidOperationException(
                $"Protection class '{ClassName}' at bus {Bus}: the detection latency "
                + $"{_detectionLatencyS.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} s is longer than the "
                + $"{MeasurementDelay.Capacity}-sample measurement history holds at this substep "
                + $"({ctx.DtSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} s); shorten the latency or lengthen the substep.");
        (double fMeas, double rocofMeas, double _) = _delay.Read(ctx.ClockSeconds, _detectionLatencyS);
        double phaseStep = _delay.PhaseStep(ctx.ClockSeconds, System.Math.Max(ctx.DtSeconds, 0.02));

        double trippedInfeedThisStep = 0.0;
        bool trippedNow = false;

        for (int k = 0; k < _stages.Length; k++)
        {
            (ProtectionStage stage, ProtectionEvaluator eval) = _stages[k];
            double value = stage.Quantity switch
            {
                MonitoredQuantity.Frequency => fMeas,
                MonitoredQuantity.Rocof => System.Math.Abs(rocofMeas) * stage.LocalUpliftFactor,
                MonitoredQuantity.Voltage => ctx.LocalVoltagePu,
                MonitoredQuantity.LoadingFraction => ctx.LoadingFraction,
                MonitoredQuantity.PhaseShift => System.Math.Abs(phaseStep),
                _ => 0.0,
            };
            double mw = stage.ActionMw > 0 ? stage.ActionMw : _capacityMw;

            switch (eval.Step(in ctx, value))
            {
                case ProtectionEdge.Fired:
                    switch (stage.Action)
                    {
                        case ProtectionAction.TripInfeed:
                            // One physical infeed can only be lost ONCE: a class with several
                            // trip parameters (RoCoF + df/dt + UF + ...) builds one TripInfeed
                            // stage per parameter, and each latching independently used to add
                            // the same CapacityMw to the deficit again.
                            if (!_tripped) { trippedNow = true; trippedInfeedThisStep += mw; }
                            _tripped = true;
                            break;
                        case ProtectionAction.ShedDemand:   // operated, but not an infeed trip
                            // N297: the block is a share of the demand now running (x 1.0, exactly mw, unless the live
                            // loop scaled the demand); remembered so a reclose reconnects the same MW.
                            _shedByStage[k] = mw * _demandScale;
                            _shedMw += _shedByStage[k];
                            break;
                        case ProtectionAction.InjectStep: _shedMw += stage.ActionMw; break;
                        case ProtectionAction.OpenBranch:   // topology: open the target branch (re-solved by LiveSim)
                            _tripped = true;
                            if (stage.TargetBranch is { } tb) _openBranches[tb] = _openBranches.GetValueOrDefault(tb) + 1;
                            break;
                        case ProtectionAction.RestoreBranch:
                            // Unreachable: ProtectionEvaluator refuses a RestoreBranch stage at construction (R073).
                            // Kept loud rather than a silent no-op in case that guard is ever removed.
                            throw new NotSupportedException("ProtectionAction.RestoreBranch is reserved and not implemented.");
                    }
                    break;
                case ProtectionEdge.Restored:   // auto-reclose: reverse the action
                    switch (stage.Action)
                    {
                        case ProtectionAction.ShedDemand: _shedMw -= _shedByStage[k]; _shedByStage[k] = 0.0; break;   // N297: what it shed
                        case ProtectionAction.InjectStep: _shedMw -= stage.ActionMw; break;
                        case ProtectionAction.OpenBranch:
                            // Release only THIS stage's hold; the breaker recloses (and Tripped clears)
                            // only once no other stage is still holding a branch open.
                            if (stage.TargetBranch is { } rtb && _openBranches.TryGetValue(rtb, out int held))
                            {
                                if (held <= 1) _openBranches.Remove(rtb);
                                else _openBranches[rtb] = held - 1;
                            }
                            _tripped = _openBranches.Count > 0;
                            break;
                    }
                    break;
            }
        }

        return new AssetActuation(_shedMw, trippedNow, trippedInfeedThisStep, TrippedInertiaMwS: 0.0);
    }

    /// <summary>Translate the authored class parameters into concrete stages (only for keys actually present).</summary>
    private static List<ProtectionStage> BuildStages(AssetClass cls, string? targetBranch, double systemNominalHz)
    {
        bool Has(string k) => cls.Params.ContainsKey(k);
        double P(string k, double d = 0.0) => cls.Param(k, d);

        // N297 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04; the convention is B2's FrequencyBands:
        // GB settings as hertz offsets about the system's nominal, the form PSS/E, PowerFactory, ENTSO-E SOGL and NERC
        // use). A frequency setting (UF, OF, LFDD stage) is written for the class's settings nominal (absent: the GB
        // 50 Hz); on a system at another nominal it moves by the difference. Before, the absolute GB values were applied
        // to every system, so on a 60 Hz case an under-frequency stage at 47.5 Hz could never operate and an
        // over-frequency stage at 52 Hz operated at once. At equal nominals the setting is returned as written, bit for
        // bit (every bundled class: GB data on GB models).
        double settingsNominalHz = P(AssetParams.SettingsNominalHz, AssetParamDefaults.ProtectionSettingsNominalHz);
        if (!double.IsFinite(settingsNominalHz) || !(settingsNominalHz > 0.0))
            throw new InvalidDataException(
                $"Protection class '{cls.Name}': {AssetParams.SettingsNominalHz} = {settingsNominalHz} must be a finite number of Hz above 0.");
        double Hz(string k) => settingsNominalHz == systemNominalHz ? P(k) : systemNominalHz + (P(k) - settingsNominalHz);

        // N304 (LF3, October 2026; Ruling R1: fix) and N318 (LF6, October 2026; Ruling R1: direction-aware): a frequency
        // setting on the wrong side of the nominal it is written for (UF / LFDD at or above it, OF at or below it), or more
        // than 10 Hz from it on its own side, is refused by name (AssetClassSupport.MaxFrequencySettingDistanceHz). The library already refuses it at load (AssetClassSupport.Problems); this repeats the same
        // check for a class built directly in code, so no path moves an implausible setting by the nominal difference
        // (a native 60 Hz UF at 59.3 Hz read as a GB setting would move to 69.3 Hz on a 60 Hz case and operate at once).
        IReadOnlyList<string> implausible = AssetClassSupport.ProtectionFrequencyProblems(cls);
        if (implausible.Count > 0)
            throw new InvalidDataException(string.Join("; ", implausible));

        // R137 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04). Every stage's dwell used to fall back
        // to 0 s through P's default when the class did not set it, so a class that set rocofTripHzPerS without
        // rocofTripConfirmS became an INSTANTANEOUS RoCoF trip, while the same omission on an inverter class gave a
        // 0.5 s confirmation. A relay's time setting is part of the setting (EREC G99 Issue 2 s.10.6.7.1 Table 10.1
        // gives a time delay for every function, and the note on p.126 that the RoCoF delay must be a real definite-time
        // delay), so a protection class must author it: 0 is allowed, absent is refused, naming every missing key.
        // Bundled data: every protection class in data/ already authors its dwells (prot-lom-legacy: rocofTripConfirmS
        // 0.0; prot-lfdd-stage1: lfddDwellS 0.0), so nothing that ran before is refused and no number moves.
        var missing = AssetParamDefaults.ProtectionDwellKeys
            .Where(p => Has(p.Threshold) && !Has(p.Dwell))
            .Select(p => $"'{p.Threshold}' is set but its dwell '{p.Dwell}' is not")
            .ToList();
        if (missing.Count > 0)
            throw new InvalidDataException(
                $"Protection class '{cls.Name}': {string.Join("; ", missing)}. A protection stage's definite-time delay is " +
                "part of its setting and has no default (write 0 for an instantaneous stage).");

        // A recloser class (has an auto-reclose delay) opens a branch and recloses; otherwise stages latch and trip infeed.
        bool recloser = Has(AssetParams.AutoRecloseDelayS);
        ProtectionAction tripAction = recloser ? ProtectionAction.OpenBranch : ProtectionAction.TripInfeed;
        double autoRestore = recloser ? P(AssetParams.AutoRecloseDelayS, DefaultAutoRecloseDelayS) : double.PositiveInfinity;
        string? branch = recloser ? targetBranch : null;

        var stages = new List<ProtectionStage>();
        double uplift = P(AssetParams.LocalRocofUpliftFactor, DefaultLocalRocofUpliftFactor);

        ProtectionStage Trip(MonitoredQuantity q, ThresholdSense s, double thr, double dwell) =>
            new(q, s, thr, dwell, tripAction, LocalUpliftFactor: q == MonitoredQuantity.Rocof ? uplift : 1.0,
                AutoRestoreDelayS: autoRestore, TargetBranch: branch);

        if (Has(AssetParams.RocofTripHzPerS)) stages.Add(Trip(MonitoredQuantity.Rocof, ThresholdSense.Above, P(AssetParams.RocofTripHzPerS), P(AssetParams.RocofTripConfirmS)));
        if (Has(AssetParams.DfDtTripHzPerS)) stages.Add(Trip(MonitoredQuantity.Rocof, ThresholdSense.Above, P(AssetParams.DfDtTripHzPerS), P(AssetParams.DfDtTripDwellS)));
        if (Has(AssetParams.UfTripHz)) stages.Add(Trip(MonitoredQuantity.Frequency, ThresholdSense.Below, Hz(AssetParams.UfTripHz), P(AssetParams.UfTripDwellS)));
        if (Has(AssetParams.OfTripHz)) stages.Add(Trip(MonitoredQuantity.Frequency, ThresholdSense.Above, Hz(AssetParams.OfTripHz), P(AssetParams.OfTripDwellS)));
        if (Has(AssetParams.UvTripPu)) stages.Add(Trip(MonitoredQuantity.Voltage, ThresholdSense.Below, P(AssetParams.UvTripPu), P(AssetParams.UvTripDwellS)));
        if (Has(AssetParams.LvrtVoltagePu) && P(AssetParams.LvrtVoltagePu) > 0) stages.Add(Trip(MonitoredQuantity.Voltage, ThresholdSense.Below, P(AssetParams.LvrtVoltagePu), 0.0));
        if (Has(AssetParams.OvTripPu)) stages.Add(Trip(MonitoredQuantity.Voltage, ThresholdSense.Above, P(AssetParams.OvTripPu), P(AssetParams.OvTripDwellS)));
        if (Has(AssetParams.OverloadTripFraction)) stages.Add(Trip(MonitoredQuantity.LoadingFraction, ThresholdSense.Above, P(AssetParams.OverloadTripFraction), P(AssetParams.OverloadTripDwellS)));
        if (Has(AssetParams.VectorShiftTripDeg) && double.IsFinite(P(AssetParams.VectorShiftTripDeg))) stages.Add(Trip(MonitoredQuantity.PhaseShift, ThresholdSense.Above, P(AssetParams.VectorShiftTripDeg), 0.0));

        // LFDD is always a demand-shed, regardless of the recloser flag.
        if (Has(AssetParams.LfddStageHz))
            stages.Add(new(MonitoredQuantity.Frequency, ThresholdSense.Below, Hz(AssetParams.LfddStageHz),
                P(AssetParams.LfddDwellS), ProtectionAction.ShedDemand, ActionMw: P(AssetParams.LfddBlockMw)));
        return stages;
    }
}
