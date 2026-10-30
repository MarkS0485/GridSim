namespace GridSim.Core.Diagnostics;

/// <summary>
/// The one rule GridSim uses for turning an input <see cref="DateTime"/> into UTC.
///
/// <para>.NET's <see cref="DateTime.ToUniversalTime"/> treats a <see cref="DateTimeKind.Unspecified"/> value as
/// <b>machine-local</b> time and shifts it by the local offset. Every GridSim timestamp is UTC by contract, so an
/// Unspecified stamp (a parsed naive string, a <c>new DateTime(y, m, d)</c> without a kind) is UTC that simply
/// lost its label. Converting it as local made the same input give a different answer on a BST machine than on
/// a UTC server: a bridge start or seek time moved by an hour, and an audit-log hash computed on one machine
/// failed to verify on another. <see cref="ForwardInferenceGuard"/> and <see cref="RestrictionReview"/> already
/// read Unspecified as UTC; this helper is that same rule in one place so every caller agrees.</para>
/// </summary>
public static class UtcTime
{
    /// <summary>
    /// Return <paramref name="t"/> as a UTC-kind value. An Unspecified value is relabelled as UTC with no shift
    /// (it was UTC all along). A Local value is converted to UTC in the normal way. A UTC value is returned
    /// unchanged.
    /// </summary>
    public static DateTime Normalise(DateTime t) => t.Kind switch
    {
        DateTimeKind.Unspecified => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        DateTimeKind.Local => t.ToUniversalTime(),
        _ => t,
    };

    /// <summary>
    /// The ISO 8601 shapes <see cref="TryParseIso8601"/> accepts, all with a four-digit year, two-digit month and day,
    /// a literal <c>T</c> between date and time, and 24-hour two-digit fields. <c>K</c> takes <c>Z</c>, an offset such
    /// as <c>+01:00</c>, or nothing (a naive stamp, read as UTC by GridSim's contract). <c>FFFFFFF</c> takes one to
    /// seven fraction digits, so the round-trip ("o") form <c>2024-03-01T12:00:00.0000000Z</c> is one of them.
    /// </summary>
    private static readonly string[] Iso8601Formats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mmK",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
    ];

    /// <summary>
    /// D-DOC-063 (October 2026): parse a command-line instant strictly as ISO 8601 (or the .NET round-trip form, which
    /// is ISO 8601), with the invariant culture, and return it as UTC. Before, <c>--utc-now</c> and
    /// <c>--phaseangle-at</c> went through the lenient <c>DateTime.TryParse</c>, which accepted <c>01/02/2100</c> (read as 2 January by the invariant culture, but 1 February to a UK reader),
    /// <c>2 Jan 2100</c> and other shapes whose meaning depends on who wrote them. A time instant that steers the
    /// horizon guard must have exactly one reading.
    ///
    /// <para>Accepted: <c>yyyy-MM-dd</c> (midnight UTC), <c>yyyy-MM-ddTHH:mm</c>, <c>yyyy-MM-ddTHH:mm:ss</c> and
    /// <c>yyyy-MM-ddTHH:mm:ss.f</c> (one to seven fraction digits), each optionally followed by <c>Z</c> or an offset
    /// <c>+HH:mm</c> / <c>-HH:mm</c>. A stamp with no zone is UTC (the GridSim contract, as <see cref="Normalise"/>).
    /// Refused: any other shape, leading or trailing white space, a space instead of <c>T</c>, a fraction point with no
    /// digits, and out-of-range fields (month 13, hour 24).</para>
    /// </summary>
    /// <param name="text">The text to parse.</param>
    /// <param name="utc">The instant as a UTC-kind <see cref="DateTime"/> when the parse succeeds.</param>
    /// <returns>True when <paramref name="text"/> is one of the accepted shapes.</returns>
    public static bool TryParseIso8601(string? text, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrEmpty(text)) return false;
        // ParseExact with no AllowWhiteSpaces style already refuses white space; this says so explicitly.
        if (text.Length != text.Trim().Length) return false;
        // The 'F' fraction specifier also matches a bare '.' with no digits ("...:05.Z"); a fraction point must be
        // followed by a digit.
        int dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0 && (dot + 1 >= text.Length || !char.IsAsciiDigit(text[dot + 1]))) return false;
        if (!DateTime.TryParseExact(text, Iso8601Formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out DateTime parsed))
            return false;
        utc = Normalise(parsed);
        return true;
    }
}
