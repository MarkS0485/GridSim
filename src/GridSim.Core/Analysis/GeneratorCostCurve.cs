using System.Globalization;
using GridSim.Core.Model;

namespace GridSim.Core.Analysis;

/// <summary>
/// A generator's active-power cost curve in the form the DC-OPF can optimise (F19, October 2026): a convex
/// quadratic polynomial <c>C(P) = a P^2 + b P + c</c> (a &gt;= 0), or a convex piecewise-linear curve through a
/// list of (MW, cost per hour) points.
///
/// <para><b>What is accepted, and why.</b> The input is a <see cref="GeneratorCost"/> exactly as a MATPOWER
/// <c>mpc.gencost</c> row gives it (or the JSON cost file, which uses the same layout). MATPOWER's own DC OPF
/// accepts polynomial costs up to quadratic and piecewise-linear costs, and needs them convex; this class applies the
/// same limits and refuses anything else, naming the generator, rather than quietly approximating it:</para>
/// <list type="bullet">
/// <item>Polynomial: NCOST 1 (a constant), 2 (linear) or 3 (quadratic). A longer polynomial is accepted only when
/// every coefficient above the quadratic one is exactly zero (pglib and MATPOWER files often pad that way); a real
/// cubic or higher term is refused. A negative quadratic coefficient (a concave curve) is refused.</item>
/// <item>Piecewise linear: at least 2 points, MW strictly increasing, slopes never decreasing (convex). Outside the
/// points the first and last segments are extended in a straight line, which is what MATPOWER's constrained cost
/// variable formulation does (each segment is an inequality valid on the whole axis).</item>
/// </list>
/// <para>Every number must be finite (the MATPOWER parser already refuses non-finite gencost numbers; the cost-file
/// reader does too). Units are the case's own: cost per hour, MW, so a marginal cost or a nodal price comes out in cost
/// per MWh of the same currency.</para>
/// </summary>
public sealed class GeneratorCostCurve
{
    /// <summary>Quadratic coefficient a (cost per MW^2 per hour) of a polynomial curve; 0 for a piecewise-linear one.</summary>
    public double Quadratic { get; }

    /// <summary>Linear coefficient b (cost per MWh) of a polynomial curve; unused for a piecewise-linear one.</summary>
    public double Linear { get; }

    /// <summary>Constant c (cost per hour) of a polynomial curve; unused for a piecewise-linear one.</summary>
    public double Constant { get; }

    /// <summary>The breakpoints (MW) of a piecewise-linear curve, strictly increasing; empty for a polynomial.</summary>
    public IReadOnlyList<double> PointsMw { get; }

    /// <summary>The cost (per hour) at each breakpoint of a piecewise-linear curve; empty for a polynomial.</summary>
    public IReadOnlyList<double> PointsCost { get; }

    /// <summary>True for a piecewise-linear curve, false for a polynomial.</summary>
    public bool IsPiecewiseLinear => PointsMw.Count > 0;

    private GeneratorCostCurve(double a, double b, double c, double[] xs, double[] ys)
    {
        Quadratic = a; Linear = b; Constant = c; PointsMw = xs; PointsCost = ys;
    }

    /// <summary>
    /// Build the optimisable curve from a parsed cost row, refusing (with <see cref="InvalidDataException"/> naming
    /// <paramref name="who"/>) any curve the DC-OPF cannot optimise exactly: see the class note.
    /// </summary>
    public static GeneratorCostCurve From(GeneratorCost cost, string who)
    {
        ArgumentNullException.ThrowIfNull(cost);
        IReadOnlyList<double> k = cost.Coefficients;
        foreach (double v in k)
            if (!double.IsFinite(v))
                throw new InvalidDataException($"{who}: the cost curve holds {v.ToString("R", CultureInfo.InvariantCulture)}, which is not a finite number.");

        if (cost.Model == GeneratorCostModel.Polynomial)
        {
            int n = k.Count;
            if (n < 1) throw new InvalidDataException($"{who}: a polynomial cost needs at least one coefficient.");
            // Coefficients are highest order first: c(n-1) ... c1 c0. Anything above the quadratic term must be zero.
            for (int i = 0; i < n - 3; i++)
                if (k[i] != 0.0)
                    throw new InvalidDataException(
                        $"{who}: the polynomial cost has a non-zero term of order {n - 1 - i} ({k[i].ToString("R", CultureInfo.InvariantCulture)}); "
                        + "the DC-OPF optimises costs up to quadratic only (as MATPOWER's DC OPF does).");
            double c0 = k[n - 1];
            double c1 = n >= 2 ? k[n - 2] : 0.0;
            double c2 = n >= 3 ? k[n - 3] : 0.0;
            if (c2 < 0.0)
                throw new InvalidDataException(
                    $"{who}: the quadratic cost coefficient is {c2.ToString("R", CultureInfo.InvariantCulture)}; a negative value makes the curve concave, which an optimal dispatch cannot be defined on.");
            return new GeneratorCostCurve(c2, c1, c0, Array.Empty<double>(), Array.Empty<double>());
        }

        if (cost.Model != GeneratorCostModel.PiecewiseLinear)
            throw new InvalidDataException($"{who}: unknown cost model {(int)cost.Model}.");
        if (k.Count % 2 != 0)
            throw new InvalidDataException($"{who}: a piecewise-linear cost needs (MW, cost) pairs; {k.Count} number(s) given.");
        int pts = k.Count / 2;
        if (pts < 2)
            throw new InvalidDataException($"{who}: a piecewise-linear cost needs at least 2 points; {pts} given.");
        var xs = new double[pts];
        var ys = new double[pts];
        for (int i = 0; i < pts; i++) { xs[i] = k[2 * i]; ys[i] = k[2 * i + 1]; }
        for (int i = 1; i < pts; i++)
            if (!(xs[i] > xs[i - 1]))
                throw new InvalidDataException(
                    $"{who}: the piecewise-linear cost's MW points must strictly increase; point {i + 1} ({xs[i].ToString("R", CultureInfo.InvariantCulture)} MW) does not exceed point {i} ({xs[i - 1].ToString("R", CultureInfo.InvariantCulture)} MW).");
        double prevSlope = double.NegativeInfinity;
        for (int i = 1; i < pts; i++)
        {
            double s = (ys[i] - ys[i - 1]) / (xs[i] - xs[i - 1]);
            if (s < prevSlope)
                throw new InvalidDataException(
                    $"{who}: the piecewise-linear cost is not convex: segment {i} has slope {s.ToString("R", CultureInfo.InvariantCulture)}, below the previous segment's {prevSlope.ToString("R", CultureInfo.InvariantCulture)}.");
            prevSlope = s;
        }
        return new GeneratorCostCurve(0.0, 0.0, 0.0, xs, ys);
    }

    /// <summary>Slope (cost per MWh) of piecewise-linear segment <paramref name="i"/> (between points i and i + 1).</summary>
    public double SegmentSlope(int i) => (PointsCost[i + 1] - PointsCost[i]) / (PointsMw[i + 1] - PointsMw[i]);

    /// <summary>The index of the piecewise-linear segment that holds <paramref name="pMw"/>: a breakpoint belongs to
    /// the segment BELOW it (the left derivative), except the first point, which belongs to segment 0; below the first
    /// point it is segment 0 extended, above the last it is the last segment extended.</summary>
    public int SegmentOf(double pMw)
    {
        int last = PointsMw.Count - 2;
        for (int i = 0; i < last; i++)
            if (pMw <= PointsMw[i + 1]) return i;
        return last;
    }

    /// <summary>The cost per hour at output <paramref name="pMw"/>: a P^2 + b P + c, or the piecewise-linear value
    /// (the end segments extended).</summary>
    public double CostAt(double pMw)
    {
        if (!IsPiecewiseLinear) return Quadratic * pMw * pMw + Linear * pMw + Constant;
        int s = SegmentOf(pMw);
        return PointsCost[s] + SegmentSlope(s) * (pMw - PointsMw[s]);
    }

    /// <summary>The marginal cost (cost per MWh) at output <paramref name="pMw"/>: 2 a P + b, or the slope of the
    /// segment holding P (<see cref="SegmentOf"/>; at a breakpoint, the segment below it).</summary>
    public double MarginalCostAt(double pMw)
        => IsPiecewiseLinear ? SegmentSlope(SegmentOf(pMw)) : 2.0 * Quadratic * pMw + Linear;
}
