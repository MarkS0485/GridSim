using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Stochastic;

/// <summary>
/// Direction numbers for the Sobol sequence, DERIVED by GridSim rather than copied from a published table.
///
/// <para><b>Why not Joe-Kuo.</b> The Joe-Kuo tables (new-joe-kuo-6.21201) are the usual choice, but their web page states
/// only that the numbers are "covered by this BSD-style licence" and the licence text itself was not obtained and read
/// when this was written (R4-02, 2026-11-17). Until the terms are read and recorded in THIRD-PARTY-NOTICES.md the
/// table is not embedded. This is the construction the plan names as the fall-back.</para>
///
/// <para><b>Construction.</b> Dimension 0 is the van der Corput sequence (all m_i = 1). Dimension d >= 1 uses the (d)-th
/// primitive polynomial over GF(2), enumerated by degree and then by increasing coefficient bit pattern (x + 1;
/// x^2 + x + 1; x^3 + x + 1; x^3 + x^2 + 1; ...), found here by testing that x has multiplicative order exactly 2^s - 1
/// modulo the polynomial (which also proves irreducibility). The initial direction numbers are all m_1..m_s = 1 (odd and
/// below 2^i, as Sobol's construction requires); later ones follow the standard recurrence
/// m_i = 2 a_1 m_{i-1} xor 2^2 a_2 m_{i-2} xor ... xor 2^s m_{i-s} xor m_{i-s}. Every such choice is a valid digital
/// (t,s)-sequence; the Joe-Kuo values are optimised for the quality of two-dimensional projections, which this choice is
/// not, so the high dimensions here are weaker. Dimensions 0 and 1 are the textbook ones and reproduce the published
/// first points. Table id: <see cref="TableId"/>.</para>
///
/// <para>Only integer arithmetic: the table is the same on every platform.</para>
/// </summary>
public static class SobolDirectionNumbers
{
    /// <summary>Identifier recorded in every quasi-Monte Carlo result.</summary>
    public const string TableId = "gridsim-sobol-derived/1";
    /// <summary>Largest supported dimension count.</summary>
    public const int MaxDimension = 64;
    /// <summary>Bits per coordinate; the sequence has 2^32 points.</summary>
    public const int Bits = 32;

    private static readonly Lazy<(uint[][] V, uint[] Polys)> Table = new(Build);

    /// <summary>Direction integers v_1..v_32 (index 0..31) of dimension <paramref name="dim"/> (0-based), each m_j shifted to 32 bits.</summary>
    public static uint[] Directions(int dim)
    {
        if (dim < 0 || dim >= MaxDimension)
            throw DomainRefusal.Mark(new ArgumentOutOfRangeException(nameof(dim), dim,
                $"The quasi-Monte Carlo dimension must be below {MaxDimension} for table {TableId}."));
        return Table.Value.V[dim];
    }

    /// <summary>The primitive polynomial of dimension <paramref name="dim"/> (full bit mask, bit s set; 0 for dimension 0).</summary>
    public static uint Polynomial(int dim)
    {
        Directions(dim);
        return Table.Value.Polys[dim];
    }

    /// <summary>SHA-256 (hex) of all direction integers in order, to pin the table.</summary>
    public static string Digest()
    {
        var sb = new StringBuilder();
        for (int d = 0; d < MaxDimension; d++)
            foreach (uint v in Directions(d)) sb.Append(v).Append(',');
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(sb.ToString())));
    }

    private static (uint[][], uint[]) Build()
    {
        var polys = new uint[MaxDimension];
        int found = 1;
        for (int s = 1; found < MaxDimension; s++)
            for (uint p = (1u << s) | 1u; p < (1u << (s + 1)) && found < MaxDimension; p += 2)
                if (IsPrimitive(p, s)) polys[found++] = p;

        var v = new uint[MaxDimension][];
        for (int d = 0; d < MaxDimension; d++)
        {
            var m = new uint[Bits + 1]; // 1-based
            if (d == 0)
            {
                for (int i = 1; i <= Bits; i++) m[i] = 1;
            }
            else
            {
                uint p = polys[d];
                int s = 31 - System.Numerics.BitOperations.LeadingZeroCount(p);
                for (int i = 1; i <= Bits; i++)
                {
                    if (i <= s) { m[i] = 1; continue; }
                    uint x = m[i - s] ^ (m[i - s] << s);
                    for (int k = 1; k < s; k++)
                    {
                        // a_k is the coefficient of x^(s-k)
                        if (((p >> (s - k)) & 1u) != 0) x ^= m[i - k] << k;
                    }
                    m[i] = x;
                }
            }
            var dir = new uint[Bits];
            for (int j = 1; j <= Bits; j++) dir[j - 1] = m[j] << (Bits - j);
            v[d] = dir;
        }
        return (v, polys);
    }

    // x has multiplicative order exactly 2^s - 1 modulo p (degree s, constant term 1)  <=>  p is primitive.
    private static bool IsPrimitive(uint p, int s)
    {
        ulong order = (1UL << s) - 1;
        if (PowX(order, p, s) != 1) return false;
        ulong rest = order;
        for (ulong q = 2; q * q <= rest; q++)
        {
            if (rest % q != 0) continue;
            while (rest % q == 0) rest /= q;
            if (PowX(order / q, p, s) == 1) return false;
        }
        if (rest > 1 && PowX(order / rest, p, s) == 1) return false;
        return true;
    }

    private static ulong MulMod(ulong a, ulong b, uint p, int s)
    {
        ulong r = 0;
        while (b != 0)
        {
            if ((b & 1) != 0) r ^= a;
            b >>= 1;
            a <<= 1;
            if (((a >> s) & 1) != 0) a ^= p;
        }
        return r;
    }

    private static ulong PowX(ulong e, uint p, int s)
    {
        ulong result = 1, b = s == 1 ? 1UL : 2UL; // x (for s = 1, x reduces to 1 modulo x + 1)
        while (e != 0)
        {
            if ((e & 1) != 0) result = MulMod(result, b, p, s);
            b = MulMod(b, b, p, s);
            e >>= 1;
        }
        return result;
    }
}
