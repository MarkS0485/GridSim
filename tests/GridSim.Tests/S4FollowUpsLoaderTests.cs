using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S4 follow-ups to the JSON loader (October 2026): N137 (a null generator "type" crashed the loader with a bare
/// NullReferenceException, as N098 did for a bus type or role) and N139 (the loader accepted "+Infinity", a spelling
/// neither v1 nor v2 schema admits).
/// </summary>
public sealed class S4FollowUpsLoaderTests
{
    private static string UkSource() => Path.Combine(AppContext.BaseDirectory, "data", "uk");

    // Copy data/uk to a fresh temporary folder and replace the FIRST occurrence of `find` in `file` with `replace`.
    // Fails loud when the text to edit is not there (a changed bundled file must not turn a test into a no-op).
    private static string EditedCopy(string file, string find, string replace)
    {
        string src = UkSource();
        Assert.True(Directory.Exists(src), $"bundled case folder missing: {src}");
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-s4-followups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        string path = Path.Combine(dir, file);
        string text = File.ReadAllText(path);
        int at = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{find}' not found in {file}; the test's edit no longer applies to the bundled case");
        File.WriteAllText(path, text[..at] + replace + text[(at + find.Length)..]);
        return dir;
    }

    private static void Delete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static InvalidDataException Refused(string dir)
    {
        try { return Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir)); }
        finally { Delete(dir); }
    }

    private static GridModel Loads(string dir)
    {
        try { return JsonGridLoader.LoadDirectory(dir); }
        finally { Delete(dir); }
    }

    // ---- N137 -----------------------------------------------------------------------------------------------------

    [Fact]
    public void N137_null_generator_type_without_a_capacity_factor_is_refused_by_name()
    {
        // Confirmed by running the 1.95.125 build on this exact edit: "error loading case ...: Object reference not set
        // to an instance of an object." (the default capacity-factor lookup on a null type), exit 1.
        string dir = EditedCopy("generation.json",
            "\"type\": \"hydro\",        \"capacityMva\": 1300, \"capacityFactor\": 0.50,",
            "\"type\": null,        \"capacityMva\": 1300,");
        InvalidDataException ex = Refused(dir);
        Assert.Contains("generation.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'North Scotland Hydro' (bus 1)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"type\": null", ex.Message, StringComparison.Ordinal);
        Assert.IsNotType<NullReferenceException>(ex.InnerException);
    }

    [Fact]
    public void N137_null_generator_type_with_a_capacity_factor_is_refused_too()
    {
        // With capacityFactor present the null used to load silently into Generator.Type.
        string dir = EditedCopy("generation.json", "\"type\": \"hydro\",", "\"type\": null,");
        Assert.Contains("\"type\": null", Refused(dir).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N137_an_empty_type_and_an_absent_type_still_load()
    {
        // "" is what convert writes for a MATPOWER unit with no technology (N101 extension); absent means ccgt.
        GridModel empty = Loads(EditedCopy("generation.json", "\"type\": \"hydro\",", "\"type\": \"\","));
        Assert.Equal("", empty.Generators.First(g => g.Name == "North Scotland Hydro").Type);
        GridModel absent = Loads(EditedCopy("generation.json", "\"type\": \"hydro\",", ""));
        Assert.Equal("ccgt", absent.Generators.First(g => g.Name == "North Scotland Hydro").Type);
    }

    // ---- N139 -----------------------------------------------------------------------------------------------------

    [Fact]
    public void N139_plus_infinity_is_refused_naming_the_spellings_allowed()
    {
        // Confirmed by running the 1.95.125 build: the same edit loaded and solved (exit 0).
        string dir = EditedCopy("generation.json", "\"capacityMva\": 1300,", "\"capacityMva\": 1300, \"qMaxMvar\": \"+Infinity\",");
        InvalidDataException ex = Refused(dir);
        Assert.Contains("generation.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'+Infinity' is not a number", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"Infinity\" and \"-Infinity\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N139_the_canonical_spellings_still_load()
    {
        GridModel m = Loads(EditedCopy("generation.json", "\"capacityMva\": 1300,",
            "\"capacityMva\": 1300, \"qMaxMvar\": \"Infinity\", \"qMinMvar\": \"-Infinity\","));
        Generator g = m.Generators.First(x => x.Name == "North Scotland Hydro");
        Assert.Equal(double.PositiveInfinity, g.Qmax);
        Assert.Equal(double.NegativeInfinity, g.Qmin);
    }

    // ---- N119 -----------------------------------------------------------------------------------------------------

    [Fact]
    public void N119_transformer_json_carries_the_b_and_c_ratings()
    {
        // transmission.json has carried ratingBMva / ratingCMva since F20; transformer.json now does too. Before, the
        // loader refused the two keys as unknown members (R268), so a transformer could only carry rating A.
        GridModel m = Loads(EditedCopy("transformer.json", "\"ratingMva\": 1000 }", "\"ratingMva\": 1000, \"ratingBMva\": 1100, \"ratingCMva\": 1200 }"));
        Branch t1 = m.Branches.Single(b => b.Name == "T1 GSP N Scotland");
        Assert.True(t1.IsTransformer);
        Assert.Equal(1000.0, t1.RateAMva);
        Assert.Equal(1100.0, t1.RateBMva);
        Assert.Equal(1200.0, t1.RateCMva);
        // Absent: 0 (not given), as before for every other transformer.
        Assert.Equal(0.0, m.Branches.Single(b => b.Name == "T2 GSP S Scotland").RateBMva);
    }
}
