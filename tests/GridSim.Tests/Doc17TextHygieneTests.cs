using System.Text;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// DOC-17, the text and hygiene sweep of the docs session's findings (D-DOC-135, 136, 162, 193, 195, 212, 215, 222).
/// These tests pin the text-level fixes so they cannot drift back. None of them touches a number: the sweep changes
/// messages, comments and names only.
/// </summary>
public class Doc17TextHygieneTests
{
    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-135: non-ASCII output. batchsolve printed a Greek capital delta, the BESS step table a degree sign, and
    // the evidence / storage summaries a pound sign, which the Windows console shows as '?' or a stray byte. Every
    // string literal in the CLI, Core, GDA and GPU sources must now be plain ASCII (a sign the output genuinely needs
    // is spelt out: "GBP", "deg C", "Delta"). Comments may still hold such signs (they never reach a console), and a
    // character written as a \uXXXX escape is ASCII in the source and is a deliberate, reviewed choice (the help
    // text's ASCII folding table in VerbHelp.cs, the BOM strip in two readers).
    // The test project does not reference the CLI, so this reads the source, like the other CLI contract tests.
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void No_string_literal_in_the_console_facing_projects_holds_a_non_ascii_character()
    {
        string root = RepoRoot();
        string[] projects = ["GridSim.Cli", "GridSim.Core", "GridSim.Gda", "GridSim.Gpu"];
        var offenders = new List<string>();
        int filesScanned = 0;
        foreach (string project in projects)
        {
            string dir = Path.Combine(root, "src", project);
            // A missing project folder is a broken test set-up, not a pass: fail loud.
            Assert.True(Directory.Exists(dir), $"source folder not found: {dir}");
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // Build output (obj/bin) holds generated files that are not ours to police.
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)) continue;
                filesScanned++;
                string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string? bad = NonAsciiInsideStringLiteral(lines[i]);
                    if (bad is not null) offenders.Add($"{rel}:{i + 1}: U+{(int)bad[0]:X4} in \"{lines[i].Trim()}\"");
                }
            }
        }
        // Guard against a scan that silently found nothing (a wrong root would otherwise pass vacuously).
        Assert.True(filesScanned > 100, $"only {filesScanned} source files scanned under {root}/src");
        Assert.True(offenders.Count == 0,
            "non-ASCII characters in string literals (spell the sign out, as in \"GBP\", \"deg C\", \"Delta\"):\n"
            + string.Join("\n", offenders));
    }

    [Theory]
    // Ordinary text and escapes pass.
    [InlineData("Console.WriteLine(\"plain\");", null)]
    [InlineData("var s = \"a\\\"b\";", null)]
    // A comment after the code may hold any sign.
    [InlineData("int x = 1; // 120\u00b0 apart", null)]
    [InlineData("/// <summary>n\u00d7n systems</summary>", null)]
    // A non-ASCII sign inside a string is caught, also inside an interpolated string and after an escaped quote.
    [InlineData("Console.WriteLine(\"T\u00b0C\");", "\u00b0")]
    [InlineData("var s = $\"cost \u00a3{x:N0}\";", "\u00a3")]
    [InlineData("var s = \"a\\\"\u0394\";", "\u0394")]
    // A "//" inside a string does not start a comment.
    [InlineData("var u = \"http://x/\u00a3\";", "\u00a3")]
    public void The_literal_scanner_tells_strings_from_comments(string line, string? expected)
        => Assert.Equal(expected, NonAsciiInsideStringLiteral(line));

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-136 / D-DOC-195: RestrictionReview's refusal pointed the reader at docs/restriction-review-log.md, which
    // is not in the repository (removed in July 2026), and its XML comment said the review table was empty although
    // it holds four entries. The code table is the review log.
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_restriction_review_refusal_points_at_the_code_table_not_a_missing_document()
    {
        var ex = Assert.Throws<GridSim.Core.Diagnostics.RestrictionViolationException>(
            () => GridSim.Core.Diagnostics.RestrictionReview.AssertPermitted("SomeUnreviewedEkf", usesDynamicsModel: true));
        Assert.DoesNotContain("restriction-review-log.md", ex.Message, StringComparison.Ordinal);
        Assert.Contains("RestrictionReview.Reviewed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("src/GridSim.Core/Diagnostics/RestrictionReview.cs", ex.Message, StringComparison.Ordinal);
        // The file the message names must exist, so the pointer can never go stale again unnoticed.
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "src", "GridSim.Core", "Diagnostics", "RestrictionReview.cs")));
    }

    [Fact]
    public void The_restriction_review_table_holds_the_four_reviewed_trackers()
    {
        // The XML comment now names these four; if one is added or removed, the comment must change with it.
        Assert.Equal(new[] { "Ekf", "Ukf", "EnKf", "FrequencyRocofKf" },
                     GridSim.Core.Diagnostics.RestrictionReview.Reviewed.Select(e => e.Method).ToArray());
    }

    [Fact]
    public void No_core_or_cli_source_cites_the_missing_restriction_review_log()
    {
        string root = RepoRoot();
        // Fail loud if the premise changes: should the document come back, this test must be revisited, not pass.
        Assert.False(File.Exists(Path.Combine(root, "docs", "restriction-review-log.md")),
            "docs/restriction-review-log.md exists again: revisit D-DOC-136 (the code may point at it once more)");
        var offenders = new List<string>();
        foreach (string project in new[] { "GridSim.Cli", "GridSim.Core" })
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
                if (File.ReadAllText(file).Contains("docs/restriction-review-log.md", StringComparison.Ordinal))
                    offenders.Add(Path.GetRelativePath(root, file));
        Assert.True(offenders.Count == 0, "sources still citing docs/restriction-review-log.md: " + string.Join(", ", offenders));
    }

    // ------------------------------------------------------------------------------------------------------------
    // D-DOC-212: the provenance rules are reviewer rules (no code ranks tags), and the existing Inv3 test only checks
    // that three enum members differ. These new tests pin what the code DOES guarantee, so it cannot drift: the one
    // tag combinator never raises trust, and the model-output records default to non-recorded tags.
    // ------------------------------------------------------------------------------------------------------------

    // The trust rank a reader uses (technical ch. 8): 0 = Recorded, 1 = DerivedFromRecorded, 2 = anything weaker.
    // Only the top two levels matter for "never raised": nothing may become Recorded or DerivedFromRecorded by merging.
    private static int TrustRank(GridSim.Core.Diagnostics.ProvenanceTag t) => t switch
    {
        GridSim.Core.Diagnostics.ProvenanceTag.Recorded => 0,
        GridSim.Core.Diagnostics.ProvenanceTag.DerivedFromRecorded => 1,
        _ => 2,
    };

    [Fact]
    public void Merging_two_provenance_tags_never_raises_trust_above_either_input()
    {
        var all = Enum.GetValues<GridSim.Core.Diagnostics.ProvenanceTag>();
        // Guard: the enum has the seven members ch. 8 documents; a new member must be ranked deliberately here.
        Assert.Equal(7, all.Length);
        foreach (var a in all)
            foreach (var b in all)
            {
                var w = GridSim.Core.Evidence.AvoidedCost.Weakest(a, b);
                // Downgrade-only: the result is at most as trusted as the LESS trusted input.
                Assert.True(TrustRank(w) >= Math.Max(TrustRank(a), TrustRank(b)),
                    $"Weakest({a}, {b}) = {w} is more trusted than an input");
                // Symmetric, so the order of the arguments cannot change a published tag.
                Assert.Equal(w, GridSim.Core.Evidence.AvoidedCost.Weakest(b, a));
            }
    }

    [Fact]
    public void Model_output_records_default_to_non_recorded_tags()
    {
        // A forward (swing) solve export is Synthetic unless a caller states otherwise.
        var freq = new GridSim.Core.IO.FrequencyExportMeta("e", "g", "t", 0.0, "m");
        Assert.Equal(GridSim.Core.Diagnostics.ProvenanceTag.Synthetic, freq.Provenance);
        // An H = 0 study is a counterfactual.
        var h0 = new GridSim.Core.IO.H0StudyMeta("e", "g", "t", "m", "gfm", "gfl");
        Assert.Equal(GridSim.Core.Diagnostics.ProvenanceTag.Counterfactual, h0.Provenance);
        // A replay of recorded steps without storage is derived from the record, never Recorded itself.
        var replay = new GridSim.Core.Simulation.GdaReplayResult("s", 0, 0, Array.Empty<GridSim.Core.Simulation.GdaReplayStep>());
        Assert.Equal(GridSim.Core.Diagnostics.ProvenanceTag.DerivedFromRecorded, replay.Provenance);
    }

    // ------------------------------------------------------------------------------------------------------------
    // N325 (LF7, done in DOC-17): the web and WPF inertia read-outs summed H x MBase over EVERY unit, so a
    // converter-typed unit with H > 0 counted there and nowhere else (trip, screen, the lumped and distributed models use
    // GridModel.SystemInertiaMwS since N289 / N314 / N321). They now use SystemInertiaMwS and, per unit,
    // GridModel.RotatingInertiaMwS. On every bundled case the terms and their order are unchanged, bit for bit.
    // ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void N325_no_presentation_or_cli_source_sums_H_times_MBase_over_every_unit()
    {
        string root = RepoRoot();
        // A sum of H x MBase over a generator list, written inline (the pattern every old read-out used).
        var allUnitSum = new System.Text.RegularExpressions.Regex(
            @"Sum\(\s*(\w+)\s*=>\s*\1\.H\s*\*\s*\1\.MBase\s*\)", System.Text.RegularExpressions.RegexOptions.Compiled);
        var offenders = new List<string>();
        // Test retargeted 2026-10-20 (UI programme 0.2): code moved from src/GridSim.Wpf to src/GridSim.Ui; strengthened:
        // GridSim.Ui is scanned too, so the moved desktop code stays covered.
        foreach (string project in new[] { "GridSim.Cli", "GridSim.Web", "GridSim.Wpf", "GridSim.Ui" })
        {
            string dir = Path.Combine(root, "src", project);
            Assert.True(Directory.Exists(dir), $"source folder not found: {dir}");
            foreach (string file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal)))
            {
                string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)) continue;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                    if (allUnitSum.IsMatch(lines[i]) && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal))
                        offenders.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.True(offenders.Count == 0,
            "an inertia total summed over every unit (use GridModel.SystemInertiaMwS / RotatingInertiaMwS):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void N325_the_web_fuel_breakdown_counts_no_converter_inertia_and_is_unchanged_on_a_bundled_case()
    {
        GridSim.Core.Model.GridModel uk = GridSim.Core.IO.Json.JsonGridLoader.LoadDirectory(Path.Combine(RepoRoot(), "data", "uk"));

        // Bit-identity on the bundled case: the old expression (H x MBase / 1000 per unit, summed in fleet order).
        double old = 0;
        foreach (GridSim.Core.Model.Generator g in uk.Generators) old += g.H * g.MBase / 1000.0;
        var fb = new GridSim.Web.Projection.FuelBreakdown(uk);
        Assert.Equal(old, fb.InertiaGvaS);

        // A converter-typed unit given H = 5 s adds nothing (it used to add 5 x MBase / 1000 GVA.s here only).
        GridSim.Core.Model.Generator[] gens = uk.Generators.ToArray();
        int k = Array.FindIndex(gens, g => g.Name == "North Scotland Onshore Wind");
        Assert.True(k >= 0, "the uk onshore wind unit this test edits is missing");
        gens[k] = gens[k] with { H = 5.0 };
        GridSim.Core.Model.GridModel withH = uk.CopyWith(generators: gens);
        Assert.Equal(fb.InertiaGvaS, new GridSim.Web.Projection.FuelBreakdown(withH).InertiaGvaS);
        Assert.Equal(uk.SystemInertiaMwS, withH.SystemInertiaMwS);
    }

    [Fact]
    public void N325_the_gda_fleet_inertia_needs_no_filter_because_every_converter_type_has_H_zero()
    {
        // The GDA fleet screen (gridsim gda --fleet) sums H x capacity over every NESO register unit. That is the
        // rotating-mass total only while FuelMap gives every converter technology H = 0, which this pins against the
        // technology map GridModel.SystemInertiaMwS uses (PoleMap): no type PoleMap knows as non-synchronous may have
        // a FuelMap H, and every type with a FuelMap H must be a known synchronous machine there.
        string[] types =
        {
            "nuclear", "coal", "biomass", "ccgt", "ocgt", "chp", "gas", "recip", "hydro", "pumped-storage",
            "storage", "battery", "bess", "wind", "wind-onshore", "wind-offshore", "onshore", "offshore", "solar", "pv",
            "tidal", "interconnector", "hvdc", "converter", "grid", "other", "",
        };
        foreach (string t in types)
        {
            double h = GridSim.Gda.FuelMap.InertiaH(t);
            var sp = GridSim.Core.Dynamics.Distributed.PoleMap.ForType(t);
            bool knownConverter = sp.IsKnownType && sp.Kind != GridSim.Core.Dynamics.Distributed.DomainKind.Synchronous;
            if (knownConverter) Assert.True(h == 0.0, $"FuelMap gives converter type '{t}' H = {h}");
            if (h > 0.0)
                Assert.True(sp.IsKnownType && sp.Kind == GridSim.Core.Dynamics.Distributed.DomainKind.Synchronous,
                    $"FuelMap gives '{t}' H = {h} but PoleMap does not know it as a synchronous machine");
        }
    }

    /// <summary>
    /// The first non-ASCII character that sits inside a string literal on this one line, or null. A small hand
    /// scanner: it tracks whether it is inside "..." (with backslash escapes; verbatim @"..." treats "" as a quote),
    /// skips character literals, and stops at a // comment that starts outside a string. A string that spans lines
    /// (a multi-line verbatim string) is scanned line by line; the CLI and Core sources have no raw """ strings.
    /// </summary>
    internal static string? NonAsciiInsideStringLiteral(string line)
    {
        bool inString = false, verbatim = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (!inString)
            {
                // A comment outside a string ends the code on this line.
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return null;
                // A character literal such as '"' or '\'' must not open a string.
                if (c == '\'')
                {
                    int start = i + (i + 1 < line.Length && line[i + 1] == '\\' ? 3 : 2);
                    int close = start < line.Length ? line.IndexOf('\'', start) : -1;
                    if (close > i) { i = close; continue; }
                }
                if (c == '"')
                {
                    inString = true;
                    // @"..." or $@"..." or @$"...": the prefix characters sit just before the quote.
                    verbatim = (i >= 1 && line[i - 1] == '@') || (i >= 2 && line[i - 2] == '@' && line[i - 1] == '$');
                }
                continue;
            }
            // Inside a string.
            if (c > 127) return c.ToString();
            if (!verbatim && c == '\\') { i++; continue; }       // skip the escaped character
            if (c == '"')
            {
                if (verbatim && i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }   // "" inside @"..."
                inString = false;
            }
        }
        return null;
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        return dir!.FullName;
    }
}
