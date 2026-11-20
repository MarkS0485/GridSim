using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.Emt.Machines;

// RC4 R4-17. Rotating machines on the R4-16 nodal solver. Both machines are written as a linear flux-linkage system
//     d(psi)/dt = w0 * ( u - R x + w * S psi ),      x = L^-1 psi,
// in per unit on the machine's own base (time in seconds, w0 = 2 pi f0, w the speed in pu), where psi holds the stator (d, q [, 0])
// and rotor flux linkages, x the matching currents (MOTOR convention: currents into the machine), u the applied voltages and S
// the speed-voltage coupling. The trapezoidal (or backward-Euler, after a switching step) discretisation
//     psi(n) = psi(n-1) + dt [ (1-c) F(n-1) + c ( A(n) psi(n) + w0 B u(n) ) ],      c = 1/2 (or 1),   A = w0 ( -R L^-1 + w S ),
// is solved for psi(n) in terms of the stator voltage, which gives the stator a Norton equivalent in machine coordinates
//     x_stator(n) = i_hist + Y v_stator(n),
// transformed to the network phases by the Park (or, for the induction machine, Clarke) transformation and stamped on the nodal
// matrix. The matrix therefore changes at every step (rotor angle, speed) and the solver refactorises each step; the cost is a
// dense LU of the network size. Speed is predicted for the step (w + dt * accel), the electromagnetic torque is evaluated on the
// solved step, and the speed is then corrected by the trapezoidal rule: a one-step predictor on the speed-voltage term only.

/// <summary>Base of the flux-linkage machine elements. Stator terminals are three nodes; the synchronous machine also has a neutral
/// node (the zero-sequence path), the induction machine is three-wire.</summary>
public abstract class FluxMachineElement : NodalElement
{
    private static readonly double SqrtThreeOver2 = Math.Sqrt(3.0) / 2.0;

    private readonly int[] _term;
    private readonly int _neutral;
    private readonly bool _hasNeutral;

    // model, filled by the subclass constructor
    private protected int N, NS;
    private protected double[] Lmat = Array.Empty<double>(), Lam = Array.Empty<double>(), Rd = Array.Empty<double>(), Smat = Array.Empty<double>();
    private protected int FieldIndex = -1;

    // bases
    private protected readonly double W0, Vb, Ib, Zb;

    // state
    private protected double[] Psi = Array.Empty<double>(), FPrev = Array.Empty<double>();
    private protected double Omega, Alpha, Theta, EfdNow, Te;
    private double[] _initPsi = Array.Empty<double>(), _initF = Array.Empty<double>();
    private double _initOmega, _initTheta;
    private protected bool Initialised;

    // step workspace
    private double _dt = double.NaN, _c = 0.5;
    private double[] _k = Array.Empty<double>(), _kinv = Array.Empty<double>(), _rhs = Array.Empty<double>(), _psiH = Array.Empty<double>(), _dPsi = Array.Empty<double>();
    private double[] _iH = Array.Empty<double>(), _y = Array.Empty<double>(), _x = Array.Empty<double>(), _col = Array.Empty<double>();
    private readonly double[] _t = new double[9], _ti = new double[9], _g3 = new double[9], _h3 = new double[3];
    private readonly double[] _iAbc = new double[3], _vs = new double[3];
    private DenseLu? _lu;
    private double _tNow;

    private protected FluxMachineElement(string name, int a, int b, int c, int neutral, bool hasNeutral, double ratingMva, double ratedKvLl, double frequencyHz) : base(name)
    {
        if (a < 0 || b < 0 || c < 0) throw new ArgumentException($"{name}: the three stator terminals must be nodes, not ground.");
        if (a == b || b == c || a == c) throw new ArgumentException($"{name}: the stator terminals must be three different nodes.");
        if (!(ratingMva > 0) || !double.IsFinite(ratingMva) || !(ratedKvLl > 0) || !double.IsFinite(ratedKvLl) || !(frequencyHz > 0) || !double.IsFinite(frequencyHz))
            throw new ArgumentException($"{name}: the rating, rated voltage and frequency must be finite and positive.");
        _term = [a, b, c]; _neutral = neutral; _hasNeutral = hasNeutral;
        W0 = 2.0 * Math.PI * frequencyHz;
        Vb = ratedKvLl * 1000.0 * Math.Sqrt(2.0 / 3.0);          // peak phase-to-neutral, volts
        Ib = (2.0 / 3.0) * ratingMva * 1e6 / Vb;                 // peak phase current, amps (S base = 3/2 Vb Ib)
        Zb = Vb / Ib;                                            // = Vll^2 / S
        RatedFrequencyHz = frequencyHz;
        BaseImpedanceOhm = Zb; BaseVoltagePeakVolts = Vb; BaseCurrentPeakAmps = Ib;
    }

    /// <summary>Rated frequency, hertz.</summary>
    public double RatedFrequencyHz { get; }
    /// <summary>Base impedance <c>kV_ll^2 / MVA</c>, ohms.</summary>
    public double BaseImpedanceOhm { get; }
    /// <summary>Base voltage, peak phase to neutral, volts.</summary>
    public double BaseVoltagePeakVolts { get; }
    /// <summary>Base current, peak, amps (the power base is 3/2 of the product, so that pu power equals the pu of the machine rating).</summary>
    public double BaseCurrentPeakAmps { get; }

    internal override int CurrentChannels => 3;
    internal override bool IsMatrixVarying => true;

    // ---- subclass hooks ------------------------------------------------------------------------------------------

    /// <summary>Predict the step's speed (returned) and set <see cref="Theta"/> for the frame transformation.</summary>
    private protected abstract double Predict(double t, double dt, double c);
    /// <summary>The field voltage in pu at time t (0 for a machine without a field).</summary>
    private protected virtual double FieldVoltageAt(double t) => 0.0;
    /// <summary>The electromagnetic torque in pu from the solved fluxes and currents.</summary>
    private protected abstract double Torque(double[] psi, double[] x);
    /// <summary>Advance the mechanics by one step with torque <paramref name="te"/>; return the corrected speed.</summary>
    private protected abstract double Advance(double te, double t, double dt, double c);
    /// <summary>Sign of the terminal current probe: +1 into the machine (motor convention), -1 out of it (generator convention).</summary>
    private protected abstract double ProbeSign { get; }

    // ---- model assembly ------------------------------------------------------------------------------------------

    /// <summary>Allocate the workspace once <see cref="N"/>, <see cref="NS"/> and the matrices are set.</summary>
    private protected void FinishModel()
    {
        Lam = Invert(Lmat, N);
        _k = new double[N * N]; _kinv = new double[N * N]; _rhs = new double[N]; _psiH = new double[N]; _dPsi = new double[N * NS];
        _iH = new double[NS]; _y = new double[NS * NS]; _x = new double[N]; _col = new double[N];
        Psi = new double[N]; FPrev = new double[N]; _initPsi = new double[N]; _initF = new double[N];
        _lu = new DenseLu(N);
    }

    private static double[] Invert(double[] a, int n)
    {
        var lu = new DenseLu(n);
        if (!lu.Factor(a)) throw new ArgumentException("The machine inductance matrix is singular.");
        var inv = new double[n * n];
        var col = new double[n];
        for (int j = 0; j < n; j++)
        {
            Array.Clear(col);
            col[j] = 1.0;
            lu.Solve(col);
            for (int i = 0; i < n; i++) inv[i * n + j] = col[i];
        }
        return inv;
    }

    /// <summary>Set the initial state (fluxes, speed and angle) the run starts from; also computes the derivative at the start.</summary>
    private protected void SetInitial(double[] psi, double omega, double theta, double vd, double vq, double v0, double efd)
    {
        Array.Copy(psi, _initPsi, N);
        _initOmega = omega; _initTheta = theta;
        double[] x = Mul(Lam, psi, N);
        for (int j = 0; j < N; j++)
        {
            double u = j == 0 ? vd : j == 1 ? vq : (j == 2 && NS == 3) ? v0 : j == FieldIndex ? efd : 0.0;
            double sp = 0.0;
            for (int m = 0; m < N; m++) sp += Smat[j * N + m] * psi[m];
            _initF[j] = W0 * (-Rd[j] * x[j] + omega * sp + u);
        }
        Initialised = true;
        Reset();
    }

    private static double[] Mul(double[] a, double[] x, int n)
    {
        var r = new double[n];
        for (int i = 0; i < n; i++) { double s = 0.0; for (int j = 0; j < n; j++) s += a[i * n + j] * x[j]; r[i] = s; }
        return r;
    }

    // ---- NodalElement --------------------------------------------------------------------------------------------

    internal override void Validate(double dt, int nodeCount)
    {
        if (!Initialised) throw new ArgumentException("the machine has no initial state (call its initialiser first)");
        foreach (int a in _term) if (a >= nodeCount) throw new ArgumentException("a stator node is out of range");
        if (_hasNeutral && _neutral >= nodeCount) throw new ArgumentException("the neutral node is out of range");
    }

    internal override void Configure(double dt, IntegrationRule rule)
    {
        _dt = dt;
        _c = rule == IntegrationRule.BackwardEuler ? 1.0 : 0.5;
    }

    internal override void Reset()
    {
        if (!Initialised) return;
        Array.Copy(_initPsi, Psi, N);
        Array.Copy(_initF, FPrev, N);
        Omega = _initOmega; Theta = _initTheta; Alpha = 0.0; Te = 0.0; _tNow = 0.0;
        Array.Clear(_iAbc);
        ResetMechanics();
    }

    private protected virtual void ResetMechanics() { }

    internal override void PrepareStep(double t, double dt)
    {
        int n = N;
        double c = _c;
        double wp = Predict(t, dt, c);
        EfdNow = FieldVoltageAt(t);
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double a = W0 * (-Rd[i] * Lam[i * n + j] + wp * Smat[i * n + j]);
                _k[i * n + j] = (i == j ? 1.0 : 0.0) - c * dt * a;
            }
        if (!_lu!.Factor(_k)) throw new InvalidOperationException($"{Name}: the machine step matrix is singular at t = {t:R} s.");
        for (int j = 0; j < n; j++)
        {
            Array.Clear(_col);
            _col[j] = 1.0;
            _lu.Solve(_col);
            for (int i = 0; i < n; i++) _kinv[i * n + j] = _col[i];
        }
        for (int i = 0; i < n; i++) _rhs[i] = Psi[i] + (1.0 - c) * dt * FPrev[i];
        if (FieldIndex >= 0) _rhs[FieldIndex] += c * dt * W0 * EfdNow;
        for (int i = 0; i < n; i++)
        {
            double s = 0.0;
            for (int j = 0; j < n; j++) s += _kinv[i * n + j] * _rhs[j];
            _psiH[i] = s;
            for (int r = 0; r < NS; r++) _dPsi[i * NS + r] = c * dt * W0 * _kinv[i * n + r];
        }
        for (int s = 0; s < NS; s++)
        {
            double h = 0.0;
            for (int j = 0; j < n; j++) h += Lam[s * n + j] * _psiH[j];
            _iH[s] = h;
            for (int r = 0; r < NS; r++)
            {
                double y = 0.0;
                for (int j = 0; j < n; j++) y += Lam[s * n + j] * _dPsi[j * NS + r];
                _y[s * NS + r] = y;
            }
        }
        BuildTransform(Theta);
        // G3 = Tinv * Y3 * T and h3 = Tinv * iH3 (a missing zero-sequence part is zero).
        var y3 = new double[9];
        for (int s = 0; s < NS; s++) for (int r = 0; r < NS; r++) y3[s * 3 + r] = _y[s * NS + r];
        var ty = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double a = 0.0;
                for (int m = 0; m < 3; m++) a += _ti[i * 3 + m] * y3[m * 3 + j];
                ty[i * 3 + j] = a;
            }
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double a = 0.0;
                for (int m = 0; m < 3; m++) a += ty[i * 3 + m] * _t[m * 3 + j];
                _g3[i * 3 + j] = a;
            }
        for (int i = 0; i < 3; i++)
        {
            double a = 0.0;
            for (int m = 0; m < NS; m++) a += _ti[i * 3 + m] * _iH[m];
            _h3[i] = a;
        }
    }

    /// <summary>Amplitude-invariant transformation abc to (d, q, 0) at angle <paramref name="theta"/> and its inverse (the induction machine
    /// uses theta = 0, which is the Clarke transformation with alpha = d and beta = q).</summary>
    private void BuildTransform(double theta)
    {
        (double s, double co) = DetMath.SinCos(theta);
        double s1 = -0.5 * s - SqrtThreeOver2 * co, c1 = -0.5 * co + SqrtThreeOver2 * s;       // sin, cos of theta - 120 deg
        double s2 = -0.5 * s + SqrtThreeOver2 * co, c2 = -0.5 * co - SqrtThreeOver2 * s;       // sin, cos of theta + 120 deg
        double k = 2.0 / 3.0;
        _t[0] = k * co; _t[1] = k * c1; _t[2] = k * c2;
        _t[3] = -k * s; _t[4] = -k * s1; _t[5] = -k * s2;
        _ti[0] = co; _ti[1] = -s; _ti[2] = 1.0;
        _ti[3] = c1; _ti[4] = -s1; _ti[5] = 1.0;
        _ti[6] = c2; _ti[7] = -s2; _ti[8] = 1.0;
        _t[6] = _t[7] = _t[8] = 1.0 / 3.0;                       // zero sequence: the mean
    }

    internal override void StampMatrix(NodalStamper s)
    {
        double sc = 1.0 / Zb;
        for (int k = 0; k < 3; k++)
            for (int j = 0; j < 3; j++)
            {
                double g = _g3[k * 3 + j] * sc;
                s.Add(_term[k], _term[j], g);
                if (_hasNeutral)
                {
                    s.Add(_term[k], _neutral, -g);
                    s.Add(_neutral, _term[j], -g);
                    s.Add(_neutral, _neutral, g);
                }
            }
    }

    internal override void StampSources(double t, NodalStamper s)
    {
        for (int k = 0; k < 3; k++)
        {
            double h = Ib * _h3[k];
            s.AddCurrentInto(_term[k], -h);
            if (_hasNeutral) s.AddCurrentInto(_neutral, h);
        }
    }

    internal override void Accept(double t, double[] v)
    {
        int n = N;
        double vn = _hasNeutral ? V(v, _neutral) : 0.0;
        double va = (V(v, _term[0]) - vn) / Vb, vb = (V(v, _term[1]) - vn) / Vb, vc = (V(v, _term[2]) - vn) / Vb;
        for (int s = 0; s < NS; s++) _vs[s] = _t[s * 3] * va + _t[s * 3 + 1] * vb + _t[s * 3 + 2] * vc;
        for (int i = 0; i < n; i++)
        {
            double a = _psiH[i];
            for (int r = 0; r < NS; r++) a += _dPsi[i * NS + r] * _vs[r];
            Psi[i] = a;
        }
        for (int i = 0; i < n; i++)
        {
            double a = 0.0;
            for (int j = 0; j < n; j++) a += Lam[i * n + j] * Psi[j];
            _x[i] = a;
        }
        Te = Torque(Psi, _x);
        double omega = Advance(Te, t, _dt, _c);
        for (int j = 0; j < n; j++)
        {
            double u = j < NS ? _vs[j] : j == FieldIndex ? EfdNow : 0.0;
            double sp = 0.0;
            for (int m = 0; m < n; m++) sp += Smat[j * n + m] * Psi[m];
            FPrev[j] = W0 * (-Rd[j] * _x[j] + omega * sp + u);
        }
        for (int i = 0; i < 3; i++)
        {
            double a = 0.0;
            for (int m = 0; m < NS; m++) a += _ti[i * 3 + m] * _x[m];
            _iAbc[i] = Ib * a;
        }
        _tNow = t;
    }

    internal override double ProbeCurrent(int channel) => ProbeSign * _iAbc[channel];

    // accessors for the subclasses' probes
    private protected double TimeNow => _tNow;
    private protected double[] CurrentsPu => _x;
    private protected double[] StatorVoltagesPu => _vs;
}
