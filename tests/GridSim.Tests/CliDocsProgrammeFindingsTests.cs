using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Source-level anti-drift checks for the CLI fixes made from the October 2026 docs-programme findings. The test
/// project does not reference the CLI, so, like <c>CliExitCodeContractTests</c>, these read src/GridSim.Cli. Each
/// test names the defect it stops coming back. (The behaviour itself was checked by running the CLI; see the commit.)
/// </summary>
public class CliDocsProgrammeFindingsTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }

    private static string Cli(string file)
    {
        string path = Path.Combine(Root(), "src", "GridSim.Cli", file);
        Assert.True(File.Exists(path), $"CLI source not found: {path}");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    /// <summary>The body of a local function, from its signature to the next blank line followed by a non-indented
    /// line (good enough for the short functions checked here). Fails loudly if the signature is missing.</summary>
    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' not found");
        int end = src.IndexOf("\n}\n", at, StringComparison.Ordinal);
        Assert.True(end > at, $"end of '{signature}' not found");
        return src[at..end];
    }

    [Fact]
    public void A_missing_case_file_or_folder_exits_1_not_with_a_crash()
    {
        // A missing .m file or --dir folder escaped as an unhandled exception (stack trace, exit -532462766).
        string gated = Cli("Gated.cs");
        Assert.Contains("RequireDirectory(path);", Body(gated, "public static GridModel Dir(string path)"));
        Assert.Contains("RequireFile(path, \"MATPOWER case file\");", Body(gated, "public static GridModel Matpower(string path)"));
        // A case that exists but cannot be parsed (a folder with no buses.json, a malformed .m) is an input error
        // too: wrapped as CaseLoadException (an IOException, so batch verbs still catch it) and exit 1 if uncaught.
        Assert.Contains("Unloadable(path, \"case folder\"", Body(gated, "public static GridModel Dir(string path)"));
        Assert.Contains("Unloadable(path, \"MATPOWER case file\"", Body(gated, "public static GridModel Matpower(string path)"));
        Assert.Contains("internal sealed class CaseLoadException : IOException", gated);

        // The process-wide net converts an uncaught missing-path exception into a message and exit 1, and it is
        // registered before any verb runs (before the --audit-log handler and before the first verb dispatch).
        string program = Cli("Program.cs");
        // Test changed 2026-10-19: 1.95.343 (GR-672) added a "!missingAssembly &&" guard to the net's condition, so a
        // missing ASSEMBLY (a broken install, a defect) still crashes loudly; the missing-path net itself is unchanged.
        int net = program.IndexOf("if (!missingAssembly && e.ExceptionObject is FileNotFoundException or DirectoryNotFoundException or CaseLoadException)", StringComparison.Ordinal);
        Assert.True(net >= 0, "the missing-path UnhandledException net is gone");
        int exit1 = program.IndexOf("Environment.Exit(1);", net, StringComparison.Ordinal);
        Assert.True(exit1 > net && exit1 - net < 400, "the missing-path net must exit 1");
        int audit = program.IndexOf("recorder.SetOutcome($\"unhandled=", StringComparison.Ordinal);
        int firstVerb = program.IndexOf("rest[0].Equals(\"freq\"", StringComparison.Ordinal);
        Assert.True(audit > net && firstVerb > net, "the missing-path net must be registered before the audit handler and every verb");
    }

    [Fact]
    public void A_non_converged_case9_report_keeps_exit_code_3()
    {
        // PrintAndExitCase9 called Environment.Exit(0) after PrintDetailed had set exit code 3.
        string body = Body(Cli("Program.cs"), "GridModel PrintAndExitCase9()");
        Assert.DoesNotContain("Environment.Exit(0);", body);   // the statement (the comment may mention the old call)
        Assert.Contains("Environment.Exit(Environment.ExitCode)", body);
    }

    [Fact]
    public void The_report_totals_use_generator_output_not_positive_injections()
    {
        // "Generation" was r.PInjMw.Where(p => p > 0).Sum(): case14 read 0.25 GW against 0.26 GW demand + losses.
        string program = Cli("Program.cs");
        string totals = Body(program, "static (double gen, double load) Totals(");
        Assert.Contains("SolvedGeneration.GeneratorOutputMw(m, r)", totals);
        Assert.DoesNotMatch(new Regex(@"PInjMw\.Where\(\s*p\s*=>\s*p\s*>\s*0\s*\)"), program);
        Assert.Contains("Generator output {GenOutText(r, gen)}", program);
        // On a non-converged state the figure is "n/a", never a number read off a transient iterate.
        Assert.Contains("r.Converged ? (genMw / 1000.0).ToString(\"F2\"", Body(program, "static string GenOutText("));
    }

    [Fact]
    public void An_explicit_ticks_of_zero_or_less_is_refused()
    {
        // "--ticks 0" (or negative) silently meant interactive, so a script passing 0 hung waiting for a key.
        string program = Cli("Program.cs");
        int parse = program.IndexOf("int ti = rest.IndexOf(\"--ticks\");", StringComparison.Ordinal);
        Assert.True(parse >= 0, "--ticks parse not found");
        int guard = program.IndexOf("if (ticks < 1)", parse, StringComparison.Ordinal);
        Assert.True(guard > parse && guard - parse < 1500, "--ticks must refuse a value below 1 right where it is parsed");
    }

    [Fact]
    public void Help_points_at_the_real_cli_reference()
    {
        // --help said "See docs/cli-reference", a stub. The full reference is docs/manual/0019 CLI reference.md.
        string program = Cli("Program.cs");
        Assert.Contains("See docs/manual/0019 CLI reference.md", program);
        Assert.DoesNotContain("See docs/cli-reference ", program);
        Assert.True(File.Exists(Path.Combine(Root(), "docs", "manual", "0019 CLI reference.md")));
    }
}
