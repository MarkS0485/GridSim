using GridSim.Core.Diagnostics;
using GridSim.Core.Numerics;
using GridSim.Core.Stochastic;

namespace GridSim.Core.Analysis.Probabilistic;

/// <summary>
/// A univariate distribution of an uncertain input, with the three operations the methods need: the quantile function
/// (inverse transform, for Monte Carlo and quasi-Monte Carlo), the CDF and, for discrete laws, the atoms (for
/// convolution). Every parameter is validated on construction: a non-finite or out-of-range parameter is refused, never
/// repaired. Only <see cref="DetMath"/> is used for transcendental functions.
/// </summary>
public abstract record UncertainDistribution
{
    /// <summary>The study-file kind name.</summary>
    public abstract string Kind { get; }
    /// <summary>The quantile function for p in (0, 1).</summary>
    public abstract double Quantile(double p);
    /// <summary>P(X &lt;= x).</summary>
    public abstract double Cdf(double x);
    /// <summary>The mean.</summary>
    public abstract double Mean { get; }
    /// <summary>The variance.</summary>
    public abstract double Variance { get; }
    /// <summary>True for a law carried by a finite set of atoms (<see cref="Atoms"/>).</summary>
    public virtual bool IsDiscrete => false;
    /// <summary>The atoms (value, probability) in ascending value order; defined for discrete laws only.</summary>
    public virtual IReadOnlyList<(double Value, double Probability)> Atoms =>
        throw new InvalidOperationException(Kind + " is a continuous distribution.");
    /// <summary>True for a normal law (a Gaussian copula can then use the correlated normal deviate directly).</summary>
    public virtual bool IsNormal => false;

    internal static void RequireFinite(double v, string what)
    {
        if (!double.IsFinite(v))
            throw DomainRefusal.Mark(new ArgumentException($"{what} must be a finite number (got {v:R})."));
    }

    internal static void RequirePositive(double v, string what)
    {
        RequireFinite(v, what);
        if (!(v > 0.0)) throw DomainRefusal.Mark(new ArgumentException($"{what} must be above 0 (got {v:R})."));
    }

    /// <summary>p clamped into the open interval (0, 1) (the one-grid-step nudge the Gaussian inverse uses).</summary>
    internal static double Open(double p)
    {
        const double lo = 1.0 / 18014398509481984.0;      // 2^-54
        const double hi = 1.0 - 1.0 / 9007199254740992.0; // 1 - 2^-53
        return p < lo ? lo : p > hi ? hi : p;
    }
}

/// <summary>Normal law, mean <c>Mu</c> and standard deviation <c>Sigma</c> above 0.</summary>
public sealed record NormalDistribution : UncertainDistribution
{
    /// <summary>Mean.</summary>
    public double Mu { get; }
    /// <summary>Standard deviation.</summary>
    public double Sigma { get; }
    /// <summary>Validate and build.</summary>
    public NormalDistribution(double mu, double sigma)
    {
        RequireFinite(mu, "mean"); RequirePositive(sigma, "standard deviation");
        Mu = mu; Sigma = sigma;
    }
    /// <inheritdoc />
    public override string Kind => "normal";
    /// <inheritdoc />
    public override bool IsNormal => true;
    /// <inheritdoc />
    public override double Quantile(double p) => Mu + Sigma * GaussianInverse.Quantile(Open(p));
    /// <inheritdoc />
    public override double Cdf(double x) => GaussianCdf.Cdf((x - Mu) / Sigma);
    /// <inheritdoc />
    public override double Mean => Mu;
    /// <inheritdoc />
    public override double Variance => Sigma * Sigma;
}

/// <summary>Lognormal law: <c>ln X</c> is normal with mean <c>Mu</c> and standard deviation <c>Sigma</c> above 0.</summary>
public sealed record LogNormalDistribution : UncertainDistribution
{
    /// <summary>Mean of ln X.</summary>
    public double Mu { get; }
    /// <summary>Standard deviation of ln X.</summary>
    public double Sigma { get; }
    /// <summary>Validate and build.</summary>
    public LogNormalDistribution(double mu, double sigma)
    {
        RequireFinite(mu, "mu"); RequirePositive(sigma, "sigma");
        Mu = mu; Sigma = sigma;
    }
    /// <inheritdoc />
    public override string Kind => "lognormal";
    /// <inheritdoc />
    public override double Quantile(double p) => DetMath.Exp(Mu + Sigma * GaussianInverse.Quantile(Open(p)));
    /// <inheritdoc />
    public override double Cdf(double x) => x <= 0.0 ? 0.0 : GaussianCdf.Cdf((DetMath.Log(x) - Mu) / Sigma);
    /// <inheritdoc />
    public override double Mean => DetMath.Exp(Mu + 0.5 * Sigma * Sigma);
    /// <inheritdoc />
    public override double Variance => (DetMath.Exp(Sigma * Sigma) - 1.0) * DetMath.Exp(2.0 * Mu + Sigma * Sigma);
}

/// <summary>Weibull law with <c>Scale</c> and <c>Shape</c> above 0: <c>F(x) = 1 - exp(-(x/scale)^shape)</c>.</summary>
public sealed record WeibullDistribution : UncertainDistribution
{
    /// <summary>Scale parameter.</summary>
    public double Scale { get; }
    /// <summary>Shape parameter.</summary>
    public double Shape { get; }
    /// <summary>Validate and build.</summary>
    public WeibullDistribution(double scale, double shape)
    {
        RequirePositive(scale, "scale"); RequirePositive(shape, "shape");
        Scale = scale; Shape = shape;
    }
    /// <inheritdoc />
    public override string Kind => "weibull";
    /// <inheritdoc />
    public override double Quantile(double p) => Scale * DetMath.Pow(-DetMath.Log(1.0 - Open(p)), 1.0 / Shape);
    /// <inheritdoc />
    public override double Cdf(double x) => x <= 0.0 ? 0.0 : -DetMath.Expm1(-DetMath.Pow(x / Scale, Shape));
    /// <inheritdoc />
    public override double Mean => Scale * SpecialFunctions.Gamma(1.0 + 1.0 / Shape);
    /// <inheritdoc />
    public override double Variance
    {
        get
        {
            double g1 = SpecialFunctions.Gamma(1.0 + 1.0 / Shape), g2 = SpecialFunctions.Gamma(1.0 + 2.0 / Shape);
            return Scale * Scale * (g2 - g1 * g1);
        }
    }
}

/// <summary>Beta law on [<c>Lo</c>, <c>Hi</c>] with shape parameters <c>A</c>, <c>B</c> above 0 (standard beta when Lo = 0, Hi = 1).</summary>
public sealed record BetaDistribution : UncertainDistribution
{
    /// <summary>First shape.</summary>
    public double A { get; }
    /// <summary>Second shape.</summary>
    public double B { get; }
    /// <summary>Lower end of the support.</summary>
    public double Lo { get; }
    /// <summary>Upper end of the support.</summary>
    public double Hi { get; }
    /// <summary>Validate and build.</summary>
    public BetaDistribution(double a, double b, double lo = 0.0, double hi = 1.0)
    {
        RequirePositive(a, "alpha"); RequirePositive(b, "beta"); RequireFinite(lo, "lower bound"); RequireFinite(hi, "upper bound");
        if (!(hi > lo)) throw DomainRefusal.Mark(new ArgumentException("The beta upper bound must exceed the lower bound."));
        A = a; B = b; Lo = lo; Hi = hi;
    }
    /// <inheritdoc />
    public override string Kind => "beta";
    /// <inheritdoc />
    public override double Cdf(double x)
    {
        double t = (x - Lo) / (Hi - Lo);
        return t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : SpecialFunctions.RegularisedIncompleteBeta(A, B, t);
    }
    /// <inheritdoc />
    public override double Quantile(double p)
    {
        p = Open(p);
        double lo = 0.0, hi = 1.0;
        for (int i = 0; i < 80; i++)   // fixed iteration count: deterministic, bisection down to the grid of doubles
        {
            double mid = 0.5 * (lo + hi);
            if (SpecialFunctions.RegularisedIncompleteBeta(A, B, mid) < p) lo = mid; else hi = mid;
        }
        return Lo + (Hi - Lo) * 0.5 * (lo + hi);
    }
    /// <inheritdoc />
    public override double Mean => Lo + (Hi - Lo) * A / (A + B);
    /// <inheritdoc />
    public override double Variance => (Hi - Lo) * (Hi - Lo) * A * B / ((A + B) * (A + B) * (A + B + 1.0));
}

/// <summary>
/// A finite discrete law. Atoms with equal value are merged and the atoms are sorted ascending; probabilities must be
/// non-negative and sum to 1 within 1e-9 (they are then used as given, not renormalised).
/// </summary>
public record DiscreteDistribution : UncertainDistribution
{
    private readonly (double Value, double Probability)[] _atoms;
    private readonly double[] _cum;

    /// <summary>Validate and build from atoms.</summary>
    public DiscreteDistribution(IEnumerable<(double Value, double Probability)> atoms)
    {
        ArgumentNullException.ThrowIfNull(atoms);
        var merged = new SortedDictionary<double, double>();
        double sum = 0.0;
        foreach ((double v, double p) in atoms)
        {
            RequireFinite(v, "atom value"); RequireFinite(p, "atom probability");
            if (p < 0.0) throw DomainRefusal.Mark(new ArgumentException("An atom probability must not be negative."));
            merged[v] = merged.GetValueOrDefault(v) + p;
            sum += p;
        }
        if (merged.Count == 0) throw DomainRefusal.Mark(new ArgumentException("A discrete distribution needs at least one atom."));
        if (Math.Abs(sum - 1.0) > 1e-9)
            throw DomainRefusal.Mark(new ArgumentException($"The atom probabilities must sum to 1 (got {sum:R})."));
        _atoms = merged.Select(kv => (kv.Key, kv.Value)).ToArray();
        _cum = new double[_atoms.Length];
        double c = 0.0;
        for (int i = 0; i < _atoms.Length; i++) { c += _atoms[i].Probability; _cum[i] = c; }
    }

    /// <inheritdoc />
    public override string Kind => "discrete";
    /// <inheritdoc />
    public override bool IsDiscrete => true;
    /// <inheritdoc />
    public override IReadOnlyList<(double Value, double Probability)> Atoms => _atoms;
    /// <inheritdoc />
    public override double Quantile(double p)
    {
        for (int i = 0; i < _atoms.Length - 1; i++) if (p <= _cum[i]) return _atoms[i].Value;
        return _atoms[^1].Value;
    }
    /// <inheritdoc />
    public override double Cdf(double x)
    {
        double c = 0.0;
        foreach ((double v, double p) in _atoms) { if (v <= x) c += p; else break; }
        return c;
    }
    /// <inheritdoc />
    public override double Mean
    {
        get { double m = 0.0; foreach ((double v, double p) in _atoms) m += v * p; return m; }
    }
    /// <inheritdoc />
    public override double Variance
    {
        get { double m = Mean, s = 0.0; foreach ((double v, double p) in _atoms) s += (v - m) * (v - m) * p; return s; }
    }
}

/// <summary>A two-state availability law: capacity <c>Capacity</c> with probability <c>Availability</c>, else 0.</summary>
public sealed record TwoStateDistribution : DiscreteDistribution
{
    /// <summary>Capacity when up.</summary>
    public double Capacity { get; }
    /// <summary>Probability of being up, in [0, 1].</summary>
    public double Availability { get; }
    /// <summary>Validate and build.</summary>
    public TwoStateDistribution(double capacity, double availability)
        : base(Atoms2(capacity, availability))
    {
        Capacity = capacity; Availability = availability;
    }

    private static (double, double)[] Atoms2(double capacity, double availability)
    {
        RequireFinite(capacity, "capacity"); RequireFinite(availability, "availability");
        if (capacity < 0.0) throw DomainRefusal.Mark(new ArgumentException("The capacity must not be negative."));
        if (availability < 0.0 || availability > 1.0)
            throw DomainRefusal.Mark(new ArgumentException($"The availability must lie in [0, 1] (got {availability:R})."));
        return new[] { (0.0, 1.0 - availability), (capacity, availability) };
    }
    /// <inheritdoc />
    public override string Kind => "two-state";
}

/// <summary>An empirical law: the given observations, equally weighted (equal values merge).</summary>
public sealed record EmpiricalDistribution : DiscreteDistribution
{
    /// <summary>Validate and build.</summary>
    public EmpiricalDistribution(IReadOnlyList<double> observations)
        : base(Weights(observations)) { }

    private static (double, double)[] Weights(IReadOnlyList<double> obs)
    {
        ArgumentNullException.ThrowIfNull(obs);
        if (obs.Count == 0) throw DomainRefusal.Mark(new ArgumentException("An empirical distribution needs at least one observation."));
        return obs.Select(v => (v, 1.0 / obs.Count)).ToArray();
    }
    /// <inheritdoc />
    public override string Kind => "empirical";
}

/// <summary>
/// Special functions for the distributions above (deterministic: only add, multiply, divide, sqrt and
/// <see cref="DetMath"/>). The tests check each against closed forms (Gamma at integers and 1/2; the incomplete beta at
/// integer parameters, where it is a polynomial).
/// </summary>
public static class SpecialFunctions
{
    // Lanczos approximation, g = 7, n = 9 (Godfrey's coefficients); confirmed by the tests against Gamma at integers and 1/2.
    private static readonly double[] LanczosCoefficients =
    {
        0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
        -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
    };

    /// <summary>ln Gamma(x) for x above 0.</summary>
    public static double LogGamma(double x)
    {
        if (!(x > 0.0)) throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(x), x, "LogGamma needs x above 0."));
        if (x < 0.5) // reflection: Gamma(x) Gamma(1-x) = pi / sin(pi x)
            return DetMath.Log(Math.PI / DetMath.Sin(Math.PI * x)) - LogGamma(1.0 - x);
        x -= 1.0;
        double a = LanczosCoefficients[0];
        double t = x + 7.5;
        for (int i = 1; i < 9; i++) a += LanczosCoefficients[i] / (x + i);
        return 0.5 * DetMath.Log(2.0 * Math.PI) + (x + 0.5) * DetMath.Log(t) - t + DetMath.Log(a);
    }

    /// <summary>Gamma(x) for x above 0.</summary>
    public static double Gamma(double x) => DetMath.Exp(LogGamma(x));

    /// <summary>The regularised incomplete beta function I_x(a, b) for x in [0, 1] (continued fraction, modified Lentz).</summary>
    public static double RegularisedIncompleteBeta(double a, double b, double x)
    {
        if (!(a > 0.0 && b > 0.0)) throw DomainRefusal.Mark(new ArgumentException("The beta shapes must be above 0."));
        if (x <= 0.0) return 0.0;
        if (x >= 1.0) return 1.0;
        double ln = LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * DetMath.Log(x) + b * DetMath.Log(1.0 - x);
        double front = DetMath.Exp(ln);
        if (x < (a + 1.0) / (a + b + 2.0)) return front * BetaFraction(a, b, x) / a;
        return 1.0 - front * BetaFraction(b, a, 1.0 - x) / b;
    }

    private static double BetaFraction(double a, double b, double x)
    {
        const double tiny = 1e-300;
        double qab = a + b, qap = a + 1.0, qam = a - 1.0;
        double c = 1.0, d = 1.0 - qab * x / qap;
        if (Math.Abs(d) < tiny) d = tiny;
        d = 1.0 / d;
        double h = d;
        for (int m = 1; m <= 500; m++)
        {
            int m2 = 2 * m;
            double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
            d = 1.0 + aa * d; if (Math.Abs(d) < tiny) d = tiny;
            c = 1.0 + aa / c; if (Math.Abs(c) < tiny) c = tiny;
            d = 1.0 / d; h *= d * c;
            aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
            d = 1.0 + aa * d; if (Math.Abs(d) < tiny) d = tiny;
            c = 1.0 + aa / c; if (Math.Abs(c) < tiny) c = tiny;
            d = 1.0 / d;
            double del = d * c;
            h *= del;
            if (Math.Abs(del - 1.0) < 1e-16) break;
        }
        return h;
    }
}
