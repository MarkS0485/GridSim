using System.Globalization;

namespace GridSim.Core.Protection;

/// <summary>
/// Reads a real sequence-of-events / alarm log for <see cref="SequenceOfEvents.CrossReference"/> from CSV (R095).
///
/// <para><b>Format</b> (UTF-8, one header line, then one alarm per line):
/// <c>at_s,device,from,to,description</c>. <c>at_s</c> is seconds from the event start (t = 0; map a real alarm's
/// UTC to it first), a finite number in invariant culture. <c>device</c> is the ANSI device id as logged (for
/// example <c>51</c> or <c>21-Z1</c>), non-empty. <c>from</c> and <c>to</c> are the branch's end-bus ids; a
/// non-branch alarm (bus voltage or frequency) has both 0 and takes no part in the branch-trip match. The
/// description is free text and is everything after the fourth comma, so it may itself contain commas. Blank
/// lines and lines starting with <c>#</c> are skipped.</para>
///
/// <para><b>Fail loud.</b> A wrong header, a missing field, a time that is not a finite number, a non-integer or
/// negative bus id, exactly one of <c>from</c> / <c>to</c> being 0, or an empty device each throw
/// <see cref="InvalidDataException"/> naming the line, rather than dropping the alarm (a silently dropped alarm
/// would turn into a false "predicted, not observed").</para>
/// </summary>
public static class AlarmLogCsv
{
    /// <summary>The required header line.</summary>
    public const string Header = "at_s,device,from,to,description";

    /// <summary>Read an alarm log file. A missing file throws <see cref="FileNotFoundException"/>.</summary>
    public static IReadOnlyList<AlarmEvent> Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new StreamReader(path);
        return Parse(reader, path);
    }

    /// <summary>Parse an alarm log from a reader; <paramref name="sourceName"/> is used in error messages.</summary>
    public static IReadOnlyList<AlarmEvent> Parse(TextReader reader, string sourceName = "alarm log")
    {
        ArgumentNullException.ThrowIfNull(reader);
        var alarms = new List<AlarmEvent>();
        int lineNo = 0;
        bool headerSeen = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            if (!headerSeen)
            {
                if (!string.Equals(trimmed, Header, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"{sourceName} line {lineNo}: the header must be exactly '{Header}', got '{trimmed}'.");
                headerSeen = true;
                continue;
            }
            alarms.Add(ParseRow(line, lineNo, sourceName));
        }
        if (!headerSeen)
            throw new InvalidDataException($"{sourceName}: empty; the first line must be the header '{Header}'.");
        return alarms;
    }

    private static AlarmEvent ParseRow(string line, int lineNo, string sourceName)
    {
        // Split into at most five fields: the description (last) keeps any commas of its own.
        string[] f = line.Split(',', 5);
        if (f.Length < 5)
            throw new InvalidDataException(
                $"{sourceName} line {lineNo}: expected 5 fields ({Header}), got {f.Length}: '{line}'.");

        if (!double.TryParse(f[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double at) || !double.IsFinite(at))
            throw new InvalidDataException($"{sourceName} line {lineNo}: at_s '{f[0].Trim()}' is not a finite number.");

        string device = f[1].Trim();
        if (device.Length == 0)
            throw new InvalidDataException($"{sourceName} line {lineNo}: the device id is empty.");

        int from = BusId(f[2], "from", lineNo, sourceName);
        int to = BusId(f[3], "to", lineNo, sourceName);
        if ((from == 0) != (to == 0))
            throw new InvalidDataException(
                $"{sourceName} line {lineNo}: from={from}, to={to}: a branch alarm needs both end buses and a non-branch " +
                "alarm has both 0; exactly one 0 is ambiguous.");

        return new AlarmEvent(at, device, from, to, f[4].Trim());
    }

    private static int BusId(string text, string field, int lineNo, string sourceName)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id < 0)
            throw new InvalidDataException(
                $"{sourceName} line {lineNo}: {field} '{text.Trim()}' is not a non-negative integer bus id.");
        return id;
    }
}
