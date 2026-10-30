using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R068 (October 2026 review): the CLI reference claimed to document every verb but had drifted, and `--help`
/// listed fewer than half the verbs (and named a "protection" verb that does not exist). These source-level checks
/// fail when a verb is dispatched in Program.cs but missing from `--help` or from docs/manual/0019 CLI reference.md, or
/// when `--help` names a verb that is not dispatched.
/// </summary>
public class CliHelpContractTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Program() => File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");

    /// <summary>Every verb name the CLI dispatches on (rest[0] comparisons), plus the licence verb.</summary>
    private static SortedSet<string> DispatchedVerbs()
    {
        string src = Program();
        var verbs = new SortedSet<string>(StringComparer.Ordinal) { "licence" };
        foreach (Match m in Regex.Matches(src, @"rest\[0\]\.Equals\(""([a-z][\w-]*)""")) verbs.Add(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(src, @"rest\[0\] is ""([a-z][\w-]*)""")) verbs.Add(m.Groups[1].Value);
        Assert.True(verbs.Count > 30, $"verb scan found only {verbs.Count} verbs; the dispatch pattern may have changed");
        return verbs;
    }

    /// <summary>The text of the --help block.</summary>
    private static string HelpText()
    {
        string src = Program();
        int i = src.IndexOf("if (rest.Contains(\"--help\") || rest.Contains(\"-h\"))", StringComparison.Ordinal);
        Assert.True(i >= 0, "--help block not found");
        int end = src.IndexOf("Environment.Exit(0);", i, StringComparison.Ordinal);
        return string.Concat(Regex.Matches(src[i..end], @"Console\.WriteLine\(""([^""]*)""\)").Select(m => m.Groups[1].Value + "\n"));
    }

    private static IEnumerable<string> Words(string text) => Regex.Matches(text, @"[a-z][\w-]*").Select(m => m.Value);

    [Fact]
    public void Every_dispatched_verb_is_listed_by_help()
    {
        var help = new HashSet<string>(Words(HelpText()));
        var missing = DispatchedVerbs().Where(v => !help.Contains(v)).ToList();
        Assert.True(missing.Count == 0, "verbs missing from --help: " + string.Join(", ", missing));
    }

    [Fact]
    public void Help_names_no_verb_that_is_not_dispatched()
    {
        // The verb lines of --help: every word after the "group:" label must be a dispatched verb (or a known
        // non-verb token such as the case placeholders).
        var verbs = DispatchedVerbs();
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "none", "live", "uk", "model", "case", "file", "m", "dir", "path", "mpc", "once" };
        var stray = new List<string>();
        foreach (string line in HelpText().Split('\n'))
        {
            Match g = Regex.Match(line, @"^\s*(run|contingency|dynamics|analysis|data|tools):\s*(.*)$");
            if (!g.Success) continue;
            foreach (string w in Words(g.Groups[2].Value))
                if (!verbs.Contains(w) && !allowed.Contains(w)) stray.Add(w);
        }
        Assert.True(stray.Count == 0, "--help names verbs that are not dispatched: " + string.Join(", ", stray));
    }

    [Fact]
    public void Every_dispatched_verb_is_documented_in_the_cli_reference()
    {
        string doc = File.ReadAllText(Path.Combine(Root(), "docs", "manual", "0019 CLI reference.md"));
        var missing = DispatchedVerbs().Where(v =>
            !Regex.IsMatch(doc, $@"^#{{2,3}} {Regex.Escape(v)}\b", RegexOptions.Multiline)      // its own section
            && !doc.Contains($"(alias `{v}`)", StringComparison.Ordinal)                     // or a named alias
            && !doc.Contains($"`gridsim {v}", StringComparison.Ordinal)).ToList();          // or a usage line
        Assert.True(missing.Count == 0, "verbs missing from docs/manual/0019 CLI reference.md: " + string.Join(", ", missing));
    }
}
