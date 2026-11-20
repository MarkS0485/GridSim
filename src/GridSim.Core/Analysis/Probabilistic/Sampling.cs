using GridSim.Core.Diagnostics;
using GridSim.Core.Numerics;
using GridSim.Core.Stochastic;

namespace GridSim.Core.Analysis.Probabilistic;

/// <summary>
/// The uniform source of one replicate of a sampled run: the value of coordinate <c>dim</c> of sample <c>sample</c>, a
/// pure function of (method, seed, domain, replicate, sample, dim), so any block of samples can be drawn in any order or on
/// any thread and give the same bits.
///
/// <para>Monte Carlo: draw <c>sample</c> of the <see cref="CounterStream"/> keyed (seed, domain + ":u", index = dim).
/// Quasi-Monte Carlo: coordinate <c>dim</c> of Sobol point <c>sample</c>; unscrambled, or with the digital shift keyed
/// (seed, domain + ":rep" + replicate) so each replicate is an independent randomisation.</para>
/// </summary>
public sealed class UniformSource
{
    private readonly CounterStream[]? _streams;
    private readonly SobolSequence? _sobol;

    /// <summary>The method.</summary>
    public ProbabilisticMethod Method { get; }
    /// <summary>The dimension count.</summary>
    public int Dimension { get; }

    /// <summary>Build; refuses convolution (it draws nothing), a non-positive dimension, and a quasi-Monte Carlo
    /// dimension above the direction-number table.</summary>
    public UniformSource(ProbabilisticMethod method, int dimension, ulong seed, string domain, int replicate = 0)
    {
        if (method == ProbabilisticMethod.Convolution)
            throw DomainRefusal.Mark(new ArgumentException("Convolution draws no samples."));
        if (dimension < 1) throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(dimension), dimension, "At least one dimension is needed."));
        if (string.IsNullOrEmpty(domain)) throw DomainRefusal.Mark(new ArgumentException("A non-empty domain string is required.", nameof(domain)));
        Method = method; Dimension = dimension;
        switch (method)
        {
            case ProbabilisticMethod.MonteCarlo:
                _streams = new CounterStream[dimension];
                for (int d = 0; d < dimension; d++) _streams[d] = new CounterStream(new StreamKey(seed, domain + ":u", (ulong)d));
                break;
            case ProbabilisticMethod.QuasiMonteCarlo:
                _sobol = new SobolSequence(dimension);
                break;
            default:
                _sobol = new SobolSequence(dimension, seed, domain + ":rep" + replicate.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }

    /// <summary>Coordinate <paramref name="dim"/> of sample <paramref name="sample"/>, in [0, 1).</summary>
    public double U(long sample, int dim) =>
        _streams is not null ? _streams[dim].Uniform((ulong)sample) : _sobol!.Coordinate((ulong)sample, dim);
}

/// <summary>The shape of a sampled run: samples per replicate and replicates (1 except for shifted quasi-Monte Carlo).</summary>
public readonly record struct SampleLayout(int SamplesPerReplicate, int Replicates)
{
    /// <summary>Total samples.</summary>
    public long Total => (long)SamplesPerReplicate * Replicates;
    /// <summary>Replicate of a global sample index.</summary>
    public int ReplicateOf(int index) => index / SamplesPerReplicate;
    /// <summary>Point index within its replicate.</summary>
    public int PointOf(int index) => index % SamplesPerReplicate;

    /// <summary>Validate a method's layout: positive counts; replicates only for the shifted method (at least 2 there); a
    /// total below 2^31 - 1; and Monte Carlo / unscrambled quasi-Monte Carlo take exactly one replicate.</summary>
    public static SampleLayout Create(ProbabilisticMethod method, int samplesPerReplicate, int? replicates)
    {
        Uniform.RequireCount(samplesPerReplicate);
        int r;
        if (method == ProbabilisticMethod.QuasiMonteCarloShifted)
        {
            r = replicates ?? 8;
            if (r < 2) throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(replicates), r, "Shifted quasi-Monte Carlo needs at least 2 replicates for an error estimate."));
        }
        else
        {
            if (replicates.HasValue && replicates.Value != 1)
                throw DomainRefusal.Mark(new ArgumentException("Replicates apply only to the qmc-shifted method."));
            r = 1;
        }
        if ((long)samplesPerReplicate * r > int.MaxValue / 2)
            throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(samplesPerReplicate), "Too many samples."));
        return new SampleLayout(samplesPerReplicate, r);
    }
}

/// <summary>Fixed-order statistics over samples: the sums use the foundation's pairwise tree, so they do not depend on the
/// thread count.</summary>
public static class SampleStatistics
{
    /// <summary>The fixed-tree mean.</summary>
    public static double Mean(double[] x, int maxThreads = 0) => FixedOrderReduction.Mean(x, maxThreads);

    /// <summary>The unbiased sample variance (two passes, each a fixed-tree sum); 0 for a single value.</summary>
    public static double Variance(double[] x, double mean, int maxThreads = 0)
    {
        if (x.Length < 2) return 0.0;
        double s = FixedOrderReduction.Sum(x.Length, i => (x[i] - mean) * (x[i] - mean), maxThreads);
        return s / (x.Length - 1);
    }

    /// <summary>
    /// The type 7 quantile (Hyndman and Fan; the default of R and NumPy) of an ASCENDING array:
    /// <c>x[j] + (h - j)(x[j+1] - x[j])</c> with <c>h = (n - 1) p</c>, <c>j = floor(h)</c>.
    /// </summary>
    public static double QuantileType7(double[] ascending, double p)
    {
        int n = ascending.Length;
        if (n == 0) throw DomainRefusal.Mark(new ArgumentException("No values."));
        if (!(p >= 0.0 && p <= 1.0)) throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(p), p, "A quantile probability must lie in [0, 1]."));
        double h = (n - 1) * p;
        int j = (int)Math.Floor(h);
        if (j >= n - 1) return ascending[n - 1];
        return ascending[j] + (h - j) * (ascending[j + 1] - ascending[j]);
    }

    /// <summary>
    /// The estimate of a mean over index-ordered samples laid out as <paramref name="layout"/>, with the error of the
    /// method: Monte Carlo sd/sqrt(N); shifted quasi-Monte Carlo the spread of the replicate means / sqrt(R); unshifted
    /// quasi-Monte Carlo none.
    /// </summary>
    public static (double Mean, double? StdError, double[] ReplicateMeans) Estimate(double[] values, SampleLayout layout,
        ProbabilisticMethod method, int maxThreads = 0)
    {
        var repMeans = new double[layout.Replicates];
        for (int r = 0; r < layout.Replicates; r++)
        {
            var slice = new double[layout.SamplesPerReplicate];
            Array.Copy(values, (long)r * layout.SamplesPerReplicate, slice, 0, layout.SamplesPerReplicate);
            repMeans[r] = Mean(slice, maxThreads);
        }
        double mean = layout.Replicates == 1 ? repMeans[0] : Mean(repMeans, 1);
        double? se = null;
        switch (method)
        {
            case ProbabilisticMethod.MonteCarlo:
                se = Math.Sqrt(Variance(values, mean, maxThreads) / values.Length);
                break;
            case ProbabilisticMethod.QuasiMonteCarloShifted:
                se = Math.Sqrt(Variance(repMeans, mean, 1) / layout.Replicates);
                break;
        }
        return (mean, se, repMeans);
    }

    /// <summary>A sorted copy, by <see cref="FrozenIntroSort"/> (values are finite: ties are bit-equal values).</summary>
    public static double[] SortedCopy(double[] x)
    {
        var c = (double[])x.Clone();
        FrozenIntroSort.Sort(c, 0, c.Length, (a, b) => a.CompareTo(b));
        return c;
    }
}

/// <summary>An expectation estimate with the error statement of its method.</summary>
/// <param name="Mean">The estimate.</param>
/// <param name="StdError">The standard error where the method has one (Monte Carlo; replicated shifts); null otherwise.</param>
/// <param name="ErrorModel">What <paramref name="StdError"/> is, or why there is none.</param>
/// <param name="Samples">Total samples drawn.</param>
/// <param name="ReplicateMeans">The replicate estimates (one for the unshifted methods).</param>
public sealed record ExpectationEstimate(double Mean, double? StdError, string ErrorModel, long Samples, double[] ReplicateMeans);

/// <summary>The estimators' error statements, one place, so a result and its documentation cannot disagree.</summary>
public static class ErrorModels
{
    /// <summary>Monte Carlo.</summary>
    public const string MonteCarlo = "Monte Carlo standard error: sample standard deviation / sqrt(N)";
    /// <summary>Unscrambled quasi-Monte Carlo.</summary>
    public const string QuasiMonteCarlo = "deterministic point set, no error bar";
    /// <summary>Shifted quasi-Monte Carlo.</summary>
    public const string QuasiMonteCarloShifted = "replicated digital shift: standard deviation of the replicate estimates / sqrt(R)";
    /// <summary>Convolution.</summary>
    public const string Convolution = "analytical convolution: no sampling error; discretisation step stated in the result";

    /// <summary>The statement for a method.</summary>
    public static string For(ProbabilisticMethod m) => m switch
    {
        ProbabilisticMethod.MonteCarlo => MonteCarlo,
        ProbabilisticMethod.QuasiMonteCarlo => QuasiMonteCarlo,
        ProbabilisticMethod.QuasiMonteCarloShifted => QuasiMonteCarloShifted,
        _ => Convolution,
    };
}

/// <summary>
/// The generic sampled-expectation estimator under the three sampling methods: <c>E[f(U)]</c> for U uniform on the unit
/// cube, with one function evaluation per sample, every sample independent, collected in index order and reduced by the
/// foundation's fixed tree, so the answer is bit-identical for one thread and for sixteen. Used by the probabilistic
/// studies' tests as the integration oracle's host, and available to any caller that needs a selectable-method mean.
/// </summary>
public static class SampledExpectation
{
    /// <summary>Estimate E[f(U)] (f receives the point's coordinates; it must be pure).</summary>
    public static ExpectationEstimate Estimate(ProbabilisticMethod method, int dimension, int samplesPerReplicate, int? replicates,
        ulong seed, string domain, Func<double[], double> f, int maxThreads = 0)
    {
        ArgumentNullException.ThrowIfNull(f);
        SampleLayout layout = SampleLayout.Create(method, samplesPerReplicate, replicates);
        var values = new double[layout.Total];
        var sources = new UniformSource[layout.Replicates];
        for (int r = 0; r < layout.Replicates; r++) sources[r] = new UniformSource(method, dimension, seed, domain, r);
        FixedOrderReduction.ForBlocks(values.Length, maxThreads, (lo, hi) =>
        {
            var u = new double[dimension];
            for (int i = lo; i < hi; i++)
            {
                UniformSource s = sources[layout.ReplicateOf(i)];
                int point = layout.PointOf(i);
                for (int d = 0; d < dimension; d++) u[d] = s.U(point, d);
                values[i] = f(u);
            }
        });
        (double mean, double? se, double[] repMeans) = SampleStatistics.Estimate(values, layout, method, maxThreads);
        return new ExpectationEstimate(mean, se, ErrorModels.For(method), layout.Total, repMeans);
    }
}

/// <summary>
/// Turns uniforms into the values of correlated uncertain inputs. Independent inputs: <c>x_j = Quantile_j(u_j)</c>.
/// Correlated inputs: a Gaussian copula. <c>z_j = Phi^-1(u_j)</c>, <c>w = L z</c> with L the Cholesky factor of the
/// correlation matrix (applied in fixed row order), and <c>x_j = w_j</c> through the marginal: a normal marginal uses
/// <c>mu + sigma w_j</c>, any other uses <c>Quantile_j(Phi(w_j))</c>. The correlation is the normal-score (copula)
/// correlation, which for normal marginals is the ordinary linear correlation.
/// </summary>
public sealed class InputTransform
{
    private readonly IReadOnlyList<UncertainDistribution> _dist;
    private readonly double[,]? _l;

    /// <summary>The input count.</summary>
    public int Count => _dist.Count;
    /// <summary>True when a copula is applied.</summary>
    public bool Correlated => _l is not null;

    /// <summary>Build; a null or identity correlation means independent. Refuses a matrix of the wrong size, with a
    /// diagonal other than 1, with an entry outside [-1, 1], or not positive definite.</summary>
    public InputTransform(IReadOnlyList<UncertainDistribution> distributions, double[,]? correlation)
    {
        ArgumentNullException.ThrowIfNull(distributions);
        if (distributions.Count == 0) throw DomainRefusal.Mark(new ArgumentException("At least one uncertain input is needed."));
        _dist = distributions;
        if (correlation is null) return;
        int n = distributions.Count;
        if (correlation.GetLength(0) != n || correlation.GetLength(1) != n)
            throw DomainRefusal.Mark(new ArgumentException("The correlation matrix must have one row and column per input."));
        bool identity = true;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                double v = correlation[i, j];
                if (!double.IsFinite(v) || Math.Abs(v) > 1.0)
                    throw DomainRefusal.Mark(new ArgumentException($"Correlation ({i}, {j}) must be a finite number in [-1, 1]."));
                if (i == j && v != 1.0) throw DomainRefusal.Mark(new ArgumentException($"The correlation diagonal must be 1 (entry {i})."));
                if (i != j && v != 0.0) identity = false;
            }
        if (!identity) _l = Cholesky.Factor(correlation);
    }

    /// <summary>The input values of one sample, from its uniforms (length Count).</summary>
    public void Transform(ReadOnlySpan<double> u, Span<double> x)
    {
        int n = _dist.Count;
        if (_l is null)
        {
            for (int j = 0; j < n; j++) x[j] = _dist[j].Quantile(u[j]);
            return;
        }
        Span<double> z = n <= 128 ? stackalloc double[n] : new double[n];
        Span<double> w = n <= 128 ? stackalloc double[n] : new double[n];
        for (int j = 0; j < n; j++) z[j] = GaussianInverse.FromUniform(u[j]);
        Cholesky.MultiplyLower(_l, z, w);
        for (int j = 0; j < n; j++)
        {
            UncertainDistribution d = _dist[j];
            x[j] = d is NormalDistribution nd ? nd.Mu + nd.Sigma * w[j] : d.Quantile(GaussianCdf.Cdf(w[j]));
        }
    }
}
