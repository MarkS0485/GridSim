using System.Text.Json;
using GridSim.Tests.V2;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N309 (B3 open point; ruled by main under Ruling R1): the kind keys B3 added for the machine dynamic data file (F23)
/// carried no <c>codeDefaults</c> although each has a code default in its consumer's constructor. The eleven entries are
/// now declared, one per consumer, so the kind register is complete; the pinned total in
/// <see cref="V2KindSpecCodeDefaultTests"/> moves 583 -> 594 with them. Each row below is checked exactly as the Q24 rows
/// are: exactly one entry names the consumer, it carries the value, and the code agrees bit for bit.
/// </summary>
public sealed partial class B5ConverterModelsTests
{
    /// <summary>(kind, param, consumer fragment, value). The value is a number, or "+Infinity" / "-Infinity" as the kind
    /// files write an infinite default.</summary>
    public static TheoryData<string, string, string, string> N309Rows() => new()
    {
        { "governor", "valveMaxPu", "Tgov1 constructor default", "+Infinity" },
        { "governor", "valveMaxPu", "Ieeeg1 constructor default", "+Infinity" },
        { "governor", "valveMaxPu", "HydroGovernor constructor default (gateMaxPu", "+Infinity" },
        { "governor", "ieeeg1K2", "Ieeeg1 constructor default", "0" },
        { "governor", "ieeeg1K4", "Ieeeg1 constructor default", "0" },
        { "governor", "ieeeg1K6", "Ieeeg1 constructor default", "0" },
        { "governor", "ieeeg1K8", "Ieeeg1 constructor default", "0" },
        { "governor", "ieeeg1UoPuPerS", "Ieeeg1 constructor default (valveOpeningRatePuPerS", "+Infinity" },
        { "governor", "ieeeg1UcPuPerS", "Ieeeg1 constructor default (valveClosingRatePuPerS", "-Infinity" },
        { "synchronousMachine", "xlPu", "SubtransientInit.MachineData record default (Xl", "0" },
        { "synchronousMachine", "xlPu", "SubtransientMachine.Parameters record default (LeakageReactance", "0" },
    };

    [Theory]
    [MemberData(nameof(N309Rows))]
    public void N309_the_B3_keys_carry_exactly_the_code_defaults_of_their_consumers(string kind, string param, string consumerFragment, string value)
    {
        JsonElement p = KindFileSet.Load(kind).GetProperty("params").GetProperty(param);
        var hits = p.GetProperty("codeDefaults").EnumerateArray()
            .Where(c => c.GetProperty("consumer").GetString()!.Contains(consumerFragment, StringComparison.Ordinal))
            .ToList();
        Assert.True(hits.Count == 1, $"{kind}.{param}: {hits.Count} entries name consumer '{consumerFragment}', expected exactly one");
        JsonElement v = hits[0].GetProperty("value");
        if (value.EndsWith("Infinity", StringComparison.Ordinal))
            Assert.Equal(value, v.GetString());
        else
            Assert.Equal(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture), v.GetDouble());
        Assert.Null(CodeDefaultRefResolver.Mismatch(hits[0].GetProperty("ref").GetString()!, v));
    }

    [Fact]
    public void N309_the_rows_cover_every_code_default_of_the_eight_keys()
    {
        // No entry of these keys escapes the rows above: the per-key entry counts match the rows exactly.
        var rows = N309Rows().Select(r => ((string)r[0], (string)r[1])).ToList();
        foreach (var g in rows.GroupBy(r => r))
            Assert.Equal(g.Count(), KindFileSet.Load(g.Key.Item1).GetProperty("params").GetProperty(g.Key.Item2)
                .GetProperty("codeDefaults").GetArrayLength());
        Assert.Equal(11, rows.Count);
    }
}
