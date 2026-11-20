using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Stochastic;

/// <summary>
/// Names one random stream of generator <c>gridsim-rng/1</c>: a seed, a domain string and a stream index. Streams are
/// split by changing the domain (what the numbers are for: "load-scale", "outage") and/or the index (which sample,
/// which worker block, which dimension), never by sharing mutable state, so parallel use needs no coordination and
/// cannot change a result.
///
/// <para>The stream base is <c>seed XOR H(domain) XOR (index * 0x9E3779B97F4A7C15)</c> (wrap-around 64-bit), where
/// <c>H(domain)</c> is the first eight bytes of SHA-256 of the UTF-8 domain string read as a big-endian unsigned
/// integer. There is no default seed: a silent default would make a result unrepeatable from its stated inputs.</para>
/// </summary>
public readonly struct StreamKey
{
    /// <summary>The generator version recorded in every stochastic result.</summary>
    public const string GeneratorVersion = "gridsim-rng/1";

    internal const ulong Golden = 0x9E3779B97F4A7C15UL;

    /// <summary>The explicit seed.</summary>
    public ulong Seed { get; }
    /// <summary>The domain string (UTF-8, ordinal; non-empty).</summary>
    public string Domain { get; }
    /// <summary>The stream index within the domain.</summary>
    public ulong Index { get; }
    /// <summary>The 64-bit stream base.</summary>
    public ulong Base { get; }

    /// <summary>Build a key; refuses a null or empty domain.</summary>
    public StreamKey(ulong seed, string domain, ulong index = 0)
    {
        if (string.IsNullOrEmpty(domain))
            throw DomainRefusal.Mark(new ArgumentException("A stream needs a non-empty domain string.", nameof(domain)));
        Seed = seed; Domain = domain; Index = index;
        Base = unchecked(seed ^ DomainHash(domain) ^ (index * Golden));
    }

    /// <summary>The same seed and domain at another index.</summary>
    public StreamKey WithIndex(ulong index) => new(Seed, Domain, index);

    /// <summary>H(domain): first 8 bytes of SHA-256 of the UTF-8 domain, big-endian.</summary>
    public static ulong DomainHash(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(domain), hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }
}
