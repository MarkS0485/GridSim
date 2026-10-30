using System.Text.RegularExpressions;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// F12 (release register sections 0 and 5): the README and the desktop HARMONICS tab claimed more than the code does
/// (a stale "1,260+ tests", DNP3 / GOOSE "codecs" that are framing only, a "loss-aware AC-OPF" that is a DC dispatch
/// with an AC loss loop, IEC 60909 impedance-correction factors that are never applied, harmonic and CIM engines no
/// verb can reach, "four" estimation methods where there are six, links to the cli-reference redirect stub, and a
/// web port no default run uses). These tests stop each of those claims coming back.
/// </summary>
public class ReadmeHonestyTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Readme() => File.ReadAllText(Path.Combine(Root(), "README.md")).Replace("\r\n", "\n");

    [Theory]
    [InlineData("1,260+")]                              // the test count was 3,380 at the 1.95.109 batch run
    [InlineData("frame codecs")]                        // DNP3 and GOOSE are link framing only
    [InlineData("a loss-aware **AC-OPF**")]             // acopf is a DC dispatch iterated against AC losses
    [InlineData("decay and impedance-correction factors")] // K_G / K_T apply only with --corrections
    [InlineData("decay / impedance-correction factors")]
    [InlineData("four methods")]                        // wls, constrained, huber, lav, schweppe-gm, qr
    [InlineData("](docs/cli-reference.md)")]            // a redirect stub; ch. 19 is the reference
    [InlineData("localhost:5217")]                      // dotnet run binds no such port by default
    [InlineData("**CIM/CGMES** import/export")]         // an importer only, and no verb reaches it
    public void Readme_does_not_repeat_a_retracted_claim(string claim)
        => Assert.DoesNotContain(claim, Readme(), StringComparison.Ordinal);

    [Fact]
    public void Readme_states_the_limits_it_used_to_hide()
    {
        string r = Readme();
        Assert.Contains("not a full AC-OPF", r, StringComparison.Ordinal);
        Assert.Contains("at the framing level only", r, StringComparison.Ordinal);
        Assert.Contains("K_G / K_T exist as closed forms but are not yet applied", r, StringComparison.Ordinal);
        Assert.Contains("not yet reachable from the CLI or the apps", r, StringComparison.Ordinal);
        Assert.Contains("[`docs/manual/0019 CLI reference.md`](<docs/manual/0019 CLI reference.md>)", r, StringComparison.Ordinal);
        // The README's own test count must be at least the last recorded batch count (it may lag, never overclaim
        // an old number again): every "N,NNN+ tests" figure it quotes is 3,380 or more.
        foreach (Match m in Regex.Matches(r, @"(\d{1,2},\d{3})\+ tests"))
            Assert.True(int.Parse(m.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture) >= 3380,
                $"README quotes {m.Value}");
    }

    [Fact]
    public void Desktop_harmonics_tab_says_which_engines_cannot_be_run()
    {
        string xaml = File.ReadAllText(Path.Combine(Root(), "src", "GridSim.Wpf", "MainWindow.xaml"));
        Assert.Contains("coupled harmonic power flow and harmonic state estimation exist in GridSim.Core as experimental engines but cannot be run from this app or the CLI yet",
            xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("three-phase harmonics, coupled harmonic power flow, and harmonic state estimation. The engine is grounded",
            xaml, StringComparison.Ordinal);
    }
}
