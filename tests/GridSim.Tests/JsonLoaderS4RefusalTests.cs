using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// S4 "CLI and loader" (October 2026): the JSON case loader refuses input it used to read silently or crash on
/// (N096 unknown bus type / generator role, N098 JSON null in either, N097 boundary branch names that match nothing or
/// several branches). Each test copies the bundled GB reduced case (data/uk) to a fresh folder, makes one edit, and
/// checks the named refusal: InvalidDataException naming the file, the element and the value. The unedited copy must
/// still load, so the edit, not the copy, is what is refused.
/// </summary>
public sealed class JsonLoaderS4RefusalTests
{
    private static string UkSource() => Path.Combine(AppContext.BaseDirectory, "data", "uk");

    // Copy data/uk to a fresh temporary folder and replace the FIRST occurrence of `find` in `file` with `replace`.
    // Fails loud when the text to edit is not there (a changed bundled file must not silently turn a test into a no-op).
    private static string EditedCopy(string file, string find, string replace)
    {
        string src = UkSource();
        Assert.True(Directory.Exists(src), $"bundled case folder missing: {src}");
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-s4-loader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        string path = Path.Combine(dir, file);
        string text = File.ReadAllText(path);
        int at = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{find}' not found in {file}; the test's edit no longer applies to the bundled case");
        File.WriteAllText(path, text[..at] + replace + text[(at + find.Length)..]);
        return dir;
    }

    private static InvalidDataException Refused(string dir)
    {
        try { return Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir)); }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void Unedited_copy_still_loads()
    {
        // The edit helper with a no-op edit: proves the copy itself is accepted (so every refusal below is the edit's).
        string dir = EditedCopy("buses.json", "\"baseKv\": 400,", "\"baseKv\": 400,");
        try { Assert.Equal(20, JsonGridLoader.LoadDirectory(dir).BusCount); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- N096: unknown words --------------------------------------------------------------------------------------

    [Fact]
    public void N096_unknown_bus_type_is_refused_naming_file_bus_and_value()
    {
        InvalidDataException ex = Refused(EditedCopy("buses.json", "\"baseKv\": 400,", "\"baseKv\": 400, \"type\": \"PQQ\","));
        Assert.Contains("buses.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bus 1 ('North Scotland 400kV')", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unknown type 'PQQ'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N096_unknown_generator_role_is_refused_naming_file_unit_and_value()
    {
        InvalidDataException ex = Refused(EditedCopy("generation.json", "\"voltageSetpoint\": 1.04,", "\"voltageSetpoint\": 1.04, \"role\": \"slak\","));
        Assert.Contains("generation.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'North Scotland Hydro' (bus 1)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("unknown role 'slak'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>N096: every accepted word, in any letter case, still loads (the aliases the v1 schema documents).</summary>
    [Theory]
    [InlineData("pq")]
    [InlineData("PQ")]
    [InlineData("pv")]
    [InlineData("gen")]
    [InlineData("Slack")]
    [InlineData("ref")]
    [InlineData("swing")]
    public void N096_accepted_bus_types_still_load(string word)
    {
        string dir = EditedCopy("buses.json", "\"baseKv\": 400,", $"\"baseKv\": 400, \"type\": \"{word}\",");
        try { Assert.Equal(20, JsonGridLoader.LoadDirectory(dir).BusCount); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("pv")]
    [InlineData("PQ")]
    [InlineData("pq")]
    public void N096_accepted_generator_roles_still_load(string word)
    {
        string dir = EditedCopy("generation.json", "\"voltageSetpoint\": 1.04,", $"\"voltageSetpoint\": 1.04, \"role\": \"{word}\",");
        try { Assert.Equal(20, JsonGridLoader.LoadDirectory(dir).BusCount); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- N098: JSON null ------------------------------------------------------------------------------------------

    /// <summary>
    /// N098: confirmed by running the 1.95.122 build: a bus "type": null crashed the loader with a
    /// NullReferenceException (gridsim wrote a crash log and exited with the runtime's crash code). Now a named refusal.
    /// </summary>
    [Fact]
    public void N098_null_bus_type_is_a_named_refusal_not_a_crash()
    {
        InvalidDataException ex = Refused(EditedCopy("buses.json", "\"baseKv\": 400,", "\"baseKv\": 400, \"type\": null,"));
        Assert.Contains("buses.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bus 1 ('North Scotland 400kV') has \"type\": null", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N098_null_generator_role_is_a_named_refusal_not_a_crash()
    {
        InvalidDataException ex = Refused(EditedCopy("generation.json", "\"voltageSetpoint\": 1.04,", "\"voltageSetpoint\": 1.04, \"role\": null,"));
        Assert.Contains("generation.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'North Scotland Hydro' (bus 1) has \"role\": null", ex.Message, StringComparison.Ordinal);
    }

    // ---- N097: boundary branch names -------------------------------------------------------------------------------

    [Fact]
    public void N097_boundary_branch_name_that_matches_nothing_is_refused()
    {
        InvalidDataException ex = Refused(EditedCopy("boundaries.json", "\"L1-2 N-S Scotland\"", "\"L1-2 N-S Scotlnd\""));
        Assert.Contains("boundaries.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("boundary 'B4 Upper North (Scotland)' names branch 'L1-2 N-S Scotlnd', which matches no branch of the case", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void N097_boundary_branch_name_shared_by_two_branches_is_refused()
    {
        // Rename a second line to the B6 boundary's branch name: the flow lookup used to take the last of the two.
        InvalidDataException ex = Refused(EditedCopy("transmission.json", "\"L3-4 N England-Yorks\"", "\"L2-3 B6 boundary\""));
        Assert.Contains("boundaries.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("boundary 'B6 Scotland-England (SCOTEX)' names branch 'L2-3 B6 boundary', which 2 branches share", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>N097: the check itself, on a model built in code (no file): a clean set passes, both faults are listed.</summary>
    [Fact]
    public void N097_check_lists_every_problem()
    {
        var branches = new List<Branch>
        {
            new(1, 2, 0.01, 0.1, 0, Name: "A"),
            new(2, 3, 0.01, 0.1, 0, Name: "B"),
            new(1, 3, 0.01, 0.1, 0, Name: "B"),
        };
        JsonGridLoader.CheckBoundaryBranches(new[] { new Boundary("ok", 100, new[] { new BoundaryBranch("A") }) }, branches);
        var ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.CheckBoundaryBranches(
            new[] { new Boundary("bad", 100, new[] { new BoundaryBranch("Z"), new BoundaryBranch("B", -1) }) }, branches));
        Assert.Contains("2 boundary branch name(s)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Z', which matches no branch", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'B', which 2 branches share", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>N096 / N097: every bundled JSON case folder still loads under the stricter rules.</summary>
    [Theory]
    [InlineData("uk")]
    [InlineData("gb-full")]
    [InlineData("intl/ireland-all-island")]
    [InlineData("intl/france")]
    [InlineData("intl/iberia")]
    [InlineData("intl/continental-europe")]
    public void Bundled_case_folders_still_load(string folder)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "data", folder);
        Assert.True(Directory.Exists(dir), $"bundled case folder missing: {dir}");
        GridModel m = JsonGridLoader.LoadDirectory(dir, dispatch: false);
        Assert.True(m.BusCount > 0);
    }
}
