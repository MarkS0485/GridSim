using GridSim.Core.Model.Substations;
using Xunit;

namespace GridSim.Tests.Substations;

/// <summary>
/// RC4 CLI audit F10: <c>substation-state</c> used to read its node-breaker file without going through the licence gate
/// (Gated), so a projected case above the free tier's 1,500-bus limit was produced without the check every other user-model
/// load makes. It now goes through the same gate; the tier is capped here the way the other licence tests do it.
/// </summary>
public sealed class SubstationStateLicenceGateTests : IDisposable
{
    private readonly string _dir = TestTemp.Under("gridsim-r411-gate-" + Guid.NewGuid().ToString("N"));

    public SubstationStateLicenceGateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    // The free tier whatever licence the machine holds (as Gr190GateRefusalTests): a key that does not verify resolves to the
    // free Personal tier (1,500 buses).
    private Dictionary<string, string?> FreeTier() => new()
    {
        ["GRIDSIM_LICENCE"] = "not-a-key",
        ["XDG_DATA_HOME"] = _dir,
    };

    /// <summary>A chain of single-node substations joined by lines: one electrical bus per substation.</summary>
    private static SubstationModel Chain(int buses)
    {
        var subs = new List<Substation>();
        var lines = new List<NbLine>();
        for (int i = 0; i < buses; i++)
        {
            string n = "N" + i.ToString("D5");
            subs.Add(SubstationFixtures.Sub("S" + i.ToString("D5"), 132, new[] { n }));
            if (i > 0) lines.Add(new NbLine("L" + i.ToString("D5"), "N" + (i - 1).ToString("D5"), n, 0, SubstationFixtures.LineX));
        }
        return new SubstationModel
        {
            Name = "chain-" + buses,
            Substations = subs,
            Lines = lines,
            Generators = new[] { new NbGenerator("G0", "N00000", 0, Slack: true) },
            Loads = new[] { new NbLoad("LD", "N" + (buses - 1).ToString("D5"), 10, 0) },
        };
    }

    [Fact]
    public void A_projected_case_above_the_free_tier_limit_is_refused_with_exit_2_and_the_licence_message()
    {
        string site = Path.Combine(_dir, "big.json");
        File.WriteAllText(site, SubstationJson.Write(Chain(1600)));
        string emitted = Path.Combine(_dir, "big.m");
        CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(FreeTier(), "substation-state", site, "--emit", emitted);
        Assert.True(r.ExitCode == 2, r.ToString());
        Assert.Contains("1,600", r.StdErr, StringComparison.Ordinal);
        Assert.Contains("1,500", r.StdErr, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", r.StdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(emitted), "nothing is emitted for a refused case");
    }

    [Fact]
    public void A_projected_case_within_the_free_tier_limit_is_unaffected()
    {
        string site = Path.Combine(_dir, "small.json");
        File.WriteAllText(site, SubstationJson.Write(Chain(40)));
        CliProcess.Result r = CliProcess.RunBuiltWithEnvironment(FreeTier(), "substation-state", site);
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("40 bus(es)", r.StdOut, StringComparison.Ordinal);
    }
}
