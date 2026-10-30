using System.Text.RegularExpressions;
using GridSim.Core.Presentation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N016 (R305 remainder, October 2026): the map's <c>fuelColor</c> in map-geo.js kept its own fuel taxonomy and had
/// drifted from Core's <see cref="FuelTaxonomy"/> (no hydrogen or reciprocating-engine rule, two extra keywords). The
/// browser cannot call Core, so the JS keeps a plain-data mirror, <c>FUEL_RULES</c>; this test parses it and runs the
/// same first-match classification in C#, then checks it against <see cref="FuelTaxonomy.Classify"/> for every keyword in
/// the table and a set of real-world probe strings. Any edit to either side that is not mirrored fails here.
/// </summary>
public class FuelTaxonomyJsAntiDriftTests
{
    private static (List<(string[] Keys, string Color)> Rules, string Other) ParseJs()
    {
        string js = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "wwwroot", "js", "map-geo.js");
        Match table = Regex.Match(js, @"const FUEL_RULES = \[(.*?)\n\s*\];", RegexOptions.Singleline);
        Assert.True(table.Success, "FUEL_RULES table not found in map-geo.js");
        var rules = new List<(string[], string)>();
        foreach (Match row in Regex.Matches(table.Groups[1].Value, @"\[\[([^\]]*)\],\s*'(#[0-9A-Fa-f]{6})'\]"))
        {
            string[] keys = Regex.Matches(row.Groups[1].Value, @"'([^']*)'").Select(k => k.Groups[1].Value).ToArray();
            rules.Add((keys, row.Groups[2].Value));
        }
        Match other = Regex.Match(js, @"const FUEL_OTHER = '(#[0-9A-Fa-f]{6})';");
        Assert.True(other.Success, "FUEL_OTHER not found in map-geo.js");
        return (rules, other.Groups[1].Value);
    }

    /// <summary>The JS fuelColor, run in C#: trim + lower-case, first rule with any keyword as a substring wins.</summary>
    private static string JsColor(List<(string[] Keys, string Color)> rules, string other, string type)
    {
        string s = type.Trim().ToLowerInvariant();
        foreach ((string[] keys, string color) in rules)
            if (keys.Any(s.Contains)) return color;
        return other;
    }

    [Fact]
    public void Js_table_parses_into_the_expected_number_of_rules()
    {
        (var rules, string other) = ParseJs();
        Assert.Equal(18, rules.Count);
        Assert.Equal(FuelTaxonomy.Classify("something unknown").Color, other);
    }

    [Fact]
    public void Every_js_keyword_classifies_to_the_same_colour_as_core()
    {
        (var rules, string other) = ParseJs();
        foreach ((string[] keys, _) in rules)
            foreach (string k in keys)
                Assert.True(FuelTaxonomy.Classify(k).Color == JsColor(rules, other, k),
                    $"keyword '{k}': Core {FuelTaxonomy.Classify(k).Color} vs JS {JsColor(rules, other, k)}");
    }

    [Theory]
    [InlineData("Nuclear")]
    [InlineData("CCGT")]
    [InlineData("Gas engine")]
    [InlineData("Diesel")]
    [InlineData("recip")]
    [InlineData("Hydrogen fuel cell")]
    [InlineData("Hydroelectric")]
    [InlineData("Pumped storage hydro")]
    [InlineData("Offshore wind")]
    [InlineData("Wind")]
    [InlineData("Solar PV")]
    [InlineData("Battery (BESS)")]
    [InlineData("HVDC interconnector")]
    [InlineData("Grid infeed")]
    [InlineData("Energy from waste")]
    [InlineData("EfW")]
    [InlineData("Incinerator")]
    [InlineData("Landfill gas")]
    [InlineData("Tidal stream")]
    [InlineData("  OCGT peaker  ")]
    [InlineData("")]
    [InlineData("Unknown")]
    public void Probe_strings_classify_identically_in_js_and_core(string type)
    {
        (var rules, string other) = ParseJs();
        Assert.Equal(FuelTaxonomy.Classify(type).Color, JsColor(rules, other, type));
    }
}
