using System.Text.Json;
using GridSim.Core.Dynamics.Models;
using GridSim.Tests.V2;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GitHub issue #12: the GAST governor's three optional parameters (VMIN, VMAX, DTURB) have constructor defaults in
/// <see cref="Gast"/> that the governor kind file documented in its descriptions but did not register as code
/// defaults. They are now registered, one entry each. These tests pin each entry exactly as the other register rows
/// are pinned: exactly one entry names the GAST consumer, it carries the value, and the code agrees bit for bit.
/// </summary>
public sealed class GastCodeDefaultRegisterTests
{
    /// <summary>(param, value): the value is a number, or "+Infinity" as the kind files write an infinite default.</summary>
    public static TheoryData<string, string> Rows() => new()
    {
        { "valveMinPu", "0" },
        { "valveMaxPu", "+Infinity" },
        { "turbineDampingPu", "0" },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Each_GAST_optional_parameter_carries_exactly_the_Gast_constructor_default(string param, string value)
    {
        JsonElement p = KindFileSet.Load("governor").GetProperty("params").GetProperty(param);
        var hits = p.GetProperty("codeDefaults").EnumerateArray()
            .Where(c => c.GetProperty("ref").GetString()!.Contains("GridSim.Core.Dynamics.Models.Gast:.ctor:", StringComparison.Ordinal))
            .ToList();
        // Exactly one GAST entry per parameter: a duplicate or a lost entry both fail here.
        Assert.True(hits.Count == 1, $"governor.{param}: {hits.Count} GAST entries, expected exactly one");
        Assert.StartsWith("Gast constructor default", hits[0].GetProperty("consumer").GetString()!, StringComparison.Ordinal);
        JsonElement v = hits[0].GetProperty("value");
        if (value.EndsWith("Infinity", StringComparison.Ordinal))
            Assert.Equal(value, v.GetString());
        else
            Assert.Equal(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture), v.GetDouble());
        // The resolver reads the compile-time parameter default by reflection: the register and the code agree exactly.
        Assert.Null(CodeDefaultRefResolver.Mismatch(hits[0].GetProperty("ref").GetString()!, v));
    }

    [Fact]
    public void The_GastData_record_defaults_match_the_Gast_constructor_defaults()
    {
        // GastData (the gridsim-dynamics/2 loader's record) forwards to the Gast constructor; its own positional defaults
        // must be the same three values, or a file that omits a key would get a different default by a different path.
        var d = new GastData(0.05, 0.4, 0.1, 3.0, 1.0, 2.0);
        Assert.Equal(0.0, d.ValveMinPu);
        Assert.Equal(double.PositiveInfinity, d.ValveMaxPu);
        Assert.Equal(0.0, d.TurbineDampingPu);
    }
}
