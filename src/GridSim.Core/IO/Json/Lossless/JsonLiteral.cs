using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GridSim.Core.IO.Json.Lossless;

/// <summary>
/// The JSON literals the case editor writes, and the strict input rules for what a user may type.
///
/// Numbers (Q2, resolved to the plan's recommendation): a typed number is written as the SHORTEST literal that
/// round-trips to the same double ("R" format, invariant culture), never the characters the user typed. So "1300.0",
/// "1300" and "1.3e3" all become 1300, and reading the literal back (by System.Text.Json, as the loader does) gives
/// exactly the same bits. Only finite numbers exist in JSON; NaN and infinities are refused.
///
/// Strings: only '"', '\' and control characters (below U+0020) are escaped; every other character, including
/// non-ASCII letters and '&amp;', is written raw as UTF-8, so a name typed into an intl or uk file looks like its
/// neighbours. An unpaired surrogate cannot be encoded as UTF-8 and is refused.
/// </summary>
internal static partial class JsonLiteral
{
    /// <summary>The JSON number grammar (RFC 8259 section 6), anchored: what a user may type for a number field.</summary>
    [GeneratedRegex(@"\A-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex NumberGrammar();

    /// <summary>An integer literal: optional minus, then digits with no leading zero.</summary>
    [GeneratedRegex(@"\A-?(0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerGrammar();

    /// <summary>
    /// The shortest literal that reads back as exactly <paramref name="value"/> (invariant, round-trip "R").
    /// Throws <see cref="ArgumentOutOfRangeException"/> for NaN or an infinity, which JSON cannot represent.
    /// </summary>
    public static string FormatNumber(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "JSON numbers must be finite; NaN and infinities cannot be written.");
        // .NET Core 3.0 and later: "R" is the shortest string that round-trips, e.g. 0.1 -> "0.1", 1300 -> "1300",
        // 1e-320 -> "1E-320", -0.0 -> "-0". Every such string matches the JSON number grammar.
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>An integer as plain digits (invariant). Integer fields are always written this way, never as 1.0.</summary>
    public static string FormatInteger(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A JSON boolean literal.</summary>
    public static string FormatBool(bool value) => value ? "true" : "false";

    /// <summary>
    /// A JSON string literal, quotes included. Escapes only what JSON requires ('"', '\', control characters);
    /// writes everything else raw. Throws <see cref="ArgumentException"/> on an unpaired surrogate.
    /// </summary>
    public static string FormatString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    throw new ArgumentException($"the text has an unpaired high surrogate at character {i}, which cannot be written as UTF-8.", nameof(value));
                sb.Append(c).Append(value[i + 1]);
                i++;
                continue;
            }
            if (char.IsLowSurrogate(c))
                throw new ArgumentException($"the text has an unpaired low surrogate at character {i}, which cannot be written as UTF-8.", nameof(value));
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Parse what a user typed for a number field: the strict JSON number grammar (no leading '+', no leading zeros,
    /// no "1.", no ".5", no thousands separators, no culture), and the value must be a finite double. Leading and
    /// trailing spaces are trimmed first. Returns false with a reason otherwise.
    /// </summary>
    public static bool TryParseNumber(string text, out double value, out string why)
    {
        ArgumentNullException.ThrowIfNull(text);
        value = 0;
        string t = text.Trim();
        if (!NumberGrammar().IsMatch(t))
        {
            why = $"'{text}' is not a JSON number (write digits with an optional '-', '.' and exponent, for example 1300, -0.5 or 1.2e-3).";
            return false;
        }
        double d = double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (!double.IsFinite(d))
        {
            why = $"'{text}' is outside the range of a double.";
            return false;
        }
        value = d;
        why = "";
        return true;
    }

    /// <summary>
    /// Parse what a user typed for an integer field: optional '-', digits, no leading zeros, inside the 32-bit range
    /// (every v1 integer field is a C# int, and System.Text.Json refuses "1.0" for an int). Returns false with a reason.
    /// </summary>
    public static bool TryParseInt32(string text, out int value, out string why)
    {
        ArgumentNullException.ThrowIfNull(text);
        value = 0;
        string t = text.Trim();
        if (!IntegerGrammar().IsMatch(t))
        {
            why = $"'{text}' is not a whole number (digits with an optional '-', no decimal point).";
            return false;
        }
        if (!int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int i))
        {
            why = $"'{text}' is outside the range {int.MinValue} to {int.MaxValue}.";
            return false;
        }
        value = i;
        why = "";
        return true;
    }

    /// <summary>Parse a typed boolean: y, yes, true, n, no, false (any case). Returns false with a reason otherwise.</summary>
    public static bool TryParseBool(string text, out bool value, out string why)
    {
        ArgumentNullException.ThrowIfNull(text);
        switch (text.Trim().ToLowerInvariant())
        {
            case "y": case "yes": case "true": value = true; why = ""; return true;
            case "n": case "no": case "false": value = false; why = ""; return true;
            default: value = false; why = $"'{text}' is not a yes / no value (type y, n, true or false)."; return false;
        }
    }

    /// <summary>True when the text holds an unpaired surrogate (which cannot be written as UTF-8).</summary>
    public static bool HasUnpairedSurrogate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(text[i])) return true;
        }
        return false;
    }
}
