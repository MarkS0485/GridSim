using System.Globalization;
using GridSim.Core.Dynamics;

namespace GridSim.Core.Assets;

/// <summary>
/// A grid-following (PLL-behind current-source) inverter as an explicit
/// <b>sample -&gt; compute -&gt; actuate -&gt; protect</b> state machine, integrated on the swing substep.
///
/// <para>Sample: local (Hz, RoCoF, phase) pushed through a transport delay (<c>detectionLatencyS</c>) -
/// modelling PLL/measurement latency and the fact a PLL sees its OWN bus, not the COI. Compute: a
/// deadbanded droop leg (shape shared with <see cref="SwingKernel.FastResponseTarget"/>) plus an
/// optional df/dt leg, capped by SoC/headroom. Actuate: ramp-limit the fast slew, then first-order lag
/// (<c>actuateTauS</c>). Protect: on sustained local RoCoF over <c>rocofTripHzPerS</c>, a phase step over
/// <c>vectorShiftTripDeg</c>, or under-voltage below <c>lvrtVoltagePu</c>, the unit trips and its
/// dispatched output <c>Pg</c> becomes a deficit (Loss-of-Mains) - the mechanism behind 2019-08-09.</para>
///
/// <para>In the degenerate regime (zero latencies, df/dt gain 0, SoC/ramp disabled, symmetric headroom,
/// trip disabled) one unit reduces to a <see cref="Fleet.FleetUnit"/> FFR device - "generalises, not forks".</para>
/// </summary>
public sealed class GridFollowingInverter : IActiveAssetDynamics
{
    // N345 (CF3, October 2026): the fall-back value of each class parameter this code reads when the class does not
    // set it, named so the v2 kind specs (schemas/v2/kinds) cite it by symbol instead of quoting the source line.
    // Each constant is the literal it replaces, character for character, so no number moves.
    internal const double DefaultActuateTauS = 0.25;   // AssetParams.ActuateTauS
    internal const double DefaultComputeLatencyS = 0.0;   // AssetParams.ComputeLatencyS
    internal const double DefaultDeadbandHz = 0.015;   // AssetParams.DeadbandHz
    internal const double DefaultDetectionLatencyS = 0.0;   // AssetParams.DetectionLatencyS
    internal const double DefaultDroopGainMwPerHzPerMw = 2.0;   // AssetParams.DroopGainMwPerHzPerMw
    internal const double DefaultFastSlewDownMwPerSPerMw = 0.0;   // AssetParams.FastSlewDownMwPerSPerMw
    internal const double DefaultFastSlewUpMwPerSPerMw = 0.0;   // AssetParams.FastSlewUpMwPerSPerMw
    internal const double DefaultHeadroomDownFraction = 0.2;   // AssetParams.HeadroomDownFraction
    internal const double DefaultHeadroomUpFraction = 0.2;   // AssetParams.HeadroomUpFraction
    internal const double DefaultLocalRocofUpliftFactor = 1.0;   // AssetParams.LocalRocofUpliftFactor
    internal const double DefaultLvrtVoltagePu = 0.0;   // AssetParams.LvrtVoltagePu
    internal const double DefaultRocofGainMwsPerHz = 0.0;   // AssetParams.RocofGainMwsPerHz
    internal const double DefaultRocofTripHzPerS = double.PositiveInfinity;   // AssetParams.RocofTripHzPerS
    internal const double DefaultVectorShiftTripDeg = double.PositiveInfinity;   // AssetParams.VectorShiftTripDeg

    private readonly double _f0;
    private double _pgMw;               // dispatched output (lost as deficit on trip); R043: follows SetDispatch
    private readonly double _ratedMw;   // MBase - normalises the per-MW gains/headroom

    private readonly double _deadbandHz, _droopGain, _rocofGain, _detectionLatencyS, _computeLatencyS, _actuateTauS;
    private readonly double _slewUp, _slewDown, _capUp, _capDown;
    private readonly IDcSource? _dc;   // the energy store behind the DC link (null = unbounded)
    private readonly double _rocofTrip, _rocofTripConfirmS, _vectorShiftTripDeg, _lvrtVoltagePu, _localUplift;

    private readonly MeasurementDelay _delay;   // built in the constructor with this inverter's nominal f0 (R196)
    private double _p;             // current lagged output (MW, + = injecting)
    private double _armAtS = double.PositiveInfinity;
    // N235: how long local RoCoF has been over the trip threshold, counted in whole substeps (was a running sum of dt).
    private ConfirmTimer _rocofConfirm;
    // R253 (B2): how long the measured frequency has been back inside the deadband while armed, in whole substeps.
    private ConfirmTimer _insideDeadband;
    private bool _tripped;

    public GridFollowingInverter(AssetClass cls, int bus, double pgMw, double ratedMw, double f0 = FrequencyLimits.NominalHz)
    {
        ClassName = cls.Name;
        Bus = bus;
        _f0 = f0;
        _delay = new MeasurementDelay(f0);   // R196: an empty measurement history reads as THIS inverter's nominal
        _pgMw = pgMw;
        _ratedMw = Math.Max(0.0, ratedMw);

        _deadbandHz = cls.Param(AssetParams.DeadbandHz, DefaultDeadbandHz);
        _droopGain = cls.Param(AssetParams.DroopGainMwPerHzPerMw, DefaultDroopGainMwPerHzPerMw);
        _rocofGain = cls.Param(AssetParams.RocofGainMwsPerHz, DefaultRocofGainMwsPerHz);
        _detectionLatencyS = cls.Param(AssetParams.DetectionLatencyS, DefaultDetectionLatencyS);
        _computeLatencyS = cls.Param(AssetParams.ComputeLatencyS, DefaultComputeLatencyS);
        _actuateTauS = cls.Param(AssetParams.ActuateTauS, DefaultActuateTauS);
        _slewUp = cls.Param(AssetParams.FastSlewUpMwPerSPerMw, DefaultFastSlewUpMwPerSPerMw);     // 0 => no ramp limit
        _slewDown = cls.Param(AssetParams.FastSlewDownMwPerSPerMw, DefaultFastSlewDownMwPerSPerMw);
        double headUp = cls.Param(AssetParams.HeadroomUpFraction, DefaultHeadroomUpFraction);
        double headDown = cls.Param(AssetParams.HeadroomDownFraction, DefaultHeadroomDownFraction);
        _capUp = headUp * _ratedMw;
        _capDown = headDown * _ratedMw;
        _dc = DcSources.FromClass(cls, _ratedMw);                        // capacityMwh 0 => unbounded
        _rocofTrip = cls.Param(AssetParams.RocofTripHzPerS, DefaultRocofTripHzPerS);
        // R137 (B4, 2026-10): the one shared fall-back (0.5 s, EREC G99 Issue 2 Table 10.1); the same value as before.
        _rocofTripConfirmS = cls.Param(AssetParams.RocofTripConfirmS, AssetParamDefaults.RocofTripConfirmS);
        _vectorShiftTripDeg = cls.Param(AssetParams.VectorShiftTripDeg, DefaultVectorShiftTripDeg);
        _lvrtVoltagePu = cls.Param(AssetParams.LvrtVoltagePu, DefaultLvrtVoltagePu);      // 0 => never trips on voltage
        _localUplift = cls.Param(AssetParams.LocalRocofUpliftFactor, DefaultLocalRocofUpliftFactor);   // local RoCoF > COI (sub-cycle); 1.0 = no uplift
    }

    public string ClassName { get; }
    public AssetKind Kind => AssetKind.GridFollowingInverter;
    public int Bus { get; }
    public bool Tripped => _tripped;
    public double CurrentMw => _tripped ? 0.0 : _p;
    public double SocFraction => _dc?.SocFraction ?? double.NaN;

    /// <summary>D-DOC-038: an LVRT threshold above 0 reads the bus voltage, which only a network solve provides.</summary>
    public bool UsesNetworkState => _lvrtVoltagePu > 0;

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

    public void Reset()
    {
        _delay.Clear();
        _p = 0; _armAtS = double.PositiveInfinity; _rocofConfirm.Clear(); _insideDeadband.Clear(); _tripped = false; _dc?.Reset();
    }

    public AssetActuation Update(in AssetStepContext ctx)
    {
        if (_tripped) return default;   // no output, no further trip edges

        double dt = ctx.DtSeconds;
        _delay.Push(ctx.ClockSeconds, ctx.LocalHz, ctx.LocalRocofHzPerS, ctx.LocalPhaseDeg);
        // GR-620: a detection latency longer than the stored history used to be shortened to it without a word (the
        // read falls back to the oldest kept sample). Once the history is full that is a wrong measurement, not a
        // start-up transient, so it is refused.
        if (_delay.TruncatesLatency(ctx.ClockSeconds, _detectionLatencyS))
            throw new InvalidOperationException(
                $"{ClassName} at bus {Bus}: the detection latency {_detectionLatencyS.ToString("R", CultureInfo.InvariantCulture)} s is longer "
                + $"than the {MeasurementDelay.Capacity}-sample measurement history holds at this substep "
                + $"({dt.ToString("R", CultureInfo.InvariantCulture)} s); shorten the latency or lengthen the substep.");
        (double fMeas, double rocofMeas, double phaseMeas) = _delay.Read(ctx.ClockSeconds, _detectionLatencyS);

        // --- protect (on the delayed measurement the relay actually acts on) ---
        // The fast LoM relay acts on the sub-cycle LOCAL RoCoF, which the grounded data shows runs ~1.16x
        // above the 1 s COI value the sampler carries (technical ch. 13, docs/technical/0013 Asset-dynamics data grounding.md).
        // Uplift it for the trip only -
        // the slower FFR response effectively acts on the averaged value.
        bool overRocof = Math.Abs(rocofMeas) * _localUplift > _rocofTrip;
        // N235 (October 2026): the confirm time is counted in whole substeps (ConfirmTimer), the D-DOC-095 rule. It was a
        // running sum (_rocofDwellS += dt), which ran short of the confirm by an ulp: a 0.2 s confirm at 20 ms substeps
        // tripped on the 11th step. Every bundled confirm (0, 0.5, 1.0 s) at the substeps used trips on the same step.
        bool rocofTrip = false;
        if (overRocof) rocofTrip = ConfirmTimer.Reached(_rocofConfirm.Count(dt), _rocofTripConfirmS);
        else _rocofConfirm.Clear();
        double phaseStep = _delay.PhaseStep(ctx.ClockSeconds, Math.Max(dt, 0.02)); // deg change over ~a cycle window
        bool vectorTrip = Math.Abs(phaseStep) > _vectorShiftTripDeg;
        bool lvrtTrip = _lvrtVoltagePu > 0 && ctx.LocalVoltagePu < _lvrtVoltagePu;
        if (rocofTrip || vectorTrip || lvrtTrip)
        {
            _tripped = true;
            return new AssetActuation(0.0, TrippedNow: true, TrippedInfeedMw: _pgMw, TrippedInertiaMwS: 0.0);
        }

        // --- compute ---
        double df = fMeas - _f0;
        if (Math.Abs(df) > _deadbandHz)
        {
            // Outside the deadband: arm on the first step out; the response starts after the compute latency.
            _insideDeadband.Clear();
            if (double.IsPositiveInfinity(_armAtS))
                _armAtS = ctx.ClockSeconds + _computeLatencyS;
        }
        else if (!double.IsPositiveInfinity(_armAtS)
                 && ConfirmTimer.Reached(_insideDeadband.Count(dt), _computeLatencyS))
        {
            // R253, grid-following part (October 2026 review, stream B2; Ruling R1: Mark delegated EE calls 2026-10-04):
            // the unit RE-ARMS once the measured frequency has stayed inside its deadband for its own compute latency, so
            // the next excursion meets the latency again. Before, the unit armed on the first excursion and stayed armed
            // for good, so in the live loop (days of simulated time, ambient wander in and out of the 15 mHz deadband)
            // every later event met an already-armed unit and the latency did nothing: the same defect B1 fixed for the
            // fleet units (FleetUnitBank, R253). Same rule as there: GB fast-response services act continuously on the
            // measured frequency (NESO Dynamic Containment service terms: a static delivery curve, no one-shot arming),
            // so a detection / compute delay belongs to every excursion; a hold equal to the latency is the symmetric
            // detection rule (as long to see the frequency come back as to see it leave), and it stops chatter at the
            // deadband edge from re-imposing the delay every step. The hold is counted in whole substeps (ConfirmTimer,
            // the N235 / D-DOC-095 rule), not by subtracting clock readings.
            // Bit-identity: inside the deadband the droop leg is exactly +0.0 armed or not, and with no df/dt leg
            // (rocofGainMwsPerHz 0, every bundled class) the target is +0.0 either way (0.0 + (-0.0) is +0.0), so
            // disarming changes nothing until the frequency leaves again; a zero-latency unit re-arms on that very step
            // (clock + 0 <= clock), so it is unchanged on every input. A unit WITH a df/dt leg no longer drives that leg
            // while the frequency sits inside its deadband after the hold (the leg answers excursions, as the droop leg
            // does); a unit with a latency meets it again on the next excursion. A single-loss study settles outside the
            // deadband, so it is unchanged unless the frequency comes back inside for at least the latency and leaves again.
            _armAtS = double.PositiveInfinity;
            _insideDeadband.Clear();
        }

        double target = 0.0;
        if (ctx.ClockSeconds >= _armAtS)
        {
            // The energy store gates the available headroom: empty can't discharge (inject +), full can't
            // absorb (-); a chemistry pack also caps it at its present BMS power limit.
            double capUp = _capUp, capDown = _capDown;
            if (_dc is not null)
            {
                (double up, double down) = _dc.Headroom();
                capUp = Math.Min(capUp, up);
                capDown = Math.Min(capDown, down);
            }
            double gain = _droopGain * _ratedMw;                                  // MW/Hz
            double fullDeliveryHz = gain > 0 ? Math.Max(capUp, capDown) / gain : 0.5;
            double droopLeg = SwingKernel.FastResponseTarget(df, Math.Max(capUp, capDown), _deadbandHz, fullDeliveryHz);
            double rocofLeg = -_rocofGain * rocofMeas;
            target = Math.Clamp(droopLeg + rocofLeg, -capDown, capUp);
        }

        // --- actuate: ramp-limit the fast slew, then first-order lag ---
        double ramped = target;
        if (_slewUp > 0 || _slewDown > 0)
        {
            double up = (_slewUp > 0 ? _slewUp : double.PositiveInfinity) * _ratedMw * dt;
            double down = (_slewDown > 0 ? _slewDown : double.PositiveInfinity) * _ratedMw * dt;
            ramped = Math.Clamp(target, _p - down, _p + up);
        }
        _p += (ramped - _p) / Math.Max(dt, _actuateTauS) * dt;

        if (_dc is not null) _p = _dc.Draw(_p, dt);   // what the store actually delivered

        return new AssetActuation(_p, TrippedNow: false, TrippedInfeedMw: 0.0, TrippedInertiaMwS: 0.0);
    }
}

/// <summary>
/// A small transport-delay buffer for a controller's local measurement. Push each step; Read returns
/// the most recent sample at least <c>latencyS</c> old (the current sample when latency is 0, so a
/// zero-latency controller is bit-clean). Assumes a roughly constant substep within a run.
///
/// <para>October 2026 review (R006): the history used to be a <see cref="Queue{T}"/> that <see cref="Read"/> and
/// <see cref="PhaseStep"/> each walked from the oldest sample, so once the 512-entry history was full every
/// controller update visited about 1,024 samples (gb-full binds about 2,000 active assets, and the live loop runs up
/// to 4,000 substeps per tick). It is now a fixed ring buffer of the same 512 samples, and both reads find their
/// sample by binary search over the time stamps: at most 10 probes. The answers are BIT-IDENTICAL to the old scans.
/// Both return a stored sample (no arithmetic is done on it, apart from PhaseStep's one subtraction, which is the
/// same subtraction of the same two stored values), and the sample chosen is provably the same one:</para>
/// <list type="bullet">
/// <item><see cref="Read"/> returned the LAST sample of the leading run (oldest first) whose time passes
/// <c>t &lt;= cutoff + 1e-12</c>, or the oldest sample when even the oldest fails. When the stored time stamps
/// never decrease, the test is true for a prefix and false after it, so a binary search for the end of the passing
/// prefix finds exactly that sample.</item>
/// <item><see cref="PhaseStep"/> took as its reference the phase of the LAST passing sample anywhere in the buffer
/// (or the newest sample when none passes, giving latest - latest), and as "latest" the newest sample. With
/// non-decreasing time stamps the last passing sample is the end of the passing prefix, found by the same
/// search.</item>
/// </list>
/// <para>Both arguments need the stored time stamps to be non-decreasing. Every caller's clock only moves forward,
/// but the buffer does not rely on that: each push records whether its time is below the previous one (or either is
/// NaN, which compares false everywhere), and while such a step lies inside the stored window both reads run the
/// original oldest-first scans, so even an out-of-order history gives the old answers.</para>
/// </summary>
internal sealed class MeasurementDelay
{
    /// <summary>How many samples are kept: the same 512 the old queue was trimmed to. That is 10.24 s at the 20 ms
    /// substep (the old comment said about 5 s, R006).</summary>
    internal const int Capacity = 512;

    // The samples, in four parallel ring arrays. Slot(i) maps "i-th stored sample, oldest first" to its ring slot.
    private readonly double[] _t = new double[Capacity];
    private readonly double[] _hz = new double[Capacity];
    private readonly double[] _rocof = new double[Capacity];
    private readonly double[] _phase = new double[Capacity];
    private int _head;    // ring slot of the OLDEST stored sample
    private int _count;   // number of stored samples (0 .. Capacity)

    // Order bookkeeping. _pushes counts every push since Clear; _lastDescent is the number of the most recent push
    // whose time was not >= the previous push's time (a step backwards, or a NaN on either side). The stored window
    // holds pushes (_pushes - _count + 1) .. _pushes. A descent at push d breaks the order inside the window only
    // while its predecessor, push d - 1, is still stored: d - 1 >= _pushes - _count + 1.
    private long _pushes;
    private long _lastDescent = long.MinValue;

    // R196 remainder (D2 nominal frequency, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the frequency
    // an EMPTY history reads as. It used to be the GB constant whatever the owner ran at; an inverter now passes its own
    // nominal f0 (the model's frequency, through AssetDynamicsFleet.Build), so a 60 Hz fleet's first read is 60 Hz,
    // not a 10 Hz "deviation". The default (the GB 50 Hz) is for owners with no model frequency (the free-standing
    // protection devices, whose stages compare against absolute GB thresholds) and leaves them bit-identical.
    private readonly double _emptyHz;

    /// <summary>A delay buffer whose empty history reads as <paramref name="nominalHz"/> (see the field above).</summary>
    public MeasurementDelay(double nominalHz = FrequencyLimits.NominalHz)
    {
        if (!(nominalHz > 0.0) || !double.IsFinite(nominalHz))
            throw new ArgumentOutOfRangeException(nameof(nominalHz), $"The nominal frequency must be positive and finite (got {nominalHz} Hz).");
        _emptyHz = nominalHz;
    }

    public void Clear()
    {
        _head = 0;
        _count = 0;
        _pushes = 0;
        _lastDescent = long.MinValue;
    }

    public void Push(double t, double hz, double rocof, double phase)
    {
        if (_count > 0 && !(t >= _t[Slot(_count - 1)]))
            _lastDescent = _pushes + 1;   // this push breaks the non-decreasing order
        _pushes++;

        int slot;
        if (_count < Capacity) { slot = Slot(_count); _count++; }
        else { slot = _head; _head = _head + 1 == Capacity ? 0 : _head + 1; }   // full: overwrite the oldest (the old Dequeue)
        _t[slot] = t; _hz[slot] = hz; _rocof[slot] = rocof; _phase[slot] = phase;
    }

    /// <summary>GR-620: true when the history is full and even its OLDEST sample is newer than
    /// <paramref name="latencyS"/> before <paramref name="nowS"/>, so <see cref="Read"/> would return a sample less old
    /// than the latency asks (the latency is silently shortened to the stored span). A history that is not yet full is
    /// the ordinary start of a run (the oldest sample is the run's first) and is not reported. A NaN time compares
    /// false and is not reported either (Read's own rule for it is unchanged).</summary>
    public bool TruncatesLatency(double nowS, double latencyS)
        => _count == Capacity && _t[Slot(0)] > nowS - latencyS + 1e-12;

    /// <summary>Ring slot of the i-th stored sample, oldest first (0 = oldest).</summary>
    private int Slot(int i)
    {
        int s = _head + i;
        return s >= Capacity ? s - Capacity : s;
    }

    /// <summary>True when the stored time stamps never decrease from oldest to newest (see the class remarks).
    /// Written without a subtraction on _lastDescent so long.MinValue cannot overflow.</summary>
    private bool Ordered => _lastDescent <= _pushes - _count + 1;

    /// <summary>The number of leading stored samples (oldest first) whose time passes <c>t &lt;= limit</c>. Valid
    /// only for non-decreasing time stamps, where the passing samples are exactly a prefix. The comparison is the
    /// old loop's own expression on the same stored doubles.</summary>
    private int PassingPrefix(double limit)
    {
        int lo = 0, hi = _count;   // invariant: samples [0, lo) pass, samples [hi, _count) fail
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (_t[Slot(mid)] <= limit) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public (double hz, double rocof, double phase) Read(double nowS, double latencyS)
    {
        double cutoff = nowS - latencyS;
        if (_count == 0) return (_emptyHz, 0.0, 0.0);   // empty history: the owner's nominal (R196, see the field)

        int pick;
        if (Ordered)
        {
            // The last sample of the passing prefix; the oldest when none passes (the old loop's starting 'best').
            int passing = PassingPrefix(cutoff + 1e-12);
            pick = passing > 0 ? passing - 1 : 0;
        }
        else
        {
            // Out-of-order history: the original oldest-first scan, stopping at the first failing sample.
            pick = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_t[Slot(i)] <= cutoff + 1e-12) pick = i;   // newest sample at least latency old
                else break;
            }
        }
        int s = Slot(pick);
        return (_hz[s], _rocof[s], _phase[s]);
    }

    /// <summary>Phase-angle change (deg) over the most recent <paramref name="windowS"/> - a vector-shift proxy.</summary>
    public double PhaseStep(double nowS, double windowS)
    {
        if (_count == 0) return 0.0;
        double cutoff = nowS - windowS;
        double latest = _phase[Slot(_count - 1)];   // the old loop's 'latest' always ended on the newest sample
        double refPhase;
        if (Ordered)
        {
            // The old loop's reference ended on the last passing sample, or on the newest when none passes.
            int passing = PassingPrefix(cutoff + 1e-12);
            refPhase = passing > 0 ? _phase[Slot(passing - 1)] : latest;
        }
        else
        {
            // Out-of-order history: the original loop over every sample, oldest first.
            refPhase = 0.0;
            bool haveRef = false;
            for (int i = 0; i < _count; i++)
            {
                int s = Slot(i);
                if (!haveRef || _t[s] <= cutoff + 1e-12) { refPhase = _phase[s]; haveRef = haveRef || _t[s] <= cutoff + 1e-12; }
            }
        }
        return latest - refPhase;
    }
}
