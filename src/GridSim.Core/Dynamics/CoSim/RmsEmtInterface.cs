using System.Numerics;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.CoSim;

/// <summary>How the two parts of a co-simulation are joined at the boundary.</summary>
public enum CoSimForm
{
    /// <summary>The RMS side gives the EMT side a Thevenin equivalent (a three-phase sinusoidal EMF behind a series R-L fitted at the fundamental);
    /// the EMT side gives the RMS side the positive-sequence boundary CURRENT phasor.</summary>
    Thevenin,
    /// <summary>The RMS side gives the EMT side a Norton equivalent (a three-phase sinusoidal current source in parallel with a shunt R and L fitted at the
    /// fundamental); the EMT side gives the RMS side the positive-sequence boundary VOLTAGE phasor.</summary>
    Norton,
}

/// <summary>The RMS-side equivalent seen from the boundary: <c>V_B = Emf - Z I</c>, with <c>I</c> the current drawn from the boundary into the EMT side.
/// Phasors are PEAK values in the cosine convention: <c>x(t) = Re{X e^(j w t)}</c>, phase a; phases b and c lag by 120 and 240 degrees.</summary>
public readonly record struct BoundaryEquivalent(Complex Emf, Complex Impedance);

/// <summary>
/// The RMS (phasor, positive-sequence, fundamental-frequency) part of a co-simulation, built ALONGSIDE the existing RMS transient kernel: that kernel
/// runs a whole case and has no entry that advances it to a time and hands back or accepts a boundary quantity, so it cannot be one side of an
/// exchange without core edits (a seam, reported). This side is a source behind a lossless line, solved as phasors:
/// <c>V_s = A V_r + B I_r</c> with <c>A = cos(w tau)</c>, <c>B = j Zc sin(w tau)</c> gives the Thevenin equivalent at the receiving end
/// <c>Emf = V_s / A</c>, <c>Z = B / A = j Zc tan(w tau)</c> (valid below the first line resonance, <c>w tau &lt; pi/2</c>). A source-voltage step can be
/// scheduled; it takes effect at the first EXCHANGE at or after its time.
/// </summary>
public sealed class PhasorSide
{
    private readonly Complex _a, _b;
    private Complex _source;
    private readonly List<(double T, Complex Phasor)> _schedule = new();
    private int _next;

    /// <summary>A source of peak phasor <paramref name="sourcePhasor"/> behind a lossless line of surge impedance <paramref name="zcOhm"/> and travel time
    /// <paramref name="travelSeconds"/> at <paramref name="frequencyHz"/>.</summary>
    public PhasorSide(Complex sourcePhasor, double frequencyHz, double zcOhm, double travelSeconds)
    {
        if (!(frequencyHz > 0) || !(zcOhm > 0) || !(travelSeconds > 0)) throw new ArgumentException("Frequency, surge impedance and travel time must be positive.");
        double th = 2.0 * Math.PI * frequencyHz * travelSeconds;
        if (!(th < Math.PI / 2.0)) throw DomainRefusal.Mark(new ArgumentException("The line is longer than a quarter wave at the fundamental: the phasor equivalent is not a simple inductive Thevenin impedance."));
        _a = DetMath.Cos(th);
        _b = new Complex(0.0, zcOhm * DetMath.Sin(th));
        _source = sourcePhasor;
    }

    /// <summary>A source behind a series impedance (a lumped line or transformer).</summary>
    public PhasorSide(Complex sourcePhasor, Complex seriesImpedance)
    {
        _a = Complex.One; _b = seriesImpedance; _source = sourcePhasor;
    }

    /// <summary>The Thevenin equivalent at the boundary now.</summary>
    public BoundaryEquivalent Equivalent => new(_source / _a, _b / _a);

    /// <summary>Schedule a new source phasor from time <paramref name="atSeconds"/> (applied at the first exchange at or after it; schedule in time order).</summary>
    public void ScheduleSourceChange(double atSeconds, Complex newSourcePhasor)
    {
        if (_schedule.Count > 0 && atSeconds < _schedule[^1].T) throw new ArgumentException("Schedule changes in time order.");
        _schedule.Add((atSeconds, newSourcePhasor));
    }

    /// <summary>Back to the initial source (called at the start of a run).</summary>
    internal void Reset(Complex initialSource) { _source = initialSource; _next = 0; }

    internal Complex InitialSource { get; private set; }
    internal void Capture() => InitialSource = _source;

    /// <summary>Apply the changes due at exchange time <paramref name="t"/> and return the equivalent.</summary>
    internal BoundaryEquivalent Advance(double t)
    {
        while (_next < _schedule.Count && t >= _schedule[_next].T - 1e-9) _source = _schedule[_next++].Phasor;
        return Equivalent;
    }

    /// <summary>The boundary voltage for a load current <paramref name="current"/> drawn from the boundary: <c>Emf - Z I</c>.</summary>
    public Complex BoundaryVoltage(Complex current)
    {
        BoundaryEquivalent e = Equivalent;
        return e.Emf - e.Impedance * current;
    }
}

/// <summary>Interface settings.</summary>
/// <param name="Form">Thevenin or Norton.</param>
/// <param name="ExchangeEverySteps">EMT steps between exchanges (the fixed exchange interval is this times the step).</param>
/// <param name="FrequencyHz">The fundamental the phasors are taken at.</param>
public sealed record CoSimOptions(CoSimForm Form, int ExchangeEverySteps, double FrequencyHz);

/// <summary>One exchange (a row of the co-simulation log).</summary>
/// <param name="TimeSeconds">The exchange time (the end of an EMT step on the exchange grid).</param>
/// <param name="EmtVoltage">The positive-sequence boundary voltage phasor the EMT side measured (one fundamental cycle ending at this time), peak, V.</param>
/// <param name="EmtCurrent">The positive-sequence boundary current phasor drawn into the EMT side, peak, A.</param>
/// <param name="RmsVoltage">The boundary voltage the RMS side computes from that current (Thevenin form) or the EMT voltage it was handed (Norton form).</param>
/// <param name="Equivalent">The equivalent the RMS side handed to the EMT side, held until the next exchange.</param>
/// <param name="MeasurementValid">False in the first cycle, before a full measurement window exists (the previous equivalent is held).</param>
public sealed record ExchangeRecord(double TimeSeconds, Complex EmtVoltage, Complex EmtCurrent, Complex RmsVoltage, BoundaryEquivalent Equivalent, bool MeasurementValid);

/// <summary>
/// The RMS-EMT co-simulation interface: it places the RMS equivalent of the rest of the network in an EMT network at a three-phase boundary and runs a
/// FIXED, deterministic exchange schedule. Stated contract:
/// <list type="bullet">
/// <item><b>Schedule.</b> Every <c>ExchangeEverySteps</c> EMT steps, at the end of the step (after it is accepted): (1) the EMT side measures the boundary by a
/// discrete Fourier transform over the last fundamental cycle (it needs the cycle to be a whole number of steps) and forms positive-sequence phasors;
/// (2) the RMS side takes the measured quantity (Thevenin form: the current; Norton form: the voltage), applies any scheduled RMS event due at this time and returns
/// its equivalent; (3) the EMT source is replaced by the new equivalent, a zero-order hold of the phasor with continuous phase (the sinusoid <c>Re{E e^(j w t)}</c>), used from
/// the next step. An RMS event therefore reaches the EMT side with a latency of at most one exchange interval plus one step.</item>
/// <item><b>Equivalent.</b> The impedance is fitted at the fundamental only: a series R-L (Thevenin) or a parallel R and L (Norton). It is constant for the run; the
/// EMF (or the equivalent current) changes at exchanges. The equivalent reproduces the RMS side at the fundamental and is wrong off it: the high-frequency behaviour of
/// the RMS side (travelling waves, resonances) is not represented, which is the band an RMS model cannot represent.</item>
/// <item><b>Reproducibility.</b> Bit-repeatable with itself (no threads, fixed order). It is NOT bit-comparable with the all-EMT run or the RMS run of the same
/// network: the equivalent is an approximation by construction.</item>
/// </list>
/// </summary>
public sealed class RmsEmtInterface
{
    private readonly PhasorSide _rms;
    private readonly CoSimOptions _opt;
    private readonly List<ExchangeRecord> _log = new();
    private readonly double _w;
    private BoundaryEquivalent _held;

    private RmsEmtInterface(string name, PhasorSide rms, CoSimOptions opt)
    {
        _rms = rms; _opt = opt; _w = 2.0 * Math.PI * opt.FrequencyHz;
    }

    /// <summary>The exchange log.</summary>
    public IReadOnlyList<ExchangeRecord> Exchanges => _log;

    /// <summary>The interface description for a result: form, exchange interval, schedule, latency and the non-comparability statement.</summary>
    public string Description { get; private set; } = "";

    /// <summary>The equivalent held at the moment (the last one handed to the EMT side).</summary>
    public BoundaryEquivalent Held => _held;

    /// <summary>Add the equivalent and the exchange machinery to <paramref name="net"/> at the boundary nodes <paramref name="boundary"/> (three non-ground
    /// nodes, phases a, b, c). <paramref name="stepSeconds"/> is the solver step.</summary>
    public static RmsEmtInterface Add(NodalNetwork net, string name, string nodePrefix, int[] boundary, PhasorSide rms, CoSimOptions options, double stepSeconds)
    {
        ArgumentNullException.ThrowIfNull(net);
        ArgumentNullException.ThrowIfNull(rms);
        ArgumentNullException.ThrowIfNull(options);
        static Exception Refuse(string m) => DomainRefusal.Mark(new ArgumentException(m));
        if (boundary is null || boundary.Length != 3 || boundary.Any(b => b < 0)) throw Refuse($"{name}: three non-ground boundary nodes are needed.");
        if (options.ExchangeEverySteps < 1 || !(options.FrequencyHz > 0) || !(stepSeconds > 0)) throw Refuse($"{name}: exchange interval, frequency and step must be positive.");
        double cycle = 1.0 / (options.FrequencyHz * stepSeconds);
        int n = (int)Math.Round(cycle);
        if (Math.Abs(cycle - n) > 1e-6 * cycle || n < 8) throw Refuse($"{name}: one cycle ({1.0 / options.FrequencyHz:R} s) must be a whole number (at least 8) of {stepSeconds:R} s steps for the phasor measurement.");
        rms.Capture();
        var itf = new RmsEmtInterface(name, rms, options) { _held = rms.Equivalent };
        BoundaryEquivalent eq = itf._held;
        double w = itf._w;
        if (!(eq.Impedance.Imaginary > 0) || eq.Impedance.Real < 0) throw Refuse($"{name}: the equivalent impedance {eq.Impedance} is not inductive (R >= 0, X > 0); a series R-L or parallel R-L cannot represent it.");
        var meas = new double[3][];
        var cur = new double[3][];
        for (int k = 0; k < 3; k++) { meas[k] = new double[n]; cur[k] = new double[n]; }
        NodalElement[] iSense = new NodalElement[3];
        double Re(Complex e, double t, int k)
        {
            (double s, double c) = DetMath.SinCos(w * t - k * (2.0 * Math.PI / 3.0));
            return e.Real * c - e.Imaginary * s;
        }
        if (options.Form == CoSimForm.Thevenin)
        {
            double r = eq.Impedance.Real, l = eq.Impedance.Imaginary / w;
            for (int k = 0; k < 3; k++)
            {
                int kk = k;
                int gen = net.Node($"{nodePrefix}{name}.gen{k}");
                net.Add(new NodalVoltageSource($"{name}.e{k}", gen, 0.0, t => Re(itf._held.Emf, t, kk)));
                iSense[k] = net.Add(NodalBranch.SeriesRl($"{name}.z{k}", gen, boundary[k], r, l));
            }
        }
        else
        {
            Complex y = Complex.One / eq.Impedance;
            double g = y.Real, b = -y.Imaginary;           // inductive: y = g - j b
            if (!(b > 0)) throw Refuse($"{name}: the Norton admittance is not inductive.");
            for (int k = 0; k < 3; k++)
            {
                int kk = k;
                net.Add(new NodalCurrentSource($"{name}.i{k}", boundary[k], t => Re(itf._held.Emf / itf._held.Impedance, t, kk)));
                if (g > 0) net.Add(NodalBranch.Resistor($"{name}.rp{k}", boundary[k], NodalNetwork.Ground, 1.0 / g));
                iSense[k] = net.Add(NodalBranch.Inductor($"{name}.lp{k}", boundary[k], NodalNetwork.Ground, 1.0 / (w * b)));
            }
        }
        net.Add(new Monitor($"{name}.mon", itf, boundary, iSense, options, n, meas, cur));
        itf.Description = $"RMS-EMT interface '{name}': {options.Form} equivalent at the fundamental ({options.FrequencyHz:R} Hz) at a three-phase boundary; fixed exchange every "
            + $"{options.ExchangeEverySteps} step(s) = {options.ExchangeEverySteps * stepSeconds:R} s; EMT to RMS: positive-sequence "
            + (options.Form == CoSimForm.Thevenin ? "current" : "voltage") + $" phasor from a {n}-step fundamental-cycle DFT; RMS to EMT: zero-order-hold phasor with continuous phase; "
            + "an RMS event reaches the EMT side after at most one exchange interval plus one step; the equivalent impedance is fixed for the run and fitted at the fundamental only, "
            + "so what the RMS side does off the fundamental (waves, resonances) is not represented; bit-repeatable with itself, not bit-comparable with the all-EMT or the all-RMS run.";
        return itf;
    }

    /// <summary>The positive-sequence peak phasor of three sampled phases over the window ending at time <paramref name="tEnd"/> (n samples of step dt).</summary>
    internal static Complex PositiveSequence(double[][] ring, int head, int n, double tEnd, double dt, double w)
    {
        var p = new Complex[3];
        for (int k = 0; k < 3; k++)
        {
            double re = 0.0, im = 0.0;
            for (int m = 0; m < n; m++)
            {
                int idx = (head + 1 + m) % n;                   // oldest first
                double t = tEnd - (n - 1 - m) * dt;
                (double s, double c) = DetMath.SinCos(w * t);
                double x = ring[k][idx];
                re += x * c; im -= x * s;
            }
            p[k] = new Complex(2.0 * re / n, 2.0 * im / n);
        }
        var a = new Complex(-0.5, Math.Sqrt(3.0) / 2.0);
        return (p[0] + a * p[1] + a * a * p[2]) / 3.0;
    }

    /// <summary>The element that records the boundary every step and performs the exchange on the schedule.</summary>
    private sealed class Monitor : NodalElement
    {
        private readonly RmsEmtInterface _i;
        private readonly int[] _b;
        private readonly NodalElement[] _sense;
        private readonly CoSimOptions _o;
        private readonly int _n;
        private readonly double[][] _v, _c;
        private double _dt;
        private long _step;
        private int _head = -1;

        internal Monitor(string name, RmsEmtInterface i, int[] b, NodalElement[] sense, CoSimOptions o, int n, double[][] v, double[][] c) : base(name)
        {
            _i = i; _b = b; _sense = sense; _o = o; _n = n; _v = v; _c = c;
        }

        internal override void Configure(double dt, IntegrationRule rule) => _dt = dt;

        internal override void Reset()
        {
            _step = 0; _head = -1;
            foreach (double[] a in _v) Array.Clear(a);
            foreach (double[] a in _c) Array.Clear(a);
            _i._log.Clear();
            _i._rms.Reset(_i._rms.InitialSource);
            _i._held = _i._rms.Equivalent;
        }

        internal override void Accept(double t, double[] v)
        {
            _step++;
            _head = (_head + 1) % _n;
            for (int k = 0; k < 3; k++)
            {
                _v[k][_head] = V(v, _b[k]);
                // Thevenin form: the series branch current (equivalent to boundary) is the current drawn by the EMT side. The Norton form
                // measures the voltage only and derives the current from the held equivalent.
                _c[k][_head] = _o.Form == CoSimForm.Thevenin ? _sense[k].ProbeCurrent(0) : 0.0;
            }
            if (_step % _o.ExchangeEverySteps != 0) return;
            bool valid = _step >= _n;
            Complex vb = Complex.Zero, ib = Complex.Zero;
            if (valid)
            {
                vb = PositiveSequence(_v, _head, _n, t, _dt, _i._w);
                ib = _o.Form == CoSimForm.Thevenin ? PositiveSequence(_c, _head, _n, t, _dt, _i._w) : (_i._held.Emf - vb) / _i._held.Impedance;
            }
            BoundaryEquivalent eq = _i._rms.Advance(t);
            Complex rmsV = _o.Form == CoSimForm.Thevenin ? (valid ? _i._held.Emf - _i._held.Impedance * ib : Complex.Zero) : vb;
            _i._held = eq;
            _i._log.Add(new ExchangeRecord(t, vb, ib, rmsV, eq, valid));
        }
    }
}
