using System;
using System.Collections.Generic;

namespace GridSim.Core.Compliance.GridCode;

/// <summary>
/// A fault-ride-through voltage-against-time profile (the lower limit of the connection-point voltage a unit must stay
/// connected above), in the shape of Figure 3 of Regulation (EU) 2016/631, which GB Figures ECC.6.3.15.2 to ECC.6.3.15.7
/// use. The shape is read from the figure: t is measured from fault inception; U(t) = Uret for 0 &lt;= t &lt;= Tclear (the
/// lower value is held at Tclear itself); then a straight line from (Tclear, Uclear) to (Trec1, Urec1), Urec1 up to Trec2, a
/// line from (Trec2, Urec1) to (Trec3, Urec2), and Urec2 after Trec3. A segment of zero length is skipped. A line is
/// evaluated as <c>a + (b - a) * (t - t0) / (t1 - t0)</c>, in that order.
/// </summary>
public sealed record FrtProfile
{
    /// <summary>A profile; refused unless 0 &lt;= Tclear &lt;= Trec1 &lt;= Trec2 &lt;= Trec3 (finite) and every voltage lies in [0, 1] pu.</summary>
    public FrtProfile(double uret, double uclear, double urec1, double urec2, double tclear, double trec1, double trec2, double trec3, string clause)
    {
        foreach ((string name, double v) in new[] { ("Uret", uret), ("Uclear", uclear), ("Urec1", urec1), ("Urec2", urec2) })
            if (!(v >= 0.0 && v <= 1.0)) throw new ArgumentException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"FRT profile: {name} must lie in [0, 1] pu (got {v:R})."));
        foreach ((string name, double v) in new[] { ("Tclear", tclear), ("Trec1", trec1), ("Trec2", trec2), ("Trec3", trec3) })
            if (!double.IsFinite(v)) throw new ArgumentException($"FRT profile: {name} must be finite.");
        if (!(0.0 <= tclear && tclear <= trec1 && trec1 <= trec2 && trec2 <= trec3))
            throw new ArgumentException("FRT profile: the times must satisfy 0 <= Tclear <= Trec1 <= Trec2 <= Trec3.");
        Uret = uret; Uclear = uclear; Urec1 = urec1; Urec2 = urec2;
        Tclear = tclear; Trec1 = trec1; Trec2 = trec2; Trec3 = trec3;
        Clause = clause ?? "";
    }

    /// <summary>Retained voltage during the fault (pu).</summary>
    public double Uret { get; }
    /// <summary>Voltage at clearance (pu).</summary>
    public double Uclear { get; }
    /// <summary>First recovery voltage (pu).</summary>
    public double Urec1 { get; }
    /// <summary>Final recovery voltage (pu).</summary>
    public double Urec2 { get; }
    /// <summary>Clearing time from fault inception (s).</summary>
    public double Tclear { get; }
    /// <summary>End of the first recovery ramp (s).</summary>
    public double Trec1 { get; }
    /// <summary>End of the Urec1 plateau (s).</summary>
    public double Trec2 { get; }
    /// <summary>End of the second recovery ramp (s).</summary>
    public double Trec3 { get; }
    /// <summary>The clause or table the values were read from.</summary>
    public string Clause { get; }

    private static double Line(double a, double b, double t, double t0, double t1) => a + (b - a) * (t - t0) / (t1 - t0);

    /// <summary>The lower limit U(t) (pu) at <paramref name="t"/> seconds after fault inception (t &gt;= 0).</summary>
    public double LowerLimitPu(double t)
    {
        if (!(t >= 0.0) || double.IsPositiveInfinity(t)) throw new ArgumentOutOfRangeException(nameof(t), t, "FRT profile: t must be a finite time from fault inception, 0 or more.");
        if (t <= Tclear) return Uret;
        if (t <= Trec1) return Line(Uclear, Urec1, t, Tclear, Trec1);
        if (t <= Trec2) return Urec1;
        if (t <= Trec3) return Line(Urec1, Urec2, t, Trec2, Trec3);
        return Urec2;
    }

    /// <summary>
    /// The total time (s) the line lies strictly below <paramref name="vPu"/>: the measure of { t &gt;= 0 : U(t) &lt; vPu }.
    /// Positive infinity when Urec2 is below <paramref name="vPu"/> (the line never returns above it). Adjoining stretches
    /// below the value are joined and each joined stretch is taken as its end less its start, so a profile that rises
    /// through the value once gives exactly the crossing time.
    /// </summary>
    public double TimeBelow(double vPu)
    {
        if (!double.IsFinite(vPu)) throw new ArgumentOutOfRangeException(nameof(vPu), vPu, "FRT profile: the voltage must be finite.");
        if (Urec2 < vPu) return double.PositiveInfinity;
        var runs = new List<(double Start, double End)>();
        void Add(double s, double e)
        {
            if (!(e > s)) return;
            if (runs.Count > 0 && runs[^1].End == s) runs[^1] = (runs[^1].Start, e);
            else runs.Add((s, e));
        }
        void Linear(double a, double b, double t0, double t1)
        {
            if (!(t1 > t0)) return;
            bool aBelow = a < vPu, bBelow = b < vPu;
            if (aBelow && bBelow) { Add(t0, t1); return; }
            if (!aBelow && !bBelow) return;
            double tc = t0 + (vPu - a) * (t1 - t0) / (b - a);
            if (aBelow) Add(t0, tc); else Add(tc, t1);
        }
        if (Uret < vPu) Add(0.0, Tclear);
        Linear(Uclear, Urec1, Tclear, Trec1);
        if (Urec1 < vPu) Add(Trec1, Trec2);
        Linear(Urec1, Urec2, Trec2, Trec3);
        double total = 0.0;
        foreach ((double s, double e) in runs) total += e - s;
        return total;
    }
}
