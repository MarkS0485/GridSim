namespace GridSim.Core.Assets;

/// <summary>
/// A grid-forming (voltage-source) inverter on the same seam as the grid-following one, but a
/// different control law: near-instant <b>synthetic inertia as a power injection</b>
/// <c>p = -synInertia*(df/dt) - damping*(f-f0)</c>. The synthetic-inertia term lowers initial RoCoF
/// WITHOUT touching E - the codebase's non-negotiable separation of real (1/2)Jw^2 energy from
/// df/dt emulation. There is no detection latency (a voltage source responds essentially instantly)
/// and protection is ride-through biased (a high/disabled RoCoF trip), so a GFM fleet stabilises
/// where a GFL fleet can trip.
/// </summary>
public sealed class GridFormingInverter : IActiveAssetDynamics
{
    // N345 (CF3, October 2026): the fall-back value of each class parameter this code reads when the class does not
    // set it, named so the v2 kind specs (schemas/v2/kinds) cite it by symbol instead of quoting the source line.
    // Each constant is the literal it replaces, character for character, so no number moves.
    internal const double DefaultActuateTauS = 0.05;   // AssetParams.ActuateTauS
    internal const double DefaultDampingMwPerHz = 0.0;   // AssetParams.DampingMwPerHz
    internal const double DefaultDeadbandHz = 0.0;   // AssetParams.DeadbandHz
    internal const double DefaultHeadroomDownFraction = 0.2;   // AssetParams.HeadroomDownFraction
    internal const double DefaultHeadroomUpFraction = 0.2;   // AssetParams.HeadroomUpFraction
    internal const double DefaultLocalRocofUpliftFactor = 1.0;   // AssetParams.LocalRocofUpliftFactor
    internal const double DefaultLvrtVoltagePu = 0.0;   // AssetParams.LvrtVoltagePu
    internal const double DefaultRocofTripHzPerS = double.PositiveInfinity;   // AssetParams.RocofTripHzPerS
    internal const double DefaultSynInertiaMwsPerHz = 0.0;   // AssetParams.SynInertiaMwsPerHz

    private readonly double _f0;
    private double _pgMw;   // dispatched output (lost as deficit on trip); R043: follows SetDispatch
    private readonly double _ratedMw;
    private readonly double _synInertia, _damping, _actuateTauS, _capUp, _capDown, _deadbandHz;
    private readonly IDcSource? _dc;   // the energy store behind the DC link (null = unbounded)
    private readonly double _rocofTrip, _rocofTripConfirmS, _lvrtVoltagePu, _localUplift;

    private double _p;
    // N235: how long local RoCoF has been over the trip threshold, counted in whole substeps (was a running sum of dt).
    private ConfirmTimer _rocofConfirm;
    private bool _tripped;

    public GridFormingInverter(AssetClass cls, int bus, double pgMw, double ratedMw, double f0 = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz)
    {
        ClassName = cls.Name;
        Bus = bus;
        _f0 = f0;
        _pgMw = pgMw;
        _ratedMw = Math.Max(0.0, ratedMw);

        _synInertia = cls.Param(AssetParams.SynInertiaMwsPerHz, DefaultSynInertiaMwsPerHz);   // MW.s/Hz - power per df/dt
        _damping = cls.Param(AssetParams.DampingMwPerHz, DefaultDampingMwPerHz);
        _deadbandHz = cls.Param(AssetParams.DeadbandHz, DefaultDeadbandHz);
        _actuateTauS = cls.Param(AssetParams.ActuateTauS, DefaultActuateTauS);
        double headUp = cls.Param(AssetParams.HeadroomUpFraction, DefaultHeadroomUpFraction);
        double headDown = cls.Param(AssetParams.HeadroomDownFraction, DefaultHeadroomDownFraction);
        _capUp = headUp * _ratedMw;
        _capDown = headDown * _ratedMw;
        _dc = DcSources.FromClass(cls, _ratedMw);
        _rocofTrip = cls.Param(AssetParams.RocofTripHzPerS, DefaultRocofTripHzPerS);
        // R137 (B4, 2026-10; Ruling R1, source EREC G99 Issue 2 s.10.6.7.1 Table 10.1, LoM (RoCoF) time delay 0.5 s):
        // the one shared fall-back. Moved 1.0 s -> 0.5 s; only a grid-forming class that sets a reachable RoCoF
        // threshold and no confirmation time is affected (no bundled class: see AssetParamDefaults.RocofTripConfirmS).
        _rocofTripConfirmS = cls.Param(AssetParams.RocofTripConfirmS, AssetParamDefaults.RocofTripConfirmS);
        _lvrtVoltagePu = cls.Param(AssetParams.LvrtVoltagePu, DefaultLvrtVoltagePu);
        _localUplift = cls.Param(AssetParams.LocalRocofUpliftFactor, DefaultLocalRocofUpliftFactor);
    }

    public string ClassName { get; }
    public AssetKind Kind => AssetKind.GridFormingInverter;
    public int Bus { get; }
    public bool Tripped => _tripped;
    public double CurrentMw => _tripped ? 0.0 : _p;
    public double SocFraction => _dc?.SocFraction ?? double.NaN;

    /// <summary>The energy store behind the DC link, if modelled.</summary>
    public IDcSource? DcSource => _dc;

    /// <summary>The dispatched output (MW) a trip would book as lost infeed now.</summary>
    public double DispatchMw => _pgMw;

    /// <summary>
    /// R043 (October 2026 review, stream B2): set the dispatched output (MW) this unit would lose on a trip to the
    /// operating point now running. See <see cref="IActiveAssetDynamics.SetDispatch"/>.
    /// </summary>
    public void SetDispatch(double pgMw)
    {
        if (!double.IsFinite(pgMw))
            throw new ArgumentOutOfRangeException(nameof(pgMw), pgMw, $"{ClassName} at bus {Bus}: the dispatched output must be a finite number of MW.");
        _pgMw = pgMw;
    }

    /// <summary>D-DOC-038: an LVRT threshold above 0 reads the bus voltage, which only a network solve provides.</summary>
    public bool UsesNetworkState => _lvrtVoltagePu > 0;

    public void Reset() { _p = 0; _rocofConfirm.Clear(); _tripped = false; _dc?.Reset(); }

    public AssetActuation Update(in AssetStepContext ctx)
    {
        if (_tripped) return default;

        double dt = ctx.DtSeconds;
        bool overRocof = Math.Abs(ctx.LocalRocofHzPerS) * _localUplift > _rocofTrip;
        // N235 (October 2026): the confirm time is counted in whole substeps (ConfirmTimer, the D-DOC-095 rule), not
        // summed (_rocofDwellS += dt, which tripped a 0.2 s confirm at 20 ms substeps on the 11th step).
        bool rocofTrip = false;
        if (overRocof) rocofTrip = ConfirmTimer.Reached(_rocofConfirm.Count(dt), _rocofTripConfirmS);
        else _rocofConfirm.Clear();
        bool lvrtTrip = _lvrtVoltagePu > 0 && ctx.LocalVoltagePu < _lvrtVoltagePu;
        if (rocofTrip || lvrtTrip)
        {
            _tripped = true;
            return new AssetActuation(0.0, TrippedNow: true, TrippedInfeedMw: _pgMw, TrippedInertiaMwS: 0.0);
        }

        double df = ctx.LocalHz - _f0;
        double capUp = _capUp, capDown = _capDown;
        if (_dc is not null)
        {
            (double up, double down) = _dc.Headroom();
            capUp = Math.Min(capUp, up);
            capDown = Math.Min(capDown, down);
        }
        double dfActive = Math.Abs(df) > _deadbandHz ? df : 0.0;
        double target = Math.Clamp(-_synInertia * ctx.LocalRocofHzPerS - _damping * dfActive, -capDown, capUp);
        _p += (target - _p) / Math.Max(dt, _actuateTauS) * dt;

        if (_dc is not null) _p = _dc.Draw(_p, dt);

        return new AssetActuation(_p, TrippedNow: false, TrippedInfeedMw: 0.0, TrippedInertiaMwS: 0.0);
    }
}
