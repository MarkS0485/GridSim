using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Emt.PowerElectronics;

/// <summary>
/// A sampled dq current controller for a grid-connected two-level voltage-source converter, built from the RMS study's control
/// blocks (<see cref="PiController"/>, the R4-08 block library). It is a <see cref="NodalElement"/> with no electrical stamp: at every
/// control sample (every <c>controlEvery</c> solver steps, on the step grid) it reads the three filter currents and the grid voltages
/// from the last accepted step, forms the dq components in a frame at the stated grid angle <c>w t + phi</c> (a stated reference
/// angle: no PLL is modelled here), runs two PI regulators with cross-coupling decoupling and grid-voltage feed-forward, and holds the
/// three modulation signals <c>m_k = v*_k / (Vdc / 2)</c> (clamped to +/-1) until the next sample. The converter's PWM reads
/// <see cref="Reference"/>. The control computation therefore acts one solver step late and the held reference adds a further half
/// sample of delay: the closed loop is the ideal first-order response <c>alpha / (s + alpha)</c> delayed by about one and a half
/// sample periods (the oracle the tests use).
/// <para>Tuning (pole-zero cancellation): <c>Kp = alpha L</c>, <c>Ki = alpha R</c> for a filter of series <c>R</c> and <c>L</c>, which
/// gives the closed-loop bandwidth <c>alpha</c> rad/s.</para>
/// </summary>
public sealed class VscCurrentController : NodalElement
{
    private readonly NodalElement[] _filter;      // three series branches, positive current converter to grid
    private readonly int[] _gridNodes;
    private readonly int _dcP, _dcN;
    private readonly double _w, _phi, _lFilter, _vdcNominal;
    private readonly int _every;
    private readonly PiController _pid, _piq;
    private readonly double _kp, _ki;
    private readonly Func<double, (double Id, double Iq)> _reference;
    private readonly double[] _m = new double[3];
    private long _step;
    private double _dtSeconds, _idMeasured, _iqMeasured;
    private double _vdc;

    /// <summary>Create the controller.</summary>
    /// <param name="name">Element name.</param>
    /// <param name="filterBranches">The three filter branch elements (their current, converter to grid, is the controlled current).</param>
    /// <param name="gridNodes">The three grid-side (PCC) nodes for the feed-forward voltage.</param>
    /// <param name="dcPositive">DC positive node (for the measured link voltage); <paramref name="dcNegative"/> the negative node.</param>
    /// <param name="dcNegative">DC negative node.</param>
    /// <param name="frequencyHz">Grid frequency of the reference frame.</param>
    /// <param name="phaseRad">Frame phase: the d axis is at electrical angle <c>2 pi f t + phi</c> from the phase-a axis.</param>
    /// <param name="rOhm">Filter resistance (tuning).</param>
    /// <param name="lHenry">Filter inductance (tuning and decoupling).</param>
    /// <param name="bandwidthRadPerS">Closed-loop current bandwidth alpha.</param>
    /// <param name="nominalVdcVolts">DC voltage used for the modulation until the first measurement.</param>
    /// <param name="controlEvery">Solver steps per control sample (at least 1).</param>
    /// <param name="reference">The d and q current references (amperes, peak) versus time.</param>
    public VscCurrentController(string name, NodalElement[] filterBranches, int[] gridNodes, int dcPositive, int dcNegative, double frequencyHz, double phaseRad,
        double rOhm, double lHenry, double bandwidthRadPerS, double nominalVdcVolts, int controlEvery, Func<double, (double Id, double Iq)> reference) : base(name)
    {
        if (filterBranches is null || filterBranches.Length != 3) throw new ArgumentException($"{name}: three filter branches are needed.");
        if (gridNodes is null || gridNodes.Length != 3 || gridNodes.Any(g => g < 0)) throw new ArgumentException($"{name}: three non-ground grid nodes are needed.");
        if (!(frequencyHz > 0) || !(rOhm >= 0) || !(lHenry > 0) || !(bandwidthRadPerS > 0) || !(nominalVdcVolts > 0) || controlEvery < 1)
            throw new ArgumentException($"{name}: frequency, inductance, bandwidth and DC voltage must be positive, R not negative, controlEvery at least 1.");
        _filter = (NodalElement[])filterBranches.Clone(); _gridNodes = (int[])gridNodes.Clone(); _dcP = dcPositive; _dcN = dcNegative;
        _w = 2.0 * Math.PI * frequencyHz; _phi = phaseRad; _lFilter = lHenry; _vdcNominal = nominalVdcVolts; _every = controlEvery;
        _kp = bandwidthRadPerS * lHenry; _ki = bandwidthRadPerS * rOhm;
        _pid = new PiController(_kp, _ki); _piq = new PiController(_kp, _ki);
        _reference = reference ?? throw new ArgumentNullException(nameof(reference));
        Reset();
    }

    /// <summary>The modulation reference of leg <paramref name="leg"/> (held between samples); pass it to a <see cref="SinePwm"/>.</summary>
    public double Reference(double t, int leg) => _m[leg];

    /// <summary>The measured d current at the last control sample, amperes.</summary>
    public double MeasuredId => _idMeasured;
    /// <summary>The measured q current at the last control sample, amperes.</summary>
    public double MeasuredIq => _iqMeasured;

    internal override int CurrentChannels => 1;
    internal override void Configure(double dt, IntegrationRule rule) => _dtSeconds = dt;
    internal override void Reset()
    {
        Array.Clear(_m); _step = 0; _idMeasured = 0.0; _iqMeasured = 0.0; _vdc = _vdcNominal;
        // a fresh integral state per run (the blocks keep their own state)
        _pid.Initialize(0.0, 0.0); _piq.Initialize(0.0, 0.0);
    }

    internal override void Accept(double t, double[] v)
    {
        _step++;
        double vdc = V(v, _dcP) - V(v, _dcN);
        if (vdc > 0.0) _vdc = vdc;
        if (_step % _every != 0) return;
        (double s, double c) = DetMath.SinCos(_w * t + _phi);
        const double Th = 0.8660254037844386;
        double i0 = _filter[0].ProbeCurrent(0), i1 = _filter[1].ProbeCurrent(0), i2 = _filter[2].ProbeCurrent(0);
        double v0 = V(v, _gridNodes[0]), v1 = V(v, _gridNodes[1]), v2 = V(v, _gridNodes[2]);
        // amplitude-invariant abc -> dq at the frame angle (d on the cosine axis): d = 2/3 sum x_k cos(th - 120k), q = -2/3 sum x_k sin(th - 120k)
        double c1 = -0.5 * c + Th * s, s1 = -0.5 * s - Th * c;      // cos, sin (th - 120)
        double c2 = -0.5 * c - Th * s, s2 = -0.5 * s + Th * c;      // cos, sin (th + 120)
        double k = 2.0 / 3.0;
        double id = k * (i0 * c + i1 * c1 + i2 * c2), iq = -k * (i0 * s + i1 * s1 + i2 * s2);
        double vgd = k * (v0 * c + v1 * c1 + v2 * c2), vgq = -k * (v0 * s + v1 * s1 + v2 * s2);
        _idMeasured = id; _iqMeasured = iq;
        (double idRef, double iqRef) = _reference(t);
        double ts = _every * _dtSeconds;
        double ud = _pid.Step(idRef - id, ts), uq = _piq.Step(iqRef - iq, ts);
        double vcd = vgd + ud - _w * _lFilter * iq;
        double vcq = vgq + uq + _w * _lFilter * id;
        // dq -> abc
        double va = vcd * c - vcq * s, vb = vcd * c1 - vcq * s1, vc = vcd * c2 - vcq * s2;
        double half = 0.5 * _vdc;
        _m[0] = Math.Max(-1.0, Math.Min(1.0, va / half));
        _m[1] = Math.Max(-1.0, Math.Min(1.0, vb / half));
        _m[2] = Math.Max(-1.0, Math.Min(1.0, vc / half));
    }
}
