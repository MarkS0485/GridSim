using GridSim.Core.Diagnostics;

namespace GridSim.Core.Analysis.Probabilistic;

/// <summary>
/// A probability mass function on the lattice <c>Constant + k * Step</c> (integer k from <see cref="FirstIndex"/>), the
/// carrier of the convolution method. The distribution of a sum of independent terms is the convolution of their lattice
/// masses (direct summation, fixed index order, so the result is bit-identical on every run).
///
/// <para><b>Exactness.</b> A discrete term whose values are integer multiples of the step lands on the lattice without
/// error: the convolution of such terms is the exact distribution of their sum (this is how the capacity outage table is
/// exact). A continuous term is cut into cells of width <c>Step</c> centred on the lattice points, each given the exact
/// probability of its cell (the tails beyond the 1e-13 quantiles are lumped into the end cells), so the sum is the
/// distribution of the sum of the cell-rounded terms: the mean is preserved to rounding and the variance is inflated by
/// about <c>Step^2 / 12</c> per term (Sheppard), which is the stated discretisation tolerance.</para>
/// </summary>
public sealed class LatticePmf
{
    /// <summary>Maximum cells of one mass function (refusal above).</summary>
    public const int MaxCells = 4_000_000;

    /// <summary>The lattice step (above 0).</summary>
    public double Step { get; }
    /// <summary>The lattice origin: the value of index 0.</summary>
    public double Constant { get; }
    /// <summary>The lattice index of <see cref="Mass"/>[0].</summary>
    public long FirstIndex { get; }
    /// <summary>The cell masses, in index order.</summary>
    public double[] Mass { get; }
    /// <summary>True while every term was discrete and landed on the lattice: the masses are exact atoms, not cells.</summary>
    public bool AtomsExact { get; }

    private LatticePmf(double step, double constant, long first, double[] mass, bool atomsExact)
    {
        Step = step; Constant = constant; FirstIndex = first; Mass = mass; AtomsExact = atomsExact;
    }

    /// <summary>A point mass at <paramref name="value"/> (lattice origin at the value).</summary>
    public static LatticePmf PointMass(double value, double step) => new(step, value, 0, new[] { 1.0 }, true);

    /// <summary>The value at lattice index <paramref name="index"/>.</summary>
    public double ValueAt(long index) => Constant + index * Step;

    /// <summary>
    /// The mass function of <c>scale * X</c> for the law <paramref name="d"/> on the lattice of <paramref name="step"/>
    /// (origin 0). A discrete law gives one atom per value, at the nearest lattice index; <see cref="AtomsExact"/> is
    /// false when some atom was off the lattice by more than 1e-9 of a step.
    /// </summary>
    public static LatticePmf OfTerm(UncertainDistribution d, double scale, double step)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (!(step > 0.0) || !double.IsFinite(step)) throw DomainRefusal.Mark(new ArgumentException("The lattice step must be a finite number above 0."));
        if (!double.IsFinite(scale)) throw DomainRefusal.Mark(new ArgumentException("The term scale must be finite."));
        if (d.IsDiscrete)
        {
            var cells = new SortedDictionary<long, double>();
            bool exact = true;
            foreach ((double v, double p) in d.Atoms)
            {
                double pos = scale * v / step;
                long k = (long)Math.Round(pos, MidpointRounding.AwayFromZero);
                if (Math.Abs(pos - k) > 1e-9) exact = false;
                cells[k] = cells.GetValueOrDefault(k) + p;
            }
            long first = cells.Keys.First(), last = cells.Keys.Last();
            RequireSize(last - first + 1);
            var mass = new double[last - first + 1];
            foreach ((long k, double p) in cells) mass[k - first] = p;
            return new LatticePmf(step, 0.0, first, mass, exact);
        }
        if (scale == 0.0) return PointMass(0.0, step);
        double xa = d.Quantile(1e-13), xb = d.Quantile(1.0 - 1e-13);
        double ta = scale * xa, tb = scale * xb;
        double tlo = Math.Min(ta, tb), thi = Math.Max(ta, tb);
        long kmin = (long)Math.Round(tlo / step, MidpointRounding.AwayFromZero);
        long kmax = (long)Math.Round(thi / step, MidpointRounding.AwayFromZero);
        RequireSize(kmax - kmin + 1);
        var m = new double[kmax - kmin + 1];
        double G(double t) => scale > 0 ? d.Cdf(t / scale) : 1.0 - d.Cdf(t / scale);
        double prev = 0.0;
        for (long k = kmin; k <= kmax; k++)
        {
            double upper = k == kmax ? 1.0 : G((k + 0.5) * step);
            m[k - kmin] = Math.Max(0.0, upper - prev);
            prev = upper;
        }
        return new LatticePmf(step, 0.0, kmin, m, false);
    }

    private static void RequireSize(long cells)
    {
        if (cells > MaxCells)
            throw DomainRefusal.Mark(new InvalidOperationException(
                $"The lattice needs {cells} cells (limit {MaxCells}); use a coarser step or fewer cells per standard deviation."));
    }

    /// <summary>The distribution of the sum of this and an independent term on the same step.</summary>
    public LatticePmf Convolve(LatticePmf other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Step != Step) throw new ArgumentException("The lattices must share one step.");
        long n = Mass.Length + (long)other.Mass.Length - 1;
        RequireSize(n);
        var r = new double[n];
        for (int i = 0; i < Mass.Length; i++)
        {
            double a = Mass[i];
            if (a == 0.0) continue;
            for (int j = 0; j < other.Mass.Length; j++) r[i + j] += a * other.Mass[j];
        }
        return new LatticePmf(Step, Constant + other.Constant, FirstIndex + other.FirstIndex, r, AtomsExact && other.AtomsExact);
    }

    /// <summary>The same distribution shifted by <paramref name="delta"/> (a constant added to the sum).</summary>
    public LatticePmf Shift(double delta) => new(Step, Constant + delta, FirstIndex, Mass, AtomsExact);

    /// <summary>The mean.</summary>
    public double Mean
    {
        get
        {
            double s = 0.0;
            for (int i = 0; i < Mass.Length; i++) s += Mass[i] * i;
            return Constant + (FirstIndex + s) * Step;
        }
    }

    /// <summary>The variance of the lattice masses (includes the cell-rounding inflation of continuous terms).</summary>
    public double Variance
    {
        get
        {
            double m = 0.0;
            for (int i = 0; i < Mass.Length; i++) m += Mass[i] * i;
            double v = 0.0;
            for (int i = 0; i < Mass.Length; i++) v += Mass[i] * (i - m) * (i - m);
            return v * Step * Step;
        }
    }

    /// <summary>The total mass (1 up to rounding).</summary>
    public double TotalMass
    {
        get { double s = 0.0; foreach (double m in Mass) s += m; return s; }
    }

    /// <summary>P(Y &lt;= x). Exact atoms: the step function. Cells: linear between cell edges (each cell's mass spread
    /// uniformly), which converges to the continuous CDF as the step shrinks.</summary>
    public double Cdf(double x)
    {
        if (AtomsExact)
        {
            double eps = 1e-9 * Step, c = 0.0;
            for (int i = 0; i < Mass.Length; i++)
            {
                if (ValueAt(FirstIndex + i) <= x + eps) c += Mass[i]; else break;
            }
            return Math.Min(1.0, c);
        }
        double pos = (x - Constant) / Step - FirstIndex + 0.5;   // in cell units: cell i spans [i, i + 1)
        if (pos <= 0.0) return 0.0;
        if (pos >= Mass.Length) return 1.0;
        int cell = (int)Math.Floor(pos);
        double cum = 0.0;
        for (int i = 0; i < cell; i++) cum += Mass[i];
        return Math.Min(1.0, cum + (pos - cell) * Mass[cell]);
    }

    /// <summary>P(Y &gt; x).</summary>
    public double Exceedance(double x) => Math.Max(0.0, 1.0 - Cdf(x));

    /// <summary>The p-quantile: exact atoms, the smallest atom with CDF at least p; cells, the linear inverse of
    /// <see cref="Cdf"/>.</summary>
    public double Quantile(double p)
    {
        if (!(p >= 0.0 && p <= 1.0)) throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(p), p, "A quantile probability must lie in [0, 1]."));
        double cum = 0.0;
        if (AtomsExact)
        {
            for (int i = 0; i < Mass.Length; i++)
            {
                cum += Mass[i];
                if (cum >= p - 1e-15 && Mass[i] > 0.0) return ValueAt(FirstIndex + i);
            }
            return ValueAt(FirstIndex + Mass.Length - 1);
        }
        for (int i = 0; i < Mass.Length; i++)
        {
            double next = cum + Mass[i];
            if (next >= p && Mass[i] > 0.0)
            {
                double frac = (p - cum) / Mass[i];
                return Constant + (FirstIndex + i - 0.5 + frac) * Step;
            }
            cum = next;
        }
        return Constant + (FirstIndex + Mass.Length - 0.5) * Step;
    }
}
