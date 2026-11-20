using GridSim.Core.Stochastic;

namespace GridSim.Core.Analysis.Probabilistic;

/// <summary>Outcome of one method on one request. A method outside its domain says so; it is never approximated.</summary>
public static class ProbabilisticStatus
{
    /// <summary>The method ran and produced every requested quantity.</summary>
    public const string Complete = "COMPLETE";
    /// <summary>The method cannot answer this request (for example convolution on an AC evaluator).</summary>
    public const string OutOfScope = "OUT OF SCOPE";
    /// <summary>The method ran but could not produce a number (for example no sample converged).</summary>
    public const string Incomplete = "INCOMPLETE";
}

/// <summary>
/// What makes a probabilistic result repeatable from its inputs: the R4-02 provenance fields plus a digest of the drawn
/// samples (different seeds give different sample digests). Convolution draws nothing: <c>RandomNumbersUsed</c> is false,
/// the generator version is still recorded, and the seed is echoed because every request must carry one.
/// </summary>
public sealed record ProbabilisticProvenance(
    ulong Seed,
    IReadOnlyList<string> Domains,
    string Method,
    long SampleCount,
    int Replicates,
    string GeneratorVersion,
    string? DirectionTableId,
    bool RandomNumbersUsed,
    string SampleDigest,
    string ResultDigest)
{
    /// <summary>Build for a sampled method through the foundation's validating factory.</summary>
    public static ProbabilisticProvenance ForSampled(ulong? seed, string domain, ProbabilisticMethod method, SampleLayout layout,
        IEnumerable<double> sampleValues, IEnumerable<double> resultValues)
    {
        StochasticProvenance p = StochasticProvenance.Create(seed, new[] { domain }, ProbabilisticMethods.ToSampling(method),
            layout.Total, resultValues);
        return new ProbabilisticProvenance(p.Seed, p.Domains, ProbabilisticMethods.Name(method), p.SampleCount, layout.Replicates,
            p.GeneratorVersion, p.DirectionTableId, true, StochasticProvenance.Digest(sampleValues), p.ResultDigest);
    }

    /// <summary>Build for convolution (no samples).</summary>
    public static ProbabilisticProvenance ForConvolution(ulong? seed, string domain, IEnumerable<double> resultValues)
    {
        if (!seed.HasValue)
            throw Diagnostics.DomainRefusal.Mark(new ArgumentException("No seed given: every probabilistic request carries an explicit seed, even where the method draws none."));
        return new ProbabilisticProvenance(seed.Value, new[] { domain }, ProbabilisticMethods.Name(ProbabilisticMethod.Convolution), 0, 1,
            StreamKey.GeneratorVersion, null, false, StochasticProvenance.Digest(Array.Empty<double>()), StochasticProvenance.Digest(resultValues));
    }
}
