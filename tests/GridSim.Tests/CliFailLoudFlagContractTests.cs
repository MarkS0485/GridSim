using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// WS8 (October 2026 review) CLI fail-loud parsing: R215, R216, R217, R219, R222. The test project does not reference
/// the CLI, so like <see cref="CliExitCodeContractTests"/> these are source-level anti-drift checks over
/// src/GridSim.Cli: they fail if a flag helper goes back to silently substituting a default.
/// </summary>
public class CliFailLoudFlagContractTests
{
    private static string RepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        string path = Path.Combine(dir!.FullName, rel);
        Assert.True(File.Exists(path), $"source not found: {path}");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static string Program() => RepoFile("src/GridSim.Cli/Program.cs");

    /// <summary>The body of a top-level static helper, from its signature to the next blank line.</summary>
    private static string Body(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"helper not found: {signature}");
        int end = src.IndexOf("\n\n", i, StringComparison.Ordinal);
        return src[i..(end < 0 ? src.Length : end)];
    }

    [Theory]
    [InlineData("static double FlagDouble(")]
    [InlineData("static int FlagInt(")]
    [InlineData("static string? FlagString(")]
    public void A_present_flag_with_no_value_fails_loud(string signature)
    {
        // R219: only an ABSENT flag may take the default. The old helpers returned the default when the flag was the
        // last token ("i + 1 >= a.Count) return dflt") or when FlagString's next token was another flag.
        string body = Body(Program(), signature);
        Assert.Contains("FlagValueMissing(a, i)", body);
        Assert.Contains("FailFlag<", body);
        Assert.DoesNotContain("i + 1 >= a.Count) return", body);
    }

    [Fact]
    public void A_missing_value_means_last_token_or_another_double_dash_flag()
    {
        string body = Body(Program(), "static bool FlagValueMissing(");
        Assert.Contains("i + 1 >= a.Count", body);
        Assert.Contains("StartsWith(\"--\"", body);
    }

    [Fact]
    public void Number_flags_refuse_NaN_and_infinity()
    {
        // R219: double.TryParse accepts "NaN" and "Infinity"; FlagDouble must also demand a finite value.
        Assert.Contains("double.IsFinite(v)", Body(Program(), "static double FlagDouble("));
    }

    [Theory]
    [InlineData("--rule", "trap or be")]
    [InlineData("--converter", "gfl or gfm")]
    [InlineData("--criterion", "siso or mimo")]
    public void Enum_flags_reject_unknown_values(string flag, string expected)
    {
        // R217: each enum flag is a switch whose default arm is FailFlag, like --solver and --method.
        Assert.Matches(new Regex($@"FailFlag<\w+>\(""{Regex.Escape(flag)}"", bad, ""{Regex.Escape(expected)}""\)"), Program());
    }

    [Fact]
    public void Harmonics_bus_flag_is_parsed_fail_loud()
    {
        // R217: an unparseable --bus used to fall back silently to the worst-THD bus via int.TryParse.
        string src = Program();
        Assert.Contains("int busFlag = FlagInt(a, \"--bus\", int.MinValue);", src);
        Assert.DoesNotContain("int.TryParse(a[busArg + 1]", src);
    }

    [Fact]
    public void Unknown_gpu_vendor_fails_loud()
    {
        // R222: --gpu=rcom (a typo) used to fall into AMD-first auto-detect.
        Assert.Contains("SolverBackend.IsKnownSpec(gpuSpec)", Program());
        string backend = RepoFile("src/GridSim.Cli/SolverBackend.cs");
        Assert.Contains("public static bool IsKnownSpec(", backend);
        Assert.Contains("throw new ArgumentException($\"Unknown GPU backend", backend);
    }

    [Fact]
    public void Transient_refuses_non_positive_step_and_duration()
    {
        // R216: --dt-ms 0 ran forever; a negative step or --minutes 0 reported 'nadir 999 Hz' with exit 0.
        string src = Program();
        Assert.Contains("if (!(dtMs > 0)) FailFlag<double>(\"--dt-ms\"", src);
        Assert.Contains("if (!(minutes > 0)) FailFlag<double>(\"--minutes\"", src);
        Assert.Contains("is less than one slice - nothing to run", src);
        Assert.DoesNotContain("double nadir = 999", src);
    }

    [Fact]
    public void Batch_verbs_do_not_exit_0_when_some_cases_fail_to_load()
    {
        // R215: transient, nminus1-sweep --all and stress --all only failed when every case failed to load.
        string src = Program();
        Assert.Contains("else if (failed > 0)", src);
        Assert.Equal(2, Regex.Count(src, @"else if \(loadFailed > 0\)"));
        Assert.Equal(2, Regex.Count(src, @"LOAD FAIL: \{ex\.Message\}[^\n]*loadFailed\+\+; continue;"));
    }

    [Fact]
    public void Crossval_skip_is_exit_77_and_a_python_exit_2_is_a_failure()
    {
        // R218: the sidecar signalled "pandapower absent" with exit 2, which Python also uses for argparse usage
        // errors and "can't open file", so an argument mismatch read as an environment skip (77).
        string py = RepoFile("tools/crossval/crossval.py");
        Assert.Contains("EXIT_DEPENDENCY_ABSENT = 77", py);
        Assert.Contains("return EXIT_DEPENDENCY_ABSENT", py);
        Assert.DoesNotContain("return 2", py);

        string src = Program();
        int skip = src.IndexOf("if (code == 77)", StringComparison.Ordinal);
        int fail2 = src.IndexOf("if (code == 2)", StringComparison.Ordinal);
        Assert.True(skip >= 0 && fail2 > skip, "crossval verb must test 77 (skip) before treating 2 as a failure");
        string twoArm = src[fail2..src.IndexOf("return;", fail2, StringComparison.Ordinal)];
        Assert.Contains("Environment.ExitCode = 3;", twoArm);
        Assert.DoesNotContain("77", twoArm);
    }

    [Fact]
    public void Run_gpu_script_cannot_hang_and_diffs_cpu_against_gpu()
    {
        // R057: bare case118 / case300 / .m calls started the interactive live loop (hung forever with stdout
        // redirected) and the script never compared the outputs it wrote.
        string sh = RepoFile("tools/run-gpu.sh");
        Assert.Contains("--enableExport=", sh);
        Assert.DoesNotMatch(new Regex(@"\$CLI \$c --(cpu|gpu) >"), sh);
        Assert.Contains("max|dVm|", sh);
        Assert.Contains("GPU VALIDATION FAILED", sh);
    }

    [Fact]
    public void Every_matpower_load_is_unit_normalised_in_one_place()
    {
        // R021: the raw-unit repair was wrapped around some .m load sites and not others, so crossval, stress, chain,
        // nminus1-sweep, cycle, losses and bench solved a raw ohm/kW feeder un-normalised. Gated.Matpower now
        // normalises every .m load; no call site may parse or normalise on its own.
        string src = Program();
        Assert.DoesNotContain("MatpowerUnitNormalizer", src);
        Assert.DoesNotContain("MatpowerParser.Parse", src);
        // Moved 2026-10 (A8, final batch; D2 N187, Ruling R1 and Mark's go on the final-batch moves 2026-10-10): the
        // one-place rule still holds; D2 added the frequency argument for --frequency-hz. Before -> after:
        // "MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(path))" ->
        // "MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(path, frequencyHz: frequencyHz))".
        Assert.Contains("MatpowerUnitNormalizer.Normalize(MatpowerParser.ParseFile(path, frequencyHz: frequencyHz))", RepoFile("src/GridSim.Cli/Gated.cs"));
    }
}
