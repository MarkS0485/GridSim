using System.Globalization;
using GridSim.Core.Dispatch;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-10-29 (GridSim 2.0.183), IO and dispatch findings. See BugHunt1029DynamicsTests.cs.</summary>
public sealed partial class BugHunt1029Tests
{
    /// <summary>Run <paramref name="body"/> with a comma-decimal current culture on this thread, then restore it.</summary>
    private static T BHUnderGermanCulture<T>(Func<T> body)
    {
        CultureInfo before = CultureInfo.CurrentCulture, beforeUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
            CultureInfo.CurrentUICulture = beforeUi;
        }
    }

    /// <summary>A private copy of the bundled data/uk case (a v1 case folder), to break one file of.</summary>
    private static string BHCopyUkCase()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-bh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(Path.Combine(CliProcess.Root(), "data", "uk")))
            File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH31: refusal messages print numbers in the invariant culture.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH31_loader_refusal_prints_a_decimal_point_under_a_comma_decimal_culture()
    {
        string dir = BHCopyUkCase();
        try
        {
            string path = Path.Combine(dir, "demand.json");
            string text = File.ReadAllText(path);
            int at = text.IndexOf("\"powerFactor\": 0.97", StringComparison.Ordinal);
            Assert.True(at >= 0);
            File.WriteAllText(path, text[..at] + "\"powerFactor\": 1.5" + text[(at + "\"powerFactor\": 0.97".Length)..]);
            var ex = BHUnderGermanCulture(() => Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir)));
            Assert.Contains("power factor 1.5;", ex.Message, StringComparison.Ordinal);
            Assert.Contains("between 0.01 and 1", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("1,5", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("0,01", ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH78: no local-time stamp anywhere in the CLI or the core library (every stamp is UTC).
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH78_no_code_in_the_cli_or_core_reads_the_local_clock()
    {
        var offenders = new List<string>();
        foreach (string project in new[] { "GridSim.Cli", "GridSim.Core" })
        {
            string root = Path.Combine(CliProcess.Root(), "src", project);
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = lines[i].TrimStart();
                    if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal)) continue;
                    if (System.Text.RegularExpressions.Regex.IsMatch(code, @"\bDateTime\.Now\b"))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
                }
            }
        }
        Assert.Empty(offenders);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH44: harmonic source set validation prints numbers in the invariant culture.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void BH44_harmonic_source_set_validation_prints_a_decimal_point_under_a_comma_decimal_culture()
    {
        var set = new HarmonicSourceSet { Case = "bh44", BaseMva = 99.5 };
        var ex = BHUnderGermanCulture(() => Assert.Throws<InvalidDataException>(() => set.Validate(GridSim.Core.IO.IeeeCases.Case9())));
        Assert.Contains("baseMva 99.5 does not match", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("99,5", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH32: a case file whose content is the JSON literal null is refused, not read as "none".
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("demand.json")]
    [InlineData("transformer.json")]
    [InlineData("system.json")]
    public void BH32_a_case_file_holding_the_json_literal_null_is_refused_by_name(string file)
    {
        string dir = BHCopyUkCase();
        try
        {
            File.WriteAllText(Path.Combine(dir, file), "null");
            var ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir));
            Assert.Contains(file, ex.Message, StringComparison.Ordinal);
            Assert.Contains("JSON literal null", ex.Message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // BH30: the schema's hyphenated technology names must reach the default capacity-factor table.
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("wind-onshore", "wind_onshore")]
    [InlineData("wind-offshore", "wind_offshore")]
    [InlineData("Wind-Offshore", "wind_offshore")]
    [InlineData("pumped-storage", "pumpedstorage")]
    public void BH30_hyphenated_technology_names_take_the_same_capacity_factor_as_the_underscore_form(string hyphen, string known)
    {
        Assert.Equal(Dispatcher.DefaultCapacityFactor(known), Dispatcher.DefaultCapacityFactor(hyphen));
        Assert.NotEqual(Dispatcher.DefaultCapacityFactor("no-such-technology"), Dispatcher.DefaultCapacityFactor(hyphen));
    }
}
