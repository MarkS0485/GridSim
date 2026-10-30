using System.Globalization;
using GridSim.Core.Diagnostics;

namespace GridSim.Core.Frequency;

/// <summary>
/// Streams NESO system-frequency CSV (<c>dtm,f</c>  -  timestamp, Hz) one row at a
/// time, so a month of 1-second data (~2.7 M rows) reads without loading it all
/// into memory. Every row's timestamp is passed through <see cref="ForwardInferenceGuard"/> - a recording newer
/// than the historical horizon is rejected, so this ingester can only ever read the past.
///
/// <para>D-DOC-027 (October 2026, docs-session finding): what is skipped and what is refused. Blank lines are
/// skipped. The FIRST non-blank line is skipped as the column header when it is not a data row and its first field
/// holds no digit (NESO writes <c>dtm,f</c>; <c>timestamp,frequency</c> is skipped the same way). Every other line must
/// be a data row - a timestamp, a comma, and a finite frequency above 0 Hz - or the read stops with an
/// <see cref="InvalidDataException"/> that names the file, the line number and the line. The reader used to skip every
/// line it could not parse without a word, so a file with no parsable rows at all gave <c>freq</c> zero statistics and
/// exit 0, and a damaged row inside a trace silently shortened the comparison window.</para>
/// </summary>
public static class NesoFrequencyCsv
{
    /// <summary>Read the file's samples in file order (lazily). See the type comment for what is refused.</summary>
    /// <exception cref="InvalidDataException">A non-blank line other than the header is not a valid data row.</exception>
    /// <exception cref="ForwardInferenceException">A row is newer than the historical horizon.</exception>
    public static IEnumerable<FrequencySample> Read(string path)
    {
        using var reader = new StreamReader(path);
        string? line;
        int lineNumber = 0;          // 1-based, counting every physical line (blank ones too), as an editor shows them
        bool seenNonBlank = false;   // whether the first non-blank line (the only candidate header) has been passed
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;   // a blank line carries nothing: skipped, as before
            bool firstNonBlank = !seenNonBlank;
            seenNonBlank = true;

            if (!TryParseRow(line, out DateTime t, out double hz, out string why))
            {
                // The column header: the first non-blank line, not a data row, with no digit in its first field (so a
                // damaged FIRST data row, which would hold the digits of a date, is refused rather than taken for a header).
                if (firstNonBlank && IsHeaderLike(line)) continue;
                throw new InvalidDataException(
                    $"NESO frequency file '{path}' line {lineNumber} is not a 'timestamp,Hz' data row ({why}): '{Clip(line)}'. " +
                    "Every line after the header must be a timestamp, a comma and a finite frequency in Hz above 0.");
            }

            ForwardInferenceGuard.RejectFuture(t, what: "NESO frequency sample");
            yield return new FrequencySample(t, hz);
        }
    }

    /// <summary>
    /// Parse one data row. False (with the reason in <paramref name="why"/>) when the line has no comma ahead of a
    /// timestamp, the timestamp does not parse, the frequency does not parse, or the frequency is not a finite number
    /// above 0 Hz. The parsing rules are the ones the reader always used (invariant culture, stamps landed in UTC).
    /// </summary>
    private static bool TryParseRow(string line, out DateTime t, out double hz, out string why)
    {
        t = default;
        hz = 0.0;
        int comma = line.IndexOf(',');
        if (comma <= 0) { why = "no 'timestamp,' field before a comma"; return false; }

        ReadOnlySpan<char> tsSpan = line.AsSpan(0, comma).Trim();
        ReadOnlySpan<char> fSpan = line.AsSpan(comma + 1).Trim();

        // Land every stamp in UTC: a Z/+01:00-offset row converts to UTC (not machine-local wall time under
        // DateTimeStyles.None), and a naive row is assumed UTC. Otherwise the RMSE anchor in ReplayValidator,
        // which compares recorded times to the UTC event instant by raw ticks, drifts by the local offset in
        // BST - enough to demote a Reproduced baseline to Marginal on a UK machine.
        if (!DateTime.TryParse(tsSpan, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out t))
        { why = "the timestamp does not parse"; return false; }
        if (!double.TryParse(fSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out hz))
        { why = "the frequency does not parse as a number"; return false; }
        // A frequency that is NaN, infinite, zero or negative is not a measurement of a running system.
        if (!double.IsFinite(hz) || hz <= 0.0)
        { why = "the frequency is not a finite number above 0 Hz"; return false; }
        why = "";
        return true;
    }

    /// <summary>Whether a line reads as a column header: its first field (up to the first comma, or the whole line)
    /// holds no decimal digit. A date always holds digits, so a damaged data row is never mistaken for a header.</summary>
    private static bool IsHeaderLike(string line)
    {
        int comma = line.IndexOf(',');
        ReadOnlySpan<char> first = comma < 0 ? line.AsSpan() : line.AsSpan(0, comma);
        foreach (char c in first)
            if (c is >= '0' and <= '9') return false;
        return true;
    }

    /// <summary>The line as quoted in a message: at most 80 characters, so a binary file does not flood the console.</summary>
    private static string Clip(string line) => line.Length <= 80 ? line : line[..80] + "...";
}
