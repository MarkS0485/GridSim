using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F-064: the CLI flags --inertia (scenario, evidence), --sync-inertia-scale (h0-study) and --device (scenario, evidence)
/// must fail loud on bad input. The test project does not reference the CLI, so, like the other CLI flag contract tests,
/// these are source-level anti-drift checks over src/GridSim.Cli/Program.cs. Each asserts the SPECIFIC named failure
/// path (the flag name, the FailFlag route and the exact wording), not merely that something throws.
/// </summary>
public class CliF064InertiaScaleDeviceFlagTests
{
    private static string Program()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, $"GridSim.slnx not found above {AppContext.BaseDirectory}");
        string path = Path.Combine(dir!.FullName, "src", "GridSim.Cli", "Program.cs");
        Assert.True(File.Exists(path), $"source not found: {path}");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    /// <summary>From a signature to the next blank line (the helpers are separated by blank lines).</summary>
    private static string Body(string src, string signature)
    {
        int i = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(i >= 0, $"helper not found: {signature}");
        int end = src.IndexOf("\n\n", i, StringComparison.Ordinal);
        return src[i..(end < 0 ? src.Length : end)];
    }

    [Fact]
    public void F064_list_splitter_refuses_every_empty_entry_and_names_the_flag()
    {
        string body = Body(Program(), "static string[] SplitListStrict(");
        // No RemoveEmptyEntries: a trailing, leading or doubled comma (and "," alone) must be an error.
        Assert.DoesNotContain("RemoveEmptyEntries", body);
        Assert.Contains("t.Length == 0", body);
        Assert.Contains("FailFlag<string>(flag, csv,", body);
        Assert.Contains("no empty entries (no leading, trailing or doubled comma)", body);
    }

    [Theory]
    [InlineData("static IReadOnlyList<double>? ParseDoubleList(")]
    [InlineData("static IReadOnlyList<int>? ParseIntList(")]
    [InlineData("static IReadOnlyList<Corner>? ParseCorners(")]
    public void F064_every_list_parser_goes_through_the_strict_splitter(string signature)
    {
        string body = Body(Program(), signature);
        Assert.Contains("SplitListStrict(", body);
        Assert.DoesNotContain("RemoveEmptyEntries", body);
        // Only an absent flag (null) takes the default; a blank value is no longer treated as absent.
        Assert.Contains("csv is null ? null", body);
        Assert.DoesNotContain("IsNullOrWhiteSpace", body);
    }

    [Fact]
    public void F064_double_list_demands_finite_values_inside_the_range_and_names_the_bad_token()
    {
        string body = Body(Program(), "static IReadOnlyList<double>? ParseDoubleList(");
        Assert.Contains("double.IsFinite(v)", body);                       // NaN, Infinity, overflow such as 1e999
        Assert.Contains("minExclusive ? v > min : v >= min", body);        // zero refused where the minimum is exclusive
        Assert.Contains("v <= max", body);                                 // above 1 refused for the scale
        Assert.Contains("FailFlag<double>(flag, t,", body);                // the offending token, under the real flag name
    }

    [Fact]
    public void F064_inertia_flag_is_positive_exclusive_in_scenario_and_evidence()
    {
        string src = Program();
        // 0 and negatives are refused (minExclusive: true with min 0), in both verbs that take --inertia as a list.
        Assert.Equal(2, Regex.Count(src,
            @"ParseDoubleList\(FlagString\(a, ""--inertia""\), ""--inertia"", 0, minExclusive: true\)"));
    }

    [Fact]
    public void F064_sync_inertia_scale_is_bounded_zero_to_one_inclusive()
    {
        Assert.Contains("ParseDoubleList(FlagString(a, \"--sync-inertia-scale\"), \"--sync-inertia-scale\", 0, 1)", Program());
    }

    [Fact]
    public void F064_flag_given_twice_or_without_a_value_is_refused_by_the_shared_helpers()
    {
        string src = Program();
        // FlagString (used by all three flags) refuses a missing value; FlagIndex refuses a repeated flag.
        Assert.Contains("FlagValueMissing(a, i)", Body(src, "static string? FlagString("));
        Assert.Contains("given more than once", Body(src, "static int FlagIndex("));
    }

    [Fact]
    public void F064_device_file_is_loaded_only_through_the_named_failure_loader()
    {
        string src = Program();
        // Both verbs (scenario, evidence) use the loader; neither calls DeviceConfig.Load directly any more.
        Assert.Equal(2, Regex.Count(src, @"device = LoadDeviceFileOrFail\(deviceFile\);"));
        Assert.DoesNotContain("DeviceConfig.Load(deviceFile)", src);
    }

    [Theory]
    [InlineData("a device JSON file but that is a directory")]
    [InlineData("an existing device JSON file but it was not found")]
    [InlineData("only the known device keys but found unknown key")]
    [InlineData("each key once but")]
    [InlineData("the required key")]
    [InlineData("a non-empty name")]
    [InlineData("rotationalInertiaMwS finite and 0 or more")]
    [InlineData("fastResponseMw finite and 0 or more")]
    [InlineData("fastResponseTimeConstantS finite and above 0")]
    [InlineData("fastResponseDeadbandHz finite and 0 or more")]
    public void F064_device_loader_has_each_named_refusal(string wording)
    {
        string body = Body(Program(), "static DeviceConfig LoadDeviceFileOrFail(");
        Assert.Contains("FailFlag<DeviceConfig>(\"--device\", path,", body);
        Assert.Contains(wording, body);
    }

    [Fact]
    public void F064_device_loader_turns_malformed_and_unreadable_files_into_a_named_failure()
    {
        string body = Body(Program(), "static DeviceConfig LoadDeviceFileOrFail(");
        // Malformed JSON, empty file, I/O and permission errors, and a null document all end in FailFlag, never a stack trace.
        Assert.Contains("System.Text.Json.JsonException or IOException or UnauthorizedAccessException or InvalidDataException", body);
        Assert.Contains("could not be read:", body);
        Assert.Contains("ValueKind != System.Text.Json.JsonValueKind.Object", body);
    }

    /// <summary>The strict loader must still accept the device file GridSim ships: its "_provenance" annotation is
    /// skipped, and every other key is a known device key of the right type, with the required keys present.</summary>
    [Fact]
    public void F064_bundled_tsgb_fleet_device_file_passes_the_strict_key_rules()
    {
        Assert.Contains("if (p.Name.StartsWith('_'))", Body(Program(), "static DeviceConfig LoadDeviceFileOrFail("));

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        string path = Path.Combine(dir!.FullName, "data", "devices", "tsgb-fleet.json");
        Assert.True(File.Exists(path), $"bundled device file not found: {path}");

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "name", "rotationalInertiaMwS", "fastResponseMw", "fastResponseTimeConstantS", "fastResponseDeadbandHz" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            Assert.True(seen.Add(p.Name), $"repeated key '{p.Name}'");
            if (p.Name.StartsWith('_')) continue;
            Assert.True(known.Contains(p.Name), $"unknown key '{p.Name}' would be refused");
            Assert.Equal(p.Name == "name" ? System.Text.Json.JsonValueKind.String : System.Text.Json.JsonValueKind.Number,
                p.Value.ValueKind);
        }
        foreach (string req in new[] { "name", "rotationalInertiaMwS", "fastResponseMw" })
            Assert.Contains(req, seen);
    }
}
