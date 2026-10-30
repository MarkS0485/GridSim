using System;
using System.Collections.Generic;
using System.Numerics;

namespace GridSim.Core.Dynamics.Models.Wecc;

/// <summary>
/// Exact network-interface solution for a current source injected along its own terminal-voltage angle whose current in
/// that frame depends only on the terminal voltage MAGNITUDE x and is affine in x between known break points (REGC_A:
/// <c>g(x) = Ip LVACM(x) - j max(Iqx - Khv max(x - Volim, 0), Iolim)</c>). DECISIONS D38 of the WECC expansion.
///
/// <para>The kernel gives the Thevenin view of the unit's node, <c>V c1 = a + c2 I</c> (<see cref="Regcb.SolveWithNetwork"/>
/// explains a, c1, c2). With <c>V = x e^{j phi}</c> and <c>I = g(x) e^{j phi}</c> the frame condition is the scalar
/// equation <c>|x c1 - c2 g(x)| = |a|</c>, <c>phi = arg a - arg(x c1 - c2 g(x))</c>; on each affine piece it is a quadratic,
/// solved in closed form. Of the roots with x above <see cref="RmsConverterState.PllLockMinVoltagePu"/> the one whose angle
/// is nearest the last committed angle is taken. With no root (for example active current into a near-terminal bolted
/// fault: no terminal angle is consistent) the frame is held at the last angle, as the converter holds it below the lock
/// voltage: then <c>V c1 = a + c2 g(|V|) e^{j theta}</c>, again a scalar quadratic per piece in x = |V|, and the root
/// nearest the last committed magnitude is taken.</para>
/// </summary>
internal static class CurrentSourceNetworkSolver
{
    /// <summary>The solution: node voltage, terminal current (machine base, network frame), frame angle, frame held.</summary>
    internal readonly record struct Solution(Complex Voltage, Complex Current, double FrameAngle, bool FrameHeld);

    public static Solution Solve(Func<double, Complex> g, IReadOnlyList<double> breaks, Complex a, Complex c1, Complex c2,
        double heldAngle, double previousMagnitude)
    {
        double c1m = c1.Magnitude;
        if (!(c1m > 1e-12) || !double.IsFinite(c1m))
            throw new InvalidOperationException($"Converter: the unit's node has no network connection besides the unit itself (|c1| = {WeccCheck.N(c1m)}).");
        double lo = RmsConverterState.PllLockMinVoltagePu, am = a.Magnitude;
        // Beyond its last break point g is constant, so |g| is largest at a break point or at lo.
        double gmax = g(lo).Magnitude;
        foreach (double b in breaks) if (b > lo && double.IsFinite(b)) gmax = Math.Max(gmax, g(b).Magnitude);
        double hi = (am + c2.Magnitude * gmax) / c1m * (1.0 + 1e-9) + 1e-9;
        var bp = new List<double> { lo, hi };
        foreach (double b in breaks) if (double.IsFinite(b) && b > lo && b < hi) bp.Add(b);
        bp.Sort();

        // Terminal frame.
        double best = double.PositiveInfinity, bx = 0.0, bphi = heldAngle;
        for (int k = 0; k + 1 < bp.Count; k++)
        {
            double l = bp[k], r = bp[k + 1];
            if (!(r > l)) continue;
            (Complex g0, Complex g1) = Affine(g, l, r);
            // B(x) = x (c1 - c2 g1) - c2 g0 = x kk - w;  |B| = |a|
            foreach (double x in QuadraticRoots(c1 - c2 * g1, c2 * g0, am, l, r))
            {
                Complex b = x * c1 - c2 * g(x);
                double phi = am > 0.0 && b.Magnitude > 0.0 ? DetMath.Phase(a) - DetMath.Phase(b) : heldAngle;
                double d = Math.Abs(Regcb.Wrap(phi - heldAngle));
                if (d < best) { best = d; bx = x; bphi = phi; }
            }
        }
        if (best < double.PositiveInfinity)
        {
            Complex axis = DetMath.FromPolar(1.0, bphi);
            return new Solution(DetMath.FromPolar(bx, bphi), g(bx) * axis, bphi, false);
        }

        // Held frame: |a + c2 e^{j theta} g(x)| = |c1| x.
        Complex e = DetMath.FromPolar(1.0, heldAngle);
        var hb = new List<double> { 0.0 };
        foreach (double b in breaks) if (double.IsFinite(b) && b > 0.0) hb.Add(b);
        double hHi = (am + c2.Magnitude * Math.Max(gmax, g(0.0).Magnitude)) / c1m * (1.0 + 1e-9) + 1e-9;
        hb.Add(Math.Max(hHi, hb[^1] + 1.0));
        hb.Sort();
        double bestM = double.PositiveInfinity, hx = double.NaN;
        for (int k = 0; k + 1 < hb.Count; k++)
        {
            double l = hb[k], r = hb[k + 1];
            if (!(r > l)) continue;
            (Complex g0, Complex g1) = Affine(g, l, r);
            Complex p = a + c2 * e * g0, q = c2 * e * g1;
            double qa = q.Real * q.Real + q.Imaginary * q.Imaginary - c1m * c1m;
            double qb = 2.0 * (p.Real * q.Real + p.Imaginary * q.Imaginary);
            double qc = p.Real * p.Real + p.Imaginary * p.Imaginary;
            foreach (double x in Roots(qa, qb, qc))
            {
                double tol = 1e-12 * Math.Max(1.0, Math.Abs(x));
                if (!(x >= l - tol && x <= r + tol)) continue;
                double xc = Math.Clamp(x, l, r);
                double d = Math.Abs(xc - previousMagnitude);
                if (d < bestM) { bestM = d; hx = xc; }
            }
        }
        if (double.IsNaN(hx)) hx = Math.Max(previousMagnitude, 0.0);   // no consistent point: the kernel's check reports it
        Complex ih = g(hx) * e;
        return new Solution((a + c2 * ih) / c1, ih, heldAngle, true);
    }

    // g on [l, r] as g0 + g1 x (exact for an affine piece).
    private static (Complex G0, Complex G1) Affine(Func<double, Complex> g, double l, double r)
    {
        Complex gl = g(l), gr = g(r);
        Complex g1 = (gr - gl) / (r - l);
        return (gl - g1 * l, g1);
    }

    // Roots in [l, r] of |x k - w| = am.
    private static IEnumerable<double> QuadraticRoots(Complex k, Complex w, double am, double l, double r)
    {
        double kk = k.Real * k.Real + k.Imaginary * k.Imaginary;
        if (!(kk > 0.0)) yield break;
        double b = k.Real * w.Real + k.Imaginary * w.Imaginary;
        double c = w.Real * w.Real + w.Imaginary * w.Imaginary - am * am;
        double disc = b * b - kk * c;
        if (disc < 0.0) yield break;
        double sq = Math.Sqrt(disc);
        foreach (double x in new[] { (b - sq) / kk, (b + sq) / kk })
        {
            double tol = 1e-12 * Math.Max(1.0, Math.Abs(x));
            if (x >= l - tol && x <= r + tol) yield return Math.Clamp(x, l, r);
        }
    }

    // Real roots of qa x^2 + qb x + qc = 0 (linear when qa = 0).
    private static IEnumerable<double> Roots(double qa, double qb, double qc)
    {
        if (qa == 0.0)
        {
            if (qb != 0.0) yield return -qc / qb;
            yield break;
        }
        double disc = qb * qb - 4.0 * qa * qc;
        if (disc < 0.0) yield break;
        double sq = Math.Sqrt(disc);
        yield return (-qb - sq) / (2.0 * qa);
        yield return (-qb + sq) / (2.0 * qa);
    }
}
