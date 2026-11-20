using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace GridSim.Core.Presentation.Workbench;

/// <summary>
/// Shared, culture-free text helpers of the workbench (R4-10): the canonical number form, the SHA-256 of a text, and
/// the display rounding of a cell. Presentation only; nothing here touches a computed number.
/// </summary>
public static class WorkbenchText
{
    /// <summary>The canonical text of a double: the round-trip ("R") form in the invariant culture, a negative zero as 0,
    /// and NaN and the infinities by name. The same number always gives the same text, on every machine.</summary>
    public static string Canon(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "Infinity";
        if (double.IsNegativeInfinity(v)) return "-Infinity";
        if (v == 0) return "0";
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>The canonical text of an int.</summary>
    public static string Canon(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Lower-case hexadecimal SHA-256 of the UTF-8 bytes of <paramref name="text"/>.</summary>
    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>Lower-case hexadecimal SHA-256 of <paramref name="bytes"/>.</summary>
    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The visible text of an absent value: a dash, never zero.</summary>
    public const string Gap = "-";

    /// <summary>A number for display with <paramref name="decimals"/> decimals (invariant); a non-finite value is a gap.</summary>
    public static string Show(double v, int decimals)
    {
        if (!double.IsFinite(v)) return Gap;
        if (v == 0) v = 0;   // no "-0.000"
        string s = v.ToString("F" + Math.Clamp(decimals, 0, 15).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return s.StartsWith("-0", StringComparison.Ordinal) && IsAllZero(s) ? s[1..] : s;
    }

    private static bool IsAllZero(string s)
    {
        foreach (char c in s) if (c is >= '1' and <= '9') return false;
        return true;
    }
}
