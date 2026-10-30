namespace GridSim.Core.Assets;

/// <summary>
/// A transformer as PASSIVE plant dynamics on the steady-state tick: a mechanically-delayed on-load tap
/// changer plus first-order thermal derating. Unlike the solver's instantaneous OLTC loop, a tap step
/// commits only after the controlled-bus voltage has stayed outside the deadband for
/// <c>interTapDelayS</c> (and <c>initialTapDelayS</c> before the very first move) - the seconds-to-tens-
/// of-seconds mechanical delay real tap changers have. Thermal state is a first-order lag of loading vs
/// ambient (top-oil/winding), driving a rating derate. Emits a <see cref="PlantStateUpdate"/>, never a
/// swing-power term - a different (voltage-stability/thermal) timescale from the frequency window.
/// </summary>
/// <remarks>
/// <para><b>Which way a tap step moves the controlled bus (R058, October 2026).</b> GridSim follows the MATPOWER
/// convention: the ideal a:1 tap sits on the branch's From end, so V_to ~ V_from / a. If the controlled bus is the
/// To (untapped) end, RAISING the tap LOWERS it, so a high voltage needs a tap RAISE. If the controlled bus is the
/// From (tapped) end, V_from ~ a * V_to, so a high voltage needs a tap LOWER. Before October 2026 this controller
/// always used the From-end rule, which is backwards for every gb-full OLTC (all 1383 regulate their To bus), so
/// once wired it would have run those taps the wrong way to a limit. The rule is now chosen by
/// <see cref="ControlsTappedEnd"/>, matching the solver's own OLTC loop (SteadyStateSolver, R013).</para>
/// <para><b>Starting tap (R058).</b> The tap starts at the bound branch's own <c>TapRatio</c>, not 1.0, so the first
/// update does not silently reset an off-nominal tap to nominal.</para>
/// </remarks>
public sealed class TransformerDynamics : IPassiveAssetDynamics
{
    // N345 (CF3, October 2026): the fall-back value of each class parameter this code reads when the class does not
    // set it, named so the v2 kind specs (schemas/v2/kinds) cite it by symbol instead of quoting the source line.
    // Each constant is the literal it replaces, character for character, so no number moves.
    internal const double DefaultAmbientLoadingFraction = 0.0;   // AssetParams.AmbientLoadingFraction
    internal const double DefaultDerateAtFullThermal = 0.0;   // AssetParams.DerateAtFullThermal
    internal const double DefaultInitialTapDelayS = 10.0;   // AssetParams.InitialTapDelayS
    internal const double DefaultInterTapDelayS = 5.0;   // AssetParams.InterTapDelayS
    internal const double DefaultMaxTap = 1.1;   // AssetParams.MaxTap
    internal const double DefaultMinTap = 0.9;   // AssetParams.MinTap
    internal const double DefaultTapDeadbandPu = 0.01;   // AssetParams.TapDeadbandPu
    internal const double DefaultTapStepPu = 0.0125;   // AssetParams.TapStepPu
    internal const double DefaultTargetVpu = 1.0;   // AssetParams.TargetVpu
    internal const double DefaultThermalTauMin = 20.0;   // AssetParams.ThermalTauMin

    private readonly double _targetVpu, _stepPu, _deadbandPu, _minTap, _maxTap;
    private readonly double _interTapDelayS, _initialTapDelayS;
    private readonly double _thermalTauMin, _ambient, _derateFull;

    // The tap the controller starts from (and returns to on Reset): the bound branch's TapRatio.
    private readonly double _initialTap;

    private double _tap;
    private double _outOfBandS;      // time the controlled bus has been outside the deadband
    private bool _movedOnce;
    private double _thermal;         // normalised thermal state (0 = ambient)

    /// <summary>
    /// A free-standing controller with no bound branch: it starts at tap 1.0 and regulates a controlled bus on the
    /// From (tapped) end, so a high voltage lowers the tap. Kept for unit tests and callers that model the tap
    /// in isolation; the fleet always uses the full constructor, which states the real starting tap and side.
    /// </summary>
    public TransformerDynamics(AssetClass cls, string branchName)
        : this(cls, branchName, initialTap: 1.0, controlsTappedEnd: true) { }

    /// <summary>
    /// A controller bound to a real branch. <paramref name="initialTap"/> is that branch's present
    /// <c>TapRatio</c>; <paramref name="controlsTappedEnd"/> is true when the controlled bus is the branch's From
    /// (tapped) end and false when it is the To end (or a remote bus beyond it, the solver's historical rule).
    /// Throws when the starting tap is not finite or lies outside the class's [minTap, maxTap]: the authored
    /// class cannot represent that branch, and clamping it silently on the first step would move the network.
    /// </summary>
    public TransformerDynamics(AssetClass cls, string branchName, double initialTap, bool controlsTappedEnd)
    {
        ClassName = cls.Name;
        BranchName = branchName;
        _targetVpu = cls.Param(AssetParams.TargetVpu, DefaultTargetVpu);
        _stepPu = cls.Param(AssetParams.TapStepPu, DefaultTapStepPu);
        _deadbandPu = cls.Param(AssetParams.TapDeadbandPu, DefaultTapDeadbandPu);
        _minTap = cls.Param(AssetParams.MinTap, DefaultMinTap);
        _maxTap = cls.Param(AssetParams.MaxTap, DefaultMaxTap);
        _interTapDelayS = cls.Param(AssetParams.InterTapDelayS, DefaultInterTapDelayS);
        _initialTapDelayS = cls.Param(AssetParams.InitialTapDelayS, DefaultInitialTapDelayS);
        _thermalTauMin = cls.Param(AssetParams.ThermalTauMin, DefaultThermalTauMin);
        _ambient = cls.Param(AssetParams.AmbientLoadingFraction, DefaultAmbientLoadingFraction);
        _derateFull = cls.Param(AssetParams.DerateAtFullThermal, DefaultDerateAtFullThermal);

        // Fail loud on a starting tap the class cannot hold (see the summary).
        if (!double.IsFinite(initialTap) || initialTap < _minTap || initialTap > _maxTap)
            throw new ArgumentOutOfRangeException(nameof(initialTap),
                $"Transformer '{branchName}': its tap ratio {initialTap} lies outside the bound OLTC class " +
                $"'{cls.Name}' range [{_minTap}, {_maxTap}]. Fix the branch tap or the class minTap/maxTap.");

        _initialTap = initialTap;
        ControlsTappedEnd = controlsTappedEnd;
        _tap = _initialTap;
        _thermal = _ambient;
    }

    public string ClassName { get; }
    public string BranchName { get; }
    public AssetKind Kind => AssetKind.Transformer;
    public int Bus => -1;   // a branch, not a bus
    public bool Tripped => false;
    public double Tap => _tap;

    /// <summary>True when the controlled bus is the branch's From (tapped) end; false for the To end.</summary>
    public bool ControlsTappedEnd { get; }

    public void Reset() { _tap = _initialTap; _outOfBandS = 0; _movedOnce = false; _thermal = _ambient; }

    public PlantStateUpdate Advance(in AssetStepContext ctx)
    {
        double dt = ctx.DtSeconds;
        double v = ctx.LocalVoltagePu;

        // A non-finite voltage means the controlled bus was not observed this tick (for example it is not in the
        // solved result). A real tap changer with no valid measurement does nothing, so hold the tap AND the delay
        // timer exactly as they are rather than reacting to a made-up value.
        if (double.IsFinite(v))
        {
            double err = v - _targetVpu;
            if (Math.Abs(err) > _deadbandPu)
            {
                _outOfBandS += dt;
                double delayNeeded = _movedOnce ? _interTapDelayS : _initialTapDelayS;
                if (_outOfBandS >= delayNeeded)
                {
                    // Controlled bus on the tapped (From) end: high voltage -> LOWER the tap (V_from ~ a * V_to).
                    // Controlled bus on the untapped (To) end: high voltage -> RAISE the tap (V_to ~ V_from / a).
                    bool voltageHigh = err > 0;
                    bool lowerTap = ControlsTappedEnd ? voltageHigh : !voltageHigh;
                    _tap = Math.Clamp(_tap + (lowerTap ? -_stepPu : _stepPu), _minTap, _maxTap);
                    _movedOnce = true;
                    _outOfBandS = 0.0;
                }
            }
            else
            {
                _outOfBandS = 0.0;
            }
        }

        // First-order thermal lag of loading (per minute tau -> seconds), then a linear derate.
        double tauS = Math.Max(dt, _thermalTauMin * 60.0);
        _thermal += (Math.Max(ctx.LoadingFraction, _ambient) - _thermal) / tauS * dt;
        double derate = _derateFull * Math.Clamp(_thermal, 0.0, 1.0);

        return new PlantStateUpdate(TapRatio: _tap, RatingDerateFraction: derate, ThermalStatePu: _thermal);
    }
}
