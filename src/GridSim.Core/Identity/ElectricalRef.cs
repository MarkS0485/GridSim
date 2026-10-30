using System.Globalization;
using System.Text;

namespace GridSim.Core.Identity;

// =====================================================================================================================
// The electrical reference (2026-10-24): a number written as electrical words, one word per byte, most significant
// byte first, joined by '.':  0x3C042C with 3 words is "copper.tesla.kron".
//
// ENCODE takes a value and a word count (1 to 8) and refuses a value that does not fit in that many bytes, so a
// reference never silently loses its high bytes.
//
// DECODE is strict, because a reference is an identity and a forgiving reader would turn a typo into a different
// asset: lower case only, '.' between words and nowhere else, no spaces, exactly the expected number of words, every
// word one of the 256. A refusal says exactly what is wrong and, for an unknown word, names the nearest valid words by
// edit distance (what the reader most likely meant), so a person can correct it by eye; it never picks one for them.
//
// THE CHECK WORD (optional). One more word appended after the data words: the CRC-8 of the data bytes. The CRC is
// CRC-8/SAE-J1850 (polynomial 0x1D, initial value 0xFF, final XOR 0xFF). Its generator x^8+x^4+x^3+x^2+1 is
// irreducible, which is why it catches EVERY single changed word (any burst of 8 bits or fewer) and EVERY swap of two
// adjacent data words (the error is d(x)(x^8+1)x^k with d of degree below 8, and an irreducible degree-8 generator
// divides neither factor); the common polynomial 0x07 has the factor x+1 and misses some swaps. The tests check both
// properties exhaustively over a sample, including changes to and swaps with the check word itself. Decoding with the
// check verifies it and refuses on a mismatch.
// =====================================================================================================================

/// <summary>A refused electrical reference: the text could not be read, and the message says why.</summary>
public sealed class ElectricalRefException : FormatException
{
    /// <summary>A refusal with its reason.</summary>
    public ElectricalRefException(string message) : base(message) { }

    /// <summary>An empty refusal (the standard exception constructor; prefer the one with a reason).</summary>
    public ElectricalRefException() { }

    /// <summary>A refusal with its reason and cause.</summary>
    public ElectricalRefException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Electrical references: a number as electrical words (<see cref="ElectricalWords"/>), one per byte, big-endian,
/// joined by <see cref="Separator"/>, with an optional CRC-8 check word. Pure and deterministic.
/// </summary>
public static class ElectricalRef
{
    /// <summary>The character between words.</summary>
    public const char Separator = '.';

    /// <summary>The fewest data words a reference can have.</summary>
    public const int MinWords = 1;

    /// <summary>The most data words a reference can have (8 bytes: a whole <see cref="ulong"/>).</summary>
    public const int MaxWords = 8;

    /// <summary>How many nearest words a refusal of an unknown word suggests (at most).</summary>
    public const int Suggestions = 3;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ======================================== encode ========================================

    /// <summary>
    /// <paramref name="value"/> as <paramref name="wordCount"/> words, most significant byte first, joined by '.'.
    /// With <paramref name="withCheck"/> one more word, the CRC-8 of the data bytes, is appended. Throws when the word
    /// count is outside 1..8 or the value does not fit in that many bytes.
    /// </summary>
    public static string Encode(ulong value, int wordCount, bool withCheck = false)
    {
        byte[] data = ToBytes(value, wordCount);
        var sb = new StringBuilder();
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(Separator);
            sb.Append(ElectricalWords.WordOf(data[i]));
        }
        if (withCheck) sb.Append(Separator).Append(ElectricalWords.WordOf(Crc8(data)));
        return sb.ToString();
    }

    /// <summary>
    /// The <paramref name="wordCount"/> bytes of <paramref name="value"/>, most significant first. Throws when the word
    /// count is outside 1..8 or the value needs more bytes than that.
    /// </summary>
    public static byte[] ToBytes(ulong value, int wordCount)
    {
        CheckWordCount(wordCount);
        if (wordCount < MaxWords && value >> (8 * wordCount) != 0)
            throw new ArgumentOutOfRangeException(nameof(value),
                $"{value.ToString(Inv)} does not fit in {wordCount.ToString(Inv)} word(s): the largest is {(MaxValue(wordCount)).ToString(Inv)}.");
        var data = new byte[wordCount];
        for (int i = wordCount - 1; i >= 0; i--)
        {
            data[i] = (byte)(value & 0xFF);
            value >>= 8;
        }
        return data;
    }

    /// <summary>The largest value <paramref name="wordCount"/> words can carry (2^(8n) - 1).</summary>
    public static ulong MaxValue(int wordCount)
    {
        CheckWordCount(wordCount);
        return wordCount == MaxWords ? ulong.MaxValue : (1UL << (8 * wordCount)) - 1;
    }

    // ======================================== decode ========================================

    /// <summary>
    /// The value of <paramref name="text"/>, which must be exactly <paramref name="wordCount"/> words (plus the check
    /// word when <paramref name="withCheck"/>), strict as the file header says. Throws
    /// <see cref="ElectricalRefException"/> with the reason on anything else, and on a check word that does not match.
    /// </summary>
    public static ulong Decode(string text, int wordCount, bool withCheck = false)
    {
        CheckWordCount(wordCount);
        if (TryDecode(text, wordCount, withCheck, out ulong value, out string? error)) return value;
        throw new ElectricalRefException(error!);
    }

    /// <summary>
    /// As <see cref="Decode"/>, without throwing: false, with the refusal's reason in <paramref name="error"/>, when
    /// the text is not a valid reference of that shape.
    /// </summary>
    public static bool TryDecode(string text, int wordCount, bool withCheck, out ulong value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        CheckWordCount(wordCount);
        value = 0;
        int expected = wordCount + (withCheck ? 1 : 0);
        string shape = expected.ToString(Inv) + " word(s)" + (withCheck ? " (" + wordCount.ToString(Inv) + " and a check word)" : "")
            + " joined by '" + Separator + "'";

        if (text.Length == 0) { error = "The reference is empty: expected " + shape + "."; return false; }
        foreach (char c in text)
        {
            if (c == Separator || c is >= 'a' and <= 'z') continue;
            error = c is >= 'A' and <= 'Z'
                ? "The reference '" + text + "' has upper-case letters: references are written in lower case only."
                : char.IsWhiteSpace(c)
                    ? "The reference '" + text + "' has a space or other white space in it: expected " + shape + ", nothing else."
                    : "The reference '" + text + "' has the character '" + c + "' in it: only lower-case letters and '" + Separator + "' are allowed.";
            return false;
        }
        string[] parts = text.Split(Separator);
        if (parts.Any(p => p.Length == 0))
        {
            error = "The reference '" + text + "' has an empty word (a '" + Separator + "' at the start or end, or two together): expected " + shape + ".";
            return false;
        }
        if (parts.Length != expected)
        {
            error = "The reference '" + text + "' has " + parts.Length.ToString(Inv) + " word(s): expected " + shape + ".";
            return false;
        }

        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (ElectricalWords.TryByteOf(parts[i], out bytes[i])) continue;
            error = "The reference '" + text + "': '" + parts[i] + "' (word " + (i + 1).ToString(Inv) + ") is not an electrical word. Nearest: "
                + string.Join(", ", NearestWords(parts[i])) + ".";
            return false;
        }

        if (withCheck)
        {
            byte want = Crc8(bytes.AsSpan(0, wordCount));
            if (bytes[wordCount] != want)
            {
                error = "The reference '" + text + "' fails its check word: '" + parts[wordCount] + "' does not match the words before it"
                    + " (one of them is wrong or two have been swapped).";
                return false;
            }
        }
        for (int i = 0; i < wordCount; i++) value = (value << 8) | bytes[i];
        error = null;
        return true;
    }

    // ======================================== the check word ========================================

    /// <summary>
    /// CRC-8/SAE-J1850 of <paramref name="data"/>: polynomial 0x1D, initial value 0xFF, final XOR 0xFF, most
    /// significant bit first (file header: why this polynomial).
    /// </summary>
    public static byte Crc8(ReadOnlySpan<byte> data)
    {
        int crc = 0xFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x80) != 0 ? ((crc << 1) ^ 0x1D) & 0xFF : (crc << 1) & 0xFF;
        }
        return (byte)(crc ^ 0xFF);
    }

    // ======================================== nearest words ========================================

    /// <summary>
    /// Up to <see cref="Suggestions"/> electrical words nearest to <paramref name="word"/> by edit distance
    /// (Levenshtein: one insertion, deletion or substitution each), nearest first, ties in list order.
    /// </summary>
    public static IReadOnlyList<string> NearestWords(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        return ElectricalWords.Words
            .Select((w, i) => (Word: w, Index: i, Distance: EditDistance(word, w)))
            .OrderBy(x => x.Distance).ThenBy(x => x.Index)
            .Take(Suggestions)
            .Select(x => x.Word)
            .ToList();
    }

    /// <summary>The Levenshtein edit distance between <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static int EditDistance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static void CheckWordCount(int wordCount)
    {
        if (wordCount is < MinWords or > MaxWords)
            throw new ArgumentOutOfRangeException(nameof(wordCount), $"A reference has {MinWords} to {MaxWords} words, not {wordCount.ToString(Inv)}.");
    }
}
