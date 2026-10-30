using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.Harmonics;

/// <summary>The grid and refinement settings for a <see cref="HarmonicFrequencySweep"/>.</summary>
public sealed record FrequencySweepOptions
{
    /// <summary>Lowest frequency multiple swept (h; 1.5 = 75 Hz on a 50 Hz system).</summary>
    public double MinOrder { get; init; } = 1.5;

    /// <summary>Highest frequency multiple swept (h; 50 = 2.5 kHz on a 50 Hz system, just past the 49th).</summary>
    public double MaxOrder { get; init; } = 50.0;

    /// <summary>Grid spacing in orders. 0.1 order = 5 Hz at 50 Hz: finer than the half-power bandwidth h/Q of
    /// a resonance with Q up to 50 at h = 5, so a sharp peak between integer orders cannot fall between grid
    /// points unseen; the golden-section refinement then pins the peak itself.</summary>
    public double Step { get; init; } = 0.1;

    /// <summary>Width (in orders) to which the peak's bracket is narrowed by golden-section search.</summary>
    public double RefineTolerance { get; init; } = 1e-6;

    /// <summary>Hard cap on grid points, so a mistyped step cannot launch an unbounded sweep.</summary>
    public const int MaxGridPoints = 100_000;
}

/// <summary>
/// The result of a fine driving-point frequency sweep. <see cref="Orders"/> is the sweep grid;
/// <see cref="PositiveZ"/> is the positive-sequence driving-point impedance on it and <see cref="NegativeZ"/> the
/// negative-sequence one (swept only when the network has a phase-shifting transformer - otherwise the two
/// networks are identical and it is null). <see cref="PeakOrder"/> / <see cref="PeakMagnitude"/> are the
/// REFINED resonance: the largest |Z| found, located between grid points by golden-section search.
/// </summary>
public sealed record FrequencySweepResult(
    int BusId,
    IReadOnlyList<double> Orders,
    Complex[] PositiveZ,
    Complex[]? NegativeZ,
    double PeakOrder,
    double PeakMagnitude,
    HarmonicSequenceKind PeakSequence,
    double FundamentalHz)
{
    /// <summary>The refined resonance frequency in hertz.</summary>
    public double PeakHz => PeakOrder * FundamentalHz;
}

/// <summary>
/// A fine driving-point impedance sweep (R063, October 2026). The order-by-order
/// <see cref="HarmonicImpedanceScan"/> samples |Z| only at the analysed integer orders, so a sharp, lightly
/// damped parallel resonance BETWEEN orders - at h = 6.2, say, the very signature the sharpness metric is
/// meant to catch - was never sampled and was reported at the nearest order with a much smaller peak. This
/// sweeps a fine grid of frequency multiples on the positive-sequence network (and the negative-sequence one
/// when a phase shifter makes them differ), then refines the largest peak by golden-section search.
///
/// <para>Deterministic: grid points are <c>MinOrder + k*Step</c> (never accumulated), every point is solved
/// independently and written by index, the peak tie-break is the first (lowest) grid point, and the refinement
/// runs a fixed number of steps. A singular point fails loud (<see cref="SingularHarmonicNetworkException"/>,
/// lowest frequency first).</para>
/// </summary>
public static class HarmonicFrequencySweep
{
    /// <summary>Sweep the driving-point impedance at <paramref name="busId"/>. The harmonic model options (machines,
    /// loads, skin, long line) define the network; the sweep options define the grid.</summary>
    public static FrequencySweepResult DrivingPoint(
        GridModel model,
        int busId,
        HarmonicModelOptions? options = null,
        FrequencySweepOptions? sweep = null)
    {
        HarmonicModelOptions opt = options ?? new HarmonicModelOptions();
        FrequencySweepOptions sw = sweep ?? new FrequencySweepOptions();
        Validate(sw);
        // N348 / N149 (end pass, 2026-10-11): refuse a non-physical active-front-end impedance once, up front, so the
        // refusal is a plain ArgumentException and not one wrapped in an AggregateException by the parallel loop below
        // (as HarmonicPenetration.Solve). Validation only: no number changes.
        if (opt.ActiveFrontEndReactancePu is double xAfe)
            FrequencyDependentYBus.ValidateActiveFrontEndImpedance(opt.ActiveFrontEndResistancePu, xAfe);

        int n = model.BusCount;
        int k = -1;
        for (int i = 0; i < n; i++) if (model.Buses[i].Id == busId) { k = i; break; }
        if (k < 0) throw new ArgumentException($"bus {busId} is not in the model.", nameof(busId));

        int points = (int)GridPointCount(sw);   // validated above with the same count (GR-587)
        var grid = new double[points];
        for (int p = 0; p < points; p++) grid[p] = sw.MinOrder + p * sw.Step;

        // The two sequence networks differ only through transformer phase shifts.
        bool hasShift = false;
        foreach (Branch br in model.Branches) if (br.InService && br.PhaseShiftDeg != 0.0) { hasShift = true; break; }

        Complex[] zPos = SweepGrid(model, k, grid, HarmonicSequenceKind.Positive, opt);
        Complex[]? zNeg = hasShift ? SweepGrid(model, k, grid, HarmonicSequenceKind.Negative, opt) : null;

        // Grid peak: first (lowest-frequency) maximum, positive sequence before negative on an exact tie.
        int bestP = 0;
        HarmonicSequenceKind bestSeq = HarmonicSequenceKind.Positive;
        double bestMag = zPos[0].Magnitude;
        for (int p = 1; p < points; p++)
            if (zPos[p].Magnitude > bestMag) { bestMag = zPos[p].Magnitude; bestP = p; }
        if (zNeg is not null)
            for (int p = 0; p < points; p++)
                if (zNeg[p].Magnitude > bestMag) { bestMag = zNeg[p].Magnitude; bestP = p; bestSeq = HarmonicSequenceKind.Negative; }

        // Refine between the neighbouring grid points by golden-section search on |Z(h)|.
        double lo = grid[Math.Max(bestP - 1, 0)];
        double hi = grid[Math.Min(bestP + 1, points - 1)];
        (double peakH, double peakMag) = Refine(model, k, bestSeq, opt, lo, hi, sw.RefineTolerance, grid[bestP], bestMag);

        return new FrequencySweepResult(busId, grid, zPos, zNeg, peakH, peakMag, bestSeq, model.FrequencyHz);
    }

    private static void Validate(FrequencySweepOptions sw)
    {
        if (!(sw.MinOrder > 0.0) || !double.IsFinite(sw.MinOrder))
            throw new ArgumentException($"MinOrder must be positive and finite; got {sw.MinOrder:R}.");
        if (!(sw.MaxOrder > sw.MinOrder) || !double.IsFinite(sw.MaxOrder))
            throw new ArgumentException($"MaxOrder ({sw.MaxOrder:R}) must be finite and above MinOrder ({sw.MinOrder:R}).");
        if (!(sw.Step > 0.0) || !double.IsFinite(sw.Step))
            throw new ArgumentException($"Step must be positive and finite; got {sw.Step:R}.");
        if (!(sw.RefineTolerance > 0.0) || !double.IsFinite(sw.RefineTolerance))
            throw new ArgumentException($"RefineTolerance must be positive and finite; got {sw.RefineTolerance:R}.");
        // GR-587: the count the grid is built with (the same 1e-9 nudge), so a span that is a whole number of steps
        // short by rounding cannot pass as MaxGridPoints and then be built with one point more.
        double count = GridPointCount(sw);
        if (count < 2 || count > FrequencySweepOptions.MaxGridPoints)
            throw new ArgumentException(
                $"The sweep grid would have {count} points; it needs between 2 and {FrequencySweepOptions.MaxGridPoints}.");
    }

    // The number of grid points from MinOrder in steps of Step up to MaxOrder, with a 1e-9 nudge so a span that is a
    // whole number of steps to rounding includes its last point. A double, so the range check above cannot overflow.
    private static double GridPointCount(FrequencySweepOptions sw)
        => Math.Floor((sw.MaxOrder - sw.MinOrder) / sw.Step + 1e-9) + 1;

    /// <summary>Driving-point Z at every grid point, in parallel with a per-worker solver (the real-expanded
    /// solver's ordering cache is per instance and not thread-safe). Results written by index.</summary>
    private static Complex[] SweepGrid(GridModel model, int k, double[] grid, HarmonicSequenceKind seq, HarmonicModelOptions opt)
    {
        var z = new Complex[grid.Length];
        var singular = new bool[grid.Length];
        using (var pool = new ThreadLocal<IComplexLinearSolver>(() => new RealExpandedComplexSolver()))
        {
            DeterministicParallel.For(grid.Length, p =>   // an exception surfaces unwrapped, lowest grid point first
            {
                Complex? v = DrivingPointAt(model, k, grid[p], seq, opt, pool.Value!);
                if (v is null) singular[p] = true;
                else z[p] = v.Value;
            });
        }
        for (int p = 0; p < grid.Length; p++)
            if (singular[p]) throw new SingularHarmonicNetworkException("Harmonic frequency sweep", grid[p]);
        return z;
    }

    /// <summary>Z_kk at frequency multiple h on the given sequence network, or null if Y(h) is singular.</summary>
    private static Complex? DrivingPointAt(GridModel model, int k, double h, HarmonicSequenceKind seq,
        HarmonicModelOptions opt, IComplexLinearSolver solver)
    {
        ComplexCsr y = FrequencyDependentYBus.BuildSequence(model, h, seq, opt);
        var e = new Complex[y.N];
        e[k] = Complex.One;
        Complex[]? x = solver.Solve(y, e);
        return x is null ? null : x[k];
    }

    /// <summary>
    /// Golden-section search for the maximum of |Z(h)| on [lo, hi], a fixed number of steps (enough to narrow
    /// the bracket below <paramref name="tol"/>), sequential and deterministic. Returns the best point seen,
    /// which is never worse than the grid peak it started from.
    /// </summary>
    private static (double H, double Mag) Refine(GridModel model, int k, HarmonicSequenceKind seq,
        HarmonicModelOptions opt, double lo, double hi, double tol, double gridH, double gridMag)
    {
        double bestH = gridH, bestMag = gridMag;
        if (!(hi - lo > tol)) return (bestH, bestMag);

        var solver = new RealExpandedComplexSolver();
        double Mag(double h)
        {
            Complex? z = DrivingPointAt(model, k, h, seq, opt, solver);
            if (z is null) throw new SingularHarmonicNetworkException("Harmonic frequency sweep", h);
            double m = z.Value.Magnitude;
            if (m > bestMag) { bestMag = m; bestH = h; }
            return m;
        }

        double invPhi = (Math.Sqrt(5.0) - 1.0) / 2.0;   // 1/golden ratio: each step keeps this fraction
        int steps = (int)Math.Ceiling(DetMath.Log(tol / (hi - lo)) / DetMath.Log(invPhi));
        double a = lo, b = hi;
        double c = b - invPhi * (b - a), d = a + invPhi * (b - a);
        double fc = Mag(c), fd = Mag(d);
        for (int s = 0; s < steps; s++)
        {
            if (fc >= fd) { b = d; d = c; fd = fc; c = b - invPhi * (b - a); fc = Mag(c); }
            else { a = c; c = d; fc = fd; d = a + invPhi * (b - a); fd = Mag(d); }
        }
        return (bestH, bestMag);
    }
}
