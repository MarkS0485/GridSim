using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Tranche A (October 2026): a PSS/E .raw file is a case wherever a .m file is (the bare run, every verb's positional
/// case, convert's input), loaded in one place (Gated.Psse), with --allow-unmodelled for files holding devices GridSim
/// does not model. Runs the built CLI.
/// </summary>
public class PsseCliTests
{
    private static string Fixture(string name) => Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", name);

    private static string RepoFile(string rel) => File.ReadAllText(Path.Combine(CliProcess.Root(), rel)).Replace("\r\n", "\n");

    private static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "gridsim-psse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void A_raw_file_runs_as_a_case_and_its_notes_go_to_stderr()
    {
        CliProcess.Result r = CliProcess.RunBuilt(Fixture("sample-v35.raw"), "--once");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("note: sample-v35.raw: added 1 star-point bus(es) (11..11) for three-winding transformers.", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("note:", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Convert_takes_a_raw_input_and_proves_the_round_trip()
    {
        string dir = TempDir();
        try
        {
            string output = Path.Combine(dir, "case14.m");
            CliProcess.Result r = CliProcess.RunBuilt("convert", Fixture("case14.raw"), output);
            Assert.True(r.ExitCode == 0, r.ToString());
            Assert.Contains("converted ", r.StdOut, StringComparison.Ordinal);
            Assert.True(File.Exists(output));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Unmodelled_devices_are_refused_unless_allowed()
    {
        string dir = TempDir();
        try
        {
            // Test input changed 2026-10-25 (device item B): MATPOWER's revision 33 file held two plain DC links, which
            // are modelled now, so it no longer exercises the refusal. One link is made capacitor-commutated (XCAP 2
            // on DCLINE 1's rectifier), a device GridSim still does not model; the assertions are the same, for that kind.
            string raw = Path.Combine(dir, "nine.raw");
            File.WriteAllText(raw, File.ReadAllText(Fixture("t_case9_save2psse.raw")).Replace("    0, 0,     5,", "    1, 0,     5,")
                .Replace("     3, 1, 25.0, 15.0, 0, 0,      345, 1.0, 1.0, 1.5, 0.51, 0.00625, 0, 0, 0, '1', 0.0", "     3, 1, 25.0, 15.0, 0, 0,      345, 1.0, 1.0, 1.5, 0.51, 0.00625, 0, 0, 0, '1', 2.0"));
            CliProcess.Result refused = CliProcess.RunBuilt(raw, "--once");
            Assert.True(refused.ExitCode == 1, refused.ToString());
            Assert.Contains("capacitor-commutated two-terminal DC link(s)", refused.StdErr, StringComparison.Ordinal);
            Assert.Contains("--allow-unmodelled", refused.StdErr, StringComparison.Ordinal);

            CliProcess.Result allowed = CliProcess.RunBuilt(raw, "--once", "--allow-unmodelled");
            Assert.True(allowed.ExitCode == 0, allowed.ToString());
            Assert.Contains("in-service capacitor-commutated two-terminal DC link(s) read past and NOT modelled", allowed.StdErr, StringComparison.Ordinal);
            Assert.DoesNotContain("had no effect", allowed.StdErr, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Allow_unmodelled_with_no_raw_file_says_it_had_no_effect()
    {
        CliProcess.Result r = CliProcess.RunBuilt("case14", "--once", "--allow-unmodelled");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("warning: --allow-unmodelled had no effect: this run loaded no PSS/E .raw file.", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stated_base_frequency_and_a_different_declared_one_are_refused()
    {
        CliProcess.Result r = CliProcess.RunBuilt(Fixture("sample-v35.raw"), "--once", "--frequency-hz", "60");
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains("the file's base frequency is 50 Hz but 60 Hz was asked for", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_raw_load_goes_through_one_place()
    {
        Assert.DoesNotContain("PsseRawParser", RepoFile("src/GridSim.Cli/Program.cs"), StringComparison.Ordinal);
        Assert.Contains("PsseRawParser.ParseFile(path,", RepoFile("src/GridSim.Cli/Gated.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Raw_input_and_the_flag_are_in_help_and_the_cli_reference()
    {
        CliProcess.Result help = CliProcess.RunBuilt("--help");
        Assert.True(help.ExitCode == 0, help.ToString());
        // Test changed 2026-10-24 (revision 30 support): the help must name every revision read, 30 included.
        Assert.Contains("a PSS/E .raw file (revisions 30, 33, 34, 35;", help.StdOut, StringComparison.Ordinal);
        Assert.Contains("--allow-unmodelled", help.StdOut, StringComparison.Ordinal);
        string doc = RepoFile("docs/manual/0019 CLI reference.md");
        Assert.Contains("--allow-unmodelled", doc, StringComparison.Ordinal);
        // Test changed 2026-10-24 (UCTE-DEF reader): convert's usage now names .uct beside .raw; the .raw input is still
        // required in it.
        Assert.Contains("gridsim convert <input.m|input.raw|input.uct|case-dir|case>", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dyr_file_is_dynamics_data_for_its_raw_case()
    {
        CliProcess.Result r = CliProcess.RunBuilt("modal", Fixture("case14.raw"), "--dyn-data", Fixture("case14.dyr"));
        Assert.True(r.ExitCode is 0 or 1, r.ToString());   // 1 would be a right-half-plane mode, still a finished result
        Assert.Contains("PSS/E DYR case14", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("error", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dyr_file_on_a_case_that_is_not_its_raw_file_is_refused()
    {
        CliProcess.Result r = CliProcess.RunBuilt("modal", Path.Combine(CliProcess.Root(), "data", "matpower", "lib", "case14.m"), "--dyn-data", Fixture("case14.dyr"));
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains("is a PSS/E DYR file, which names machines by the bus and ID of a PSS/E .raw case", r.StdErr, StringComparison.Ordinal);
    }
    // ---- Revision 30 and SEQ (Tranche A, 2026-10-24) ------------------------------------------------------------------

    [Fact]
    public void A_revision_30_file_without_a_rev_field_runs_with_psse_revision()
    {
        CliProcess.Result refused = CliProcess.RunBuilt(Fixture("ukgds-style-v30.raw"), "--once", "--frequency-hz", "50");
        Assert.True(refused.ExitCode == 1, refused.ToString());
        Assert.Contains("the file does not say its PSS/E revision", refused.StdErr, StringComparison.Ordinal);

        CliProcess.Result r = CliProcess.RunBuilt(Fixture("ukgds-style-v30.raw"), "--once", "--frequency-hz", "50", "--psse-revision", "30");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("note: ukgds-style-v30.raw: 1 bus record(s) carry a shunt (GL, BL; revision 30)", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("had no effect", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Seq_data_gives_iec60909_its_earth_paths()
    {
        string[] common = { "iec60909", Fixture("ukgds-style-v30.raw"), "--bus", "3", "--type", "slg", "--frequency-hz", "50", "--psse-revision", "30" };
        // Without sequence data the case has no transformer winding connections: no earthed neutral, exit 3.
        CliProcess.Result bare = CliProcess.RunBuilt(common);
        Assert.True(bare.ExitCode == 3, bare.ToString());

        // The SEQ file holds the grid infeed's zero-sequence impedance. 2026-10-25: modelled now (device item H), so the
        // file loads without --allow-unmodelled (it used to be refused, exit 1); with the flag it changes nothing.
        CliProcess.Result plain = CliProcess.RunBuilt(common.Concat(new[] { "--seq-data", Fixture("ukgds-style-v30.seq") }).ToArray());
        Assert.True(plain.ExitCode == 0, plain.ToString());
        Assert.Contains("1 machine(s) have a zero-sequence impedance (RZERO, XZERO)", plain.StdErr, StringComparison.Ordinal);

        CliProcess.Result r = CliProcess.RunBuilt(common.Concat(new[] { "--seq-data", Fixture("ukgds-style-v30.seq"), "--allow-unmodelled" }).ToArray());
        Assert.Equal(plain.StdOut, r.StdOut);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("note: ukgds-style-v30.seq: applied sequence data: 2 machine X''d, 2 machine X2, 2 line Z0, 3 transformer Z0 and winding connections.", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Psse_revision_and_seq_data_values_are_checked_and_say_when_they_reached_nothing()
    {
        foreach (string[] args in new[]
                 {
                     new[] { "case14", "--once", "--psse-revision", "31" },
                     new[] { "case14", "--once", "--psse-revision", "x" },
                     new[] { "case14", "--once", "--psse-revision" },
                     new[] { "case14", "--once", "--seq-data", "grid.txt" },
                     new[] { "case14", "--once", "--seq-data", "a.seq", "--seq-data", "b.seq" },
                 })
        {
            CliProcess.Result bad = CliProcess.RunBuilt(args);
            Assert.True(bad.ExitCode == 2, string.Join(" ", args) + ": " + bad);
        }
        CliProcess.Result r = CliProcess.RunBuilt("case14", "--once", "--psse-revision", "30");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("warning: --psse-revision had no effect: this run loaded no PSS/E .raw file.", r.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Psse_revision_and_seq_data_are_in_help_and_the_cli_reference()
    {
        CliProcess.Result help = CliProcess.RunBuilt("--help");
        Assert.Contains("--psse-revision <n> for a file that does not say; --seq-data <file.seq> adds its sequence data", help.StdOut, StringComparison.Ordinal);
        string doc = RepoFile("docs/manual/0019 CLI reference.md");
        Assert.Contains("| `--psse-revision <n>` |", doc, StringComparison.Ordinal);
        Assert.Contains("| `--seq-data <file.seq>` |", doc, StringComparison.Ordinal);
        Assert.Contains("### Sequence data (SEQ)", RepoFile("docs/manual/0002 Loading networks.md"), StringComparison.Ordinal);
    }
}
