using System.Globalization;
using System.Text.RegularExpressions;

namespace GridSim.Core.IO.Psse;

/// <summary>
/// Reads PSS/E RAW revision 36 by rewriting it, field by field and by name, as the revision 35 text the one RAW reader
/// reads.
/// </summary>
/// <remarks>
/// <para><b>What revision 36 changes</b> (PSS/E 36.0.0 release notes, "Power Flow Data (RAW and RAWX formats) File
/// Changes"): new records (SWDRATNAM in the system-wide data, voltage droop control, switching device rating table, load
/// type, interface and interface element data); a NAME of up to 40 characters on load, fixed shunt, generator, switched
/// shunt and induction machine records; DROOPNAME on the generator and BYPASS on the branch; and the system and station
/// switching device records using the rating table. The release notes give the changes, not the field positions.</para>
/// <para><b>How it is read.</b> Every revision 36 file PSS/E writes carries an <c>@!</c> heading line above each record
/// group that names its fields. This reader takes the field order from that heading, rebuilds each load, fixed shunt,
/// generator, branch, system switching device, switched shunt and induction machine record in the revision 35 order by
/// name, and drops the names revision 35 has no field for (NAME, DROOPNAME). So it never reads one field as its neighbour:
/// a record group with no heading line is refused, naming the line. Sections it does not model (droop control, rating
/// tables, load types, interfaces) are read past and counted in a note. Bus, transformer, area, DC, FACTS, zone, owner and
/// substation records are passed through unchanged. A branch with BYPASS 1 (treated by PSS/E as a zero-impedance line)
/// is refused rather than guessed at. Sections must be ended by terminators that carry PSS/E's
/// <c>END OF x DATA, BEGIN y DATA</c> label, which is how the section a group belongs to is known.</para>
/// <para><b>Verification.</b> The test fixtures are written from the release notes and from the revision 35 layout this
/// reader already reads; no vendor-written revision 36 file was available. The equality test is that a revision 36 case
/// and its revision 35 equivalent give the same model, bit for bit.</para>
/// </remarks>
internal static class PsseRevision36
{
    /// <summary>The revision the REV field or the comment of the first record names, or null.</summary>
    internal static int? PeekRevision(string[] lines)
    {
        int h = 0;
        while (h < lines.Length && (lines[h].Trim().Length == 0 || PsseText.IsHeading(lines[h]))) h++;
        if (h >= lines.Length) return null;
        List<string?> f;
        string? comment;
        try { (f, comment) = PsseText.Split(lines[h], h + 1, "CASE IDENTIFICATION"); }
        catch (InvalidDataException) { return null; }
        if (f.Count > 2 && !string.IsNullOrWhiteSpace(f[2])
            && int.TryParse(f[2]!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rev) && rev != 0)
            return rev;
        if (comment is not null)
        {
            Match m = Regex.Match(comment, @"PSS(?:/|\(tm\)|\(TM\)|\(R\)|\xAE)?E-(\d+)");
            if (m.Success) return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        return null;
    }

    private static readonly string[] DroppedKeywords = { "DROOP", "RATING", "LOAD TYPE", "INTERFACE" };

    /// <summary>The revision 35 text of a revision 36 file, with notes on what was read past.</summary>
    internal static string[] ToRevision35(string[] lines, List<string> notes)
    {
        var outp = new List<string>(lines.Length);
        int at = 0;
        while (at < lines.Length && (lines[at].Trim().Length == 0 || PsseText.IsHeading(lines[at]))) { outp.Add(lines[at]); at++; }
        if (at >= lines.Length) throw new InvalidDataException("PSS/E RAW text has no case identification line.");
        (List<string?> idf, string? idComment) = PsseText.Split(lines[at], at + 1, "CASE IDENTIFICATION");
        var idOut = new List<string?>(idf);
        while (idOut.Count < 3) idOut.Add("");
        idOut[2] = "35";
        outp.Add(string.Join(",", idOut.Select(v => v ?? "")) + (idComment is null ? "" : " / " + idComment));
        at++;
        for (int t = 0; t < 2; t++)
        {
            while (at < lines.Length && PsseText.IsHeading(lines[at])) { at++; }
            if (at >= lines.Length) throw new InvalidDataException("PSS/E RAW line " + (at + 1).ToString(CultureInfo.InvariantCulture) + ": the file ends before the two title lines.");
            outp.Add(lines[at]);
            at++;
        }

        string? current = null;           // the section being read: a label as the file writes it (upper case)
        List<string>? heading = null;     // the field names of the latest heading line
        var dropped = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = at; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0) continue;
            if (PsseText.IsHeading(line)) { heading = PsseFieldRemap.HeadingNames(line); continue; }
            (List<string?> f, string? comment) = PsseText.Split(line, i + 1, current ?? "SECTION");
            if (f.Count == 0) continue;
            bool terminator = f.Count == 1 && f[0] == "0";
            bool quit = f.Count >= 1 && string.Equals(f[0], "Q", StringComparison.OrdinalIgnoreCase) && !line.TrimStart().StartsWith('\'');
            if (current is null)
                current = f[0] is { Length: > 0 } first0 && char.IsLetter(first0[0]) && !terminator ? "SYSTEM-WIDE" : "BUS";

            if (quit)
            {
                outp.Add(line);
                continue;
            }

            if (terminator)
            {
                string? end = PsseFieldRemap.EndLabel(comment);
                string? begin = PsseFieldRemap.BeginLabel(comment);
                if (end is null && begin is null)
                    throw new InvalidDataException(FormattableString.Invariant($"PSS/E RAW line {i + 1} ({current} data): a revision 36 section terminator must carry its '0 / END OF x DATA, BEGIN y DATA' label, which is how the sections are told apart; this one has none."));
                // Inside a substation the node, switching device and terminal groups end with their own terminators.
                if (current == "SUBSTATION" && end is not null && end != "SUBSTATION")
                {
                    outp.Add(line);
                    continue;
                }
                heading = null;
                bool keptSection = !IsDropped(current);
                if (keptSection) outp.Add(line);
                current = begin is null ? null : Normalise(begin);
                if (current is null) current = "BUS";   // no BEGIN label: the end of the data follows
                continue;
            }

            if (IsDropped(current))
            {
                dropped[current] = dropped.GetValueOrDefault(current) + 1;
                continue;
            }

            string[]? spec = SpecOf(current);
            if (spec is null)
            {
                outp.Add(line);
                continue;
            }
            if (heading is null)
                throw new InvalidDataException(FormattableString.Invariant($"PSS/E RAW line {i + 1} ({current} data): revision 36 gives the field order in the '@!' heading line above each record group, and this group has none, so its fields cannot be placed; write the heading line, or write the file as revision 35."));
            if (current == "BRANCH" && PsseFieldRemap.ValueOf(heading, f, "BYPASS") is { } by
                && double.TryParse(by, NumberStyles.Float, CultureInfo.InvariantCulture, out double bypass) && bypass != 0)
            {
                string? st = PsseFieldRemap.ValueOf(heading, f, "ST|STAT|STATUS");
                if (st is null || st.Trim() != "0")
                    throw new InvalidDataException(FormattableString.Invariant($"PSS/E RAW line {i + 1} (BRANCH data): BYPASS is {by}; PSS/E treats a bypassed branch as a zero-impedance line, which this reader does not apply. Clear BYPASS, or write the branch as a system switching device."));
            }
            List<string?> row = PsseFieldRemap.Reorder(spec, heading, f);
            outp.Add(PsseFieldRemap.Line(spec, row));
        }
        foreach (var kv in dropped.OrderBy(k => k.Key, StringComparer.Ordinal))
            notes.Add(FormattableString.Invariant($"revision 36: {kv.Value} {kv.Key.ToLowerInvariant()} record(s) read past (GridSim has no model for them)."));
        notes.Add("revision 36: record fields were placed by the '@!' heading line of each group and rebuilt in the revision 35 order; NAME, DROOPNAME and the switching device rating table names were dropped.");
        return outp.ToArray();
    }

    private static string Normalise(string label)
    {
        string l = Regex.Replace(label.ToUpperInvariant(), @"\s+", " ").Trim();
        return l switch
        {
            "SYSTEM WIDE" => "SYSTEM-WIDE",
            "NON-TRANSFORMER BRANCH" => "BRANCH",
            "SWITCHING DEVICE" => "SYSTEM SWITCHING DEVICE",
            _ => l,
        };
    }

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "SYSTEM-WIDE", "BUS", "LOAD", "FIXED SHUNT", "GENERATOR", "BRANCH", "SYSTEM SWITCHING DEVICE", "TRANSFORMER", "AREA",
        "TWO-TERMINAL DC", "VSC DC", "IMPEDANCE CORRECTION", "MULTI-TERMINAL DC", "MULTI-SECTION LINE", "ZONE",
        "INTER-AREA TRANSFER", "OWNER", "FACTS DEVICE", "SWITCHED SHUNT", "GNE DEVICE", "INDUCTION MACHINE", "SUBSTATION",
    };

    /// <summary>True for a section revision 36 added that GridSim does not model; a label that is neither known nor one of
    /// those is refused so a different layout is never read as revision 35.</summary>
    private static bool IsDropped(string section)
    {
        if (Known.Contains(section) || SectionAliases(section)) return false;
        foreach (string k in DroppedKeywords) if (section.Contains(k, StringComparison.Ordinal)) return true;
        throw new InvalidDataException(FormattableString.Invariant($"PSS/E RAW: the section '{section}' is not one revision 35 or 36 defines, as this reader knows them."));
    }

    private static bool SectionAliases(string s) =>
        s is "VOLTAGE SOURCE CONVERTER" or "VSC DC LINE" or "TWO TERMINAL DC" or "2-TERMINAL DC" or "MULTI TERMINAL DC"
            or "MULTI SECTION LINE" or "MULTI-SECTION LINE GROUPING" or "INTERAREA TRANSFER" or "FACTS CONTROL DEVICE"
            or "FACTS" or "GNE" or "AREA INTERCHANGE" or "TRANSFORMER IMPEDANCE CORRECTION";

    private static string[]? SpecOf(string section) => section switch
    {
        "LOAD" => PsseFieldRemap.Load,
        "FIXED SHUNT" => PsseFieldRemap.FixedShunt,
        "GENERATOR" => PsseFieldRemap.Generator,
        "BRANCH" => PsseFieldRemap.Branch,
        "SYSTEM SWITCHING DEVICE" => PsseFieldRemap.SwitchingDevice,
        "SWITCHED SHUNT" => PsseFieldRemap.SwitchedShunt,
        "INDUCTION MACHINE" => PsseFieldRemap.InductionMachine,
        _ => null,
    };
}
