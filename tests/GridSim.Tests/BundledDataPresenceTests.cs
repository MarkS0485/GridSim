using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N002 (October 2026): some older tests return early, and so PASS, when the bundled data file they read is missing
/// (<c>if (!File.Exists(path)) return;</c>, written when the MATPOWER libraries were not yet committed). The files are
/// all committed now, so a missing one is a broken checkout or a packaging fault, and a green run must not hide it.
/// Since GR-404 those guards are assertions (the files are required, so a missing one fails in the test itself as well
/// as here), and opt-in or single-platform tests are skipped by attribute (EnvironmentFact, EnvironmentTheory,
/// WindowsFact) so they are reported as skipped, not passed. These tests also fail:
/// <list type="bullet">
/// <item>every bundled file the guarded tests read must be present, both in the repository and in the test output folder
/// (where the guarded tests look for it);</item>
/// <item>no test may add a new silent early return on a missing file without being listed here: the source scan below
/// counts every such guard in the test project and names any that is not accounted for.</item>
/// </list>
/// The one genuinely optional data set is the v2 template library (data/library): V2LibraryFilesValidateTests is
/// documented to pass with the library present or absent, so it is listed as optional, not required.
/// </summary>
public sealed class BundledDataPresenceTests
{
    /// <summary>Each file a guarded test reads, relative to data/, with the test that reads it.</summary>
    public static readonly TheoryData<string, string> GuardedFiles = new()
    {
        // AnalysisCoverageTests.Parsed_case_solves_to_its_own_stored_operating_point
        { "matpower/lib/case14.m", "AnalysisCoverageTests" },
        { "matpower/lib/case118.m", "AnalysisCoverageTests" },
        // DistributionSolverTests.Parser_loads_case533mt_with_sqrt_base_voltages
        { "matpower/dist/case533mt_hi.m", "DistributionSolverTests" },
        // MatpowerParserTests.Large_case_parses_and_converges
        { "matpower/pegase/case1354pegase.m", "MatpowerParserTests" },
        { "matpower/pegase/case2869pegase.m", "MatpowerParserTests" },
        { "matpower/pegase/case9241pegase.m", "MatpowerParserTests" },
        { "matpower/rts/pglib_opf_case73_ieee_rts.m", "MatpowerParserTests" },
        // SolverPropertyTests.Parsed_lib_case_obeys_conservation and Parsed_pegase_case_obeys_conservation
        { "matpower/lib/case30.m", "SolverPropertyTests" },
        { "matpower/lib/case57.m", "SolverPropertyTests" },
        { "matpower/lib/case300.m", "SolverPropertyTests" },
        // LiveSimTests.Frame_carries_the_regulated_model_on_the_uk_oltc_case
        { "uk/system.json", "LiveSimTests" },
        // Wave6IoTests.JsonGridLoader_loads_the_asset_dynamics_branch_from_gb_synthetic and
        // MatpowerUnitNormalizer_flags_and_fixes_a_raw_distribution_feeder
        { "gb-synthetic/asset-classes.json", "Wave6IoTests" },
        { "matpower/dist/case33bw.m", "Wave6IoTests" },
    };

    [Theory]
    [MemberData(nameof(GuardedFiles))]
    public void Bundled_file_read_by_a_guarded_test_is_present(string relative, string readBy)
    {
        string inRepo = Path.Combine(CliProcess.Root(), "data", relative.Replace('/', Path.DirectorySeparatorChar));
        string inOutput = Path.Combine(AppContext.BaseDirectory, "data", relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(inRepo), $"data/{relative} (read by {readBy}) is missing from the repository; {readBy} would pass without testing anything.");
        Assert.True(File.Exists(inOutput), $"data/{relative} (read by {readBy}) was not copied to the test output ({inOutput}); {readBy} would pass without testing anything.");
        Assert.True(new FileInfo(inRepo).Length > 0, $"data/{relative} is empty.");
    }

    /// <summary>
    /// Every silent early return on a missing file or folder, an unset environment variable or the operating system is
    /// accounted for. GR-404: the guards of the tests listed in GuardedFiles are now assertions, so the only ones left
    /// are the optional template library and the crash-log default-folder check (which can only check the default when
    /// the override is unset). A new guard fails here, naming its file and line, so it is either listed or written to
    /// fail loud (an assertion) or to skip visibly (an EnvironmentFact, EnvironmentTheory or WindowsFact attribute).
    /// </summary>
    [Fact]
    public void No_unlisted_test_skips_silently_on_missing_data()
    {
        var accounted = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            // GR-404: the guards of AnalysisCoverageTests, DistributionSolverTests, MatpowerParserTests,
            // SolverPropertyTests (2), LiveSimTests and Wave6IoTests (2) are assertions now, so they count 0 here.
            ["V2LibraryFilesValidateTests.cs"] = 1,   // optional data: the template library may be absent by design
            ["CrashLogTests.cs"] = 1,                 // the default folder is only observable with the override unset
        };
        // GR-404: the guard used to match only "if (!File.Exists(..)) return;" and "if (!Directory.Exists(..)) return;".
        // It now also matches the braced form, an OperatingSystem.IsXxx() test and a string.IsNullOrWhiteSpace test
        // (the environment-variable opt-in), each with or without the negation.
        var guard = new Regex(
            @"if\s*\(\s*!?\s*(?:(?:File|Directory)\.Exists\(.*\)|OperatingSystem\.Is\w+\(\s*\)|string\.IsNullOrWhiteSpace\(.*\))\s*\)\s*\{?\s*return\s*;",
            RegexOptions.CultureInvariant);
        string testsDir = Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests");
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        var where = new List<string>();
        foreach (string file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            if (Path.GetFileName(file) == nameof(BundledDataPresenceTests) + ".cs") continue;   // this file quotes the pattern
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!guard.IsMatch(lines[i])) continue;
                string name = Path.GetFileName(file);
                found[name] = found.GetValueOrDefault(name) + 1;
                where.Add($"{Path.GetRelativePath(testsDir, file)}:{i + 1}");
            }
        }
        var unaccounted = found.Where(kv => kv.Value > accounted.GetValueOrDefault(kv.Key)).Select(kv => kv.Key).ToList();
        Assert.True(unaccounted.Count == 0,
            "silent early return on missing data in: " + string.Join(", ", unaccounted) + Environment.NewLine + string.Join(Environment.NewLine, where));
    }
}
