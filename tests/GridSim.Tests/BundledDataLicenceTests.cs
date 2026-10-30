using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The bundled third-party data ships with its licences and attribution files, and THIRD-PARTY-NOTICES.md names every
/// bundled data folder. Before, the builds copied only the .json, .csv and .m files, so the MATPOWER and PyPSA-GB
/// licences and the starter maps' ODbL attribution never reached a release folder, and the notices listed only the GB
/// files. The test project copies these files exactly as the CLI does, so their presence in this build's output stands
/// for the CLI's.
/// </summary>
public class BundledDataLicenceTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    public static TheoryData<string> LicenceFiles => new()
    {
        "matpower/LICENSE-MATPOWER.txt",
        "matpower/README.md",
        "matpower/gb/LICENSE-PyPSA-GB.txt",
        "matpower/gb/README.md",
        "intl/README.md",
        "intl/ireland-all-island/ATTRIBUTION.txt",
        "intl/iberia/ATTRIBUTION.txt",
        "intl/france/ATTRIBUTION.txt",
        "intl/continental-europe/ATTRIBUTION.txt",
        "intl/ireland-all-island/MODEL_CARD.md",
        "gb-full/README.md",
        "gb-synthetic/README.md",
    };

    [Theory]
    [MemberData(nameof(LicenceFiles))]
    public void Each_licence_and_attribution_file_is_in_the_build_output(string relative)
    {
        string shipped = Path.Combine(AppContext.BaseDirectory, "data", relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(shipped), $"missing from the build output: data/{relative}");
        // Byte for byte the tracked file: shipped as is, never regenerated.
        Assert.Equal(File.ReadAllBytes(Path.Combine(Root(), "data", relative)), File.ReadAllBytes(shipped));
    }

    [Theory]
    [InlineData("data/matpower/lib")]
    [InlineData("data/matpower/pegase")]
    [InlineData("data/matpower/dist")]
    [InlineData("data/matpower/rts")]
    [InlineData("data/matpower/nesta")]
    [InlineData("data/matpower/benchmarks/cigre_mv.m")]
    [InlineData("data/matpower/gb")]
    [InlineData("data/intl/ireland-all-island")]
    [InlineData("data/intl/iberia")]
    [InlineData("data/intl/france")]
    [InlineData("data/intl/continental-europe")]
    [InlineData("data/dist3ph")]
    [InlineData("data/neso")]
    [InlineData("data/gb-full")]
    public void The_notices_name_every_bundled_data_folder(string folder)
    {
        string notices = File.ReadAllText(Path.Combine(Root(), "THIRD-PARTY-NOTICES.md"));
        Assert.Contains($"`{folder}`", notices, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cli_ships_the_licence_files_beside_the_data()
    {
        string cli = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Cli", "GridSim.Cli.csproj"));
        Assert.Contains(@"..\..\data\**\LICENSE*.txt;..\..\data\**\ATTRIBUTION.txt;..\..\data\**\README.md;..\..\data\**\MODEL_CARD.md", cli, StringComparison.Ordinal);
        foreach (string app in new[] { "GridSim.Web", "GridSim.Wpf" })
        {
            string text = File.ReadAllText(Path.Combine(Root(), "src", app, app + ".csproj"));
            Assert.Contains(@"..\..\data\matpower\**\LICENSE*.txt;..\..\data\matpower\**\README.md", text, StringComparison.Ordinal);
        }
    }
}
