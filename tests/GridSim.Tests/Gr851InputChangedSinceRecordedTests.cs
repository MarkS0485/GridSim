using System;
using System.IO;
using System.Text.RegularExpressions;
using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-851 (November 2026): an audited run hashes each input on one read and loads it on another, so a file replaced in
/// between used to be recorded with the OLD hash, naming data the run never used. <see cref="AuditRunRecorder.InputChangedSinceRecorded"/>
/// hashes the input again once it is loaded and says when it no longer matches, and the CLI refuses the run (exit 1) at
/// every audited load site: the interpose crosswalk, the estimate case and measurement set, and the crossval case.
/// The last test is a source-level wiring check (the test project does not reference the CLI).
/// </summary>
public sealed class Gr851InputChangedSinceRecordedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"gridsim-gr851-{Guid.NewGuid():N}");

    public Gr851InputChangedSinceRecordedTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort: a leftover temp folder is not a test failure */ }
    }

    private string LogPath => Path.Combine(_dir, "audit.jsonl");

    [Fact]
    public void An_unchanged_file_input_passes()
    {
        string input = Path.Combine(_dir, "m.json");
        File.WriteAllText(input, "{\"a\":1}");
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Recorded);
        run.AddInput(AuditInput.OfFile("measurements", input));

        Assert.Null(run.InputChangedSinceRecorded("measurements", input));
        run.Finish(0);
    }

    [Fact]
    public void A_file_replaced_after_it_was_recorded_is_named_with_both_hashes()
    {
        string input = Path.Combine(_dir, "m.json");
        File.WriteAllText(input, "{\"a\":1}");
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Recorded);
        AuditInput recorded = AuditInput.OfFile("measurements", input);
        run.AddInput(recorded);

        File.WriteAllText(input, "{\"a\":2}");   // the replacement in the window between the two reads
        string? why = run.InputChangedSinceRecorded("measurements", input);

        Assert.NotNull(why);
        Assert.Contains("measurements", why, StringComparison.Ordinal);
        Assert.Contains(recorded.Sha256Hex, why, StringComparison.Ordinal);
        Assert.Contains(AuditInput.OfFile("measurements", input).Sha256Hex, why, StringComparison.Ordinal);
        Assert.Contains("GR-851", why, StringComparison.Ordinal);
        run.Finish(1);
    }

    [Fact]
    public void A_case_folder_with_one_file_changed_is_caught()
    {
        string caseDir = Path.Combine(_dir, "case");
        Directory.CreateDirectory(caseDir);
        File.WriteAllText(Path.Combine(caseDir, "system.json"), "{}");
        File.WriteAllText(Path.Combine(caseDir, "buses.csv"), "id\n1\n");
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "crossval", ProvenanceTag.Recorded);
        run.AddInput(AuditRunRecorder.InputForArgument("case", caseDir));
        Assert.Null(run.InputChangedSinceRecorded("case", caseDir));

        File.WriteAllText(Path.Combine(caseDir, "buses.csv"), "id\n1\n2\n");

        Assert.NotNull(run.InputChangedSinceRecorded("case", caseDir));
        run.Finish(1);
    }

    [Fact]
    public void A_file_deleted_after_it_was_recorded_is_refused()
    {
        // A deleted file no longer hashes as a file: InputForArgument falls back to the name (case-id), which never
        // matches the recorded content hash, so the run is refused rather than recorded against data that is gone.
        string input = Path.Combine(_dir, "map.json");
        File.WriteAllText(input, "{}");
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "interpose", ProvenanceTag.Recorded);
        run.AddInput(AuditInput.OfFile("crosswalk", input));

        File.Delete(input);

        Assert.NotNull(run.InputChangedSinceRecorded("crosswalk", input));
        run.Finish(1);
    }

    [Fact]
    public void A_bundled_case_name_recorded_as_its_id_is_found_and_never_changes()
    {
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Recorded);
        string name = $"no-such-file-{Guid.NewGuid():N}";
        run.AddInput(AuditRunRecorder.InputForArgument("case", name));   // recorded as case-id

        Assert.Null(run.InputChangedSinceRecorded("case", name));
        run.Finish(0);
    }

    [Fact]
    public void Checking_an_input_that_was_never_recorded_is_a_programming_error()
    {
        AuditRunRecorder run = AuditRunRecorder.Start(LogPath, "a", "estimate", ProvenanceTag.Recorded);
        Assert.Throws<InvalidOperationException>(() => run.InputChangedSinceRecorded("measurements", Path.Combine(_dir, "x.json")));
        run.Finish(0);
    }

    // ---- CLI wiring (source-level; the test project does not reference the CLI) ----

    private static string CliSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");
    }

    [Fact]
    public void Every_audited_load_site_checks_its_input_again_after_loading_it()
    {
        string src = CliSource();
        // The helper refuses with exit 1 and names the input in the run's outcome.
        Assert.Contains("bool AuditInputChangedSinceRecorded(string name, string argument)", src, StringComparison.Ordinal);
        Assert.Contains("auditRun.InputChangedSinceRecorded(name, argument)", src, StringComparison.Ordinal);

        // interpose: after the crosswalk is loaded.
        Assert.Matches(new Regex(@"crosswalk = RtuPointMap\.Load\(crosswalkPath\);[^\n]*\n[^\n]*\n\s*if \(AuditInputChangedSinceRecorded\(""crosswalk"", crosswalkPath\)\) return;"), src);
        // estimate: after the case and after the measurement set. Corrected 2026-11-02: comment lines (GR-190 added one)
        // may sit between the load's catch and the re-check; the order the test pins is unchanged.
        Assert.Matches(new Regex(@"model = isCaseDir \? Gated\.Dir\(caseName\) : LoadNamed\(caseName, root\);\n\s*\}\n(?:\s*//[^\n]*\n)*[^\n]*\n\s*if \(AuditInputChangedSinceRecorded\(""case"", caseName\)\) return;"), src);
        Assert.Matches(new Regex(@"if \(!synth && AuditInputChangedSinceRecorded\(""measurements"", measurementsPath!\)\) return;"), src);
        // crossval: after LoadCaseArgument.
        Assert.Matches(new Regex(@"m = LoadCaseArgument\(arg, root\);\n\s*\}\n(?:\s*//[^\n]*\n)*[^\n]*\n\s*if \(AuditInputChangedSinceRecorded\(""case"", arg\)\) return;"), src);
    }
}
