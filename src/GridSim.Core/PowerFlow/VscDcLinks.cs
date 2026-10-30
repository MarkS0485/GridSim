using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.PowerFlow;

/// <summary>One converter's solved state in a VSC link: active and reactive power into its AC bus (MW, Mvar; negative
/// when drawn from the bus), its DC voltage (kV), its losses (MW), the active power order it ended with (a power
/// converter's order after any capability reduction; NaN for the DC voltage converter) and whether its MVA or current
/// rating limited it.</summary>
public sealed record VscConverterResult(int Bus, double Mw, double Mvar, double DcKv, double LossMw, double OrderMw, bool Limited);

/// <summary>One VSC link's solved state: the DC current (A, positive from the power converter towards the DC voltage
/// converter) and both converters.</summary>
public sealed record VscLinkResult(string Name, double CurrentA, VscConverterResult First, VscConverterResult Second);

/// <summary>
/// The steady-state model of a two-terminal VSC HVDC link, PSS/E's VSC DC model.
///
/// <para><b>DC side.</b> One converter holds the DC voltage (TYPE 1, DCSET kV); the other an active power order (TYPE
/// 2, DCSET MW drawn from its AC bus into the link; negative sends power to the bus). Each converter's losses are
/// max(MINLOSS, ALOSS + BLOSS x |Idc|) (kW, Idc in A), taken from the AC side. With R the DC line resistance and I the
/// current from the power converter towards the voltage converter: the power converter's AC draw P = (Vdc + R I) I +
/// loss(|I|), solved for I in the cancellation-free closed form (the root nearest zero); the voltage converter then
/// delivers Vdc I - loss(|I|) to its bus.</para>
///
/// <para><b>AC side.</b> MODE 1 (AC voltage control): the converter is a voltage-controlling source at its bus with its
/// active power fixed, its reactive power between MINQ and MAXQ, holding ACSET pu (the bus becomes a voltage-controlled
/// bus; a reactive limit reached releases it, as for a generator). MODE 2 (fixed power factor): reactive power
/// P x sqrt(1 - pf^2) / pf with pf = ACSET, of the same sign as P for pf &gt; 0.</para>
///
/// <para><b>Ratings.</b> The apparent power must stay within min(SMAX, sqrt(3) |V| base kV IMAX / 1000) (either 0 = no
/// limit). When the solved state exceeds it, the active power order and the reactive power are reduced by the power
/// weighting fraction PWF: P' = P (1 - (1 - PWF) k), Q' = Q (1 - PWF k), with k in 0..1 chosen so that P'^2 + Q'^2 is the
/// limit squared (PWF 1 reduces only the reactive power, 0 only the active power). The reduced reactive power caps the
/// MODE 1 converter's limit on that side. Under MODE 2 the reactive power is tied to the active power by the power
/// factor, so both are scaled onto the rating together (the weighting has nothing to choose between). The DC
/// voltage converter's active power is set by the link, so its weighting is taken as 1 (reactive only); if its active
/// power alone exceeds its rating the solve fails, naming the link.</para>
/// </summary>
public static class VscDcLinks
{
    /// <summary>Solve the DC side of <paramref name="link"/> with the power converter's order scaled by
    /// <paramref name="orderScale"/> (1 = the file's order). Returns the AC active power injection of each converter
    /// (MW, First then Second), the current (kA), the DC voltages (kV) and losses (MW).</summary>
    public static (double FirstMw, double SecondMw, double CurrentKa, double FirstDcKv, double SecondDcKv, double FirstLossMw, double SecondLossMw)
        SolveDc(VscDcLink link, double orderScale = 1.0)
    {
        ArgumentNullException.ThrowIfNull(link);
        bool firstIsVoltage = link.First.DcControl == VscDcControl.DcVoltage;
        VscConverter v = firstIsVoltage ? link.First : link.Second, p = firstIsVoltage ? link.Second : link.First;
        double vdc = v.DcSetpoint, r = link.ResistanceOhms, order = p.DcSetpoint * orderScale;

        // The power converter: P = (Vdc + R I) I + loss(|I|), loss = max(MIN, A + B |I|) in MW with I in kA.
        double a = p.LossAKw / 1000, b = p.LossBKwPerA, min = p.MinLossKw / 1000;
        double I = Current(order, vdc, r, a, b, link);
        if (a + b * Math.Abs(I) < min) I = Current(order, vdc, r, min, 0, link);
        double lossP = Math.Max(min, a + b * Math.Abs(I));
        double av = v.LossAKw / 1000, bv = v.LossBKwPerA, minv = v.MinLossKw / 1000;
        double lossV = Math.Max(minv, av + bv * Math.Abs(I));
        double injP = -order, injV = vdc * I - lossV;
        double vdcP = vdc + r * I;
        return firstIsVoltage
            ? (injV, injP, I, vdc, vdcP, lossV, lossP)
            : (injP, injV, I, vdcP, vdc, lossP, lossV);
    }

    /// <summary>The signed current (kA) from the power converter: for I &gt;= 0, R I^2 + (Vdc + B) I + (A - P) = 0; for
    /// I &lt; 0 with J = -I, R J^2 - (Vdc - B) J + (A - P) = 0; the root nearest zero, in the cancellation-free form.</summary>
    private static double Current(double p, double vdc, double r, double a, double b, VscDcLink link)
    {
        double c = a - p;
        if (c <= 0)
        {
            double bb = vdc + b, disc = bb * bb - 4 * r * c;
            return -2 * c / (bb + Math.Sqrt(disc));
        }
        double bn = vdc - b, dn = bn * bn - 4 * r * c;
        if (dn < 0 || !(bn > 0))
            throw new InvalidOperationException($"VSC link {link.Name}: no DC current delivers its power order of {F(p)} MW at {F(vdc)} kV through {F(r)} ohms; no steady state meets its order.");
        return -(2 * c / (bn + Math.Sqrt(dn)));
    }

    /// <summary>The apparent power limit of <paramref name="c"/> at bus voltage <paramref name="vPu"/> on base
    /// <paramref name="baseKv"/> (MVA; +inf when neither SMAX nor IMAX is given).</summary>
    public static double Limit(VscConverter c, double vPu, double baseKv)
    {
        double s = c.SmaxMva > 0 ? c.SmaxMva : double.PositiveInfinity;
        if (c.ImaxA > 0) s = Math.Min(s, Math.Sqrt(3) * vPu * baseKv * c.ImaxA / 1000);
        return s;
    }

    /// <summary>The PWF reduction of (P, Q) onto the circle of radius <paramref name="limit"/>: P' = P (1 - (1 - w) k),
    /// Q' = Q (1 - w k), k in 0..1 the root of P'^2 + Q'^2 = limit^2. Null when even k = 1 stays outside (the weighted
    /// reduction cannot reach the limit).</summary>
    public static (double P, double Q)? Reduce(double p, double q, double limit, double w)
    {
        // f(k) = (P - P(1-w) k)^2 + (Q - Q w k)^2 - L^2 = A k^2 + B k + C.
        double pa = p * (1 - w), qa = q * w;
        double qa2 = pa * pa + qa * qa, qb = -2 * (p * pa + q * qa), qc = p * p + q * q - limit * limit;
        if (qc <= 0) return (p, q);
        double k;
        if (qa2 == 0) return null;
        double disc = qb * qb - 4 * qa2 * qc;
        if (disc < 0) return null;
        k = 2 * qc / (-qb + Math.Sqrt(disc));   // the smaller root, cancellation-free (qb < 0 here)
        if (!(k <= 1 + 1e-12)) return null;
        k = Math.Min(k, 1.0);
        return (p * (1 - (1 - w) * k), q * (1 - w * k));
    }

    /// <summary>Reactive power of a fixed-power-factor converter injecting <paramref name="p"/> MW.</summary>
    public static double PowerFactorQ(double p, double pf) => p * Math.Sqrt(1 - pf * pf) / pf;

    private static string F(double v) => v.ToString("G6", CultureInfo.InvariantCulture);
}
