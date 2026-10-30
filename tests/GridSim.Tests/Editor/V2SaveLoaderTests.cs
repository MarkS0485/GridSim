using GridSim.Core.IO.Json.CaseEditing;
using Xunit;
using static GridSim.Tests.Editor.CaseSessionTests;

namespace GridSim.Tests.Editor;

/// <summary>
/// N327 (1.95.238): the v2 save gate runs the v2 loader over a staged copy of a case manifest that imports nothing and
/// reports its verdict as a note. The verdict never changes the save outcome (a refusal is a WARNING note, not an error;
/// whether it should refuse the save is Mark's ruling), and a case that imports or a lone network file keeps the
/// "not run on save" note.
/// </summary>
public class V2SaveLoaderTests
{
    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Dir = Path.Combine(Path.GetTempPath(), "gridsim-editor-n327-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
        }

        public string Dir { get; }

        public string Write(string name, string text)
        {
            string path = Path.Combine(Dir, name);
            File.WriteAllText(path, text);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private const string Manifest = "{\n  \"schema\": \"gridsim-case/2\",\n  \"name\": \"Two buses\",\n  \"system\": { \"baseMva\": 100, \"frequencyHz\": 50 },\n  \"network\": [\"a.network.json\"]\n}\n";

    private const string Buses = "    { \"id\": 1, \"baseKv\": 400, \"type\": \"slack\" },\n    { \"id\": 2, \"baseKv\": 400 }\n";

    private const string Injections = "  \"generators\": [\n    { \"name\": \"G\", \"bus\": 1, \"capacityMva\": 500, \"outputMw\": 0 }\n  ],\n"
        + "  \"demands\": [\n    { \"name\": \"D\", \"bus\": 2, \"pMw\": 50, \"qMvar\": 10 }\n  ]";

    /// <summary>Two buses joined by one line: a case the v2 loader reads.</summary>
    private static string Connected(TempDir t)
    {
        t.Write("a.network.json", "{\n  \"schema\": \"gridsim-network/2\",\n  \"buses\": [\n" + Buses + "  ],\n" + Injections + ",\n"
            + "  \"lines\": [\n    { \"name\": \"L1-2\", \"from\": 1, \"to\": 2, \"rPu\": 0.002, \"xPu\": 0.02, \"bPu\": 0.01, \"ratingMva\": 500 }\n  ]\n}\n");
        return t.Write("two.case.json", Manifest);
    }

    /// <summary>The same two buses with no branch: bus 2 is an island with no slack, which the loader refuses.</summary>
    private static string Islanded(TempDir t)
    {
        t.Write("a.network.json", "{\n  \"schema\": \"gridsim-network/2\",\n  \"buses\": [\n" + Buses + "  ],\n" + Injections + "\n}\n");
        return t.Write("two.case.json", Manifest);
    }

    [Fact]
    public void A_case_the_loader_reads_says_so_and_saves()
    {
        using var t = new TempDir();
        CaseSession s = CaseSession.OpenV2(Connected(t), Unlimited());
        SaveReport check = SaveGate.Run(s, new SaveOptions(CheckOnly: true));
        Assert.True(check.Outcome == SaveOutcome.CheckPassed, string.Join(Environment.NewLine, check.Lines()));
        Assert.Contains(check.Notes, n => n.StartsWith("the v2 loader reads the case", StringComparison.Ordinal));
        Assert.DoesNotContain(check.Notes, n => n.Contains("WARNING", StringComparison.Ordinal));
        Assert.DoesNotContain(check.Notes, n => n.Contains("is not run on save", StringComparison.Ordinal));
    }

    [Fact]
    public void A_case_the_loader_refuses_is_named_in_a_warning_note_and_the_outcome_is_unchanged()
    {
        using var t = new TempDir();
        CaseSession s = CaseSession.OpenV2(Islanded(t), Unlimited());
        SaveReport check = SaveGate.Run(s, new SaveOptions(CheckOnly: true));
        // Moved 2026-10 (END-PASS CF5 row 2, end pass; N327 part 2 / Q32, accepted by main: a case the v2 loader refuses
        // is refused by the save gate, Ruling R1 and Mark's go on the end-pass moves 2026-10-11): part 1's "warn but let it
        // pass" no longer exists. The name and summary describe part 1 and are kept so existing references resolve.
        // Before -> after: CheckPassed -> CheckFailed (observed: Expected CheckPassed, Actual CheckFailed, 1.95.273-275
        // runs). The WARNING note assertions below are unchanged (derived, not observed: the run stopped here; CF5's
        // notes say they still hold).
        Assert.Equal(SaveOutcome.CheckFailed, check.Outcome);
        string note = Assert.Single(check.Notes, n => n.StartsWith("WARNING: the v2 loader refuses the case", StringComparison.Ordinal));
        // The staged temporary folder is never shown; the message names the case's own folder or files only.
        Assert.DoesNotContain("gridsim-edit-", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lone_network_file_keeps_the_not_run_note()
    {
        using var t = new TempDir();
        Islanded(t);
        CaseSession s = CaseSession.OpenV2(Path.Combine(t.Dir, "a.network.json"), Unlimited());
        SaveReport check = SaveGate.Run(s, new SaveOptions(CheckOnly: true));
        Assert.Contains(check.Notes, n => n.Contains("the v2 loader is not run on save of a lone network file", StringComparison.Ordinal));
    }

    [Fact]
    public void The_embedded_kind_specs_equal_the_files_on_disk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-n327-schemas-" + Guid.NewGuid().ToString("N"));
        try
        {
            BundledSchemas.WriteV2SchemaFolder(dir);
            string onDisk = Path.Combine(GridSim.Tests.V2.V2TestSupport.SchemasDir, "kinds");
            var expected = Directory.EnumerateFiles(onDisk, "*.kind.json").Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var written = Directory.EnumerateFiles(Path.Combine(dir, "kinds")).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.Equal(expected, written);
            foreach (string? f in expected)
                Assert.True(File.ReadAllBytes(Path.Combine(onDisk, f!)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "kinds", f!))), f);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
