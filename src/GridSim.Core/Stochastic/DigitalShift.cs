namespace GridSim.Core.Stochastic;

/// <summary>
/// A random digital shift for a Sobol point set: one 32-bit mask per dimension, XORed into the coordinate's bits. The mask
/// is the top 32 bits of draw 0 of the stream (seed, "gridsim-sobol-shift:" + <c>domain</c>, index = dimension), so it
/// comes from <see cref="CounterStream"/> alone. A shifted set stays a (t,s)-net, each coordinate is exactly uniform
/// over the unit interval in expectation, and independent seeds give independent randomisations (so an error estimate
/// across seeds is available). Owen-style nested scrambling is deferred.
/// </summary>
public static class DigitalShift
{
    /// <summary>The domain prefix of the shift streams.</summary>
    public const string DomainPrefix = "gridsim-sobol-shift:";

    /// <summary>The shift mask of <paramref name="dimension"/>.</summary>
    public static uint Mask(ulong seed, string domain, int dimension)
    {
        var key = new StreamKey(seed, DomainPrefix + domain, (ulong)dimension);
        return (uint)(new CounterStream(key).DrawU64(0) >> 32);
    }
}
