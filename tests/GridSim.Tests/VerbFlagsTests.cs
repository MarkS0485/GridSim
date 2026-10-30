using System.Text.RegularExpressions;
using GridSim.Cli;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N103 (October 2026): the shared per-verb flag check (src/GridSim.Cli/VerbFlags.cs, compiled into this test project).
/// A verb accepts the flags its own section of the CLI reference (manual ch. 19) names, its group's shared notes, the
/// global switches and, on a writer verb, the result-file flags; any other flag exits 2. These tests read the chapter
/// from disk, as the build embeds it, and check both directions: the documented flags are accepted, other verbs' flags
/// are refused, and every flag a verb's code reads is in that verb's accepted set (so the check can never refuse a flag
/// the verb really uses).
/// </summary>
public sealed class VerbFlagsTests
{
    private static string Root() => CliProcess.Root();
    private static string Chapter() => File.ReadAllText(Path.Combine(Root(), "docs", "manual", "0019 CLI reference.md")).Replace("\r\n", "\n");
    private static string ProgramSource() => File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");

    private static string? Check(params string[] rest) => VerbFlags.Check(Chapter(), rest, rest.Length > 0 && CliOutput.WriterVerbs.Contains(rest[0]));

    [Theory]
    [InlineData("trip", "--top")]              // screen's, nminus2's, ... never trip's
    [InlineData("stability", "--out")]
    [InlineData("interpose", "--catalog")]
    [InlineData("nminus1", "--exhaustive")]    // nminus2 / n-1-1 only
    [InlineData("ptdf", "--branch")]           // lodf's; the shared code read it and ignored it
    [InlineData("lodf", "--bus")]              // ptdf's
    [InlineData("modal", "--fault-bus")]
    [InlineData("n-1", "--candidates")]        // an alias resolves to its verb (nminus1)
    public void A_flag_of_another_verb_is_refused(string verb, string flag)
    {
        string? err = Check(verb, flag, "5");
        Assert.NotNull(err);
        Assert.StartsWith($"error: {verb} does not take {flag} ", err, StringComparison.Ordinal);
        Assert.Contains($"Run 'gridsim {verb} --help'", err, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("screen", "--top", "5")]
    [InlineData("screen", "--csv", "s.csv")]       // screen's own --csv (not the writer's)
    [InlineData("nminus2", "--exhaustive", "x")]
    [InlineData("n-1-1", "--candidates", "4")]
    [InlineData("stability", "--fault-bus", "7")]
    [InlineData("modal", "--json", "x")]           // modal's own console --json
    [InlineData("trip", "--export", "f.csv")]
    [InlineData("trip", "--gpu", "x")]             // a global switch (normally removed before the check)
    [InlineData("bench", "--note", "n")]           // a mode's flag (bench --perf) counts for the verb
    [InlineData("chain", "--bess", "x")]
    [InlineData("gda-replay", "--passive-plant", "x")]
    [InlineData("dcopf", "--json", "r.json")]      // a writer verb's result flag
    public void A_documented_flag_is_accepted(string verb, string flag, string value)
        => Assert.Null(Check(verb, flag, value));

    [Fact]
    public void The_no_verb_run_and_values_are_not_checked()
    {
        Assert.Null(Check("case14", "--once"));            // a case name first: the no-verb run
        Assert.Null(Check("--dir", "data/uk", "--once"));
        Assert.Null(Check("trip", "-5"));                  // a single-dash token is a value, never a flag
        Assert.Null(Check());
    }

    [Fact]
    public void Every_dispatched_verb_has_an_accepted_set()
    {
        string md = Chapter();
        var verbs = Regex.Matches(ProgramSource(), @"rest\[0\]\.Equals\(""([a-z][\w-]*)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.True(verbs.Count > 30, $"verb scan found only {verbs.Count} verbs");
        var none = verbs.Where(v => VerbFlags.Accepted(md, v, writerVerb: false) is null).ToList();
        Assert.True(none.Count == 0, "verbs with no accepted-flag set (no ch. 19 section): " + string.Join(", ", none));
    }

    /// <summary>
    /// The direction that matters for safety: every flag a verb's code reads (the string literals in its dispatch block
    /// and in the Run* functions it calls, followed transitively; StorageCli for chain) is accepted for that verb. A flag
    /// added to the code without its ch. 19 row fails here, naming verb and flag, before any user meets the refusal.
    /// The two listed exceptions are the point of the check: ptdf and lodf share one function that reads both --bus and
    /// --branch, and each verb uses only one of them.
    /// </summary>
    [Fact]
    public void Every_flag_a_verbs_code_reads_is_accepted_for_it()
    {
        string src = ProgramSource();
        string[] lines = src.Split('\n');
        string storage = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Cli", "StorageCli.cs"));

        string BodyFrom(int i)
        {
            int depth = 0; bool started = false;
            var sb = new System.Text.StringBuilder();
            for (int j = i; j < lines.Length; j++)
            {
                sb.Append(lines[j]).Append('\n');
                foreach (char ch in lines[j]) { if (ch == '{') { depth++; started = true; } else if (ch == '}') depth--; }
                if (started && depth == 0) break;
            }
            return sb.ToString();
        }

        var funcs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Length; i++)
            if (Regex.Match(lines[i], @"^(?:static\s+)?void\s+(Run\w+)\s*\(") is { Success: true } fm) funcs[fm.Groups[1].Value] = BodyFrom(i);

        HashSet<string> Flags(string text, HashSet<string> seen)
        {
            var fl = Regex.Matches(text, @"""(--[A-Za-z][\w-]*)""").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            foreach (Match call in Regex.Matches(text, @"\b(Run\w+)\s*\("))
                if (funcs.TryGetValue(call.Groups[1].Value, out string? body) && seen.Add(call.Groups[1].Value)) fl.UnionWith(Flags(body, seen));
            return fl;
        }

        var exceptions = new HashSet<(string, string)> { ("ptdf", "--branch"), ("lodf", "--bus") };
        string md = Chapter();
        var problems = new List<string>();
        int checkedVerbs = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("if (rest.Count > 0 &&", StringComparison.Ordinal)) continue;
            string? block = null;
            foreach (Match vm in Regex.Matches(lines[i], @"rest\[0\]\.Equals\(""([\w-]+)"""))
            {
                block ??= BodyFrom(i);
                string verb = vm.Groups[1].Value;
                HashSet<string> read = Flags(block, new HashSet<string>(StringComparer.Ordinal));
                if (verb == "chain") read.UnionWith(Regex.Matches(storage, @"""(--[A-Za-z][\w-]*)""").Select(m => m.Groups[1].Value));
                IReadOnlySet<string> accepted = VerbFlags.Accepted(md, verb, CliOutput.WriterVerbs.Contains(verb))!;
                foreach (string f in read.Where(f => !accepted.Contains(f) && !exceptions.Contains((verb, f))))
                    problems.Add($"{verb} reads {f} but its ch. 19 section does not name it");
                checkedVerbs++;
            }
        }
        Assert.True(checkedVerbs > 30, $"dispatch scan found only {checkedVerbs} verbs");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>Every flag the process-wide gate knows is accepted by at least one verb (else no command could use it).</summary>
    [Fact]
    public void Every_known_flag_belongs_to_some_verb()
    {
        string src = ProgramSource();
        int start = src.IndexOf("var knownFlags = new HashSet<string>", StringComparison.Ordinal);
        int end = src.IndexOf("};", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "knownFlags block not found");
        // Comments are dropped first: they name flags that were removed (R069's --serial) as well as live ones.
        string block = string.Join('\n', src[start..end].Split('\n').Select(l => l.Contains("//", StringComparison.Ordinal) ? l[..l.IndexOf("//", StringComparison.Ordinal)] : l));
        var known = Regex.Matches(block, @"""(--[A-Za-z][\w-]*)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        string md = Chapter();
        var verbs = Regex.Matches(src, @"rest\[0\]\.Equals\(""([a-z][\w-]*)""").Select(m => m.Groups[1].Value).Distinct().ToList();
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (string v in verbs) all.UnionWith(VerbFlags.Accepted(md, v, CliOutput.WriterVerbs.Contains(v))!);
        // The no-verb run's own forms (Running a case): --mpc and --dir; --dir is also many verbs' flag.
        all.Add("--mpc");
        var orphans = known.Where(f => !all.Contains(f)).ToList();
        Assert.True(orphans.Count == 0, "flags the gate knows that no verb's ch. 19 section names: " + string.Join(", ", orphans));
    }
}
