using System.Text.Json;
using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using GridSim.Core.Synthetic;
using GridSim.Tests.V2;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N316 (LF7, October 2026; Ruling R1, Mark delegated EE calls 2026-10-04: one name, one case). The CLI name
/// <c>gb-synthetic</c> (alias <c>synthetic</c>, any case) is the shipped <c>data/gb-synthetic</c> folder for EVERY verb,
/// resolved in the one place every verb resolves a case name (Program.cs <c>LoadNamed</c>, and <c>CaseFolderOf</c> for
/// the positional-versus---dir agreement check). It used to build the network in memory (SyntheticGb.Build) for every
/// verb but trip, which N238 had switched on its own. The builder stays for <c>synth</c> (which writes the folder) and
/// for the Core tests that pin the builder itself.
///
/// <para>Also N316: the gridCodeProfile kind's "SyntheticGb model frequency" code default quoted a source line
/// ("FrequencyHz = 50.0") that SyntheticGb.cs no longer holds (the builder reads FrequencyLimits.NominalHz since R196 /
/// D2), so the reference now names that constant, and this file pins that the builder still reads it.</para>
/// </summary>
public sealed partial class LF7GbSyntheticTests
{
    private static string CliSource(string file) =>
        File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", file)).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The CLI's OWN shipped folder (beside the built gridsim.dll), the one the name resolves to.</summary>
    private static string CliGbSyntheticFolder() =>
        Path.Combine(Path.GetDirectoryName(CliProcess.BuiltCliDll())!, "data", "gb-synthetic");

    [Fact]
    public void N316_LoadNamed_resolves_the_name_to_the_shipped_folder_and_only_synth_calls_the_builder()
    {
        string program = CliSource("Program.cs");

        // The one case-name resolver: the arm loads the folder through Gated.Dir, as --dir data/gb-synthetic does.
        int at = program.IndexOf("GridModel LoadNamed(string name, string root) =>", StringComparison.Ordinal);
        Assert.True(at >= 0, "LoadNamed not found");
        string body = program[at..program.IndexOf("\n};\n", at, StringComparison.Ordinal)];
        Assert.Contains("\"gb-synthetic\" or \"synthetic\" => Gated.Dir(Path.Combine(root, \"data\", \"gb-synthetic\")),", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SyntheticGb", body.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Aggregate("", (acc, l) => acc + l + "\n"), StringComparison.Ordinal);

        // CaseFolderOf knows the name too, so "<verb> gb-synthetic --dir data/gb-synthetic" agrees instead of exit 2.
        int cf = program.IndexOf("static string? CaseFolderOf(string arg, string root)", StringComparison.Ordinal);
        Assert.True(cf >= 0, "CaseFolderOf not found");
        string cfBody = program[cf..program.IndexOf("\n}\n", cf, StringComparison.Ordinal)];
        Assert.Contains("\"gb-synthetic\" or \"synthetic\" => Path.Combine(root, \"data\", \"gb-synthetic\"),", cfBody, StringComparison.Ordinal);

        // N238's trip-only rewrite is gone: the general rule covers trip.
        Assert.DoesNotContain("tripCase = Path.Combine(baseDir, \"data\", \"gb-synthetic\")", program, StringComparison.Ordinal);

        // The builder is called exactly once in the CLI, by synth (which writes the folder), and by no other CLI, web or
        // WPF source, so no verb can build a second meaning of the name.
        Assert.Equal(1, CountOf(program, "SyntheticGb.Build("));
        Assert.Contains("GridModel synth = GridSim.Core.Synthetic.SyntheticGb.Build(sopt);", program, StringComparison.Ordinal);
        // Test retargeted 2026-10-20 (UI programme 0.2): code moved from src/GridSim.Wpf to src/GridSim.Ui; strengthened:
        // GridSim.Ui is scanned too, so the moved desktop code stays covered.
        foreach (string project in new[] { "GridSim.Cli", "GridSim.Web", "GridSim.Wpf", "GridSim.Ui", "GridSim.Gda", "GridSim.Gpu" })
        {
            string dir = Path.Combine(CliProcess.Root(), "src", project);
            if (!Directory.Exists(dir)) continue;
            foreach (string f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                         .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                  && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            {
                if (Path.GetFileName(f) == "Program.cs" && project == "GridSim.Cli") continue;   // counted above
                Assert.False(File.ReadAllText(f).Contains("SyntheticGb.Build(", StringComparison.Ordinal),
                    $"{f} builds the synthetic network in memory; the name gb-synthetic is the shipped folder (N316)");
            }
        }
    }

    private static int CountOf(string text, string snippet)
    {
        int n = 0;
        for (int i = text.IndexOf(snippet, StringComparison.Ordinal); i >= 0; i = text.IndexOf(snippet, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }

    [Theory]
    [InlineData("gb-synthetic")]
    [InlineData("synthetic")]
    [InlineData("GB-Synthetic")]
    public void N316_a_power_flow_verb_by_name_prints_exactly_what_the_folder_prints(string name)
    {
        // dcpf prints the model name, the islands with their scheduled generation (the loader's balanced dispatch, which
        // the builder did not apply) and the largest flows by branch number (the folder's branch order), so the builder
        // and the folder print different text and only the folder now matches. Byte for byte, stdout and exit code.
        string folder = CliGbSyntheticFolder();
        CliProcess.Result byName = CliProcess.RunBuilt("dcpf", name);
        CliProcess.Result byFolder = CliProcess.RunBuilt("dcpf", folder);
        Assert.True(byFolder.ExitCode == 0, byFolder.ToString());
        Assert.Equal(byFolder.ExitCode, byName.ExitCode);
        Assert.Equal(byFolder.StdOut, byName.StdOut);
        Assert.Contains("DC power flow - GB synthetic (2227 buses, 2268 branches)", byName.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void N316_the_name_and_dir_data_gb_synthetic_agree_and_another_folder_is_still_refused()
    {
        // SelectCase compares a positional name with --dir through CaseFolderOf. Before N316 only N238's trip rewrite made
        // them agree (trip only); every other SelectCase verb refused the pair with exit 2. Now the name IS the folder.
        string folder = CliGbSyntheticFolder();
        CliProcess.Result both = CliProcess.RunBuilt("trip", "gb-synthetic", "--dir", folder);
        CliProcess.Result byDir = CliProcess.RunBuilt("trip", "--dir", folder);
        Assert.True(both.ExitCode == 0, both.ToString());
        Assert.True(byDir.ExitCode == 0, byDir.ToString());
        Assert.Equal(N238Lines(byDir.StdOut), N238Lines(both.StdOut));

        // A different folder is still a different network: exit 2, naming both.
        CliProcess.Result clash = CliProcess.RunBuilt("trip", "gb-synthetic", "--dir", Path.Combine(Path.GetDirectoryName(folder)!, "uk"));
        Assert.Equal(2, clash.ExitCode);
        Assert.Contains("name different networks", clash.StdErr, StringComparison.Ordinal);
    }

    private static string[] N238Lines(string stdout) => stdout.Split('\n')
        .Select(l => l.TrimEnd('\r'))
        .Where(l => l.StartsWith("Tripped:", StringComparison.Ordinal) || l.StartsWith("Peak RoCoF:", StringComparison.Ordinal)
                    || l.StartsWith("Nadir:", StringComparison.Ordinal) || l.StartsWith("Settling:", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    public void N316_the_gridCodeProfile_SyntheticGb_entry_names_the_constant_the_builder_reads()
    {
        // The builder's model frequency is the named GB nominal, bit for bit, and the source still says so exactly once.
        GridModel built = SyntheticGb.Build();
        Assert.Equal(BitConverter.DoubleToInt64Bits(FrequencyLimits.NominalHz), BitConverter.DoubleToInt64Bits(built.FrequencyHz));
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Core", "Synthetic", "SyntheticGb.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(1, CountOf(src, "FrequencyHz = global::GridSim.Core.Dynamics.FrequencyLimits.NominalHz,"));
        // Before: "source:src/GridSim.Core/Synthetic/SyntheticGb.cs:FrequencyHz = 50.0", a snippet the file no longer held.
        Assert.DoesNotContain("FrequencyHz = 50.0", src, StringComparison.Ordinal);

        // The kind entry: one SyntheticGb consumer, now a member reference to FrequencyLimits.NominalHz that resolves to 50.
        JsonElement p = KindFileSet.Load("gridCodeProfile").GetProperty("params").GetProperty("nominalFrequencyHz");
        var hits = p.GetProperty("codeDefaults").EnumerateArray()
            .Where(c => c.GetProperty("consumer").GetString()!.StartsWith("SyntheticGb model frequency", StringComparison.Ordinal))
            .ToList();
        Assert.Single(hits);
        Assert.Equal("member:GridSim.Core:GridSim.Core.Dynamics.FrequencyLimits.NominalHz", hits[0].GetProperty("ref").GetString());
        Assert.Equal(50.0, hits[0].GetProperty("value").GetDouble());
        Assert.Null(CodeDefaultRefResolver.Mismatch(hits[0].GetProperty("ref").GetString()!, hits[0].GetProperty("value")));
    }
}
