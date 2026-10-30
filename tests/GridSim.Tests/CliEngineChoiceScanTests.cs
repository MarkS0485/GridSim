using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 docs-programme finding: several CLI verbs chose the dense or sparse Newton engine by rules of their
/// own, and the existing guard (<c>SolverRoutingTests.No_local_dense_sparse_threshold_remains_in_the_cli</c>) could
/// not see them because it only matches an inline <c>BusCount &gt; N ? ...Sparse... : ...</c> ternary:
/// <list type="bullet">
/// <item><c>losses</c> and <c>nminus1-sweep</c>: <c>bool big = m.BusCount &gt; 1000;</c> on one line, the ternary on
/// the next (and the sweep's base solve reused <c>big</c>);</item>
/// <item><c>synth</c> and <c>gda --all-gsps</c>: always <c>new SteadyStateSolver(backend.SparseSolver())</c>;</item>
/// <item><c>gda --solve</c>: compared the LARGEST ISLAND with the threshold.</item>
/// </list>
/// These now route through the shared selector (<c>SolverBackend.Pick</c> / <c>Solver</c> / <c>NewtonSolver</c>, i.e.
/// <c>SolverSelector</c>). This stronger, source-level scan fails if any CLI file other than
/// <c>SolverBackend.cs</c> (the selector's CLI home) either
/// <list type="number">
/// <item>builds an engine directly (<c>.SparseSolver()</c>, <c>.DenseSolver()</c>, <c>.DenseLinear()</c>,
/// <c>.SparseLinear()</c>, <c>new SparseNewtonRaphsonPowerFlow(</c>, <c>new NewtonRaphsonPowerFlow(</c>), or</item>
/// <item>compares a bus count (<c>BusCount</c>, <c>LargestIslandBuses</c>) with anything on a line that, or whose
/// next two lines, mention an engine (dense/sparse/SteadyStateSolver/...),</item>
/// </list>
/// unless the line carries an explicit, reasoned exemption: <c>// routing-exempt: &lt;reason&gt;</c> on the line or
/// one of the two lines above it, or a <c>// routing-exempt-begin: &lt;reason&gt;</c> ... <c>// routing-exempt-end</c>
/// region. The only exemptions today are the benchmark verbs (<c>bench</c>, <c>bench --perf</c>,
/// <c>bench --estimate</c>), whose job is to time each engine. The test project does not reference the CLI, so this
/// reads the source, like the other CLI contract tests.
/// </summary>
public class CliEngineChoiceScanTests
{
    // Direct construction of a Newton engine or its linear backend.
    private static readonly Regex Construct = new(
        @"\.(SparseSolver|DenseSolver|DenseLinear|SparseLinear)\s*\(\s*\)|new\s+(SparseNewtonRaphsonPowerFlow|NewtonRaphsonPowerFlow)\s*\(",
        RegexOptions.Compiled);

    // Any comparison of a bus count with anything (a literal, a constant, a variable).
    private static readonly Regex Compare = new(@"\b(BusCount|LargestIslandBuses)\s*(>=|<=|>|<)\s*[\w.]+", RegexOptions.Compiled);

    // Words that say "this decides an engine".
    private static readonly Regex EngineWord = new(
        @"SparseSolver|DenseSolver|DenseLinear|SparseLinear|NewtonRaphsonPowerFlow|SteadyStateSolver|\bsparse\b|\bdense\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // An exemption must give a reason (at least ten characters of it), not just the marker.
    private static readonly Regex LineExempt = new(@"//\s*routing-exempt:\s*\S.{9,}", RegexOptions.Compiled);
    private static readonly Regex RegionBegin = new(@"//\s*routing-exempt-begin:\s*\S.{9,}", RegexOptions.Compiled);
    private static readonly Regex RegionEnd = new(@"//\s*routing-exempt-end\b", RegexOptions.Compiled);

    /// <summary>Every offending line in <paramref name="text"/>, as "file:line: code". Fails loudly on an
    /// unbalanced exemption region (a begin with no end would silently exempt the rest of the file).</summary>
    internal static List<string> Offenders(string fileName, string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var offenders = new List<string>();
        bool inRegion = false;
        int regionStart = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (RegionBegin.IsMatch(line))
            {
                Assert.False(inRegion, $"{fileName}:{i + 1}: nested routing-exempt-begin (the region opened at line {regionStart + 1} is not closed)");
                inRegion = true;
                regionStart = i;
                continue;
            }
            if (RegionEnd.IsMatch(line))
            {
                Assert.True(inRegion, $"{fileName}:{i + 1}: routing-exempt-end without a begin");
                inRegion = false;
                continue;
            }

            // Comment-only lines describe history ("was a local 'BusCount > 1000'"); they choose nothing.
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;

            bool hit = Construct.IsMatch(line);
            if (!hit && Compare.IsMatch(line))
            {
                for (int j = i; j < Math.Min(lines.Length, i + 3); j++)
                    if (EngineWord.IsMatch(lines[j])) { hit = true; break; }
            }
            if (!hit || inRegion) continue;

            bool exempt = false;
            for (int j = Math.Max(0, i - 2); j <= i; j++)
                if (LineExempt.IsMatch(lines[j])) { exempt = true; break; }
            if (!exempt) offenders.Add($"{fileName}:{i + 1}: {line.Trim()}");
        }
        Assert.False(inRegion, $"{fileName}: routing-exempt-begin at line {regionStart + 1} has no routing-exempt-end");
        return offenders;
    }

    [Fact]
    public void No_cli_verb_chooses_dense_or_sparse_outside_the_shared_selector()
    {
        string cliDir = Path.Combine(RepoRoot(), "src", "GridSim.Cli");
        string[] files = Directory.GetFiles(cliDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToArray();
        // Never scan nothing: Program.cs must be among the files.
        Assert.Contains(files, f => Path.GetFileName(f) == "Program.cs");

        var offenders = new List<string>();
        foreach (string file in files)
        {
            // SolverBackend.cs IS the CLI's routing home (Pick / Solver / NewtonSolver delegate to SolverSelector).
            if (Path.GetFileName(file) == "SolverBackend.cs") continue;
            offenders.AddRange(Offenders(Path.GetFileName(file), File.ReadAllText(file)));
        }
        Assert.True(offenders.Count == 0,
            "CLI code that picks dense/sparse itself (route it through SolverBackend.Pick/Solver/NewtonSolver, or add a " +
            "reasoned '// routing-exempt: ...' if the verb genuinely must name an engine):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void The_scan_catches_every_form_the_old_guard_missed()
    {
        // Each snippet is a form that shipped before this fix; the scan must flag every one of them.
        string twoLineThreshold = "    bool big = m.BusCount > 1000;\n    IPowerFlowSolver solver = big ? backend.SparseSolver() : backend.DenseSolver();\n";
        string factoryThreshold = "        bool big = m.BusCount > 1000;\n        Func<IPowerFlowSolver> factory = big ? () => backend.SparseSolver() : () => backend.DenseSolver();\n";
        string alwaysSparse = "    var ssolver = new SteadyStateSolver(backend.SparseSolver());\n";
        string largestIsland = "        bool sparse = !a.Contains(\"--dense\") && (a.Contains(\"--sparse\") || d.LargestIslandBuses > SolverSelector.DefaultSparseThreshold);\n";
        string directEngine = "        var nr = new SparseNewtonRaphsonPowerFlow();\n";

        Assert.NotEmpty(Offenders("two-line", twoLineThreshold));
        Assert.NotEmpty(Offenders("factory", factoryThreshold));
        Assert.NotEmpty(Offenders("always-sparse", alwaysSparse));
        Assert.NotEmpty(Offenders("largest-island", largestIsland));
        Assert.NotEmpty(Offenders("direct", directEngine));
    }

    [Fact]
    public void The_scan_honours_only_reasoned_exemptions()
    {
        string reasoned = "// routing-exempt: the benchmark times this engine on purpose\nvar nr = new SparseNewtonRaphsonPowerFlow();\n";
        string bare = "// routing-exempt:\nvar nr = new SparseNewtonRaphsonPowerFlow();\n";
        string region = "// routing-exempt-begin: the benchmark builds every engine\nvar a = cpu.DenseSolver();\nvar b = cpu.SparseSolver();\n// routing-exempt-end\n";
        string afterRegion = region + "var c = cpu.SparseSolver();\n";

        Assert.Empty(Offenders("reasoned", reasoned));
        Assert.NotEmpty(Offenders("bare", bare));           // a marker with no reason does not exempt
        Assert.Empty(Offenders("region", region));
        Assert.Single(Offenders("after-region", afterRegion)); // the region ends where it says it ends
        // The shared-selector forms are clean.
        Assert.Empty(Offenders("clean", "var s = backend.Pick(m);\nIPowerFlowSolver x = backend.NewtonSolver(m);\nvar y = backend.Solver(m);\n"));
    }

    /// <summary>The repository root (the folder holding GridSim.slnx). Fails loudly if not found.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }
}
