using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridSim.Core.Dynamics.Models.Wecc;

/// <summary>
/// REGC_B: the renewable energy generator/converter model with a VOLTAGE-SOURCE network interface (Pourbeik, "Proposal
/// for new features for the renewable energy system generic models", WECC REMWG memo Rev. 27b, 2/16/23, section 2.1,
/// Figure 1 and Table 1; approved by the WECC MVS in August 2020). Values per unit on the unit's MBASE. The record order
/// GridSim reads is the memo's Table 1 order: Tg Iqrmax Iqrmin Tfltr rrpwr RateFlag Te Imax pqflag re Xe.
/// </summary>
public sealed record RegcbParameters
{
    /// <summary>Converter lag on both current commands (s); cannot be zero (the memo: at least 4 integration steps).</summary>
    public double Tg { get; init; } = 0.02;
    /// <summary>Rate limit on the network-convention Iq when the initial Q is positive (pu/s; 999 disables).</summary>
    public double Iqrmax { get; init; } = 999.0;
    /// <summary>Rate limit on the network-convention Iq when the initial Q is negative (pu/s, negative; -999 disables).</summary>
    public double Iqrmin { get; init; } = -999.0;
    /// <summary>Terminal voltage measurement lag (s); may be zero. Its output is floored at 0.01 pu.</summary>
    public double Tfltr { get; init; } = 0.02;
    /// <summary>Recovery rate of the active current (RateFlag 0) or active power (RateFlag 1), pu/s, on a rising magnitude.</summary>
    public double Rrpwr { get; init; } = 10.0;
    /// <summary>0: rrpwr limits the active current; 1: it limits the active power.</summary>
    public int RateFlag { get; init; }
    /// <summary>Lag on the source voltage (s); may be zero.</summary>
    public double Te { get; init; } = 0.02;
    /// <summary>Converter current limit applied at the network interface (pu).</summary>
    public double Imax { get; init; } = 1.3;
    /// <summary>Priority of the interface current limit: 0 reactive, 1 active (should agree with the REEC model).</summary>
    public int PqFlag { get; init; }
    /// <summary>Source resistance (pu).</summary>
    public double Re { get; init; }
    /// <summary>Source reactance (pu).</summary>
    public double Xe { get; init; } = 0.2;

    /// <summary>Every problem with these values (empty when usable).</summary>
    public IReadOnlyList<string> Check()
    {
        var p = new List<string>();
        const string m = "REGC_B";
        WeccCheck.Above(p, m, "Tg", Tg, 0.0);
        WeccCheck.Above(p, m, "Iqrmax", Iqrmax, 0.0);
        if (!double.IsFinite(Iqrmin) || !(Iqrmin < 0.0)) p.Add($"{m} Iqrmin = {WeccCheck.N(Iqrmin)}: must be a finite number below 0");
        WeccCheck.AtLeast(p, m, "Tfltr", Tfltr, 0.0);
        WeccCheck.Above(p, m, "rrpwr", Rrpwr, 0.0);
        WeccCheck.Flag(p, m, "RateFlag", RateFlag, 0, 1);
        WeccCheck.AtLeast(p, m, "Te", Te, 0.0);
        WeccCheck.Above(p, m, "Imax", Imax, 0.0);
        WeccCheck.Flag(p, m, "pqflag", PqFlag, 0, 1);
        WeccCheck.AtLeast(p, m, "re", Re, 0.0);
        WeccCheck.Above(p, m, "Xe", Xe, 0.0);
        return p;
    }
}

/// <summary>REGC_B's exact solution against the Thevenin view of its node (<see cref="Regcb.SolveWithNetwork"/>): the node
/// voltage, the terminal current (machine base, network frame), the converter frame angle and whether that frame is held.</summary>
public readonly record struct RegcbNetworkSolution(Complex Voltage, Complex Current, double FrameAngle, bool FrameHeld);

/// <summary>
/// REGC_B, the voltage-source generator/converter (see <see cref="RegcbParameters"/> for the source). Reactive current is
/// held EXPORT-positive, as in <see cref="Regca"/> (DECISIONS D2).
/// <code>
///   s0  Iqx = lag(Iqcmd, Tg), rate limits Iqrmax / Iqrmin on the side Q0 selects (as REGC_A)
///   s2  Vf  = lag(|Vt|, Tfltr);  vp = max(Vf, 0.01)                       (the 0.01 floor is on Vt itself)
///   s1  RateFlag 0: lag(Ipcmd, Tg), Ip = s1;  RateFlag 1: lag(Ipcmd vp, Tg), Ip = s1 / vp
///       rrpwr bounds a RISING magnitude of s1 (both signs: a store charging recovers at -rrpwr; memo footnote 4)
///   E target in the terminal-voltage frame (d along Vt, so Vtd = |Vt|, Vtq = 0 and id = Ip, iq = -Iqx, as the memo's
///            "Id (= Ip)" fixes the frame):  Ed = |Vt| + id re - iq Xe,  Eq = iq re + id Xe,  i.e. |Vt| + (re + j Xe)(Ip - j Iqx)
///   s3, s4  Ed, Eq = lag(target, Te)
///   network: the source (Ed + j Eq) e^{j theta} behind re + j Xe, theta the angle of the terminal voltage OF THE SAME network
///            solution (DECISIONS D36: the frame the memo defines; solved exactly with the network by
///            <see cref="SolveWithNetwork"/>, so it converges on weak nodes and a steadily turning terminal leaves no error);
///            the frame is held at the last angle only while no consistent terminal frame exists (a phase-locked loop
///            freezing). The Norton admittance y = r / (re + j Xe) is stamped on the bus; terminal current I = (E - V) / Z,
///            limited to Imax in magnitude on the frame axis with the priority pqflag (DECISIONS D31)
/// </code>
/// The states advance once per step from the start-of-step terminal voltage with the commands held; Ed and Eq are constant
/// over the step, so a change of the terminal voltage MAGNITUDE moves the current at once (the voltage-source behaviour the
/// memo introduces REGC_B for), while a rotation of the terminal angle carries the source with it exactly.
/// </summary>
public sealed class Regcb
{
    /// <summary>Floor (pu) on the measured terminal voltage (the hard-coded 0.01 of the memo's Figure 1).</summary>
    public const double VoltageFloorPu = 0.01;

    private readonly RegcbParameters _p;
    private readonly Complex _z;
    private readonly bool _limitExportFall, _limitExportRise;
    private double _s1, _iq, _vf;
    private Complex _e;

    /// <summary>Initialise at the solved operating point: the terminal voltage phasor <paramref name="v0"/> and the
    /// active and exported reactive power <paramref name="p0"/>, <paramref name="q0"/> (machine base). Refuses a point whose
    /// current exceeds Imax (the interface limit would already act).</summary>
    public Regcb(RegcbParameters p, Complex v0, double p0, double q0)
    {
        ArgumentNullException.ThrowIfNull(p);
        IReadOnlyList<string> bad = p.Check();
        if (bad.Count > 0) throw new ArgumentException("REGC_B parameters: " + string.Join("; ", bad), nameof(p));
        double vm = v0.Magnitude;
        if (!(vm > 0.0) || !double.IsFinite(vm)) throw new ArgumentOutOfRangeException(nameof(v0), vm, "REGC_B: the initial terminal voltage must be a finite number above 0.");
        _p = p;
        _z = new Complex(p.Re, p.Xe);
        double ip0 = p0 / vm, iq0 = q0 / vm;
        double i0 = Math.Sqrt(ip0 * ip0 + iq0 * iq0);
        if (i0 > p.Imax)
            throw new ArgumentOutOfRangeException(nameof(p0), i0, $"REGC_B: the initial current {WeccCheck.N(i0)} pu is above Imax = {WeccCheck.N(p.Imax)} pu, so the network-interface current limit would already act; no equilibrium.");
        _vf = vm;
        _iq = iq0;
        _s1 = p.RateFlag == 1 ? ip0 * Math.Max(vm, VoltageFloorPu) : ip0;
        _e = Target(vm, Ip, _iq);
        _limitExportFall = q0 > 0.0;
        _limitExportRise = q0 < 0.0;
    }

    public RegcbParameters Parameters => _p;

    /// <summary>The source impedance re + j Xe (machine base).</summary>
    public Complex Impedance => _z;

    /// <summary>The source voltage Ed + j Eq in the terminal-voltage frame (pu), the s3 + j s4 states.</summary>
    public Complex Source => _e;

    /// <summary>The active current command the source is built from (pu): s1, or s1 / vp for RateFlag 1.</summary>
    public double Ip => _p.RateFlag == 1 ? _s1 / Math.Max(_vf, VoltageFloorPu) : _s1;

    /// <summary>The exported reactive current lag state (pu).</summary>
    public double IqExport => _iq;

    /// <summary>The measured terminal voltage Vf (pu), the s2 state.</summary>
    public double FilteredVoltage => _vf;

    // E target in the terminal-voltage frame: |Vt| + Z (Ip - j Iqx).
    private Complex Target(double vm, double ip, double iqx) => vm + _z * new Complex(ip, -iqx);

    /// <summary>
    /// The terminal current (machine base, out of the unit) at terminal voltage <paramref name="v"/> for the source
    /// held at the frame angle <paramref name="sourceAngle"/>: <c>(E - V) / Z</c>, limited to Imax with the priority pqflag (DECISIONS D31) on the axis of <paramref name="v"/>
    /// (or <paramref name="angle"/> when the voltage has collapsed). <paramref name="limited"/> reports whether the limit
    /// acted. A blocked converter (REEC_D) passes no current.
    /// </summary>
    public Complex TerminalCurrent(Complex v, double sourceAngle, double angle, bool blocked, out bool limited)
    {
        limited = false;
        if (blocked) return Complex.Zero;
        Complex i = (_e * DetMath.FromPolar(1.0, sourceAngle) - v) / _z;
        Complex axis = DetMath.FromPolar(1.0, angle);
        double im = _p.Imax;
        if (!(i.Magnitude > im)) return i;
        limited = true;
        Complex rel = i * Complex.Conjugate(axis);   // (Ip, -Iqx) on the terminal-voltage axis
        double ip = rel.Real, iqx = -rel.Imaginary;
        if (_p.PqFlag == 0)
        {
            iqx = Math.Clamp(iqx, -im, im);
            double lim = Math.Sqrt(Math.Max(0.0, im * im - iqx * iqx));
            ip = Math.Clamp(ip, -lim, lim);
        }
        else
        {
            ip = Math.Clamp(ip, -im, im);
            double lim = Math.Sqrt(Math.Max(0.0, im * im - ip * ip));
            iqx = Math.Clamp(iqx, -lim, lim);
        }
        return new Complex(ip, -iqx) * axis;
    }

    /// <summary>The interface current limit in the converter frame (DECISIONS D31): <paramref name="u"/> is the unlimited
    /// current Ip - j Iqx on the frame axis; above Imax it is limited with the priority pqflag (Q priority: Iqx clamped to
    /// +-Imax, then Ip to +-sqrt(Imax^2 - Iqx^2); P priority the other way round). Continuous in <paramref name="u"/>.</summary>
    internal Complex Limit(Complex u, out bool limited)
    {
        double im = _p.Imax;
        limited = u.Magnitude > im;
        if (!limited) return u;
        double ip = u.Real, iqx = -u.Imaginary;
        if (_p.PqFlag == 0)
        {
            iqx = Math.Clamp(iqx, -im, im);
            double lim = Math.Sqrt(Math.Max(0.0, im * im - iqx * iqx));
            ip = Math.Clamp(ip, -lim, lim);
        }
        else
        {
            ip = Math.Clamp(ip, -im, im);
            double lim = Math.Sqrt(Math.Max(0.0, im * im - ip * ip));
            iqx = Math.Clamp(iqx, -lim, lim);
        }
        return new Complex(ip, -iqx);
    }

    /// <summary>
    /// Solves this unit's own network interface EXACTLY against the Thevenin view of its node (DECISIONS D36, the fix of the
    /// weak-grid iteration failures). With every other source held, the node voltage obeys <c>V c1 = a + c2 I</c>, where
    /// <c>I</c> is this unit's terminal current (machine base, network frame), <c>c1 = 1 - Zdd y</c> and <c>c2 = Zdd r</c>
    /// for the driving-point impedance Zdd of the network with this unit's Norton admittance y stamped in, r the rating,
    /// and <c>a</c> the node voltage with this unit's injection removed. The kernel supplies <paramref name="a"/>,
    /// <paramref name="c1"/> and <paramref name="c2"/>.
    ///
    /// <para><b>Frame.</b> The source Ed + j Eq is defined in the terminal-voltage frame (the memo's "Id (= Ip)"), so the
    /// frame angle is the angle of the solution itself: V = x e^{j phi}, I = L((E - x) / Z) e^{j phi}. Then
    /// <c>e^{j phi} B(x) = a</c> with <c>B(x) = x c1 - c2 L((E - x) / Z)</c>, so x solves the SCALAR equation
    /// <c>|B(x)| = |a|</c> and <c>phi = arg a - arg B(x)</c>. Between the break points of the limit (|u| = Imax, either
    /// component at -Imax, 0 or +Imax) B is affine in x when unlimited or saturated (the roots of a quadratic, in closed
    /// form) and smooth when partly limited (scanned on 24 points and bisected to the last bit). Of the roots with x above
    /// <see cref="RmsConverterState.PllLockMinVoltagePu"/>, the one whose frame angle is nearest the last committed angle
    /// <paramref name="heldAngle"/> is taken (continuity: the frame never jumps to the other, pole-slipped root).</para>
    ///
    /// <para><b>No consistent frame.</b> When no root exists (for example a bolted fault close to the terminal while the
    /// source still has its quadrature part Xe Ip: no terminal angle makes the source consistent), the frame is held at
    /// <paramref name="heldAngle"/>, as a phase-locked loop freezes, and the limited current is solved in closed form in
    /// that frame (<see cref="SolveHeldFrame"/>).</para>
    ///
    /// <para>A blocked converter passes no current: V = a / c1.</para>
    /// </summary>
    public RegcbNetworkSolution SolveWithNetwork(Complex a, Complex c1, Complex c2, double heldAngle, bool blocked, Complex previousCurrent)
    {
        double c1m = c1.Magnitude;
        if (!(c1m > 1e-12) || !double.IsFinite(c1m))
            throw new InvalidOperationException($"REGC_B: the unit's node has no network connection besides the unit itself (|1 - Zdd y| = {WeccCheck.N(c1m)}); its terminal voltage is undefined.");
        if (blocked)
        {
            Complex vb = a / c1;
            return new RegcbNetworkSolution(vb, Complex.Zero, vb.Magnitude > RmsConverterState.PllLockMinVoltagePu ? DetMath.Phase(vb) : heldAngle, false);
        }

        double am = a.Magnitude, im = _p.Imax;
        Complex e = _e, z = _z, invZ = 1.0 / _z, eOverZ = _e / _z;
        double xLo = RmsConverterState.PllLockMinVoltagePu;
        double xHi = (am + c2.Magnitude * im) / c1m * (1.0 + 1e-9) + 1e-9;   // above it |B(x)| >= x |c1| - |c2| Imax > |a|

        // Break points of the limit along x (u(x) = (E - x) / Z is affine in x).
        var bp = new List<double> { xLo, xHi };
        double zm2 = im * im * (z.Real * z.Real + z.Imaginary * z.Imaginary) - e.Imaginary * e.Imaginary;   // |E - x| = Imax |Z|
        if (zm2 >= 0.0) { double s = Math.Sqrt(zm2); bp.Add(e.Real - s); bp.Add(e.Real + s); }
        // Components of u(x): up = Re(E/Z) - x Re(1/Z), uqx = -Im(E/Z) + x Im(1/Z), each at -Imax, 0 and +Imax.
        foreach (double lev in new[] { -im, 0.0, im })
        {
            if (invZ.Real != 0.0) bp.Add((eOverZ.Real - lev) / invZ.Real);
            if (invZ.Imaginary != 0.0) bp.Add((lev + eOverZ.Imaginary) / invZ.Imaginary);
        }
        bp.RemoveAll(x => !(x >= xLo && x <= xHi));
        bp.Sort();

        Complex U(double x) => (e - x) * invZ;
        Complex Bx(double x) => x * c1 - c2 * Limit(U(x), out _);
        double H(double x) { Complex b = Bx(x); return b.Real * b.Real + b.Imaginary * b.Imaginary - am * am; }

        var roots = new List<double>();
        void AddRoot(double x) { foreach (double r in roots) if (Math.Abs(r - x) <= 1e-12 * Math.Max(1.0, x)) return; roots.Add(x); }
        void Quadratic(Complex k, Complex w, double l, double r)
        {
            // |x k - w|^2 = |a|^2
            double kk = k.Real * k.Real + k.Imaginary * k.Imaginary;
            double b = k.Real * w.Real + k.Imaginary * w.Imaginary;
            double c = w.Real * w.Real + w.Imaginary * w.Imaginary - am * am;
            double disc = b * b - kk * c;
            if (!(kk > 0.0) || disc < 0.0) return;
            double sq = Math.Sqrt(disc);
            foreach (double x in new[] { (b - sq) / kk, (b + sq) / kk })
            {
                double tol = 1e-12 * Math.Max(1.0, Math.Abs(x));
                if (x >= l - tol && x <= r + tol) AddRoot(Math.Clamp(x, l, r));
            }
        }
        double Bisect(double l, double r, double hl)
        {
            for (int n = 0; n < 200; n++)
            {
                double m = 0.5 * (l + r);
                if (!(m > l && m < r)) break;
                double hm = H(m);
                if ((hm < 0.0) == (hl < 0.0)) { l = m; hl = hm; } else r = m;
            }
            return 0.5 * (l + r);
        }

        for (int k = 0; k + 1 < bp.Count; k++)
        {
            double l = bp[k], r = bp[k + 1];
            if (!(r > l)) continue;
            double mid = 0.5 * (l + r);
            Complex um = U(mid);
            Complex lm = Limit(um, out bool lim);
            bool saturated = lim && (_p.PqFlag == 0 ? Math.Abs(um.Imaginary) >= im : Math.Abs(um.Real) >= im);
            if (!lim) Quadratic(c1 + c2 * invZ, c2 * eOverZ, l, r);
            else if (saturated) Quadratic(c1, c2 * lm, l, r);
            else
            {
                const int m = 24;
                double xa = l, ha = H(l);
                if (ha == 0.0) AddRoot(l);
                for (int j = 1; j <= m; j++)
                {
                    double xb = j == m ? r : l + (r - l) * j / m;
                    double hb = H(xb);
                    if (hb == 0.0) AddRoot(xb);
                    else if (ha != 0.0 && (ha < 0.0) != (hb < 0.0)) AddRoot(Bisect(xa, xb, ha));
                    xa = xb; ha = hb;
                }
            }
        }

        double best = double.PositiveInfinity, bx = 0.0, bphi = heldAngle;
        foreach (double x in roots)
        {
            Complex b = Bx(x);
            double phi = am > 0.0 && b.Magnitude > 0.0 ? DetMath.Phase(a) - DetMath.Phase(b) : heldAngle;
            double d = Math.Abs(Wrap(phi - heldAngle));
            if (d < best) { best = d; bx = x; bphi = phi; }
        }
        if (best < double.PositiveInfinity)
        {
            Complex axis = DetMath.FromPolar(1.0, bphi);
            return new RegcbNetworkSolution(DetMath.FromPolar(bx, bphi), Limit(U(bx), out _) * axis, bphi, false);
        }
        return SolveHeldFrame(a, c1, c2, heldAngle, previousCurrent);
    }

    /// <summary>Angle wrapped into (-pi, pi].</summary>
    internal static double Wrap(double d)
    {
        while (d > Math.PI) d -= 2.0 * Math.PI;
        while (d <= -Math.PI) d += 2.0 * Math.PI;
        return d;
    }

    /// <summary>
    /// The network interface with the frame held at <paramref name="frameAngle"/> (no consistent terminal frame exists).
    /// In that frame <c>u = u0 - g I</c> with <c>u0 = (E - a' / c1) / Z</c>, <c>g = c2 / (c1 Z)</c> and <c>I = L(u)</c>.
    /// Every regime is closed form: unlimited <c>u = u0 / (1 + g)</c>; saturated (the priority component at +-Imax, the
    /// other zero); partly limited (the priority component free, the other +-sqrt(Imax^2 - c^2): a quadratic). The
    /// candidates consistent with their own regime are kept and the one nearest <paramref name="previousCurrent"/> (the
    /// last committed current, machine base, network frame) is taken; with none (rounding at a boundary) the limit of the
    /// unlimited solution is used, and the kernel's convergence check reports any residual inconsistency.
    /// </summary>
    internal RegcbNetworkSolution SolveHeldFrame(Complex a, Complex c1, Complex c2, double frameAngle, Complex previousCurrent)
    {
        Complex axis = DetMath.FromPolar(1.0, frameAngle), back = Complex.Conjugate(axis);
        Complex a0 = a * back, u0 = (_e - a0 / c1) / _z, g = c2 / (c1 * _z);
        Complex prev = previousCurrent * back;
        double im = _p.Imax, tol = 1e-12;
        var cands = new List<Complex>();
        Complex uFree = u0 / (1.0 + g);
        if (uFree.Magnitude <= im * (1.0 + tol)) cands.Add(uFree);
        bool q = _p.PqFlag == 0;
        // Saturated: the priority component at sigma Imax, the other zero.
        foreach (double sg in new[] { -1.0, 1.0 })
        {
            Complex i = q ? new Complex(0.0, -sg * im) : new Complex(sg * im, 0.0);
            Complex u = u0 - g * i;
            double comp = q ? -u.Imaginary : u.Real;
            if (sg * comp >= im * (1.0 - tol)) cands.Add(i);
        }
        // Partly limited. Q priority: Iqx = uqx (free), Ip = s sqrt(Imax^2 - Iqx^2); uqx = -Im u0 + gi Ip - gr Iqx, so
        // (1 + gr) Iqx + Im u0 = gi Ip. P priority: Ip = up (free), Iqx = s sqrt(Imax^2 - Ip^2); up = Re u0 - gr Ip - gi Iqx,
        // so (1 + gr) Ip - Re u0 = -gi Iqx. Either way (alpha c - beta)^2 = gi^2 (Imax^2 - c^2) for the free component c.
        double alpha = 1.0 + g.Real, gi = g.Imaginary, beta = q ? -u0.Imaginary : u0.Real;
        double sgn = q ? 1.0 : -1.0;   // the other component = sgn (alpha c - beta) / gi
        var free = new List<(double C, double O)>();
        if (Math.Abs(gi) > 1e-15)
        {
            double qa = alpha * alpha + gi * gi, qb = -2.0 * alpha * beta, qc = beta * beta - gi * gi * im * im;
            double disc = qb * qb - 4.0 * qa * qc;
            if (disc >= 0.0)
                foreach (double c in new[] { (-qb - Math.Sqrt(disc)) / (2.0 * qa), (-qb + Math.Sqrt(disc)) / (2.0 * qa) })
                    free.Add((c, sgn * (alpha * c - beta) / gi));
        }
        else
        {
            double c = beta / alpha, o = Math.Sqrt(Math.Max(0.0, im * im - c * c));
            free.Add((c, o)); free.Add((c, -o));
        }
        foreach ((double c0, double o0) in free)
        {
            double c = c0, o = o0;
            if (!(Math.Abs(c) < im)) continue;
            // Two Newton steps on the unsquared equation R(c) = beta + sgn gi s sqrt(Imax^2 - c^2) - alpha c = 0 (s the sign of
            // the other component) remove the rounding the squared quadratic leaves.
            double s = o < 0.0 ? -1.0 : 1.0;
            for (int n = 0; n < 2; n++)
            {
                double root = Math.Sqrt(Math.Max(0.0, im * im - c * c));
                if (!(root > 0.0)) break;
                double rr = beta + sgn * gi * s * root - alpha * c;
                double dr = -alpha - sgn * gi * s * c / root;
                if (dr == 0.0) break;
                c -= rr / dr;
            }
            if (!(Math.Abs(c) < im)) continue;
            o = s * Math.Sqrt(Math.Max(0.0, im * im - c * c));
            Complex i = q ? new Complex(o, -c) : new Complex(c, -o);
            Complex u = u0 - g * i;
            double uc = q ? -u.Imaginary : u.Real, uo = q ? u.Real : -u.Imaginary;
            if (u.Magnitude >= im * (1.0 - tol) && Math.Abs(uc - c) <= 1e-9 * Math.Max(1.0, im) && (o == 0.0 || Math.Sign(uo) == Math.Sign(o)))
                cands.Add(i);
        }
        Complex pick;
        if (cands.Count == 0) pick = Limit(uFree, out _);
        else
        {
            pick = cands[0];
            double bd = (pick - prev).Magnitude;
            for (int k = 1; k < cands.Count; k++) { double d = (cands[k] - prev).Magnitude; if (d < bd) { bd = d; pick = cands[k]; } }
        }
        Complex inet = pick * axis;
        return new RegcbNetworkSolution((a + c2 * inet) / c1, inet, frameAngle, true);
    }

    /// <summary>Advance the five states one step of <paramref name="dt"/> from the start-of-step terminal voltage phasor
    /// <paramref name="v"/> (angle <paramref name="angle"/>, held when the voltage has collapsed) with the commands held.</summary>
    public void Step(double ipcmd, double iqcmd, Complex v, double angle, double dt)
    {
        double vp = Math.Max(_vf, VoltageFloorPu);
        Complex target = Target(v.Magnitude, Ip, _iq);

        double u = _p.RateFlag == 1 ? ipcmd * vp : ipcmd;
        double s1 = WeccBlocks.Lag(_s1, u, _p.Tg, dt);
        double rr = _p.Rrpwr * dt;
        if (s1 - _s1 > rr && _s1 >= 0.0) s1 = _s1 + rr;          // memo footnote 4: a rising magnitude is rate limited,
        else if (s1 - _s1 < -rr && _s1 <= 0.0) s1 = _s1 - rr;    // on either sign (a store charging recovers at -rrpwr)
        _s1 = s1;

        double iq = WeccBlocks.Lag(_iq, iqcmd, _p.Tg, dt);
        double rateUp = _limitExportRise ? -_p.Iqrmin : double.PositiveInfinity;
        double rateDown = _limitExportFall ? -_p.Iqrmax : double.NegativeInfinity;
        _iq = WeccBlocks.RateLimit(_iq, iq, rateUp, rateDown, dt);

        _e = new Complex(WeccBlocks.Lag(_e.Real, target.Real, _p.Te, dt), WeccBlocks.Lag(_e.Imaginary, target.Imaginary, _p.Te, dt));
        _vf = WeccBlocks.Lag(_vf, v.Magnitude, _p.Tfltr, dt);
    }

    /// <summary>Set the two current states to the commands the controls give at t = 0 after the residual gate (removes the
    /// last-bit difference between the closed-form starts of this model and of its control), and rebuild E from them.</summary>
    internal void Settle(double ipcmd, double iqcmd, Complex v0)
    {
        _s1 = _p.RateFlag == 1 ? ipcmd * Math.Max(_vf, VoltageFloorPu) : ipcmd;
        _iq = iqcmd;
        _e = Target(v0.Magnitude, Ip, _iq);
    }

    /// <summary>The equilibrium residuals for held commands and voltage.</summary>
    public void Residuals(double ipcmd, double iqcmd, Complex v, List<(string Name, double Value)> into)
    {
        double vp = Math.Max(_vf, VoltageFloorPu);
        into.Add(("REGC_B s1 (input - s1)", (_p.RateFlag == 1 ? ipcmd * vp : ipcmd) - _s1));
        into.Add(("REGC_B s0 Iq (Iqcmd - Iq)", iqcmd - _iq));
        into.Add(("REGC_B s2 Vf (|Vt| - Vf)", v.Magnitude - _vf));
        Complex d = Target(v.Magnitude, Ip, _iq) - _e;
        into.Add(("REGC_B s3 Ed (target - Ed)", d.Real));
        into.Add(("REGC_B s4 Eq (target - Eq)", d.Imaginary));
    }
}
