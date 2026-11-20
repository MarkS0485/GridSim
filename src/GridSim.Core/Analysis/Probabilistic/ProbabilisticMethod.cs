using GridSim.Core.Stochastic;

namespace GridSim.Core.Analysis.Probabilistic;

/// <summary>
/// The selectable method of a probabilistic study (RC4 decision 2). All three sit on the one seeded foundation of
/// <c>GridSim.Core.Stochastic</c> (generator <c>gridsim-rng/1</c>). Named <c>ProbabilisticMethod</c> rather than the
/// plan's <c>SamplingMethod</c> because <see cref="SamplingMethod"/> already exists in the foundation namespace (it names
/// the provenance of a sampled result; convolution samples nothing).
/// </summary>
public enum ProbabilisticMethod
{
    /// <summary>Pseudo-random Monte Carlo. Error falls as 1/sqrt(N); the standard error is estimated from the sample.</summary>
    MonteCarlo,
    /// <summary>Unscrambled Sobol quasi-Monte Carlo: a deterministic point set. No error bar is available, and the
    /// result says so.</summary>
    QuasiMonteCarlo,
    /// <summary>Sobol quasi-Monte Carlo with an independent random digital shift per replicate; the spread of the replicate
    /// estimates gives an error estimate.</summary>
    QuasiMonteCarloShifted,
    /// <summary>Analytical convolution on a lattice. Exact for sums of independent discrete terms; a stated
    /// discretisation step for continuous ones. Applies only where the quantity is a sum of independent terms.</summary>
    Convolution,
}

/// <summary>Names and parsing of <see cref="ProbabilisticMethod"/> in study files and results.</summary>
public static class ProbabilisticMethods
{
    /// <summary>The study-file name of a method.</summary>
    public static string Name(ProbabilisticMethod m) => m switch
    {
        ProbabilisticMethod.MonteCarlo => "monte-carlo",
        ProbabilisticMethod.QuasiMonteCarlo => "qmc",
        ProbabilisticMethod.QuasiMonteCarloShifted => "qmc-shifted",
        ProbabilisticMethod.Convolution => "convolution",
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    /// <summary>Parse a study-file method name; null when unknown.</summary>
    public static ProbabilisticMethod? Parse(string? name) => name switch
    {
        "monte-carlo" => ProbabilisticMethod.MonteCarlo,
        "qmc" => ProbabilisticMethod.QuasiMonteCarlo,
        "qmc-shifted" => ProbabilisticMethod.QuasiMonteCarloShifted,
        "convolution" => ProbabilisticMethod.Convolution,
        _ => null,
    };

    /// <summary>The foundation provenance method of a sampling method (not defined for convolution).</summary>
    public static SamplingMethod ToSampling(ProbabilisticMethod m) => m switch
    {
        ProbabilisticMethod.MonteCarlo => SamplingMethod.MonteCarlo,
        ProbabilisticMethod.QuasiMonteCarlo => SamplingMethod.QuasiMonteCarlo,
        ProbabilisticMethod.QuasiMonteCarloShifted => SamplingMethod.QuasiMonteCarloShifted,
        _ => throw new InvalidOperationException("Convolution is not a sampling method."),
    };
}
