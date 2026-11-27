using System.Text.Json;
using GridSim.Core.Model.Substations;
using Xunit;
using static GridSim.Tests.Substations.SubstationFixtures;

namespace GridSim.Tests.Substations;

/// <summary>RC4 R4-11 part A: the <c>substation-state</c> verb run as the built CLI.</summary>
public sealed class SubstationStateCliProcessTests
{
    private static string Dir()
    {
        string d = TestTemp.Under("gridsim-r411a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void The_verb_reports_the_groups_writes_the_result_and_emits_a_case_the_existing_pf_verb_solves()
    {
        string d = Dir();
        string site = Path.Combine(d, "site.json");
        File.WriteAllText(site, SubstationJson.Write(TwoFeed()));
        string result = Path.Combine(d, "r.json");
        string projected = Path.Combine(d, "projected.m");

        CliProcess.Result r = CliProcess.RunBuilt("substation-state", site, "--unspecified", "normal", "--open", "CPL",
            "--json", result, "--emit", projected);
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("3 bus(es) from 3 node group(s)", r.StdOut, StringComparison.Ordinal);
        using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(result)))
        {
            JsonElement root = doc.RootElement;
            Assert.Equal("GA+L1+X1|GB+L2+X2|R1+R2+Y", root.GetProperty("summary").GetProperty("grouping").GetString());
            Assert.Equal(3, root.GetProperty("tables").GetProperty("buses").GetArrayLength());
            Assert.True(root.GetProperty("tables").TryGetProperty("terminals", out _));
        }
        Assert.True(File.Exists(projected));
        Assert.Equal(0, CliProcess.RunBuilt("pf", projected).ExitCode);

        // The input is never altered, and an existing --emit target is never overwritten.
        Assert.Equal(SubstationJson.Write(TwoFeed()), File.ReadAllText(site));
        string before = File.ReadAllText(projected);
        Assert.Equal(0, CliProcess.RunBuilt("substation-state", site, "--unspecified", "normal", "--emit", projected).ExitCode);
        Assert.Equal(before, File.ReadAllText(projected));
    }

    [Fact]
    public void Refusals_are_exit_1_for_a_refused_topology_and_2_for_usage()
    {
        string d = Dir();
        string site = Path.Combine(d, "site.json");
        File.WriteAllText(site, SubstationJson.Write(TwoFeed()));

        CliProcess.Result unspecified = CliProcess.RunBuilt("substation-state", site, "--open", "CPL");
        Assert.Equal(1, unspecified.ExitCode);
        Assert.Contains("never inferred", unspecified.StdErr, StringComparison.Ordinal);

        string dangling = Path.Combine(d, "dangling.json");
        File.WriteAllText(dangling, SubstationJson.Write(TwoFeed())
            .Replace("\"nodes\": [", "\"nodes\": [ { \"id\": \"ZZ\", \"voltageLevel\": \"V\" },"));
        CliProcess.Result bad = CliProcess.RunBuilt("substation-state", dangling, "--unspecified", "normal");
        Assert.Equal(1, bad.ExitCode);
        Assert.Contains("dangling", bad.StdErr, StringComparison.Ordinal);

        Assert.Equal(2, CliProcess.RunBuilt("substation-state", site, "--bogus", "x").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("substation-state", site, "--open", "CPL", "--close", "CPL", "--unspecified", "normal").ExitCode);
        Assert.Equal(2, CliProcess.RunBuilt("substation-state", site, "--unspecified", "guess").ExitCode);
        Assert.Equal(1, CliProcess.RunBuilt("substation-state", Path.Combine(d, "missing.json")).ExitCode);
    }
}
