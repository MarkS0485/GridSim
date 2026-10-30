using System.Globalization;

namespace GridSim.Core.Identity;

// =====================================================================================================================
// The scatter (2026-10-24): sequential asset numbers 0, 1, 2, ... turned into references that look unrelated to each
// other, what3words-style, so neighbouring assets never get neighbouring (and easily confused) references, while no two
// numbers EVER share a reference.
//
// HOW. A keyed Feistel network over the reference's bits (16, 24 or 32: two, three or four words). The value is split
// into two equal halves L and R; each of Rounds rounds does  (L, R) <- (R, L xor F(round, R)),  where F is a keyed
// mixing function. Whatever F is, every round can be undone exactly ( (L, R) <- (R xor F(round, L), L) ), so the whole
// network is a BIJECTION on its bit width: a permutation of every possible reference. That is the no-collision
// guarantee, by construction, not by luck; the tests also check it exhaustively over the whole 24-bit space.
//
// F is the SplitMix64 finaliser (Steele, Lea and Flood, "Fast splittable pseudorandom number generators", 2014) over
// the seed, the round number and the half, masked to the half's width. Only integer operations, with wrap-around
// (unchecked) arithmetic: the same seed gives the same references on every run, CPU and operating system. The seed is
// the key: a different seed gives a different, equally collision-free, arrangement. It is not a cipher and makes no
// secrecy claim; it only spreads neighbours apart.
// =====================================================================================================================

/// <summary>
/// A keyed, collision-free scatter of sequential numbers onto electrical references (file header): <see cref="Permute"/>
/// is a bijection on the reference's bit width and <see cref="Unpermute"/> its exact inverse.
/// </summary>
public sealed class ElectricalScatter
{
    /// <summary>The Feistel rounds. Eight is ample to spread neighbours apart; it is part of the arrangement (changing
    /// it changes every scattered reference), so it is fixed.</summary>
    public const int Rounds = 8;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly ulong _seed;
    private readonly int _halfBits;
    private readonly ulong _halfMask;

    /// <summary>
    /// A scatter keyed by <paramref name="seed"/> over references of <paramref name="wordCount"/> words: 2, 3 or 4
    /// (16, 24 or 32 bits, split into two equal halves). Throws for any other word count.
    /// </summary>
    public ElectricalScatter(ulong seed, int wordCount)
    {
        if (wordCount is < 2 or > 4)
            throw new ArgumentOutOfRangeException(nameof(wordCount), $"The scatter works on 2, 3 or 4 words, not {wordCount.ToString(Inv)}.");
        _seed = seed;
        WordCount = wordCount;
        _halfBits = 4 * wordCount;   // half of 8 bits per word
        _halfMask = (1UL << _halfBits) - 1;
    }

    /// <summary>The words of every reference this scatter makes.</summary>
    public int WordCount { get; }

    /// <summary>How many numbers the scatter covers: 2^(8 x <see cref="WordCount"/>) (0 to <see cref="Count"/> - 1).</summary>
    public ulong Count => 1UL << (8 * WordCount);

    /// <summary>The scattered value of <paramref name="number"/> (0 to <see cref="Count"/> - 1). Throws outside that range.</summary>
    public ulong Permute(ulong number)
    {
        CheckRange(number, nameof(number));
        ulong l = number >> _halfBits, r = number & _halfMask;
        for (int round = 0; round < Rounds; round++)
            (l, r) = (r, l ^ F(round, r));
        return (l << _halfBits) | r;
    }

    /// <summary>The number whose scattered value is <paramref name="value"/>: the exact inverse of <see cref="Permute"/>.</summary>
    public ulong Unpermute(ulong value)
    {
        CheckRange(value, nameof(value));
        ulong l = value >> _halfBits, r = value & _halfMask;
        for (int round = Rounds - 1; round >= 0; round--)
            (l, r) = (r ^ F(round, l), l);
        return (l << _halfBits) | r;
    }

    /// <summary>The reference of asset number <paramref name="number"/>: its scattered value as words.</summary>
    public string RefOf(ulong number, bool withCheck = false) => ElectricalRef.Encode(Permute(number), WordCount, withCheck);

    /// <summary>The asset number of reference <paramref name="text"/> (strict decode, then the inverse scatter).</summary>
    public ulong NumberOf(string text, bool withCheck = false) => Unpermute(ElectricalRef.Decode(text, WordCount, withCheck));

    // The round function: SplitMix64's finaliser over (seed, round, half), masked to the half's width.
    private ulong F(int round, ulong half)
    {
        unchecked
        {
            ulong z = _seed ^ ((ulong)(round + 1) * 0x9E3779B97F4A7C15UL) ^ (half * 0xD1B54A32D192ED03UL);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return z & _halfMask;
        }
    }

    private void CheckRange(ulong v, string name)
    {
        if (v >= Count)
            throw new ArgumentOutOfRangeException(name, $"{v.ToString(Inv)} is outside 0..{(Count - 1).ToString(Inv)} for {WordCount.ToString(Inv)} words.");
    }
}
