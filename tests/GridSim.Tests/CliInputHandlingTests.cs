using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Source-level anti-drift checks for the CLI input-handling and fail-loud fixes made from the October 2026
/// docs-programme findings (positional case ignored, --dir ignored, bare .m names, empty stress range, chain footer,
/// per-verb flag checks, estimate and harmonics UX). The test project does not reference the CLI, so, like
/// <c>CliDocsProgrammeFindingsTests</c>, these read src/GridSim.Cli. The behaviour itself was checked by running the
/// CLI by hand; the before/after output is quoted in NOTES-FOR-MAIN.md.
/// </summary>
public class CliInputHandlingTests
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

    /// <summary>The text of one top-level dispatch block: from its verb test to the next "return;\n}".</summary>
    private static string Dispatch(string src, string verbTest)
    {
        int at = src.IndexOf(verbTest, StringComparison.Ordinal);
        Assert.True(at >= 0, $"dispatch '{verbTest}' not found");
        int end = src.IndexOf("return;\n}", at, StringComparison.Ordinal);
        Assert.True(end > at, $"end of dispatch '{verbTest}' not found");
        return src[at..end];
    }

    /// <summary>The body of a local function, from its signature to the next "\n}\n".</summary>
    private static string Body(string src, string signature)
    {
        int at = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{signature}' not found");
        int end = src.IndexOf("\n}\n", at, StringComparison.Ordinal);
        Assert.True(end > at, $"end of '{signature}' not found");
        return src[at..end];
    }

    [Theory]
    [InlineData("rest[0].Equals(\"nminus1\", StringComparison.OrdinalIgnoreCase)", "\"nminus1\"")]
    [InlineData("rest[0].Equals(\"nminus2\", StringComparison.OrdinalIgnoreCase)", "\"nminus2\"")]
    [InlineData("rest[0].Equals(\"n-1-1\", StringComparison.OrdinalIgnoreCase)", "\"n-1-1\"")]
    [InlineData("rest[0].Equals(\"screen\", StringComparison.OrdinalIgnoreCase)", "\"screen\"")]
    [InlineData("rest[0].Equals(\"trip\", StringComparison.OrdinalIgnoreCase)", "\"trip\"")]
    [InlineData("rest[0].Equals(\"domains\", StringComparison.OrdinalIgnoreCase)", "\"domains\"")]
    [InlineData("rest[0].Equals(\"export\", StringComparison.OrdinalIgnoreCase)", "\"export\"")]
    public void The_gb_default_verbs_load_the_positional_case_through_SelectCase(string verbTest, string verbName)
    {
        // SERIOUS finding: these verbs read only --dir and otherwise loaded data/uk, so "nminus1 case14" ran GB.
        string block = Dispatch(Cli("Program.cs"), verbTest);
        Assert.Contains($"SelectCase({verbName}", block);
        Assert.Contains("LoadSelected(", block);
        Assert.DoesNotContain("LoadDirOrUk", block);
    }

    [Fact]
    public void The_dir_only_loader_is_gone()
    {
        Assert.DoesNotContain("LoadDirOrUk", Cli("Program.cs"));
    }

    [Fact]
    public void SelectCase_refuses_a_disagreeing_pair_and_a_missing_folder_up_front()
    {
        string body = Body(Cli("Program.cs"), "(string Label, Func<GridModel> Load) SelectCase(");
        // Both given and naming different networks: usage error, exit 2.
        int both = body.IndexOf("if (dir is not null && caseArg is not null)", StringComparison.Ordinal);
        Assert.True(both >= 0);
        Assert.True(body.IndexOf("Environment.Exit(2);", both, StringComparison.Ordinal) > both);
        // A missing --dir folder: input error, exit 1, with a message, before any output is opened.
        int missing = body.IndexOf("if (!Directory.Exists(dir))", StringComparison.Ordinal);
        Assert.True(missing > both);
        Assert.Contains("case folder not found", body);
        Assert.True(body.IndexOf("Environment.Exit(1);", missing, StringComparison.Ordinal) > missing);
        // A present-but-valueless --dir is a usage error, never a silent fall-back to the default.
        Assert.Contains("FailFlag<string>(\"--dir\", \"\", \"a directory path\")", body);
    }

    [Theory]
    [InlineData("void RunNMinus1Sweep(", "SelectBatchCases(\"nminus1-sweep\"")]
    [InlineData("void RunStress(", "SelectBatchCases(\"stress\"")]
    [InlineData("void RunChain(", "SelectCase(\"chain\"")]
    [InlineData("void RunCycle(", "SelectCase(\"cycle\"")]
    [InlineData("void RunEmt(", "SelectCase(\"emt\"")]
    public void The_positional_case_verbs_honour_dir(string signature, string selection)
    {
        // Mirror finding: nminus1-sweep, stress and chain accepted --dir and ignored it.
        string body = Body(Cli("Program.cs"), signature);
        int sel = body.IndexOf(selection, StringComparison.Ordinal);
        Assert.True(sel >= 0, $"{signature} must select its case with {selection}");
        // The selection is made before any report file is opened, so a refused run leaves no stub files behind.
        int writer = body.IndexOf("new StreamWriter(", StringComparison.Ordinal);
        Assert.True(writer < 0 || sel < writer, $"{signature}: case selection must come before the first output file");
    }

    [Fact]
    public void The_chain_storage_path_honours_dir_too()
    {
        string block = Dispatch(Cli("Program.cs"), "rest[0].Equals(\"chain\", StringComparison.OrdinalIgnoreCase)");
        Assert.Contains("SelectCase(\"chain\"", block);
        Assert.DoesNotContain("PositionalArg(rest) ?? \"case118\"", block);
    }

    [Fact]
    public void Trip_finds_the_generator_name_after_a_flag()
    {
        // "trip --dir data/uk Torness" tripped Hinkley: the name was read from rest[1] only.
        string block = Dispatch(Cli("Program.cs"), "rest[0].Equals(\"trip\", StringComparison.OrdinalIgnoreCase)");
        Assert.Contains("Positionals(rest)", block);
        Assert.DoesNotContain("rest.Count > 1 && !rest[1].StartsWith('-') ? rest[1]", block);
        // An unknown name still exits 2 naming it.
        string trip = Body(Cli("Program.cs"), "void RunTrip(");
        Assert.Contains("matched no generator", trip);
    }

    [Fact]
    public void A_bare_matpower_name_is_searched_for_under_data_matpower()
    {
        string gated = Cli("Gated.cs");
        Assert.Contains("path = ResolveMatpower(path);", gated);
        Assert.Contains("SearchOption.AllDirectories", gated);
        // Deterministic order, and an ambiguous name is refused with exit 2 listing the candidates.
        Assert.Contains(".OrderBy(f => Path.GetRelativePath(library, f), StringComparer.Ordinal)", gated);
        int amb = gated.IndexOf("is ambiguous", StringComparison.Ordinal);
        Assert.True(amb >= 0);
        Assert.True(gated.IndexOf("Environment.Exit(2);", amb, StringComparison.Ordinal) > amb);
        // Every shipped .m file really is in a subfolder (the reason the old top-level-only fallback was dead).
        string lib = Path.Combine(Root(), "data", "matpower");
        Assert.Empty(Directory.GetFiles(lib, "*.m", SearchOption.TopDirectoryOnly));
        Assert.NotEmpty(Directory.GetFiles(lib, "*.m", SearchOption.AllDirectories));
        // And no two shipped .m files share a name, so every bare name resolves today.
        var names = Directory.GetFiles(lib, "*.m", SearchOption.AllDirectories).Select(f => Path.GetFileName(f).ToLowerInvariant()).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void The_built_in_case_name_set_matches_LoadNamed()
    {
        // IsCaseArgument (trip/export) asks BuiltInCases.Names; it must list exactly LoadNamed's switch labels.
        string src = Cli("Program.cs");
        int at = src.IndexOf("GridModel LoadNamed(string name, string root) =>", StringComparison.Ordinal);
        Assert.True(at >= 0, "LoadNamed not found");
        string body = src[at..src.IndexOf("\n};\n", at, StringComparison.Ordinal)];   // an expression-bodied switch
        var labels = Regex.Matches(body, "\"([a-z0-9-]+)\"(?=\\s*(or|=>))").Select(m => m.Groups[1].Value).ToHashSet();
        string gated = Cli("Gated.cs");
        int set = gated.IndexOf("internal static class BuiltInCases", StringComparison.Ordinal);
        Assert.True(set >= 0);
        string setBody = gated[set..gated.IndexOf("};", set, StringComparison.Ordinal)];
        var names = Regex.Matches(setBody, "\"([a-z0-9-]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Equal(labels.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Stress_refuses_an_empty_load_range()
    {
        string body = Body(Cli("Program.cs"), "void RunStress(");
        int guard = body.IndexOf("if (from > to)", StringComparison.Ordinal);
        Assert.True(guard >= 0, "stress must refuse --from above --to");
        Assert.True(body.IndexOf("Environment.Exit(2);", guard, StringComparison.Ordinal) > guard);
        Assert.True(guard < body.IndexOf("new StreamWriter(", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("void RunReplayEvent(", "RequireVerbFlags(\"replay-event\"")]
    [InlineData("void RunScenario(", "RequireVerbFlags(\"scenario\"")]
    [InlineData("void RunH0Study(", "RequireVerbFlags(\"h0-study\"")]
    [InlineData("void RunEvidence(", "RequireVerbFlags(\"evidence\"")]
    [InlineData("void RunFleet(", "RequireVerbFlags(\"fleet\"")]
    public void The_event_verbs_refuse_flags_they_do_not_use(string signature, string check)
    {
        // "fleet --device-ffr", "evidence --corners", "replay-event --device-ffr" were accepted and ignored.
        string src = Cli("Program.cs");
        string body = Body(src, signature);
        int at = body.IndexOf(check, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{signature} must check its flags with {check}");
        // The check comes first, before the verb reads any flag or prints usage.
        int firstFlagRead = body.IndexOf("FlagString(a,", StringComparison.Ordinal);
        Assert.True(firstFlagRead < 0 || at < firstFlagRead, $"{signature}: the flag check must come before the first flag is read");
        Assert.Contains("RefuseIgnoredGlobalSwitches(", body);

        // Every flag the verb reads is in its accepted list (so the check cannot refuse a flag the verb consumes).
        int open = at + check.Length;
        string list = body[open..body.IndexOf(");", open, StringComparison.Ordinal)];
        var accepted = Regex.Matches(list, "\"(--[a-z0-9-]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        var read = Regex.Matches(body, "Flag(?:String|Double|Int)\\(a, \"(--[a-z0-9-]+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        foreach (string f in read) Assert.True(accepted.Contains(f), $"{signature} reads {f} but does not accept it");
        // And every accepted flag is a known flag of the global gate.
        foreach (string f in accepted) Assert.Contains($"\"{f}\",", src);
    }

    [Fact]
    public void RequireVerbFlags_exits_2_naming_the_flag()
    {
        string body = Body(Cli("Program.cs"), "static void RequireVerbFlags(");
        Assert.Contains("does not use {tok}", body);
        Assert.Contains("Environment.Exit(2);", body);
    }

    [Fact]
    public void A_trace_shorter_than_the_window_is_not_scored_against_its_last_sample()
    {
        string core = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Core", "Replay", "ReplayValidator.cs")).Replace("\r\n", "\n");
        Assert.Contains("if ((recTime - traceLast).TotalSeconds > halfStepS) { pastEnd++; continue; }", core);
        Assert.Contains("does not contain the event instant", core);
        // The CLI prints the note.
        Assert.Contains("WARNING: {traceNote}", Body(Cli("Program.cs"), "void RunReplayEvent("));
    }

    [Fact]
    public void Trip_labels_the_peak_rocof_as_a_peak()
    {
        string trip = Body(Cli("Program.cs"), "void RunTrip(");
        Assert.DoesNotContain("\"Initial RoCoF:", trip.Replace("$\"", "\""));
        Assert.Contains("Peak RoCoF:", trip);
    }

    [Fact]
    public void Estimate_usage_lists_every_flag_the_verb_reads()
    {
        // The banner omitted --method, --prior, --warm-start, --backfill-removals, --lean, --trace, --export-solve and
        // --phaseangle-from-gda (and --hti / --fdia-screen in places). Every flag RunEstimate reads must be in it.
        string body = Body(Cli("Program.cs"), "void RunEstimate(");
        int u = body.IndexOf("usage: gridsim estimate", StringComparison.Ordinal);
        Assert.True(u >= 0);
        string usage = body[u..body.IndexOf("Environment.ExitCode = 2;", u, StringComparison.Ordinal)];
        var read = Regex.Matches(body, "(?:Flag(?:String|Double|Int)\\(a, |a\\.Contains\\(|GatherContiguousValues\\(a, )\"(--[a-z0-9-]+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet();
        Assert.NotEmpty(read);
        foreach (string f in read) Assert.True(usage.Contains(f, StringComparison.Ordinal), $"estimate usage omits {f}");
    }

    [Fact]
    public void Estimate_refuses_synth_with_a_measurement_file_and_names_its_method()
    {
        string body = Body(Cli("Program.cs"), "void RunEstimate(");
        int g = body.IndexOf("if (synth && measurementsPath is not null)", StringComparison.Ordinal);
        Assert.True(g >= 0, "--synth with --measurements must be refused");
        Assert.True(body.IndexOf("Environment.ExitCode = 2;", g, StringComparison.Ordinal) > g);
        Assert.Contains("=== {methodName} state estimate:", body);
        Assert.DoesNotContain("=== WLS state estimate:", body);
        Assert.Contains("est.IrlsCapReached", body);
        Assert.Contains("chi-square not run: --no-bad-data", body);
    }

    [Fact]
    public void Estimate_catches_the_dense_method_refusal_and_the_hti_singular_block()
    {
        string body = Body(Cli("Program.cs"), "void RunEstimate(");
        int ns = body.IndexOf("catch (NotSupportedException ex)\n", StringComparison.Ordinal);
        Assert.True(ns >= 0, "a NotSupportedException from a dense method above 3,000 buses must be caught (not only under --hti)");
        Assert.True(body.IndexOf("Environment.ExitCode = 2;", ns, StringComparison.Ordinal) > ns);
        int io = body.IndexOf("catch (InvalidOperationException ex) when (useHti)", StringComparison.Ordinal);
        Assert.True(io >= 0, "the HTI singular-block InvalidOperationException must be caught");
        Assert.True(body.IndexOf("Environment.ExitCode = 3;", io, StringComparison.Ordinal) > io);
    }

    [Fact]
    public void The_prior_message_claims_a_replacement_only_when_one_happened()
    {
        string body = Body(Cli("Program.cs"), "void RunEstimate(");
        Assert.DoesNotContain("voltage schedule replaced with solved profile", body);
        Assert.Contains("replaced > 0", body);
    }

    [Fact]
    public void Harmonics_fails_loud_and_uses_the_solved_fundamental_like_the_web_tab()
    {
        string body = Body(Cli("Program.cs"), "void RunHarmonics(");
        // Orders outside the analysed set are refused, not dropped into a 0.00 % "compliant" report.
        int outside = body.IndexOf("HarmonicPenetration.ComponentsOutsideOrders(", StringComparison.Ordinal);
        Assert.True(outside >= 0);
        Assert.True(body.IndexOf("Environment.ExitCode = 1;", outside, StringComparison.Ordinal) > outside);
        // An unknown --bus is refused up front (exit 2), and a triplen source's ArgumentException is caught (exit 1).
        Assert.Contains("is not a bus of", body);
        Assert.Contains("catch (ArgumentException ex)", body);
        // One Core function normalises THD in both front ends.
        Assert.Contains("HarmonicIndices.SolvedFundamentalVm(pen, fundamental)", body);
        Assert.DoesNotContain("HarmonicIndices.FromPenetration(pen);", body);
        string web = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Web", "Sessions", "SimSession.cs"));
        Assert.Contains("HarmonicIndices.SolvedFundamentalVm(pen, result)", web);
        // The case comes from SelectCase (a positional after a flag used to be dropped for case9).
        Assert.Contains("SelectCase(\"harmonics\"", body);
    }

    [Fact]
    public void The_chain_warm_start_maximum_leaves_out_step_0()
    {
        // Step 0 is not warm-started, so counting it meant the fixed-mode footer never printed.
        string body = Body(Cli("Program.cs"), "void RunChain(");
        Assert.Contains("if (s > 0) maxIters = Math.Max(maxIters, r.Iterations);", body);
    }
}
