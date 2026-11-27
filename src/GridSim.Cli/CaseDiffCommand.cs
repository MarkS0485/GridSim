using System.Globalization;
using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis.PlanningStudies;
using GridSim.Core.Analysis.StudyCases;

namespace GridSim.Cli;

/// <summary>
/// RC4 R4-01: <c>gridsim case-diff &lt;a&gt; &lt;b&gt; [--out &lt;file.json&gt;]</c>. A read-only comparison of the authored
/// JSON files of two native cases (a v1 case folder, or a v2 case manifest with its network and asset files). No case is
/// solved or loaded as a model. Exit 0: no authored value differs; exit 3: they differ.
/// </summary>
internal static class CaseDiffCommand
{
    public const string Version = "gridsim-case-diff/1";

    public static int Run(List<string> arguments)
    {
        if (!TryParse(arguments, out string a, out string b, out string? outPath))
        {
            Console.Error.WriteLine("usage: gridsim case-diff <a> <b> [--out <file.json>]");
            return CliExit.Usage;
        }
        try
        {
            string pa = Path.GetFullPath(a), pb = Path.GetFullPath(b);
            if (outPath is not null && (File.Exists(Path.GetFullPath(outPath)) || Directory.Exists(Path.GetFullPath(outPath))))
                throw new IOException("--out names an existing path; the diff is written only to a new file.");
            Dictionary<string, byte[]> fa = Files(pa), fb = Files(pb);
            CaseDiffResult result = CaseDiff.Compare(fa, fb);
            string text = Text(pa, pb, fa.Count, fb.Count, result);
            if (outPath is not null || CliOutput.Report is not null)
            {
                string document = PlanningStudyCommand.SeamJson(new
                {
                    schemaVersion = Version,
                    filesA = fa.Keys.Order(StringComparer.Ordinal),
                    filesB = fb.Keys.Order(StringComparer.Ordinal),
                    equal = result.Empty,
                    bytesIdentical = result.BytesIdentical,
                    differences = result.Entries.Count,
                    entries = result.Entries.Select(e => new { kind = e.Kind, e.File, e.Path, old = e.Old, @new = e.New, unit = e.Unit })
                });
                if (outPath is not null) PlanningStudyCommand.SeamWriteNew(Path.GetFullPath(outPath), document);
                CliResultFromJson.Fill(document);
            }
            Console.Write(text);
            return result.Empty ? CliExit.Ok : CliExit.Verdict;
        }
        catch (Exception ex) when (ex is PlanningManifestException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Console.Error.WriteLine("case-diff: " + CliExit.UserMessage(ex));
            return CliExit.CaseLoadFailure(ex);
        }
    }

    private static bool TryParse(List<string> a, out string x, out string y, out string? outPath)
    {
        x = y = "";
        outPath = null;
        if (a.Count < 3 || a[1].StartsWith('-') || a[2].StartsWith('-')) return false;
        x = a[1];
        y = a[2];
        for (int i = 3; i < a.Count; i += 2)
        {
            if (a[i] != "--out" || outPath is not null || i + 1 >= a.Count || a[i + 1].StartsWith('-')) return false;
            outPath = a[i + 1];
        }
        return true;
    }

    /// <summary>The authored files of a case by label: a v1 folder's files by name, or a v2 manifest (label
    /// <c>manifest</c>) with its <c>network/</c> and <c>assets/</c> files.</summary>
    private static Dictionary<string, byte[]> Files(string path)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        if (Directory.Exists(path))
        {
            foreach (string name in CaseIdentity.V1Names)
                if (File.Exists(Path.Combine(path, name))) files[name] = File.ReadAllBytes(Path.Combine(path, name));
            if (files.Count == 0) throw new InvalidDataException("No case files found in " + path + ".");
            return files;
        }
        if (!File.Exists(path)) throw new FileNotFoundException("Case not found: " + path);
        byte[] manifest = File.ReadAllBytes(path);
        files["manifest"] = manifest;
        using JsonDocument document = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(manifest));
        string directory = Path.GetDirectoryName(path)!;
        foreach (string kind in new[] { "network", "assets" })
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(kind, out JsonElement list)
                && list.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in list.EnumerateArray())
                    files[kind + "/" + item.GetString()] = File.ReadAllBytes(CaseIdentity.Inside(directory, item.GetString()!));
        return files;
    }

    private static string Text(string a, string b, int na, int nb, CaseDiffResult r)
    {
        var sb = new StringBuilder();
        sb.Append("case-diff ").Append(Version).Append('\n');
        sb.Append("a  ").Append(a).Append("  (").Append(na.ToString(CultureInfo.InvariantCulture)).Append(" files)\n");
        sb.Append("b  ").Append(b).Append("  (").Append(nb.ToString(CultureInfo.InvariantCulture)).Append(" files)\n");
        foreach (CaseDiffEntry e in r.Entries)
        {
            string unit = e.Unit.Length > 0 ? " " + e.Unit : "";
            sb.Append(e.Kind.ToString().ToUpperInvariant().PadRight(10)).Append(' ').Append(e.Path);
            switch (e.Kind)
            {
                case CaseDiffKind.Changed: sb.Append("  ").Append(e.Old).Append(" -> ").Append(e.New).Append(unit); break;
                case CaseDiffKind.Added: sb.Append("  ").Append(e.New).Append(unit); break;
                case CaseDiffKind.Removed: sb.Append("  ").Append(e.Old).Append(unit); break;
                case CaseDiffKind.Reordered: sb.Append("  ").Append(e.Old).Append(" -> ").Append(e.New); break;
            }
            sb.Append('\n');
        }
        sb.Append(r.Empty
            ? (r.BytesIdentical ? "result: no differences; the files are byte-identical\n"
                : "result: no authored value differs (the files differ only in formatting)\n")
            : $"result: {r.Entries.Count.ToString(CultureInfo.InvariantCulture)} difference{(r.Entries.Count == 1 ? "" : "s")}\n");
        return sb.ToString();
    }
}
