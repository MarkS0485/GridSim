using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>How a two-terminal LCC link ended up being controlled in a solve.</summary>
public enum LccControl
{
    /// <summary>Blocked: no current, no power, no reactive power.</summary>
    Blocked,
    /// <summary>The rectifier holds the current (or power) order; the inverter holds the DC voltage at its minimum
    /// extinction angle (the normal mode).</summary>
    RectifierCurrent,
    /// <summary>The rectifier could not reach the order (its firing angle at the minimum with its tap at the limit): it
    /// sits at the minimum firing angle and the inverter controls the current at the order less the margin.</summary>
    InverterCurrent,
}

/// <summary>One two-terminal LCC link's solved state: the DC current (A) and voltages (kV), the firing and extinction
/// angles (degrees), the converter taps, the active power each converter draws from or delivers to its AC bus (MW), the
/// reactive power each absorbs (Mvar), the control mode, and whether a power order became a current order (the inverter
/// voltage below VCMOD).</summary>
public sealed record LccLinkResult(
    string Name, LccControl Control, double CurrentA, double RectifierDcKv, double InverterDcKv, double AlphaDeg, double GammaDeg,
    double RectifierTap, double InverterTap, double RectifierMw, double RectifierMvar, double InverterMw, double InverterMvar,
    bool PowerOrderSwitchedToCurrent);

/// <summary>
/// The steady-state model of a two-terminal line-commutated (LCC) HVDC link, PSS/E's two-terminal DC model, and its
/// coupling to the AC power flow.
///
/// <para><b>Converter equations</b> (per converter, N bridges in series; Eac the open-circuit line-to-line voltage on the
/// valve side of the converter transformer, kV; I the DC current, kA):</para>
/// <list type="bullet">
/// <item>Eac = |V| x EBAS x TR / TAP (|V| the AC bus voltage in pu, EBAS its base kV, TR the transformer ratio, TAP the
/// tap on the AC side).</item>
/// <item>Rectifier: Vdr = N (k Eac cos(alpha) - (3/pi Xc + 2 Rc) I); inverter: Vdi = N (k Eac cos(gamma) - (3/pi Xc + 2 Rc) I),
/// k = 3 sqrt(2) / pi; Xc, Rc the commutating reactance and resistance per bridge (ohms).</item>
/// <item>Vdr = Vdi + Rdc I (the line resistance).</item>
/// <item>Active power: the rectifier draws Vdr I, the inverter delivers Vdi I (MW). Reactive power absorbed: P tan(phi)
/// with cos(phi) = (k Eac cos(angle) - 3/pi Xc I) / (k Eac), the classical fundamental power factor.</item>
/// </list>
///
/// <para><b>Control</b> (the normal PSS/E sequence). A current order is SETVL (A); a power order |SETVL| (MW) is held at
/// the rectifier (SETVL &gt; 0) or the inverter (SETVL &lt; 0). The inverter runs at its minimum extinction angle and its
/// tap is stepped (from the file's tap, by its step, within its limits) to bring the compounded DC voltage Vdi + RCOMP x I
/// as close as it can to VSCHD; under a power order the current then follows from the power and that voltage. If the
/// compounded voltage is below VCMOD, the power order becomes a current order (the order divided by VSCHD). The
/// rectifier's firing angle follows from the voltage it must reach, and its tap is stepped to keep the angle between its
/// limits. If even at the minimum angle with its tap at the limit the rectifier cannot reach the voltage, it sits at the
/// minimum angle and the inverter takes the current, at the order less the margin DELTI, with its extinction angle (and
/// tap) set by the voltage the rectifier can give. A link whose order no angle and tap within their limits can meet
/// fails the solve loud: <see cref="SolveLink"/> throws naming the link, and <see cref="Solve"/> returns a result marked
/// NOT converged with that reason in <see cref="PowerFlowResult.LccNote"/>.</para>
///
/// <para><b>Coupling.</b> Each converter is a constant-power load at its AC bus for one AC solve; the DC state is then
/// re-solved from the new AC voltages, and the two alternate until the converters' P and Q change by less than
/// 1e-9 MW (or Mvar) times (1 + |P|), at most 50 times. Deterministic: fixed loops, the AC solve bit-identical as always.</para>
/// </summary>
public static class LccDcLinks
{
    private static readonly double K = 3.0 * Math.Sqrt(2.0) / Math.PI;
    private const double KX = 3.0 / Math.PI;

    /// <summary>The AC/DC alternation cap.</summary>
    public const int MaxCouplingIterations = 50;

    /// <summary>The relative change in converter P and Q below which the AC/DC alternation has settled.</summary>
    public const double CouplingTolerance = 1e-9;

    /// <summary>Solve the DC state of <paramref name="link"/> at the AC bus voltages <paramref name="vRectifierPu"/> and
    /// <paramref name="vInverterPu"/>. Throws <see cref="InvalidOperationException"/> naming the link when no state within
    /// the angle and tap limits meets its order.</summary>
    public static LccLinkResult SolveLink(LccDcLink link, double vRectifierPu, double vInverterPu)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.Mode == LccMode.Blocked)
            return new LccLinkResult(link.Name, LccControl.Blocked, 0, 0, 0, double.NaN, double.NaN, link.Rectifier.Tap, link.Inverter.Tap, 0, 0, 0, 0, false);
        LccConverter rc = link.Rectifier, ic = link.Inverter;
        double cosGmin = DetMath.Cos(Deg(ic.AngleMinDeg)), cosGmax = DetMath.Cos(Deg(ic.AngleMaxDeg));
        double cosAmin = DetMath.Cos(Deg(rc.AngleMinDeg)), cosAmax = DetMath.Cos(Deg(rc.AngleMaxDeg));
        double bi = ic.Bridges * (KX * ic.XcOhms + 2 * ic.RcOhms);   // inverter DC drop per kA
        double br = rc.Bridges * (KX * rc.XcOhms + 2 * rc.RcOhms);   // rectifier DC drop per kA

        bool powerOrder = link.Mode == LccMode.Power;
        bool switched = false;
        double orderKa = powerOrder ? double.NaN : link.Setpoint / 1000.0;

        // ---- Inverter at gamma min: choose its tap, and the current under a power order.
        double I = 0, tapI = ic.Tap, vdi = 0;
        (double I, double Vdi, double Vcomp) AtInverterTap(double tap)
        {
            double a = ic.Bridges * K * Eac(ic, vInverterPu, tap) * cosGmin;   // Vdi = a - bi I
            double cur;
            if (!powerOrder) cur = orderKa;
            else if (link.Setpoint >= 0) cur = Root(link.ResistanceOhms - bi, a, link.Setpoint, link);    // P = (a - bi I + Rdc I) I
            else cur = Root(-bi, a, -link.Setpoint, link);                                                 // P = (a - bi I) I
            double v = a - bi * cur;
            return (cur, v, v + link.CompoundingOhms * cur);
        }
        tapI = StepTap(ic, tap => Math.Abs(AtInverterTap(tap).Vcomp - link.ScheduledDcKv));
        (I, vdi, double vcomp) = AtInverterTap(tapI);
        if (powerOrder && vcomp < link.ModeSwitchDcKv)
        {
            // Below VCMOD the power order becomes a current order.
            powerOrder = false;
            switched = true;
            orderKa = Math.Abs(link.Setpoint) / link.ScheduledDcKv;
            tapI = StepTap(ic, tap => Math.Abs(AtInverterTap(tap).Vcomp - link.ScheduledDcKv));
            (I, vdi, _) = AtInverterTap(tapI);
        }
        if (!(I > 0)) throw Fail(link, $"its order gives a DC current of {F(I * 1000)} A");
        double gamma = ic.AngleMinDeg;

        // ---- Rectifier: the firing angle that gives Vdr = Vdi + Rdc I, its tap keeping the angle within its limits.
        double vdrNeed = vdi + link.ResistanceOhms * I;
        double CosAlpha(double tap) => (vdrNeed + br * I) / (rc.Bridges * K * Eac(rc, vRectifierPu, tap));
        double tapR = rc.Tap;
        // Too little voltage (cos alpha above cos alpha_min): a lower tap raises Eac. Too much (below cos alpha_max): raise it.
        while (CosAlpha(tapR) > cosAmin && tapR - rc.TapStep >= rc.TapMin - 1e-12) tapR -= rc.TapStep;
        while (CosAlpha(tapR) < cosAmax && tapR + rc.TapStep <= rc.TapMax + 1e-12) tapR += rc.TapStep;
        double cosA = CosAlpha(tapR);
        LccControl control = LccControl.RectifierCurrent;
        double alpha, vdr;
        if (cosA > cosAmin)
        {
            // The rectifier cannot reach the voltage: it sits at alpha min and the inverter controls the current at the
            // order less the margin, its extinction angle set by the voltage the rectifier gives.
            control = LccControl.InverterCurrent;
            alpha = rc.AngleMinDeg;
            I = I * (1 - link.MarginPu);
            vdr = rc.Bridges * K * Eac(rc, vRectifierPu, tapR) * cosAmin - br * I;
            vdi = vdr - link.ResistanceOhms * I;
            double CosGamma(double tap) => (vdi + bi * I) / (ic.Bridges * K * Eac(ic, vInverterPu, tap));
            tapI = ic.Tap;
            while (CosGamma(tapI) > cosGmin && tapI - ic.TapStep >= ic.TapMin - 1e-12) tapI -= ic.TapStep;
            while (CosGamma(tapI) < cosGmax && tapI + ic.TapStep <= ic.TapMax + 1e-12) tapI += ic.TapStep;
            double cg = CosGamma(tapI);
            if (cg > cosGmin + 1e-12 || cg < cosGmax - 1e-12 || !(vdi > 0))
                throw Fail(link, $"the rectifier is at its minimum firing angle and the inverter cannot hold the current (extinction angle would be {F(DetMath.Acos(Math.Clamp(cg, -1, 1)) * 180 / Math.PI)} degrees, limits {F(ic.AngleMinDeg)}..{F(ic.AngleMaxDeg)})");
            gamma = DetMath.Acos(cg) * 180 / Math.PI;
        }
        else if (cosA < cosAmax - 1e-12)
        {
            throw Fail(link, $"the rectifier needs a firing angle of {F(DetMath.Acos(Math.Clamp(cosA, -1, 1)) * 180 / Math.PI)} degrees, above its maximum {F(rc.AngleMaxDeg)}, with its tap at the limit");
        }
        else
        {
            alpha = DetMath.Acos(cosA) * 180 / Math.PI;
            vdr = vdrNeed;
        }

        double pr = vdr * I, pi = vdi * I;
        double qr = pr * TanPhi(rc, vRectifierPu, tapR, alpha, I), qi = pi * TanPhi(ic, vInverterPu, tapI, gamma, I);
        return new LccLinkResult(link.Name, control, I * 1000, vdr, vdi, alpha, gamma, tapR, tapI, pr, qr, pi, qi, switched);
    }

    /// <summary>Solve the AC power flow of <paramref name="model"/> with its LCC links, by alternating the AC solve
    /// <paramref name="ac"/> (each converter a constant-power load) and the DC solve. The model handed to
    /// <paramref name="ac"/> has no links and the converters' powers added to their buses' loads; the returned result is
    /// that solve's, with <see cref="PowerFlowResult.LccLinks"/> set. Not settled within the cap: the last result is
    /// returned marked not converged, with the reason in <see cref="PowerFlowResult.LccNote"/>.</summary>
    public static PowerFlowResult Solve(GridModel model, PowerFlowOptions? options, Func<GridModel, PowerFlowOptions?, PowerFlowResult> ac)
        => HvdcCoupling.Solve(model, options, ac);

    /// <summary>The tap of <paramref name="c"/> on its step grid from the file's tap, within its limits, that minimises
    /// <paramref name="error"/>: from the file's tap, step in the improving direction while it improves (ties keep the
    /// tap already reached).</summary>
    private static double StepTap(LccConverter c, Func<double, double> error)
    {
        double tap = c.Tap, e = error(tap);
        if (!(c.TapStep > 0)) return tap;
        foreach (double dir in new[] { -1.0, 1.0 })
        {
            bool moved = false;
            while (true)
            {
                double next = tap + dir * c.TapStep;
                if (next < c.TapMin - 1e-12 || next > c.TapMax + 1e-12) break;
                double en = error(next);
                if (!(en < e)) break;
                tap = next; e = en; moved = true;
            }
            if (moved) break;
        }
        return tap;
    }

    private static double Eac(LccConverter c, double vPu, double tap) => vPu * c.BaseKv * c.TransformerRatio / tap;

    private static double TanPhi(LccConverter c, double vPu, double tap, double angleDeg, double iKa)
    {
        double vd0 = K * Eac(c, vPu, tap);
        double cosPhi = (vd0 * DetMath.Cos(Deg(angleDeg)) - KX * c.XcOhms * iKa) / vd0;
        cosPhi = Math.Clamp(cosPhi, -1.0, 1.0);
        return Math.Sqrt(1 - cosPhi * cosPhi) / cosPhi;
    }

    /// <summary>The positive root of a I^2 + b I - p = 0 (p &gt; 0, b &gt; 0), in the cancellation-free form.</summary>
    private static double Root(double a, double b, double p, LccDcLink link)
    {
        double disc = b * b + 4 * a * p;
        if (disc < 0) throw Fail(link, $"no DC current delivers its power order of {F(Math.Abs(link.Setpoint))} MW at the inverter's voltage");
        return 2 * p / (b + Math.Sqrt(disc));
    }

    private static double Deg(double d) => d * Math.PI / 180.0;

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    private static InvalidOperationException Fail(LccDcLink link, string why) =>
        new($"LCC link {link.Name}: {why}; no steady state within its angle and tap limits meets its order.");
}
