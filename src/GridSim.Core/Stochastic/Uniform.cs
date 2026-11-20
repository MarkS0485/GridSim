using GridSim.Core.Diagnostics;

namespace GridSim.Core.Stochastic;

/// <summary>Uniform transforms and bulk fills for <see cref="CounterStream"/>.</summary>
public static class Uniform
{
    private const double TwoPowMinus53 = 1.0 / 9007199254740992.0;

    /// <summary>The exact map of 64 random bits to [0, 1): the top 53 bits times 2^-53.</summary>
    public static double FromU64(ulong u) => (u >> 11) * TwoPowMinus53;

    /// <summary>
    /// Fill <paramref name="dest"/> with draws 0..n-1 of <paramref name="key"/>. The work is cut into fixed blocks of
    /// <see cref="FixedOrderReduction.BlockSize"/> draws that may run on up to <paramref name="maxThreads"/> threads; the
    /// values do not depend on the thread count (each draw is a pure function of its index).
    /// </summary>
    public static void Fill(StreamKey key, double[] dest, int maxThreads = 0)
    {
        ArgumentNullException.ThrowIfNull(dest);
        var s = new CounterStream(key);
        FixedOrderReduction.ForBlocks(dest.Length, maxThreads, (lo, hi) =>
        {
            for (int i = lo; i < hi; i++) dest[i] = s.Uniform((ulong)i);
        });
    }

    /// <summary>Validate a sample count (positive).</summary>
    public static void RequireCount(long n)
    {
        if (n <= 0) throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(n), n, "The sample count must be positive."));
    }
}
