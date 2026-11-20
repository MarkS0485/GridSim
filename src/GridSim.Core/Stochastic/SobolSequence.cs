using GridSim.Core.Diagnostics;

namespace GridSim.Core.Stochastic;

/// <summary>
/// The Sobol quasi-Monte Carlo sequence over <see cref="SobolDirectionNumbers"/> (32-bit, so 2^32 points). Point i is
/// computed directly (random access, no state, so blocks may be evaluated in any order or in parallel): the bitwise xor
/// of the direction integers selected by the set bits of the Gray code of i, scaled by 2^-32. Unscrambled by default,
/// so a bare QMC run is a deterministic point set that needs no seed; an optional <see cref="DigitalShift"/> randomises it.
///
/// <para>Point 0 is the origin (0, 0, ...). Callers that map to a Gaussian should skip it or rely on the end-point nudge
/// in <see cref="GaussianInverse.FromUniform"/>. The sample count should be a power of two for the net property to apply.</para>
/// </summary>
public sealed class SobolSequence
{
    private const double TwoPowMinus32 = 1.0 / 4294967296.0;
    private readonly uint[][] _dir;
    private readonly uint[] _shift;

    /// <summary>Dimension count.</summary>
    public int Dimension { get; }
    /// <summary>True when a digital shift is applied.</summary>
    public bool Shifted => _shift.Length > 0;

    /// <summary>An unscrambled sequence of <paramref name="dimension"/> dimensions (1 to <see cref="SobolDirectionNumbers.MaxDimension"/>).</summary>
    public SobolSequence(int dimension) : this(dimension, null, null, true) { }

    /// <summary>A digitally shifted sequence: the shift derives from <paramref name="seed"/> and <paramref name="domain"/>.</summary>
    public SobolSequence(int dimension, ulong seed, string domain) : this(dimension, seed, domain, true) { }

    private SobolSequence(int dimension, ulong? seed, string? domain, bool _)
    {
        if (dimension < 1 || dimension > SobolDirectionNumbers.MaxDimension)
            throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(dimension), dimension,
                $"The quasi-Monte Carlo dimension must be 1 to {SobolDirectionNumbers.MaxDimension} for table {SobolDirectionNumbers.TableId}."));
        Dimension = dimension;
        _dir = new uint[dimension][];
        for (int d = 0; d < dimension; d++) _dir[d] = SobolDirectionNumbers.Directions(d);
        if (seed.HasValue)
        {
            if (string.IsNullOrEmpty(domain))
                throw DomainRefusal.Mark(new ArgumentException("A shifted sequence needs a non-empty domain string.", nameof(domain)));
            _shift = new uint[dimension];
            for (int d = 0; d < dimension; d++) _shift[d] = DigitalShift.Mask(seed.Value, domain, d);
        }
        else _shift = Array.Empty<uint>();
    }

    /// <summary>Coordinate <paramref name="dim"/> of point <paramref name="index"/> (0 to 2^32 - 1), in [0, 1).</summary>
    public double Coordinate(ulong index, int dim)
    {
        if (index > uint.MaxValue)
            throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(index), index, "The 32-bit Sobol sequence has 2^32 points."));
        if ((uint)dim >= (uint)Dimension) throw new ArgumentOutOfRangeException(nameof(dim));
        uint gray = (uint)index ^ ((uint)index >> 1);
        uint x = 0;
        uint[] v = _dir[dim];
        for (int b = 0; gray != 0; b++, gray >>= 1)
            if ((gray & 1u) != 0) x ^= v[b];
        if (_shift.Length > 0) x ^= _shift[dim];
        return x * TwoPowMinus32;
    }

    /// <summary>Write point <paramref name="index"/> into <paramref name="point"/> (length at least Dimension).</summary>
    public void Point(ulong index, Span<double> point)
    {
        if (point.Length < Dimension) throw new ArgumentException("The point buffer is shorter than the dimension.", nameof(point));
        for (int d = 0; d < Dimension; d++) point[d] = Coordinate(index, d);
    }
}
