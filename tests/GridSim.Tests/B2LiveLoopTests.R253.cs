using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R253, grid-following part: a grid-following inverter armed its compute latency on the first excursion and stayed
/// armed for good, so in the live loop every later excursion met an already-armed unit and the latency did nothing (the
/// fleet-unit half was fixed in B1, FleetUnitBank). The inverter now re-arms once its measured frequency has stayed
/// inside its deadband for its own compute latency. The pre-B2 class is carried below VERBATIM (from 1.95.217, name
/// changed only) as the bit-identity oracle for every input the change does not reach.
/// </summary>
public sealed partial class B2LiveLoopTests
{
    /// <summary>A frequency that leaves the 15 mHz deadband, comes back inside and leaves again many times, sampled at
    /// a substep that changes (1 ms, 20 ms, 7 ms) as the live loop's does. Returns (Hz, RoCoF, dt) per step.</summary>
    private static List<(double Hz, double Rocof, double Dt)> InAndOut(double depthHz, int steps)
    {
        var s = new List<(double, double, double)>(steps);
        for (int i = 0; i < steps; i++)
        {
            double dt = i % 3 == 0 ? 0.001 : i % 3 == 1 ? 0.02 : 0.007;
            // Square-ish wave: 300 steps out (below nominal by depthHz), 300 steps at 50 Hz + 5 mHz (inside).
            bool outside = (i / 300) % 2 == 0;
            double hz = outside ? 50.0 - depthHz : 50.005;
            double rocof = outside ? -0.05 : 0.01;
            s.Add((hz, rocof, dt));
        }
        return s;
    }

    private static (List<double> Old, List<double> New) DriveBoth(AssetClass cls, List<(double Hz, double Rocof, double Dt)> signal)
    {
        var oldU = new OldGridFollowingInverter(cls, bus: 1, pgMw: 100, ratedMw: 400);
        var newU = new GridFollowingInverter(cls, bus: 1, pgMw: 100, ratedMw: 400);
        var o = new List<double>(signal.Count);
        var n = new List<double>(signal.Count);
        double clock = 0.0, phase = 0.0;
        foreach ((double hz, double rocof, double dt) in signal)
        {
            var ctx = new AssetStepContext(hz, rocof, phase, dt, clock);
            o.Add(oldU.Update(in ctx).ActivePowerMw);
            n.Add(newU.Update(in ctx).ActivePowerMw);
            clock += dt;
            phase += 360.0 * (hz - 50.0) * dt;
        }
        return (o, n);
    }

    [Fact]
    public void R253_gfl_a_zero_latency_unit_is_bit_identical_through_repeated_excursions()
    {
        // Zero compute latency, no df/dt leg (the bundled shape apart from the latency): re-arming can change nothing,
        // so the verbatim old class is the oracle on every step, detection latency and SoC store included.
        AssetClass cls = AssetTestHelpers.GflClass("gfl0", detectionLatencyS: 0.1, computeLatencyS: 0.0, actuateTauS: 0.25);
        (List<double> o, List<double> n) = DriveBoth(cls, InAndOut(0.2, 6000));
        Assert.Contains(o, p => p > 1.0);   // the unit really responded
        for (int i = 0; i < o.Count; i++) AssertBits(o[i], n[i], $"step {i} output");
    }

    [Fact]
    public void R253_gfl_a_unit_whose_frequency_never_comes_back_inside_is_bit_identical()
    {
        // The bundled 20 ms compute latency, one excursion that stays outside the deadband (a single-loss study): the
        // re-arming rule never fires, so the old class is the oracle.
        AssetClass cls = AssetTestHelpers.GflClass("gfl20", detectionLatencyS: 0.1, computeLatencyS: 0.02);
        var signal = new List<(double, double, double)>();
        for (int i = 0; i < 3000; i++) signal.Add((i < 50 ? 50.0 : 49.6 + 0.0001 * (i % 7), i < 50 ? 0.0 : -0.02, 0.02));
        (List<double> o, List<double> n) = DriveBoth(cls, signal);
        for (int i = 0; i < o.Count; i++) AssertBits(o[i], n[i], $"step {i} output");
    }

    [Fact]
    public void R253_gfl_a_unit_meets_its_compute_latency_again_on_a_second_excursion()
    {
        // 1 s compute latency at 20 ms substeps: 5 s out, 5 s back inside (well over the 1 s hold), then out again.
        AssetClass cls = AssetTestHelpers.GflClass("gfl1s", computeLatencyS: 1.0, actuateTauS: 0.05);
        var signal = new List<(double, double, double)>();
        for (int i = 0; i < 250; i++) signal.Add((49.7, 0.0, 0.02));   // first excursion, 5 s
        for (int i = 0; i < 250; i++) signal.Add((50.0, 0.0, 0.02));   // back at nominal, 5 s
        for (int i = 0; i < 25; i++) signal.Add((49.7, 0.0, 0.02));    // second excursion, 0.5 s (inside the latency)
        (List<double> o, List<double> n) = DriveBoth(cls, signal);

        // First excursion: both wait 1 s, then respond.
        Assert.True(Math.Abs(n[40]) < 1e-12, $"inside the latency the unit is idle (got {n[40]:R})");
        Assert.True(n[249] > 1.0, $"after the latency the unit responds (got {n[249]:R})");
        AssertBits(o[249], n[249], "first excursion");
        // Second excursion, 0.5 s in: the old unit was still armed and responded at once; the re-armed unit is waiting
        // out its latency again, its output only decaying from the end of the first excursion.
        Assert.True(o[^1] > 1.0, $"old unit responds at once on the second excursion (got {o[^1]:R})");
        Assert.True(n[^1] < 1e-3, $"re-armed unit waits its latency again (got {n[^1]:R})");
    }

    [Fact]
    public void R253_gfl_a_short_return_inside_the_deadband_does_not_re_arm()
    {
        // Back inside for less than the latency (chatter at the deadband edge): the unit stays armed, exactly as before.
        AssetClass cls = AssetTestHelpers.GflClass("gfl1s", computeLatencyS: 1.0, actuateTauS: 0.05);
        var signal = new List<(double, double, double)>();
        for (int i = 0; i < 100; i++) signal.Add((49.7, 0.0, 0.02));   // 2 s out
        for (int i = 0; i < 40; i++) signal.Add((50.0, 0.0, 0.02));    // 0.8 s inside (< 1 s hold)
        for (int i = 0; i < 100; i++) signal.Add((49.7, 0.0, 0.02));   // out again
        (List<double> o, List<double> n) = DriveBoth(cls, signal);
        for (int i = 0; i < o.Count; i++) AssertBits(o[i], n[i], $"step {i} output");
    }

    [Fact]
    public void R253_gfl_reset_clears_the_hold()
    {
        AssetClass cls = AssetTestHelpers.GflClass("gfl1s", computeLatencyS: 1.0, actuateTauS: 0.05);
        var u = new GridFollowingInverter(cls, bus: 1, pgMw: 100, ratedMw: 400);
        double clock = 0;
        for (int i = 0; i < 100; i++, clock += 0.02) u.Update(new AssetStepContext(49.7, 0.0, 0.0, 0.02, clock));
        for (int i = 0; i < 30; i++, clock += 0.02) u.Update(new AssetStepContext(50.0, 0.0, 0.0, 0.02, clock));   // 0.6 s of the hold
        u.Reset();
        // After the reset the unit is unarmed with no hold carried: 0.5 s into a new excursion it is still waiting.
        double p = 0;
        for (int i = 0; i < 25; i++, clock += 0.02) p = u.Update(new AssetStepContext(49.7, 0.0, 0.0, 0.02, clock)).ActivePowerMw;
        Assert.True(Math.Abs(p) < 1e-12, $"a reset unit meets its latency (got {p:R})");
    }

    // =================================================================================================================
    // The pre-B2 class, verbatim from 1.95.217 (name changed only). Do not edit: it is the oracle.
    // =================================================================================================================

    private sealed class OldGridFollowingInverter : IActiveAssetDynamics
    {
        private readonly double _f0;
        private readonly double _pgMw;      // dispatched output (lost as deficit on trip)
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
        private bool _tripped;

        public OldGridFollowingInverter(AssetClass cls, int bus, double pgMw, double ratedMw, double f0 = FrequencyLimits.NominalHz)
        {
            ClassName = cls.Name;
            Bus = bus;
            _f0 = f0;
            _delay = new MeasurementDelay(f0);   // R196: an empty measurement history reads as THIS inverter's nominal
            _pgMw = pgMw;
            _ratedMw = Math.Max(0.0, ratedMw);

            _deadbandHz = cls.Param(AssetParams.DeadbandHz, 0.015);
            _droopGain = cls.Param(AssetParams.DroopGainMwPerHzPerMw, 2.0);
            _rocofGain = cls.Param(AssetParams.RocofGainMwsPerHz, 0.0);
            _detectionLatencyS = cls.Param(AssetParams.DetectionLatencyS, 0.0);
            _computeLatencyS = cls.Param(AssetParams.ComputeLatencyS, 0.0);
            _actuateTauS = cls.Param(AssetParams.ActuateTauS, 0.25);
            _slewUp = cls.Param(AssetParams.FastSlewUpMwPerSPerMw, 0.0);     // 0 => no ramp limit
            _slewDown = cls.Param(AssetParams.FastSlewDownMwPerSPerMw, 0.0);
            double headUp = cls.Param(AssetParams.HeadroomUpFraction, 0.2);
            double headDown = cls.Param(AssetParams.HeadroomDownFraction, 0.2);
            _capUp = headUp * _ratedMw;
            _capDown = headDown * _ratedMw;
            _dc = DcSources.FromClass(cls, _ratedMw);                        // capacityMwh 0 => unbounded
            _rocofTrip = cls.Param(AssetParams.RocofTripHzPerS, double.PositiveInfinity);
            _rocofTripConfirmS = cls.Param(AssetParams.RocofTripConfirmS, 0.5);
            _vectorShiftTripDeg = cls.Param(AssetParams.VectorShiftTripDeg, double.PositiveInfinity);
            _lvrtVoltagePu = cls.Param(AssetParams.LvrtVoltagePu, 0.0);      // 0 => never trips on voltage
            _localUplift = cls.Param(AssetParams.LocalRocofUpliftFactor, 1.0);   // local RoCoF > COI (sub-cycle); 1.0 = no uplift
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

        public void Reset()
        {
            _delay.Clear();
            _p = 0; _armAtS = double.PositiveInfinity; _rocofConfirm.Clear(); _tripped = false; _dc?.Reset();
        }

        public AssetActuation Update(in AssetStepContext ctx)
        {
            if (_tripped) return default;   // no output, no further trip edges

            double dt = ctx.DtSeconds;
            _delay.Push(ctx.ClockSeconds, ctx.LocalHz, ctx.LocalRocofHzPerS, ctx.LocalPhaseDeg);
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
            if (double.IsPositiveInfinity(_armAtS) && Math.Abs(df) > _deadbandHz)
                _armAtS = ctx.ClockSeconds + _computeLatencyS;

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
}
