using System.Text.Json;
using GridSim.Core.IO.Dgs;
using Xunit;
using static GridSim.Tests.Substations.DgsFixtures;

namespace GridSim.Tests.Substations;

/// <summary>RC4 R4-11 part C: the <c>dgs-import</c> verb run as the built CLI, on SYNTHETIC fixtures (see DgsFixtures).</summary>
public sealed class DgsImportCliProcessTests
{
    private static string Dir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-r411c-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void The_verb_writes_the_case_folder_and_the_existing_pf_verb_solves_the_case_it_wrote()
    {
        string d = Dir();
        string dgs = Path.Combine(d, "site.dgs");
        File.WriteAllText(dgs, TwoFeed(coupler: 0));
        string outDir = Path.Combine(d, "case");

        CliProcess.Result r = CliProcess.RunBuilt("dgs-import", dgs, "--out", outDir, "--accept-unconfirmed");
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("provenance FormatFromPublicDescription", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("UNCONFIRMED mappings used", r.StdOut, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(outDir, "substations.json")));
        Assert.True(File.Exists(Path.Combine(outDir, "case.m")));
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "dgs-import-report.json"))))
        {
            Assert.Equal("gridsim-dgs-import/1", doc.RootElement.GetProperty("format").GetString());
            Assert.Equal("FormatFromPublicDescription", doc.RootElement.GetProperty("provenance").GetString());
            Assert.True(doc.RootElement.GetProperty("complete").GetBoolean());
        }
        Assert.Equal(0, CliProcess.RunBuilt("pf", Path.Combine(outDir, "case.m")).ExitCode);
        // The native node-breaker file written beside it is read by the part A verb, at the DGS switch states.
        Assert.Equal(0, CliProcess.RunBuilt("substation-state", Path.Combine(outDir, "substations.json"), "--unspecified", "normal").ExitCode);

        // Existing files are never overwritten.
        string before = File.ReadAllText(Path.Combine(outDir, "case.m"));
        CliProcess.Result again = CliProcess.RunBuilt("dgs-import", dgs, "--out", outDir, "--accept-unconfirmed");
        Assert.Equal(1, again.ExitCode);
        Assert.Contains("already exists", again.StdErr, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(Path.Combine(outDir, "case.m")));
    }

    [Fact]
    public void Without_acceptance_the_verb_lists_the_refusals_and_exits_1()
    {
        string d = Dir();
        string dgs = Path.Combine(d, "site.dgs");
        File.WriteAllText(dgs, TwoBus());
        CliProcess.Result r = CliProcess.RunBuilt("dgs-import", dgs, "--out", Path.Combine(d, "case"));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("REFUSED ElmLne 'L1'", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("UNCONFIRMED", r.StdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(d, "case", "case.m")));
        Assert.True(File.Exists(Path.Combine(d, "case", "dgs-import-report.json")));   // the refusals stay on record
    }

    [Fact]
    public void An_unsupported_class_is_listed_and_strict_turns_it_into_exit_3_after_the_files_are_written()
    {
        string d = Dir();
        string dgs = Path.Combine(d, "site.dgs");
        File.WriteAllText(dgs, TwoBus() + "$$ElmTr3;ID(a:40)\nTX3\n");
        CliProcess.Result loose = CliProcess.RunBuilt("dgs-import", dgs, "--out", Path.Combine(d, "a"), "--accept-unconfirmed");
        Assert.Equal(0, loose.ExitCode);
        Assert.Contains("UNSUPPORTED class ElmTr3", loose.StdOut, StringComparison.Ordinal);
        Assert.Contains("INCOMPLETE", loose.StdOut, StringComparison.Ordinal);
        CliProcess.Result strict = CliProcess.RunBuilt("dgs-import", dgs, "--out", Path.Combine(d, "b"), "--accept-unconfirmed", "--strict");
        Assert.Equal(3, strict.ExitCode);
        Assert.True(File.Exists(Path.Combine(d, "b", "case.m")));
    }

    [Fact]
    public void A_file_outside_the_documented_dialect_is_refused_with_its_line_number_and_usage_errors_are_exit_2()
    {
        string d = Dir();
        string dgs = Path.Combine(d, "bad.dgs");
        File.WriteAllText(dgs, Header + "$$ElmTerm;ID(a:40);uknom(r:20)\nT1;11,5\n");
        CliProcess.Result bad = CliProcess.RunBuilt("dgs-import", dgs, "--out", Path.Combine(d, "case"));
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains("line 5", bad.StdErr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(d, "case")));

        Assert.Equal(2, CliProcess.RunBuilt("dgs-import", dgs).ExitCode);   // --out is required
        Assert.Equal(2, CliProcess.RunBuilt("dgs-import", dgs, "--out", Path.Combine(d, "x"), "--json", Path.Combine(d, "r.json")).ExitCode);   // not a result-file verb
        Assert.Equal(1, CliProcess.RunBuilt("dgs-import", Path.Combine(d, "missing.dgs"), "--out", Path.Combine(d, "y")).ExitCode);
    }
}
