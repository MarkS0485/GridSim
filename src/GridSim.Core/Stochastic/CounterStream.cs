using System.Diagnostics.CodeAnalysis;

namespace GridSim.Core.Stochastic;

/// <summary>
/// Generator <c>gridsim-rng/1</c>: a counter-based generator. Draw k (0-based) of a stream with base b is the pure function
/// <c>Finalise(b + (k + 1) * 0x9E3779B97F4A7C15)</c> (wrap-around 64-bit), where Finalise is the SplitMix64 output
/// function (Steele, Lea and Flood 2014; Vigna's reference): <c>z = (z ^ z>>30) * 0xBF58476D1CE4E5B9;
/// z = (z ^ z>>27) * 0x94D049BB133111EB; z ^= z>>31</c>.
///
/// <para><b>Mapping onto the sequential form.</b> Sequential SplitMix64 with state s0 returns Finalise(s0 + (k+1)*G)
/// as its k-th output, so a stream whose base equals the sequential seed (see <see cref="FromBase"/>) reproduces
/// Vigna's published sequence exactly. Only unsigned 64-bit integer add, multiply, xor and shift are used: identical on
/// every CPU, runtime and operating system, in any thread order.</para>
///
/// <para><b>Statistical status.</b> SplitMix64 passes BigCrush as a sequential generator. Streams of different keys
/// are offsets of one 2^64-cycle sequence, so overlap between two streams is possible in principle (it needs the
/// bases to lie within the number of draws of one another, probability about n/2^64 per pair for n draws); no
/// stronger independence claim is made, and none is needed at sample counts below about 2^32.</para>
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "'Stream' means a random stream (RC4 plan name), not System.IO.Stream.")]
public readonly struct CounterStream
{
    private readonly ulong _base;

    /// <summary>A stream for <paramref name="key"/>.</summary>
    public CounterStream(StreamKey key) { _base = key.Base; }

    private CounterStream(ulong b) { _base = b; }

    /// <summary>A stream with an explicit base (base 0 gives Vigna's SplitMix64 seed-0 sequence). For tests and documentation.</summary>
    public static CounterStream FromBase(ulong streamBase) => new(streamBase);

    /// <summary>The SplitMix64 output function.</summary>
    public static ulong Finalise(ulong z)
    {
        unchecked
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Draw k as an unsigned 64-bit integer; independent of every other draw and of call order.</summary>
    public ulong DrawU64(ulong k) => Finalise(unchecked(_base + (k + 1UL) * StreamKey.Golden));

    /// <summary>Draw k as a uniform in [0, 1): <c>(u64 >> 11) * 2^-53</c>, exact.</summary>
    public double Uniform(ulong k) => Stochastic.Uniform.FromU64(DrawU64(k));
}
