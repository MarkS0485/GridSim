using System;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Dynamics.LongTerm;

/// <summary>
/// WP21 (RC5): one recovering load of a long-term run: the bus whose whole load recovers, and its exponential recovery data
/// (Karlsson and Hill, IEEE Trans. Power Systems 9(1), 1994).
/// </summary>
/// <param name="BusId">The bus. Its load at the start (Pd, Qd) is P0, Q0; its solved voltage at the start is V0.</param>
/// <param name="TpSeconds">The active recovery time constant Tp (s), above 0.</param>
/// <param name="TqSeconds">The reactive recovery time constant Tq (s), above 0.</param>
/// <param name="AlphaS">Steady-state active exponent: Ps = P0 (V/V0)^alphaS.</param>
/// <param name="AlphaT">Transient active exponent: Pt = P0 (V/V0)^alphaT; 0, 1 or 2 (the power flow's polynomial load).</param>
/// <param name="BetaS">Steady-state reactive exponent: Qs = Q0 (V/V0)^betaS.</param>
/// <param name="BetaT">Transient reactive exponent: Qt = Q0 (V/V0)^betaT; 0, 1 or 2.</param>
public sealed record RecoveringLoadSpec(int BusId, double TpSeconds, double TqSeconds, double AlphaS, double AlphaT, double BetaS, double BetaT);

/// <summary>
/// WP21 (RC5): the exponential recovery load in a quasi-steady-state run. With the recovery state xp:
/// <code>
///   Tp dxp/dt = -xp + Ps(V) - Pt(V),     Pd = xp + Pt(V),
///   Ps = P0 (V/V0)^alphaS,               Pt = P0 (V/V0)^alphaT,
/// </code>
/// and the same for Q with Tq, betaS and betaT. The state advances by forward Euler over the QSS step h with the voltage of
/// the step's equilibrium held: <c>xp(n+1) = xp(n) + h / Tp (-xp(n) + Ps(V) - Pt(V))</c>. Under a held voltage that is the
/// exact geometric sequence <c>xp(n) = (Ps - Pt)(1 - (1 - h/Tp)^n)</c>. Stable for h &lt; 2 Tp (refused otherwise).
/// At the start xp = 0 and V = V0, so Pd = P0.
/// </summary>
public sealed class LoadRecoveryQss
{
    private readonly RecoveringLoadSpec _s;

    /// <summary>Start the load at its pre-disturbance state: load <paramref name="p0Mw"/>, <paramref name="q0Mvar"/> at
    /// voltage <paramref name="v0Pu"/>.</summary>
    public LoadRecoveryQss(RecoveringLoadSpec spec, double p0Mw, double q0Mvar, double v0Pu)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!(spec.TpSeconds > 0.0) || !double.IsFinite(spec.TpSeconds) || !(spec.TqSeconds > 0.0) || !double.IsFinite(spec.TqSeconds))
            throw new ArgumentOutOfRangeException(nameof(spec), $"recovering load at bus {spec.BusId}: Tp and Tq must be finite numbers of seconds above 0.");
        if (!double.IsFinite(spec.AlphaS) || !double.IsFinite(spec.BetaS))
            throw new ArgumentOutOfRangeException(nameof(spec), $"recovering load at bus {spec.BusId}: the steady-state exponents must be finite.");
        if (!IsPolynomial(spec.AlphaT) || !IsPolynomial(spec.BetaT))
            throw new ArgumentOutOfRangeException(nameof(spec), $"recovering load at bus {spec.BusId}: the transient exponents must be 0, 1 or 2 "
                + "(the power flow holds the transient load as a polynomial in V).");
        if (!double.IsFinite(p0Mw) || !double.IsFinite(q0Mvar) || (p0Mw == 0.0 && q0Mvar == 0.0))
            throw new ArgumentException($"recovering load at bus {spec.BusId}: the bus has no load to recover (P0 = Q0 = 0).", nameof(p0Mw));
        if (!(v0Pu > 0.0) || !double.IsFinite(v0Pu))
            throw new ArgumentOutOfRangeException(nameof(v0Pu), v0Pu, $"recovering load at bus {spec.BusId}: the starting voltage must be above 0.");
        _s = spec;
        P0Mw = p0Mw;
        Q0Mvar = q0Mvar;
        V0Pu = v0Pu;
    }

    /// <summary>The load's data.</summary>
    public RecoveringLoadSpec Spec => _s;

    /// <summary>The pre-disturbance active load (MW).</summary>
    public double P0Mw { get; }

    /// <summary>The pre-disturbance reactive load (Mvar).</summary>
    public double Q0Mvar { get; }

    /// <summary>The pre-disturbance voltage (pu).</summary>
    public double V0Pu { get; }

    /// <summary>The active recovery state xp (MW).</summary>
    public double XpMw { get; private set; }

    /// <summary>The reactive recovery state xq (Mvar).</summary>
    public double XqMvar { get; private set; }

    /// <summary>True for an exponent the power flow can hold exactly (0, 1 or 2).</summary>
    public static bool IsPolynomial(double exponent) => exponent is 0.0 or 1.0 or 2.0;

    /// <summary>(V/V0)^e, exact for the exponents 0, 1 and 2, else by the deterministic power function.</summary>
    internal static double Ratio(double v, double v0, double e)
    {
        double r = v / v0;
        if (e == 0.0) return 1.0;
        if (e == 1.0) return r;
        if (e == 2.0) return r * r;
        return DetMath.Pow(r, e);
    }

    /// <summary>The active load drawn at voltage <paramref name="vPu"/>: xp + Pt(V) (MW).</summary>
    public double PdMw(double vPu) => XpMw + P0Mw * Ratio(vPu, V0Pu, _s.AlphaT);

    /// <summary>The reactive load drawn at voltage <paramref name="vPu"/>: xq + Qt(V) (Mvar).</summary>
    public double QdMvar(double vPu) => XqMvar + Q0Mvar * Ratio(vPu, V0Pu, _s.BetaT);

    /// <summary>Advance the recovery states one QSS step <paramref name="h"/> with the voltage <paramref name="vPu"/> held
    /// (forward Euler).</summary>
    public void Advance(double vPu, double h)
    {
        if (!(h > 0.0) || !(h < 2.0 * _s.TpSeconds) || !(h < 2.0 * _s.TqSeconds))
            throw new ArgumentOutOfRangeException(nameof(h), h, $"recovering load at bus {_s.BusId}: the QSS step must be above 0 and below 2 Tp and 2 Tq "
                + "(forward Euler is unstable beyond).");
        double ps = P0Mw * Ratio(vPu, V0Pu, _s.AlphaS), pt = P0Mw * Ratio(vPu, V0Pu, _s.AlphaT);
        double qs = Q0Mvar * Ratio(vPu, V0Pu, _s.BetaS), qt = Q0Mvar * Ratio(vPu, V0Pu, _s.BetaT);
        XpMw += h / _s.TpSeconds * (-XpMw + ps - pt);
        XqMvar += h / _s.TqSeconds * (-XqMvar + qs - qt);
    }

    /// <summary>
    /// The bus as the step's power flow holds it: the nominal load P0, Q0 with a polynomial load model whose multiplier is
    /// <c>(x + relief) / P0 + (V/V0)^alphaT</c> (and the same for Q), so the solved load is exactly xp + relief + Pt(V).
    /// <paramref name="reliefMw"/> is the load's share of the frequency damping (MW, negative for a reduction). With both
    /// transient exponents 0 the load is constant power and is returned as plain Pd, Qd.
    /// </summary>
    internal Bus AsBus(Bus bus, double reliefMw)
    {
        (double ap, double bp, double cp) = Terms(P0Mw, XpMw + reliefMw, _s.AlphaT);
        (double aq, double bq, double cq) = Terms(Q0Mvar, XqMvar, _s.BetaT);
        var zip = new ZipLoad(ap, bp, cp, aq, bq, cq);
        // A load that does not vary with V in the power flow (both drawn parts constant) is plain constant power: the
        // solver reads Pd and Qd alone then, so the constant terms go there.
        if (!zip.VariesWithVoltage)
            return bus with { Pd = XpMw + reliefMw + P0Mw, Qd = XqMvar + Q0Mvar, Zip = null };
        return bus with { Pd = P0Mw, Qd = Q0Mvar, Zip = zip };
    }

    private (double A, double B, double C) Terms(double nominal, double constant, double exponent)
    {
        if (nominal == 0.0) return (0.0, 0.0, 1.0);   // nothing drawn: the multiplier is never used
        double c = constant / nominal;
        return exponent switch
        {
            2.0 => (1.0 / (V0Pu * V0Pu), 0.0, c),
            1.0 => (0.0, 1.0 / V0Pu, c),
            _ => (0.0, 0.0, c + 1.0),
        };
    }
}
