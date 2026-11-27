using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Journal;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP27 review fixes (2.1.134): the cosim boundary log goes through the output policy, so the run journal records it
/// and <c>breaking-change validate</c> compares it with the re-run's.
/// </summary>
public sealed class Rc5Wp27ReviewFixTests : IDisposable
{
    private readonly string _dir = JournalTestSupport.NewDir("wp27fix");

    public void Dispose() => JournalTestSupport.Delete(_dir);

    [Fact]
    public void The_cosim_boundary_log_is_journaled_and_validate_compares_it()
    {
        string w = Path.Combine(_dir, "work");
        Directory.CreateDirectory(w);
        File.WriteAllText(Path.Combine(w, "cosim.json"), Rc5Wp27HarmonicCosimVerbsTests.CoSimStudy);
        string journal = Path.Combine(_dir, "j", "journal.jsonl");
        CliProcess.Result run = JournalTestSupport.Run(w, "--journal", journal, "cosim", "cosim.json", "--out", "o.json", "--boundary-csv", "boundary.csv");
        Assert.True(run.ExitCode == 0, run.ToString());

        // The journal records the log beside the result file, with the hash of the bytes on disk.
        JournalEntry e = JournalJson.Parse(File.ReadAllLines(journal)[0], out _)!;
        string csv = Path.Combine(w, "boundary.csv");
        string sha = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(csv)));
        Assert.Contains(e.Outputs, o => Path.GetFileName(o.Abs) == "boundary.csv" && o.Sha256 == sha);
        Assert.Contains(e.Outputs, o => Path.GetFileName(o.Abs) == "o.json");
        Assert.Equal("safe", e.ReplayClass);

        // validate re-runs the job: identical, the log included.
        JournalTestSupport.FromAnotherBuild(journal);
        string report = Path.Combine(_dir, "v.json");
        CliProcess.Result v = JournalTestSupport.Run(_dir, "breaking-change", "validate", "--journal", journal, "--json", report);
        Assert.True(v.ExitCode == 0, v.ToString());
        using (JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(report)))
            Assert.Equal(ReplayStatus.Identical, Assert.Single(d.RootElement.GetProperty("tables").GetProperty("entries").EnumerateArray()).GetProperty("status").GetString());

        // The recorded log is checked against its recorded hash: an edit after the run is caught.
        string[] lines = File.ReadAllLines(csv);
        string[] cells = lines[^1].Split(',');
        cells[^1] = "12345.5";   // z_im of the last exchange
        lines[^1] = string.Join(",", cells);
        File.WriteAllText(csv, string.Join("\n", lines) + "\n");
        string report2 = Path.Combine(_dir, "v2.json");
        JournalTestSupport.Run(_dir, "breaking-change", "validate", "--journal", journal, "--json", report2);
        using (JsonDocument d2 = JsonDocument.Parse(File.ReadAllBytes(report2)))
        {
            JsonElement entry = Assert.Single(d2.RootElement.GetProperty("tables").GetProperty("entries").EnumerateArray());
            Assert.Equal(ReplayStatus.CannotReplay, entry.GetProperty("status").GetString());
            Assert.StartsWith("recorded result changed since the run: ", entry.GetProperty("reason").GetString(), StringComparison.Ordinal);
            Assert.EndsWith("boundary.csv", entry.GetProperty("reason").GetString(), StringComparison.Ordinal);
        }

        // A recorded log that differs from the re-run's (the journal now holds the edited file's hash, as if the run had
        // written it) is a change in that file: validate compares the log field by field.
        string shaEdited = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(csv)));
        File.WriteAllText(journal, File.ReadAllText(journal).Replace(sha, shaEdited, StringComparison.Ordinal));
        string report3 = Path.Combine(_dir, "v3.json");
        JournalTestSupport.Run(_dir, "breaking-change", "validate", "--journal", journal, "--json", report3);
        using JsonDocument d3 = JsonDocument.Parse(File.ReadAllBytes(report3));
        Assert.Equal(ReplayStatus.Changed, Assert.Single(d3.RootElement.GetProperty("tables").GetProperty("entries").EnumerateArray()).GetProperty("status").GetString());
        Assert.Contains(d3.RootElement.GetProperty("tables").GetProperty("field_differences").EnumerateArray(),
            f => f.GetProperty("file").GetString() == "boundary.csv");
    }
}
