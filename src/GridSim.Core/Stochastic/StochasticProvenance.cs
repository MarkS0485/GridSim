using System.Buffers.Binary;
using System.Security.Cryptography;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Stochastic;

/// <summary>The sampling method of a stochastic result.</summary>
public enum SamplingMethod
{
    /// <summary>Pseudo-random Monte Carlo from <see cref="CounterStream"/>.</summary>
    MonteCarlo,
    /// <summary>Unscrambled Sobol quasi-Monte Carlo (a deterministic point set).</summary>
    QuasiMonteCarlo,
    /// <summary>Sobol quasi-Monte Carlo with a random digital shift.</summary>
    QuasiMonteCarloShifted,
}

/// <summary>
/// What every stochastic result must record so it can be repeated from its inputs: the seed, the domain strings, the
/// method, the sample count, the generator version, the direction-number table id (quasi-Monte Carlo only) and the
/// SHA-256 digest of the result values. Built only through <see cref="Create"/>, which refuses a missing seed or an
/// invalid method/count.
/// </summary>
public sealed record StochasticProvenance(
    ulong Seed,
    IReadOnlyList<string> Domains,
    SamplingMethod Method,
    long SampleCount,
    string GeneratorVersion,
    string? DirectionTableId,
    string ResultDigest)
{
    /// <summary>
    /// Validate and build. <paramref name="seed"/> is nullable only so that "no seed given" is refused here with a
    /// clear message instead of being defaulted. An unscrambled quasi-Monte Carlo run uses no seed value, but its caller
    /// must still pass one explicitly (use 0) so the record is complete.
    /// </summary>
    public static StochasticProvenance Create(ulong? seed, IEnumerable<string> domains, SamplingMethod method, long sampleCount,
        IEnumerable<double> results)
    {
        if (!seed.HasValue)
            throw DomainRefusal.Mark(new ArgumentException("No seed given: there is no default seed, because a silent default would make the result unrepeatable from its inputs.", nameof(seed)));
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(results);
        if (!Enum.IsDefined(method))
            throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(method), method, "Unknown sampling method."));
        Uniform.RequireCount(sampleCount);
        var list = domains.ToArray();
        if (list.Length == 0 || list.Any(string.IsNullOrEmpty))
            throw DomainRefusal.Mark(new ArgumentException("At least one non-empty domain string is required.", nameof(domains)));
        return new StochasticProvenance(seed.Value, list, method, sampleCount, StreamKey.GeneratorVersion,
            method == SamplingMethod.MonteCarlo ? null : SobolDirectionNumbers.TableId, Digest(results));
    }

    /// <summary>SHA-256 (upper-case hex) of the IEEE-754 bits of the values, little-endian, in order.</summary>
    public static string Digest(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buf = stackalloc byte[8];
        foreach (double v in values)
        {
            BinaryPrimitives.WriteInt64LittleEndian(buf, BitConverter.DoubleToInt64Bits(v));
            h.AppendData(buf);
        }
        return Convert.ToHexString(h.GetHashAndReset());
    }
}
