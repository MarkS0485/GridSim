using System;
using System.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R268: the JSON case loader refuses an unknown or misspelt key instead of silently applying the default
/// (a 'rating_MVA' typo used to make every line unrated, so N-1 reported "secure" everywhere). Every shipped
/// case must still load under the strict rule.
/// </summary>
public class JsonLoaderStrictKeysTests
{
    private static string WriteCase(string transmissionJson)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"gridsim-strict-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "system.json"), """{ "name": "strict", "baseMva": 100 }""");
        File.WriteAllText(Path.Combine(dir, "buses.json"), """
            [ { "id": 1, "baseKv": 400 }, { "id": 2, "baseKv": 400 } ]
            """);
        File.WriteAllText(Path.Combine(dir, "generation.json"), """
            [ { "name": "G", "bus": 1, "capacityMva": 500, "outputMw": 50, "role": "slack" } ]
            """);
        File.WriteAllText(Path.Combine(dir, "demand.json"), """[ { "name": "D", "bus": 2, "pMw": 50 } ]""");
        File.WriteAllText(Path.Combine(dir, "transmission.json"), transmissionJson);
        return dir;
    }

    [Fact]
    public void A_misspelt_key_fails_loud_naming_the_file_and_the_key()
    {
        string dir = WriteCase("""
            [ { "name": "L", "from": 1, "to": 2, "rPu": 0.01, "xPu": 0.1, "rating_MVA": 900 } ]
            """);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => JsonGridLoader.LoadDirectory(dir));
            Assert.Contains("transmission.json", ex.Message);
            Assert.Contains("rating_MVA", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Correct_keys_in_any_case_and_comments_still_load()
    {
        string dir = WriteCase("""
            // a hand-edited line, with a comment and PascalCase keys
            [ { "Name": "L", "From": 1, "To": 2, "RPu": 0.01, "XPu": 0.1, "RatingMva": 900 } ]
            """);
        try
        {
            GridModel m = JsonGridLoader.LoadDirectory(dir);
            Assert.Equal(900.0, Assert.Single(m.Branches).RateAMva);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("uk")]
    [InlineData("gb-synthetic")]
    [InlineData("gb-full")]
    [InlineData("intl/continental-europe")]
    [InlineData("intl/france")]
    [InlineData("intl/iberia")]
    [InlineData("intl/ireland-all-island")]
    public void Every_shipped_case_loads_under_the_strict_key_rule(string caseDir)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "data", caseDir.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(dir), $"missing shipped case {dir}");
        GridModel m = JsonGridLoader.LoadDirectory(dir, dispatch: false);
        Assert.True(m.BusCount > 0);
    }
}
