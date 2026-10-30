using System.Text.Json;
using Xunit;
using static GridSim.Tests.V2.V2TestSupport;

namespace GridSim.Tests.V2;

/// <summary>
/// N299 data part (October 2026, stream P): the schema fixture <c>fixtures/v2/networks/good.network.json</c> gave its
/// transformer a neutral reactance on a Delta side (<c>windings.to = "Delta"</c> with <c>neutralXPu.to = 0.0</c>). That is
/// schema-valid, but the N234 loader rule refuses a neutral reactance on a side that is not <c>WyeGrounded</c> (a delta
/// or an unearthed star has no earthed neutral to put a reactance in). The fixture is test data only and nothing loads
/// it through V2CaseLoader today, so the data was made valid by making the LV side an earthed star too (a YNyn
/// supergrid transformer, the usual 400/132 kV build); every member the fixture exercises is still present, so the
/// schema test that uses it asserts exactly what it did. This test keeps the fixture consistent with the loader rule,
/// so it stays loadable if a later test loads it.
/// </summary>
public sealed class V2FixtureNeutralRuleTests
{
    [Fact]
    public void The_good_network_fixture_puts_a_neutral_reactance_only_on_an_earthed_star_side()
    {
        string path = Path.Combine(FixturesDir, "networks", "good.network.json");
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = false };
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), options);

        int checkedSides = 0;
        foreach (JsonElement tx in doc.RootElement.GetProperty("transformers").EnumerateArray())
        {
            if (!tx.TryGetProperty("neutralXPu", out JsonElement neutral)) continue;
            // A neutral reactance needs the winding connections to say which sides are earthed (the N234 rule refuses
            // neutralXPu without windings too).
            Assert.True(tx.TryGetProperty("windings", out JsonElement windings),
                $"transformer '{tx.GetProperty("name").GetString()}' gives neutralXPu without windings");
            foreach (JsonProperty side in neutral.EnumerateObject())
            {
                string? connection = windings.TryGetProperty(side.Name, out JsonElement c) ? c.GetString() : null;
                Assert.True(connection == "WyeGrounded",
                    $"transformer '{tx.GetProperty("name").GetString()}' puts a neutral reactance on its '{side.Name}' side, "
                    + $"whose winding is '{connection ?? "(not given)"}'; only a WyeGrounded side has an earthed neutral (N234)");
                checkedSides++;
            }
        }

        // The fixture must still exercise the member on both sides (it uses every member the schema allows).
        Assert.Equal(2, checkedSides);
    }
}
