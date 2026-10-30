using System;
using System.IO;
using System.Linq;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N209 / N210 (CF2, October 2026; Ruling R1): the gb-synthetic ratings stay as built and the two limitations are
/// documented with their reasons in data/gb-synthetic/README.md. N210: MID / LDN have one 1,000 MVA supergrid
/// transformer each, overloaded whatever the dispatch (a topology property, N191). N209: YRK G8P4 132/33 and MID G2P1S3
/// 33/11 are single units sized to peak demand, pushed just past their rating by the AC-OPF's loss-inflated final pass.
/// These tests pin the data facts the README explains and that the README explains them.
/// </summary>
public sealed partial class Cf2OpenCodeFindingsTests
{
    [Fact]
    public void N209_N210_the_ratings_are_as_built_and_the_readme_records_the_decisions()
    {
        string dir = Path.Combine(CliProcess.Root(), "data", "gb-synthetic");
        GridModel m = JsonGridLoader.LoadDirectory(dir);
        double Rate(string name) => m.Branches.Single(b => b.Name == name).RateAMva;

        // N210: one 1,000 MVA unit at each of the two supergrid sites.
        Assert.Equal(1000.0, Rate("MID SGT 400/275 1"));
        Assert.Equal(1000.0, Rate("LDN SGT 400/275 1"));
        // N209: one standard unit each (132/33 kV 180 MVA, 33/11 kV 30 MVA).
        Assert.Equal(180.0, Rate("YRK G8P4 132/33"));
        Assert.Equal(30.0, Rate("MID G2P1S3 33/11"));

        string readme = File.ReadAllText(Path.Combine(dir, "README.md"));
        Assert.Contains("**Two supergrid transformers are overloaded whatever the dispatch (N210).**", readme, StringComparison.Ordinal);
        Assert.Contains("**Two radial transformers sit just past one unit at the AC-OPF's final pass (N209).**", readme, StringComparison.Ordinal);
        Assert.Equal(2, CountOrdinal(readme, "**Decided (Ruling R1, October 2026): kept, documented.**"));
    }

    private static int CountOrdinal(string text, string what)
    {
        int n = 0;
        for (int i = text.IndexOf(what, StringComparison.Ordinal); i >= 0; i = text.IndexOf(what, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
