using System;
using System.Collections.Generic;

namespace GridSim.Core.Dynamics.Models.Wecc;

// The mechanical-side WECC wind turbine models, after EPRI 3002006525 App. C (Figs. C-1 to C-4): the two-mass drive train
// WTGT_A (PSS/E WTDTA1), the linearised aerodynamics WTGAR_A (WTARA1), the pitch controller WTGPT_A (WTPTA1) and the torque
// controller WTGTRQ_A (WTTQA1). A type 4 turbine may use WTGT_A alone (mechanical power held; EPRI 3002006525 section
// 3.3, footnote 6); a type 3 turbine uses all four with REEC_A PFlag = 1. Per unit on the unit's MBASE; speeds per unit;
// pitch in degrees (DECISIONS D12).

/// <summary>WTGT_A (PSS/E WTDTA1): the two-mass drive train. H total inertia (s), DAMP generator-side damping (pu power per
/// pu speed), Htfrac turbine share of H (0 = one lumped mass), Freq1 first torsional mode (Hz), Dshaft shaft damping.</summary>
public sealed record WtgtaParameters
{
    public double H { get; init; } = 4.0;
    public double Damp { get; init; }
    public double Htfrac { get; init; } = 0.9;
    public double Freq1 { get; init; } = 1.8;
    public double Dshaft { get; init; } = 1.5;

    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        const string m = "WTGT_A";
        WeccCheck.Above(p, m, "H", H, 0.0);
        WeccCheck.AtLeast(p, m, "DAMP", Damp, 0.0);
        if (!double.IsFinite(Htfrac) || Htfrac < 0.0 || Htfrac >= 1.0) p.Add($"{m} Htfrac = {WeccCheck.N(Htfrac)}: must be in [0, 1) (0 = one lumped mass)");
        if (Htfrac > 0.0) WeccCheck.Above(p, m, "Freq1", Freq1, 0.0);
        else WeccCheck.AtLeast(p, m, "Freq1", Freq1, 0.0);
        WeccCheck.AtLeast(p, m, "Dshaft", Dshaft, 0.0);
        return p;
    }
}

/// <summary>WTGAR_A (PSS/E WTARA1): Pm = Pm0 - Ka theta (theta - theta0), with the pitch theta and the initial pitch theta0 in
/// degrees, so Ka is pu power per degree squared (EPRI 3002006525 section 3.1 quotes a typical 0.007).</summary>
public sealed record WtgaraParameters
{
    public double Ka { get; init; } = 0.007;
    public double Theta0 { get; init; }

    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        WeccCheck.AtLeast(p, "WTGAR_A", "Ka", Ka, 0.0);
        WeccCheck.Finite(p, "WTGAR_A", "theta0", Theta0);
        return p;
    }
}

/// <summary>WTGPT_A (PSS/E WTPTA1): the pitch controller (EPRI Fig. C-3). Angles in degrees, rates in degrees per second.</summary>
public sealed record WtgptaParameters
{
    public double Kiw { get; init; } = 25.0;
    public double Kpw { get; init; } = 150.0;
    public double Kic { get; init; } = 30.0;
    public double Kpc { get; init; } = 3.0;
    public double Kcc { get; init; }
    /// <summary>Pitch actuator lag T-theta (s).</summary>
    public double Tp { get; init; } = 0.3;
    public double ThetaMax { get; init; } = 27.0;
    public double ThetaMin { get; init; }
    public double RThetaMax { get; init; } = 10.0;
    public double RThetaMin { get; init; } = -10.0;

    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        const string m = "WTGPT_A";
        WeccCheck.Finite(p, m, "Kiw", Kiw);
        WeccCheck.Finite(p, m, "Kpw", Kpw);
        WeccCheck.Finite(p, m, "Kic", Kic);
        WeccCheck.Finite(p, m, "Kpc", Kpc);
        WeccCheck.Finite(p, m, "Kcc", Kcc);
        WeccCheck.AtLeast(p, m, "Tp", Tp, 0.0);
        WeccCheck.Finite(p, m, "TetaMax", ThetaMax);
        WeccCheck.Finite(p, m, "TetaMin", ThetaMin);
        WeccCheck.Ordered(p, m, "TetaMin", ThetaMin, "TetaMax", ThetaMax);
        WeccCheck.Above(p, m, "RTetaMax", RThetaMax, 0.0);
        if (!double.IsFinite(RThetaMin) || !(RThetaMin < 0.0)) p.Add($"{m} RTetaMin = {WeccCheck.N(RThetaMin)}: must be a finite number below 0");
        return p;
    }
}

/// <summary>WTGTRQ_A (PSS/E WTTQA1): the torque controller (EPRI Fig. C-4). Tflag 1 = power (torque) control on
/// (Pref0 - Pe_filt)/omega_g, 0 = speed control on omega_g - omega_ref; the speed reference is a four-point power-speed
/// table (p1..p4 pu power, spd1..spd4 pu speed) of the filtered power. TRATE (MW) is the turbine rating; GridSim requires
/// it to equal the unit's MBASE (or be 0 for "the MBASE"), since the scaling between the two bases is not publicly
/// specified.</summary>
public sealed record WtgtrqaParameters
{
    public int Tflag { get; init; }
    public double Kpp { get; init; } = 3.0;
    public double Kip { get; init; } = 0.6;
    public double Tp { get; init; } = 0.05;
    public double Twref { get; init; } = 60.0;
    public double Temax { get; init; } = 1.2;
    public double Temin { get; init; }
    public IReadOnlyList<double> P { get; init; } = new[] { 0.2, 0.4, 0.6, 0.8 };
    public IReadOnlyList<double> Spd { get; init; } = new[] { 0.58, 0.72, 0.86, 1.0 };
    public double Trate { get; init; }

    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        const string m = "WTGTRQ_A";
        WeccCheck.Flag(p, m, "Tflag", Tflag, 0, 1);
        WeccCheck.Finite(p, m, "Kpp", Kpp);
        WeccCheck.Finite(p, m, "Kip", Kip);
        WeccCheck.AtLeast(p, m, "Tp", Tp, 0.0);
        WeccCheck.AtLeast(p, m, "Twref", Twref, 0.0);
        WeccCheck.Finite(p, m, "Temax", Temax);
        WeccCheck.Finite(p, m, "Temin", Temin);
        WeccCheck.Ordered(p, m, "Temin", Temin, "Temax", Temax);
        WeccCheck.Table(p, m, "p", P, "spd", Spd);
        for (int i = 0; i < Spd.Count; i++) if (double.IsFinite(Spd[i]) && !(Spd[i] > 0.0)) p.Add($"{m} spd{i + 1} = {WeccCheck.N(Spd[i])}: a speed reference must be above 0");
        WeccCheck.AtLeast(p, m, "TRATE", Trate, 0.0);
        return p;
    }
}

/// <summary>The wind turbine mechanics of a unit: a drive train, and for a type 3 turbine the aerodynamics, pitch and torque
/// controllers. Either the drive train alone (type 4) or all four (type 3).</summary>
public sealed record WindParameters(WtgtaParameters DriveTrain, WtgaraParameters? Aero = null, WtgptaParameters? Pitch = null,
    WtgtrqaParameters? Torque = null)
{
    /// <summary>True for the full type 3 set.</summary>
    public bool IsType3 => Aero is not null && Pitch is not null && Torque is not null;

    public string Chain => IsType3 ? "WTGT_A + WTGAR_A + WTGPT_A + WTGTRQ_A" : "WTGT_A";

    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        if (DriveTrain is null) { p.Add("wind turbine mechanics need a WTGT_A drive train"); return p; }
        p.AddRange(DriveTrain.Check());
        int n = (Aero is null ? 0 : 1) + (Pitch is null ? 0 : 1) + (Torque is null ? 0 : 1);
        if (n != 0 && n != 3)
            p.Add("a type 3 turbine needs WTGAR_A, WTGPT_A and WTGTRQ_A together (the aerodynamics take the pitch, the pitch takes the "
                + "torque controller's speed reference); give all three, or only WTGT_A for a type 4 turbine");
        if (Aero is not null) p.AddRange(Aero.Check());
        if (Pitch is not null) p.AddRange(Pitch.Check());
        if (Torque is not null) p.AddRange(Torque.Check());
        if (Aero is not null && Pitch is not null && double.IsFinite(Aero.Theta0)
            && (Aero.Theta0 < Pitch.ThetaMin || Aero.Theta0 > Pitch.ThetaMax))
            p.Add($"WTGAR_A theta0 = {WeccCheck.N(Aero.Theta0)}: the initial pitch must be inside [TetaMin, TetaMax] = [{WeccCheck.N(Pitch.ThetaMin)}, {WeccCheck.N(Pitch.ThetaMax)}]");
        return p;
    }
}

/// <summary>
/// WTGT_A drive train (EPRI Fig. C-1). Speed deviations from the initial speed omega0 (pu), shaft twist in pu.s:
/// <code>
///   2 Ht dwt/dt = Pm / omega_t - K theta - Dshaft (wt - wg)
///   2 Hg dwg/dt = -Pe / omega_g + K theta + Dshaft (wt - wg) - DAMP wg
///   dtheta/dt   = wt - wg,           omega_t = omega0 + wt,  omega_g = omega0 + wg
///   Ht = Htfrac H,  Hg = H - Ht,  K = 2 Ht Hg (2 pi Freq1)^2 / H      (DECISIONS D11: the undamped mode is Freq1 Hz)
/// </code>
/// With Htfrac = 0 it is one mass: <c>2 H dw/dt = (Pm - Pe)/omega - DAMP w</c>. The torques Pm/omega_t and Pe/omega_g are
/// held across a step and the linear part is advanced by its exact zero-order hold <c>x &lt;- Phi x + Gamma [Tm; Te]</c>,
/// with Phi = exp(A dt) and Gamma = integral_0^dt exp(A s) ds B from the matrix exponential of the augmented matrix
/// [[A, B], [0, 0]] (scaling and squaring of its Taylor series, plain IEEE arithmetic in a fixed order), computed once
/// per step size. Forward Euler would grow the undamped torsional mode by |1 + j w dt| per step (DECISIONS D14).
/// </summary>
public sealed class DriveTrain
{
    private readonly WtgtaParameters _p;
    private readonly int _n;             // 3 (two mass) or 1 (one mass)
    private readonly double[,] _a, _b;   // n x n, n x 2
    private readonly double _omega0;
    private readonly double[] _x;
    private double _dtCached = double.NaN;
    private double[,]? _phi, _gamma;

    /// <summary>Initialise at rest at speed <paramref name="omega0"/> carrying power <paramref name="p0"/> (machine base).</summary>
    public DriveTrain(WtgtaParameters p, double omega0, double p0)
    {
        ArgumentNullException.ThrowIfNull(p);
        IReadOnlyList<string> bad = p.Check();
        if (bad.Count > 0) throw new ArgumentException("WTGT_A parameters: " + string.Join("; ", bad), nameof(p));
        if (!(omega0 > 0.0) || !double.IsFinite(omega0)) throw new ArgumentOutOfRangeException(nameof(omega0), omega0, "WTGT_A: the initial speed must be a finite number above 0.");
        _p = p;
        _omega0 = omega0;
        if (p.Htfrac > 0.0)
        {
            _n = 3;
            double ht = p.Htfrac * p.H, hg = p.H - ht;
            double w1 = 2.0 * Math.PI * p.Freq1;
            Stiffness = 2.0 * ht * hg * w1 * w1 / p.H;
            _a = new double[3, 3]
            {
                { -p.Dshaft / (2.0 * ht), p.Dshaft / (2.0 * ht), -Stiffness / (2.0 * ht) },
                { p.Dshaft / (2.0 * hg), -(p.Dshaft + p.Damp) / (2.0 * hg), Stiffness / (2.0 * hg) },
                { 1.0, -1.0, 0.0 },
            };
            _b = new double[3, 2] { { 1.0 / (2.0 * ht), 0.0 }, { 0.0, -1.0 / (2.0 * hg) }, { 0.0, 0.0 } };
            _x = new double[] { 0.0, 0.0, p0 / omega0 / Stiffness };   // twist carries the steady torque
        }
        else
        {
            _n = 1;
            Stiffness = double.PositiveInfinity;
            _a = new double[1, 1] { { -p.Damp / (2.0 * p.H) } };
            _b = new double[1, 2] { { 1.0 / (2.0 * p.H), -1.0 / (2.0 * p.H) } };
            _x = new double[] { 0.0 };
        }
    }

    public WtgtaParameters Parameters => _p;

    /// <summary>The shaft stiffness K (pu torque per pu.s of twist); infinite for one mass.</summary>
    public double Stiffness { get; }

    /// <summary>Turbine speed omega_t (pu).</summary>
    public double TurbineSpeed => _omega0 + _x[0];

    /// <summary>Generator speed omega_g (pu).</summary>
    public double GeneratorSpeed => _omega0 + (_n == 3 ? _x[1] : _x[0]);

    /// <summary>Shaft twist (pu.s); 0 for one mass.</summary>
    public double Twist => _n == 3 ? _x[2] : 0.0;

    /// <summary>The state derivatives for mechanical power <paramref name="pm"/> and electrical power <paramref name="pe"/>.</summary>
    public double[] Derivatives(double pm, double pe)
    {
        double tm = pm / TurbineSpeed, te = pe / GeneratorSpeed;
        var d = new double[_n];
        for (int i = 0; i < _n; i++)
        {
            double s = _b[i, 0] * tm + _b[i, 1] * te;
            for (int j = 0; j < _n; j++) s += _a[i, j] * _x[j];
            d[i] = s;
        }
        return d;
    }

    /// <summary>Advance one step with the torques held at their start-of-step values (exact zero-order hold of the linear part).</summary>
    public void Step(double pm, double pe, double dt)
    {
        if (dt != _dtCached) (_phi, _gamma, _dtCached) = (null, null, dt);
        if (_phi is null) (_phi, _gamma) = Discretise(_a, _b, dt);
        double tm = pm / TurbineSpeed, te = pe / GeneratorSpeed;
        var next = new double[_n];
        for (int i = 0; i < _n; i++)
        {
            double s = 0.0;
            for (int j = 0; j < _n; j++) s += _phi![i, j] * _x[j];
            s += _gamma![i, 0] * tm + _gamma[i, 1] * te;
            next[i] = s;
        }
        Array.Copy(next, _x, _n);
    }

    /// <summary>The one-step map (Phi, Gamma) of the linear part for step <paramref name="dt"/> (exposed for the tests).</summary>
    public (double[,] Phi, double[,] Gamma) StepMatrices(double dt) => Discretise(_a, _b, dt);

    /// <summary>Phi = exp(A dt), Gamma = integral_0^dt exp(A s) ds B, from the exponential of [[A dt, B dt], [0, 0]].</summary>
    internal static (double[,] Phi, double[,] Gamma) Discretise(double[,] a, double[,] b, double dt)
    {
        int n = a.GetLength(0), m = b.GetLength(1), k = n + m;
        var z = new double[k, k];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) z[i, j] = a[i, j] * dt;
            for (int j = 0; j < m; j++) z[i, n + j] = b[i, j] * dt;
        }
        double[,] e = MatrixExp(z);
        var phi = new double[n, n];
        var gamma = new double[n, m];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++) phi[i, j] = e[i, j];
            for (int j = 0; j < m; j++) gamma[i, j] = e[i, n + j];
        }
        return (phi, gamma);
    }

    /// <summary>exp(Z) by scaling and squaring: Z / 2^s has infinity norm at most 1/2, its Taylor series to 24 terms (the
    /// remainder is below 1e-25 relative), then squared s times. Plain IEEE arithmetic in a fixed order: the same bits on
    /// every CPU.</summary>
    internal static double[,] MatrixExp(double[,] z)
    {
        int k = z.GetLength(0);
        double norm = 0.0;
        for (int i = 0; i < k; i++)
        {
            double r = 0.0;
            for (int j = 0; j < k; j++) r += Math.Abs(z[i, j]);
            norm = Math.Max(norm, r);
        }
        int s = 0;
        double scale = 1.0;
        while (norm * scale > 0.5) { scale *= 0.5; s++; }
        var x = new double[k, k];
        for (int i = 0; i < k; i++) for (int j = 0; j < k; j++) x[i, j] = z[i, j] * scale;
        var result = Identity(k);
        var term = Identity(k);
        for (int t = 1; t <= 24; t++)
        {
            term = Multiply(term, x);
            double inv = 1.0 / t;
            for (int i = 0; i < k; i++) for (int j = 0; j < k; j++) { term[i, j] *= inv; result[i, j] += term[i, j]; }
        }
        for (int q = 0; q < s; q++) result = Multiply(result, result);
        return result;
    }

    private static double[,] Identity(int k)
    {
        var m = new double[k, k];
        for (int i = 0; i < k; i++) m[i, i] = 1.0;
        return m;
    }

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        int k = a.GetLength(0);
        var c = new double[k, k];
        for (int i = 0; i < k; i++)
            for (int j = 0; j < k; j++)
            {
                double s = 0.0;
                for (int q = 0; q < k; q++) s += a[i, q] * b[q, j];
                c[i, j] = s;
            }
        return c;
    }
}

/// <summary>WTGAR_A aerodynamics (EPRI Fig. C-2): Pm = Pm0 - Ka theta (theta - theta0), pitch in degrees.</summary>
public static class Aerodynamics
{
    public static double MechanicalPower(WtgaraParameters p, double pm0, double theta) => pm0 - p.Ka * theta * (theta - p.Theta0);
}

/// <summary>
/// WTGPT_A pitch controller (EPRI Fig. C-3), degrees:
/// <code>
///   ew = omega_t - omega_ref + Kcc (Pord - Pref);   s0' = Kiw ew in [tmin, tmax];   a = clamp(Kpw ew + s0, tmin, tmax)
///   ec = Pord - Pref;                              s1' = Kic ec in [tmin, tmax];   b = clamp(Kpc ec + s1, tmin, tmax)
///   theta = lag(a + b, Tp) in [tmin, tmax], rate in [RTetaMin, RTetaMax]                                   (s2)
/// </code>
/// Pord is the REEC power order; Pref the REEC's power reference after the speed multiplication (Psel), so the
/// compensation path acts when the power order is held below the reference (DECISIONS D20). Initialised with s0 = theta0,
/// s1 = 0 and theta = theta0 (any split of theta0 between the two integrals is an equilibrium; DECISIONS D20).
/// </summary>
public sealed class PitchController
{
    private readonly WtgptaParameters _p;
    private double _s0, _s1, _theta;

    public PitchController(WtgptaParameters p, double theta0)
    {
        ArgumentNullException.ThrowIfNull(p);
        IReadOnlyList<string> bad = p.Check();
        if (bad.Count > 0) throw new ArgumentException("WTGPT_A parameters: " + string.Join("; ", bad), nameof(p));
        ControlBlocks.RequireEquilibriumWithinLimits("WTGPT_A", "pitch angle theta0", theta0, p.ThetaMin, p.ThetaMax, nameof(theta0));
        _p = p;
        _s0 = theta0; _s1 = 0.0; _theta = theta0;
    }

    /// <summary>The pitch angle (degrees).</summary>
    public double Theta => _theta;

    private (double Ew, double Ec, double Sum) Eval(double wt, double wref, double pord, double pref)
    {
        double ec = pord - pref;
        double ew = wt - wref + _p.Kcc * ec;
        double a = Math.Clamp(_p.Kpw * ew + _s0, _p.ThetaMin, _p.ThetaMax);
        double b = Math.Clamp(_p.Kpc * ec + _s1, _p.ThetaMin, _p.ThetaMax);
        return (ew, ec, a + b);
    }

    public void Advance(double wt, double wref, double pord, double pref, double dt)
    {
        (double ew, double ec, double sum) = Eval(wt, wref, pord, pref);
        _s0 = WeccBlocks.Integrate(_s0, _p.Kiw, ew, dt, _p.ThetaMin, _p.ThetaMax);
        _s1 = WeccBlocks.Integrate(_s1, _p.Kic, ec, dt, _p.ThetaMin, _p.ThetaMax);
        double next = WeccBlocks.Lag(_theta, sum, _p.Tp, dt);
        _theta = Math.Clamp(WeccBlocks.RateLimit(_theta, next, _p.RThetaMax, _p.RThetaMin, dt), _p.ThetaMin, _p.ThetaMax);
    }

    public void Residuals(double wt, double wref, double pord, double pref, List<(string Name, double Value)> into)
    {
        (double ew, double ec, double sum) = Eval(wt, wref, pord, pref);
        into.Add(("WTGPT_A s0 speed PI input", ew));
        into.Add(("WTGPT_A s1 power PI input", ec));
        into.Add(("WTGPT_A s2 (a + b - theta)", sum - _theta));
    }
}

/// <summary>
/// WTGTRQ_A torque controller (EPRI Fig. C-4):
/// <code>
///   s1 = lag(Pe, Tp);  omega_ref = s0 = lag(f(s1), Twref)  (f: the p -> spd table, flat beyond its ends)
///   Tflag = 1: e = (Pref0 - s1) / omega_g;   Tflag = 0: e = omega_g - omega_ref
///   Tref = clamp(Kpp e + s2, Temin, Temax),  s2' = Kip e in [Temin, Temax], frozen in a voltage dip
/// </code>
/// Tref is a torque; REEC_A with PFlag = 1 multiplies it by omega_g (the PSS/E convention, EPRI 3002006525 section 3.3).
/// </summary>
public sealed class TorqueController
{
    private readonly WtgtrqaParameters _p;
    private double _s0, _s1, _s2;

    public TorqueController(WtgtrqaParameters p, double pe0, double torque0)
    {
        ArgumentNullException.ThrowIfNull(p);
        IReadOnlyList<string> bad = p.Check();
        if (bad.Count > 0) throw new ArgumentException("WTGTRQ_A parameters: " + string.Join("; ", bad), nameof(p));
        ControlBlocks.RequireEquilibriumWithinLimits("WTGTRQ_A", "torque reference", torque0, p.Temin, p.Temax, nameof(torque0));
        _p = p;
        _s1 = pe0;
        _s0 = SpeedReferenceFor(pe0);
        _s2 = torque0;
    }

    /// <summary>The power-speed table value at power <paramref name="pe"/> (pu speed).</summary>
    public double SpeedReferenceFor(double pe) => WeccBlocks.Table(_p.P, _p.Spd, pe);

    /// <summary>The speed reference omega_ref (s0).</summary>
    public double SpeedReference => _s0;

    private double Error(double pref0, double wg) => _p.Tflag == 1 ? (pref0 - _s1) / wg : wg - _s0;

    /// <summary>The torque reference from the current states.</summary>
    public double Torque(double pref0, double wg) => Math.Clamp(_p.Kpp * Error(pref0, wg) + _s2, _p.Temin, _p.Temax);

    public void Advance(double pe, double pref0, double wg, bool dip, double dt)
    {
        double e = Error(pref0, wg);
        if (!dip) _s2 = WeccBlocks.Integrate(_s2, _p.Kip, e, dt, _p.Temin, _p.Temax);
        _s0 = WeccBlocks.Lag(_s0, SpeedReferenceFor(_s1), _p.Twref, dt);
        _s1 = WeccBlocks.Lag(_s1, pe, _p.Tp, dt);
    }

    public void Residuals(double pe, double pref0, double wg, List<(string Name, double Value)> into)
    {
        into.Add(("WTGTRQ_A s1 (Pe - Pe_filt)", pe - _s1));
        into.Add(("WTGTRQ_A s0 (f(Pe_filt) - omega_ref)", SpeedReferenceFor(_s1) - _s0));
        into.Add(("WTGTRQ_A s2 PI input", Error(pref0, wg)));
    }
}
