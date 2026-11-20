using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Analysis.StudyCases;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-01: <c>gridsim result-diff &lt;a.json&gt; &lt;b.json&gt; [--out &lt;file.json&gt;] [--max-differences N]</c>. A read-only
/// comparison of two result documents. A number is the same only when it is the identical IEEE double; a differing
/// number is reported with its distance in ULPs, and the first difference in document order is marked. Exit 0: equal;
/// exit 3: they differ.
/// </summary>
internal static class ResultDiffCommand
{
    public const string Version = "gridsim-result-diff/1";

    public static int Run(List<string> arguments)
    {
        if (!TryParse(arguments, out string a, out string b, out string? outPath, out int max))
        {
            Console.Error.WriteLine("usage: gridsim result-diff <a.json> <b.json> [--out <file.json>] [--max-differences N]");
            return CliExit.Usage;
        }
        try
        {
            string pa = Path.GetFullPath(a), pb = Path.GetFullPath(b);
            if (outPath is not null && (File.Exists(Path.GetFullPath(outPath)) || Directory.Exists(Path.GetFullPath(outPath))))
                throw new IOException("--out names an existing path; the diff is written only to a new file.");
            ResultDiffResult r = ResultDiff.Compare(File.ReadAllBytes(pa), File.ReadAllBytes(pb), max);
            var sb = new StringBuilder();
            sb.Append("result-diff ").Append(Version).Append('\n');
            sb.Append("a  ").Append(pa).Append('\n').Append("b  ").Append(pb).Append('\n');
            for (int i = 0; i < r.Entries.Count; i++)
            {
                ResultDiffEntry e = r.Entries[i];
                sb.Append(i == 0 ? "FIRST      " : "           ").Append(e.Kind.ToString().ToUpperInvariant()).Append(' ').Append(e.Path);
                switch (e.Kind)
                {
                    case ResultDiffKind.Added: sb.Append("  ").Append(e.B); break;
                    case ResultDiffKind.Removed: sb.Append("  ").Append(e.A); break;
                    default:
                        sb.Append("  ").Append(e.A).Append(" -> ").Append(e.B);
                        if (e.UlpDistance is ulong u)
                            sb.Append("  (").Append(u.ToString(CultureInfo.InvariantCulture)).Append(" ulp, abs ")
                              .Append(e.AbsoluteDifference!.Value.ToString("R", CultureInfo.InvariantCulture)).Append(')');
                        break;
                }
                sb.Append('\n');
            }
            if (r.Truncated) sb.Append("... ").Append((r.Differences - r.Entries.Count).ToString(CultureInfo.InvariantCulture)).Append(" more not listed (--max-differences)\n");
            sb.Append(r.Equal
                ? (r.BytesIdentical ? "result: equal; the documents are byte-identical\n" : "result: equal in value (the documents differ only in formatting)\n")
                : $"result: {r.Differences.ToString(CultureInfo.InvariantCulture)} difference{(r.Differences == 1 ? "" : "s")}\n");
            if (outPath is not null || CliOutput.Report is not null)
            {
                string document = PlanningStudyCommand.SeamJson(new
                {
                    schemaVersion = Version,
                    equal = r.Equal,
                    bytesIdentical = r.BytesIdentical,
                    differences = r.Differences,
                    truncated = r.Truncated,
                    first = r.First is null ? null : new { kind = r.First.Kind, path = r.First.Path, a = r.First.A, b = r.First.B, ulpDistance = r.First.UlpDistance, absoluteDifference = r.First.AbsoluteDifference },
                    entries = r.Entries.Select(e => new { kind = e.Kind, e.Path, a = e.A, b = e.B, ulpDistance = e.UlpDistance, absoluteDifference = e.AbsoluteDifference })
                });
                if (outPath is not null) PlanningStudyCommand.SeamWriteNew(Path.GetFullPath(outPath), document);
                CliResultFromJson.Fill(document);
            }
            Console.Write(sb.ToString());
            return r.Equal ? CliExit.Ok : CliExit.Verdict;
        }
        catch (Exception ex) when (ex is PlanningManifestException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine("result-diff: " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
    }

    private static bool TryParse(List<string> a, out string x, out string y, out string? outPath, out int max)
    {
        x = y = "";
        outPath = null;
        max = 100;
        bool maxSet = false;
        if (a.Count < 3 || a[1].StartsWith('-') || a[2].StartsWith('-')) return false;
        x = a[1];
        y = a[2];
        for (int i = 3; i < a.Count; i += 2)
        {
            if (i + 1 >= a.Count || a[i + 1].StartsWith('-')) return false;
            switch (a[i])
            {
                case "--out" when outPath is null: outPath = a[i + 1]; break;
                case "--max-differences" when !maxSet && int.TryParse(a[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int m):
                    max = m; maxSet = true; break;
                default: return false;
            }
        }
        return true;
    }
}
