using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Emt.FrequencyDependent;

/// <summary>One propagation mode of a line: the characteristic admittance <c>Yc(s)</c>, the delay-removed propagation
/// function <c>Amin(s)</c> (the line's <c>A(s) = exp(-s tau) Amin(s)</c>) and the delay <c>tau</c>.</summary>
/// <param name="Name">A label (for example "zero" or "positive").</param>
/// <param name="Yc">Characteristic admittance, S.</param>
/// <param name="Amin">Delay-removed propagation function (dimensionless).</param>
/// <param name="DelaySeconds">The delay removed from A, seconds.</param>
public sealed record FdMode(string Name, RationalFunction Yc, RationalFunction Amin, double DelaySeconds)
{
    /// <summary>A constant-parameter mode (the Bergeron model of a lossless or distortionless line): <c>Yc = 1/Zc</c>, and
    /// <c>A(s) = h exp(-s tau)</c> where <paramref name="attenuation"/> h = exp(-(R/Zc) length) for a distortionless line
    /// (R/L = G/C), exactly; for any other line h is an approximation at one frequency. h = 1 is the lossless line.</summary>
    public static FdMode Bergeron(string name, double surgeImpedance, double delaySeconds, double attenuation = 1.0)
    {
        if (!(surgeImpedance > 0) || !double.IsFinite(surgeImpedance)) throw new ArgumentException("The surge impedance must be positive and finite.");
        if (!(delaySeconds > 0) || !double.IsFinite(delaySeconds)) throw new ArgumentException("The travel time must be positive and finite.");
        if (!(attenuation > 0 && attenuation <= 1.0)) throw new ArgumentException("The attenuation must be in (0, 1].");
        return new FdMode(name, new RationalFunction(1.0 / surgeImpedance, [], []), new RationalFunction(attenuation, [], []), delaySeconds);
    }
}

/// <summary>
/// A frequency-dependent (or constant-parameter) transmission line between two ends of n conductors, in the modal domain
/// with a constant real orthonormal transformation (exact for a transposed line; the Clarke transformation for three
/// phases). Terminal currents INTO the line at each end are <c>i_k = Yc * v_k - j_k</c> with
/// <c>j_k = A * (Yc * v_m + i_m)</c>, the wave from the other end: Marti's model. The convolutions with <c>Yc</c> and
/// <c>A_min</c> are recursive (piecewise-linear input, exact exponential integration of each real pole, so unconditionally
/// stable), and <c>exp(-s tau)</c> is a delay read from a stored history with linear interpolation (exact when tau is a
/// multiple of the step). Each end therefore stamps only a conductance block; the two ends are coupled through history alone.
/// A constant-parameter line is the special case of <see cref="FdMode.Bergeron"/> (no poles). The delay of every mode must
/// be at least one step.
/// </summary>
public sealed class FdLine : NodalElement
{
    private readonly int[][] _nodes;            // [end][phase]
    private readonly FdMode[] _modes;
    private readonly double[,] _t;              // modal voltage = T * phase voltage; phase = T^T * modal
    private readonly int _np;
    private double _dt;
    private long _n;                             // accepted steps

    // per mode coefficients
    private double[][] _yAlpha = [], _yLam = [], _yMu = [], _aBeta = [], _aLam = [], _aMu = [];
    private double[] _gm = [];
    // per [mode][end] state
    private double[][][] _yState = [], _aState = [], _aNext = [];
    private double[][] _vPrev = [], _hc = [], _j = [];
    private double[][][] _ring = [];
    private double[][] _iModal = [];
    private double[][] _iPhase = [];
    private int[] _ringLen = [];
    private int[] _qFloor = [];
    private double[] _qFrac = [];

    /// <summary>Create the line. For one conductor pass one mode and a null <paramref name="transform"/>; for n conductors
    /// pass n modes and the n by n real orthonormal matrix mapping phase voltages to modal voltages.</summary>
    public FdLine(string name, int[] endA, int[] endB, IReadOnlyList<FdMode> modes, double[,]? transform = null) : base(name)
    {
        _np = endA.Length;
        if (_np == 0 || endB.Length != _np) throw new ArgumentException($"{name}: both ends need the same non-zero number of conductors.");
        if (modes.Count != _np) throw new ArgumentException($"{name}: {_np} conductors need {_np} modes but {modes.Count} were given.");
        for (int i = 0; i < _np; i++)
            if (endA[i] < 0 && endB[i] < 0) throw new ArgumentException($"{name}: conductor {i} has both ends at ground.");
        _nodes = [(int[])endA.Clone(), (int[])endB.Clone()];
        _modes = modes.ToArray();
        _t = new double[_np, _np];
        if (transform is null)
        {
            if (_np != 1) throw new ArgumentException($"{name}: a multi-conductor line needs a modal transformation matrix.");
            _t[0, 0] = 1.0;
        }
        else
        {
            if (transform.GetLength(0) != _np || transform.GetLength(1) != _np) throw new ArgumentException($"{name}: the transformation must be {_np} by {_np}.");
            for (int i = 0; i < _np; i++)
                for (int j = 0; j < _np; j++)
                {
                    double dot = 0.0;
                    for (int k = 0; k < _np; k++) dot += transform[i, k] * transform[j, k];
                    if (Math.Abs(dot - (i == j ? 1.0 : 0.0)) > 1e-9) throw new ArgumentException($"{name}: the modal transformation must be orthonormal.");
                    _t[i, j] = transform[i, j];
                }
        }
        foreach (FdMode m in _modes)
        {
            if (!(m.DelaySeconds > 0) || !double.IsFinite(m.DelaySeconds)) throw new ArgumentException($"{name}: mode '{m.Name}' needs a positive finite delay.");
            foreach (double p in m.Yc.Poles.Concat(m.Amin.Poles))
                if (!(p < 0) || !double.IsFinite(p)) throw new ArgumentException($"{name}: mode '{m.Name}' has a pole that is not strictly negative (unstable).");
            if (m.Yc.Poles.Length != m.Yc.Residues.Length || m.Amin.Poles.Length != m.Amin.Residues.Length)
                throw new ArgumentException($"{name}: mode '{m.Name}' has poles and residues of different counts.");
        }
    }

    /// <summary>The modes.</summary>
    public IReadOnlyList<FdMode> Modes => _modes;

    internal override int CurrentChannels => 2 * _np;

    internal override void Validate(double dt, int nodeCount)
    {
        for (int m = 0; m < _modes.Length; m++)
            if (_modes[m].DelaySeconds < dt * (1.0 - 1e-12))
                throw new ArgumentException($"mode '{_modes[m].Name}' has a travel time of {_modes[m].DelaySeconds:R} s, shorter than the step {dt:R} s; the step must not exceed the shortest travel time.");
        _dt = dt;
        int nm = _modes.Length;
        _ringLen = new int[nm]; _qFloor = new int[nm]; _qFrac = new double[nm];
        _ring = new double[nm][][];
        for (int m = 0; m < nm; m++)
        {
            double q = _modes[m].DelaySeconds / dt;
            double rq = Math.Round(q);
            if (Math.Abs(q - rq) < 1e-9) q = rq;
            _qFloor[m] = (int)Math.Floor(q);
            _qFrac[m] = q - _qFloor[m];
            _ringLen[m] = _qFloor[m] + 4;
            _ring[m] = [new double[_ringLen[m]], new double[_ringLen[m]]];
        }
    }

    internal override void Configure(double dt, IntegrationRule rule)
    {
        int nm = _modes.Length;
        _yAlpha = new double[nm][]; _yLam = new double[nm][]; _yMu = new double[nm][];
        _aBeta = new double[nm][]; _aLam = new double[nm][]; _aMu = new double[nm][];
        _gm = new double[nm];
        for (int m = 0; m < nm; m++)
        {
            Coeffs(_modes[m].Yc.Poles, dt, out _yAlpha[m], out _yLam[m], out _yMu[m]);
            Coeffs(_modes[m].Amin.Poles, dt, out _aBeta[m], out _aLam[m], out _aMu[m]);
            double g = _modes[m].Yc.D;
            for (int p = 0; p < _modes[m].Yc.Poles.Length; p++) g += _modes[m].Yc.Residues[p] * _yLam[m][p];
            _gm[m] = g;
        }
    }

    /// <summary>For each pole p: alpha = exp(p h), lam = I0 - I1/h and mu = I1/h, with I0 = integral_0^h exp(p u) du and
    /// I1 = integral_0^h u exp(p u) du: the exact weights of a piecewise-linear input in the recursive convolution
    /// <c>y(n) = alpha y(n-1) + r (lam x(n) + mu x(n-1))</c>.</summary>
    internal static void Coeffs(double[] poles, double h, out double[] alpha, out double[] lam, out double[] mu)
    {
        int n = poles.Length;
        alpha = new double[n]; lam = new double[n]; mu = new double[n];
        for (int k = 0; k < n; k++)
        {
            double p = poles[k], ph = p * h;
            double e = DetMath.Exp(ph);
            double i0, i1;
            if (Math.Abs(ph) < 1e-3)
            {
                i0 = h * (1.0 + ph / 2.0 + ph * ph / 6.0 + ph * ph * ph / 24.0);
                i1 = h * h * (0.5 + ph / 3.0 + ph * ph / 8.0 + ph * ph * ph / 30.0);
            }
            else
            {
                double em1 = DetMath.Expm1(ph);
                i0 = em1 / p;
                i1 = h * e / p - em1 / (p * p);
            }
            alpha[k] = e;
            mu[k] = i1 / h;
            lam[k] = i0 - i1 / h;
        }
    }

    internal override void Reset()
    {
        int nm = _modes.Length;
        _n = 0;
        _yState = new double[nm][][]; _aState = new double[nm][][]; _aNext = new double[nm][][];
        _vPrev = new double[nm][]; _hc = new double[nm][]; _j = new double[nm][];
        _iModal = new double[nm][];
        for (int m = 0; m < nm; m++)
        {
            _yState[m] = [new double[_modes[m].Yc.Poles.Length], new double[_modes[m].Yc.Poles.Length]];
            _aState[m] = [new double[_modes[m].Amin.Poles.Length], new double[_modes[m].Amin.Poles.Length]];
            _aNext[m] = [new double[_modes[m].Amin.Poles.Length], new double[_modes[m].Amin.Poles.Length]];
            _vPrev[m] = new double[2]; _hc[m] = new double[2]; _j[m] = new double[2];
            _iModal[m] = new double[2];
            if (_ring.Length == nm) foreach (double[] r in _ring[m]) Array.Clear(r);
        }
        _iPhase = [new double[_np], new double[_np]];
    }

    internal override void StampMatrix(NodalStamper s)
    {
        for (int e = 0; e < 2; e++)
            for (int i = 0; i < _np; i++)
                for (int j = 0; j < _np; j++)
                {
                    double g = 0.0;
                    for (int m = 0; m < _modes.Length; m++) g += _t[m, i] * _gm[m] * _t[m, j];
                    s.Add(_nodes[e][i], _nodes[e][j], g);
                }
    }

    private double Ring(int m, int end, long k) => k < 0 ? 0.0 : _ring[m][end][(int)(k % _ringLen[m])];

    /// <summary>The wave leaving <paramref name="end"/> of mode m, read at time (step n - delay), interpolated.</summary>
    private double Delayed(int m, int end, long n)
    {
        long k0 = n - _qFloor[m] - (_qFrac[m] > 0 ? 1 : 0);
        double fr = _qFrac[m] > 0 ? 1.0 - _qFrac[m] : 0.0;
        double a = Ring(m, end, k0);
        return fr == 0.0 ? a : (1.0 - fr) * a + fr * Ring(m, end, k0 + 1);
    }

    internal override void StampSources(double t, NodalStamper s)
    {
        long nn = _n + 1;
        var hPhase = new double[2][];
        hPhase[0] = new double[_np]; hPhase[1] = new double[_np];
        for (int m = 0; m < _modes.Length; m++)
        {
            FdMode md = _modes[m];
            for (int e = 0; e < 2; e++)
            {
                int other = 1 - e;
                double hc = 0.0;
                for (int p = 0; p < md.Yc.Poles.Length; p++)
                    hc += _yAlpha[m][p] * _yState[m][e][p] + md.Yc.Residues[p] * _yMu[m][p] * _vPrev[m][e];
                double xn = Delayed(m, other, nn);
                double xn1 = Delayed(m, other, nn - 1);
                double j = md.Amin.D * xn;
                for (int q = 0; q < md.Amin.Poles.Length; q++)
                {
                    double z = _aBeta[m][q] * _aState[m][e][q] + md.Amin.Residues[q] * (_aLam[m][q] * xn + _aMu[m][q] * xn1);
                    _aNext[m][e][q] = z;
                    j += z;
                }
                _hc[m][e] = hc;
                _j[m][e] = j;
                double hm = hc - j;
                for (int i = 0; i < _np; i++) hPhase[e][i] += _t[m, i] * hm;
            }
        }
        for (int e = 0; e < 2; e++)
            for (int i = 0; i < _np; i++) s.AddCurrentInto(_nodes[e][i], -hPhase[e][i]);
    }

    internal override void Accept(double t, double[] v)
    {
        long nn = _n + 1;
        Array.Clear(_iPhase[0]); Array.Clear(_iPhase[1]);
        for (int m = 0; m < _modes.Length; m++)
        {
            FdMode md = _modes[m];
            for (int e = 0; e < 2; e++)
            {
                double vm = 0.0;
                for (int i = 0; i < _np; i++) vm += _t[m, i] * V(v, _nodes[e][i]);
                double yc = _gm[m] * vm + _hc[m][e];
                double im = yc - _j[m][e];
                double w = yc + im;
                for (int p = 0; p < md.Yc.Poles.Length; p++)
                    _yState[m][e][p] = _yAlpha[m][p] * _yState[m][e][p] + md.Yc.Residues[p] * (_yLam[m][p] * vm + _yMu[m][p] * _vPrev[m][e]);
                Array.Copy(_aNext[m][e], _aState[m][e], _aNext[m][e].Length);
                _vPrev[m][e] = vm;
                _ring[m][e][(int)(nn % _ringLen[m])] = w;
                _iModal[m][e] = im;
                for (int i = 0; i < _np; i++) _iPhase[e][i] += _t[m, i] * im;
            }
        }
        _n = nn;
    }

    internal override double ProbeCurrent(int channel) => _iPhase[channel / _np][channel % _np];
}
