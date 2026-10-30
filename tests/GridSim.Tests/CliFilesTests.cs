using GridSim.Cli;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-8, D-DOC-072 and D-DOC-176 (October 2026): the one output policy of the command line (src/GridSim.Cli/CliFiles.cs,
/// compiled into the tests), driven directly. The rule (user manual ch. 19, "Output files"): an output's parent folder is
/// created; an existing output is replaced; never an input of the run (a file the command line names, a file of a case
/// folder it names, a file of the bundled data folder) and never a folder.
///
/// CliFiles holds process-wide state (the run's inputs), so every test here sets it with Init and resets it at the end;
/// they live in one class so xUnit runs them one after another.
/// </summary>
public sealed class CliFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gridsim-clifiles-" + Guid.NewGuid().ToString("N"));

    public CliFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        CliFiles.Init(Array.Empty<string>());   // leave no inputs recorded for any other test
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Touch(string relative, string text = "x")
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void A_file_named_on_the_command_line_is_an_input_and_is_never_written()
    {
        string m = Touch("case9.m");
        CliFiles.Init(new[] { "pf", m, "--out", Path.Combine(_dir, "out.csv") });
        var ex = Assert.Throws<OutputRefusedException>(() => CliFiles.Output(m, "result file"));
        Assert.Contains("is an input of this run", ex.Message, StringComparison.Ordinal);
        Assert.Contains("refusing to write the result file", ex.Message, StringComparison.Ordinal);
        Assert.Equal("x", File.ReadAllText(m));   // untouched
    }

    [Fact]
    public void The_value_of_an_output_flag_is_an_output_and_an_existing_one_is_replaced()
    {
        string out1 = Touch("old-report.md", "old");
        string out2 = Touch("old.json", "old");
        CliFiles.Init(new[] { "bench", "--out", out1, "--enableExport=" + out2 });
        Assert.Null(CliFiles.WhyRefused(out1));
        Assert.Null(CliFiles.WhyRefused(out2));
        using (StreamWriter w = CliFiles.CreateText(out1, "bench report")) w.Write("new");
        Assert.Equal("new", File.ReadAllText(out1));
    }

    [Fact]
    public void Flag_values_given_with_an_equals_sign_and_comma_lists_are_inputs_too()
    {
        string a = Touch("a.m"), b = Touch("b.m"), c = Touch("c.json");
        CliFiles.Init(new[] { "bench", "--cases", $"{a},{b}", $"--costs={c}" });
        Assert.NotNull(CliFiles.WhyRefused(a));
        Assert.NotNull(CliFiles.WhyRefused(b));
        Assert.NotNull(CliFiles.WhyRefused(c));
    }

    [Fact]
    public void A_flag_after_an_output_flag_is_not_skipped_so_a_case_after_it_stays_an_input()
    {
        // modal --json takes no value: the next token is a flag here, and the case file after that is an input.
        string m = Touch("case.m");
        CliFiles.Init(new[] { "modal", "--json", "--damping", "1", m });
        Assert.NotNull(CliFiles.WhyRefused(m));
    }

    [Fact]
    public void Existing_files_of_a_named_case_folder_are_inputs_but_a_new_file_there_may_be_written()
    {
        string sys = Touch(Path.Combine("mycase", "system.json"), "{}");
        string buses = Touch(Path.Combine("mycase", "buses.json"), "[]");
        string folder = Path.GetDirectoryName(sys)!;
        CliFiles.Init(new[] { "screen", folder });
        Assert.Contains("case folder", CliFiles.WhyRefused(buses)!, StringComparison.Ordinal);
        Assert.NotNull(CliFiles.WhyRefused(sys));
        Assert.Null(CliFiles.WhyRefused(Path.Combine(folder, "screen.csv")));   // not there yet: an output may go there
    }

    [Fact]
    public void A_folder_without_system_json_is_not_a_case_folder()
    {
        // A results folder named as an input (estimate-noise-floor reads a folder of exports) does not freeze its files,
        // or a re-run could never replace its own outputs there.
        string export = Touch(Path.Combine("runs", "estimate-export.json"), "{}");
        CliFiles.Init(new[] { "estimate-noise-floor", Path.GetDirectoryName(export)! });
        Assert.Null(CliFiles.WhyRefused(export));
    }

    [Fact]
    public void Existing_files_of_the_bundled_data_folder_are_always_inputs()
    {
        string bundled = Touch(Path.Combine("data", "uk", "buses.json"), "[]");
        CliFiles.Init(new[] { "screen", "uk" }, Path.Combine(_dir, "data"));
        Assert.Contains("bundled data folder", CliFiles.WhyRefused(bundled)!, StringComparison.Ordinal);
        Assert.Null(CliFiles.WhyRefused(Path.Combine(_dir, "data", "new-output.csv")));
    }

    [Fact]
    public void A_folder_is_never_written_as_a_file()
    {
        CliFiles.Init(Array.Empty<string>());
        Assert.Contains("existing folder", CliFiles.WhyRefused(_dir)!, StringComparison.Ordinal);
        Assert.Throws<OutputRefusedException>(() => CliFiles.Output(_dir, "report"));
    }

    [Fact]
    public void D176_a_missing_parent_folder_is_created()
    {
        CliFiles.Init(Array.Empty<string>());
        string deep = Path.Combine(_dir, "not", "there", "yet", "trip.csv");
        string full = CliFiles.Output(deep, "trip trajectory CSV");
        Assert.Equal(Path.GetFullPath(deep), full);
        Assert.True(Directory.Exists(Path.GetDirectoryName(deep)));
        Assert.False(File.Exists(deep));   // Output only prepares; the caller writes
    }

    [Fact]
    public void A_run_folder_is_created_and_an_existing_file_is_refused_as_one()
    {
        CliFiles.Init(Array.Empty<string>());
        string folder = Path.Combine(_dir, "run1");
        Assert.Equal(Path.GetFullPath(folder), CliFiles.OutputFolder(folder, "output folder"));
        Assert.True(Directory.Exists(folder));
        string file = Touch("not-a-folder");
        var ex = Assert.Throws<OutputRefusedException>(() => CliFiles.OutputFolder(file, "output folder"));
        Assert.Contains("is an existing file, not a folder", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_output_flags_are_the_ones_the_cli_writes_through()
    {
        // A change to this list changes which existing files a run may replace; it is documented in ch. 19.
        Assert.Equal(
            new[] { "--audit-anchor", "--audit-log", "--csv", "--dump-catalog", "--enableExport", "--export", "--json", "--out", "--out-store", "--pv-out" },
            CliFiles.OutputFlags.Order(StringComparer.Ordinal).ToArray());
    }
}
