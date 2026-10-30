using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Dispatch;
using GridSim.Core.Model;
using GridSim.Core.Synthetic;
using GridSim.Tests.V2;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N345 (CF3, October 2026; ruled by main: "name them"): every v2 kind-spec code default now cites its value by SYMBOL.
/// Before, 186 references quoted a source line (<c>source:</c>): 147 literals inside Core / Gda method bodies
/// (<c>cls.Param(AssetParams.X, literal)</c> fall-backs, the Dispatcher and FuelMap switch arms, the SyntheticGb network
/// literals) and 39 GridSim.Cli values (16 CliDefaults constants and 23 Program.cs literals: the synthesised machine's
/// damping and the typical IEEET1 / SEXS / PSS1A settings). Each literal is now a named constant holding the literal it
/// replaced, character for character, so no number moves (a C# constant is folded into the IL exactly as the literal
/// was). The CLI is read from its built binary by <see cref="CodeDefaultRefResolver"/>. The per-entry agreement of every
/// reference with the code is <see cref="V2KindSpecCodeDefaultTests"/>'s job; these tests pin what N345 itself did.
/// </summary>
public sealed partial class Cf3OpenCodeFindingsTests
{
    private static IEnumerable<(string Kind, string Param, string Ref, JsonElement Value)> AllCodeDefaults()
    {
        foreach ((string kind, JsonElement spec) in KindFileSet.LoadAll())
            foreach (JsonProperty p in spec.GetProperty("params").EnumerateObject())
                foreach (JsonElement c in p.Value.GetProperty("codeDefaults").EnumerateArray())
                    yield return (kind, p.Name, c.GetProperty("ref").GetString()!, c.GetProperty("value"));
    }

    [Fact]
    public void N345_no_kind_reference_quotes_a_source_line_any_more()
    {
        var all = AllCodeDefaults().ToList();
        // The entry total is unchanged by N345 (591, pinned in V2KindSpecCodeDefaultTests).
        Assert.Equal(591, all.Count);
        var quoted = all.Where(e => e.Ref.StartsWith("source:", StringComparison.Ordinal)).Select(e => $"{e.Kind}.{e.Param}: {e.Ref}").ToList();
        Assert.True(quoted.Count == 0, "still quoting source lines:\n" + string.Join("\n", quoted));
    }

    /// <summary>(type the constants live on, number of kind references that now cite it). 147 + 39 = 186.</summary>
    public static TheoryData<string, int> N345Hosts() => new()
    {
        { "member:GridSim.Core:GridSim.Core.Assets.GridFollowingInverter.", 14 },
        { "member:GridSim.Core:GridSim.Core.Assets.GridFormingInverter.", 9 },
        { "member:GridSim.Core:GridSim.Core.Assets.DcSources.", 7 },
        { "member:GridSim.Core:GridSim.Core.Assets.TransformerDynamics.", 10 },
        { "member:GridSim.Core:GridSim.Core.Assets.LineThermalDynamics.", 3 },
        { "member:GridSim.Core:GridSim.Core.Assets.Protection.ProtectionController.", 3 },
        { "member:GridSim.Core:GridSim.Core.Dynamics.Impedance.GridFormingImpedance.", 4 },
        { "member:GridSim.Core:GridSim.Core.Dynamics.Impedance.GridFollowingImpedance.", 1 },
        { "member:GridSim.Core:GridSim.Core.Dynamics.SmallSignal.LinearInverterModel.", 3 },
        { "member:GridSim.Core:GridSim.Core.Dynamics.Forming.FormingAggregate.", 2 },
        { "member:GridSim.Core:GridSim.Core.Dispatch.Dispatcher.DefaultCapacityFactor", 13 },
        { "member:GridSim.Gda:GridSim.Gda.FuelMap.InertiaH", 10 },
        { "member:GridSim.Gda:GridSim.Gda.FuelMap.CapacityFactor", 17 },
        { "member:GridSim.Core:GridSim.Core.Synthetic.SyntheticGb.", 51 },   // plus the 13 Fleet[Name=...] rows of N284
        { "member:GridSim.Cli:GridSim.Cli.CliDefaults.", 39 },
    };

    [Theory]
    [MemberData(nameof(N345Hosts))]
    public void N345_each_host_carries_the_converted_references_and_each_resolves_exactly(string prefix, int expected)
    {
        // The SyntheticGb prefix also matches N284's Fleet[Name=...] selector rows; those are not N345's and are skipped.
        var hits = AllCodeDefaults()
            .Where(e => e.Ref.StartsWith(prefix, StringComparison.Ordinal) && !e.Ref.Contains("Fleet[", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(expected, hits.Count);
        foreach (var e in hits)
            Assert.True(CodeDefaultRefResolver.Mismatch(e.Ref, e.Value) is null,
                $"{e.Kind}.{e.Param}: {e.Ref} -> {CodeDefaultRefResolver.Mismatch(e.Ref, e.Value)}");
    }

    [Fact]
    public void N345_the_named_constants_hold_the_old_literals_bit_for_bit()
    {
        // A sample across every host, compared as raw bits with the literal that used to stand in the code.
        static void Same(double literal, double named) => Assert.Equal(BitConverter.DoubleToInt64Bits(literal), BitConverter.DoubleToInt64Bits(named));
        Same(0.015, GridFollowingInverter.DefaultDeadbandHz);
        Same(0.25, GridFollowingInverter.DefaultActuateTauS);
        Same(double.PositiveInfinity, GridFollowingInverter.DefaultRocofTripHzPerS);
        Same(0.05, GridFormingInverter.DefaultActuateTauS);
        Same(0.0125, TransformerDynamics.DefaultTapStepPu);
        Same(20.0, TransformerDynamics.DefaultThermalTauMin);
        Same(0.1, LineThermalDynamics.DefaultDerateAtFullThermal);
        Same(0.98, DcSources.DefaultConverterEfficiency);
        Same(double.PositiveInfinity, ProtectionController.DefaultAutoRecloseDelayS);
        Same(0.90, Dispatcher.DefaultCapacityFactorNuclear);
        Same(0.40, Dispatcher.DefaultCapacityFactorOther);
        // The dispatcher's table still gives the same values through its public entry point.
        Same(0.20, Dispatcher.DefaultCapacityFactor("battery"));
        Same(0.40, Dispatcher.DefaultCapacityFactor("no such type"));
        // FuelMap lives in GridSim.Gda (no InternalsVisibleTo): its public methods carry the same numbers.
        Same(5.0, GridSim.Gda.FuelMap.InertiaH("nuclear"));
        Same(2.5, GridSim.Gda.FuelMap.InertiaH("chp"));
        Same(0.0, GridSim.Gda.FuelMap.InertiaH("wind-onshore"));
        Same(0.65, GridSim.Gda.FuelMap.CapacityFactor("biomass"));
        Same(0.30, GridSim.Gda.FuelMap.CapacityFactor("no such type"));
    }

    [Fact]
    public void N345_the_synthetic_gb_network_still_carries_the_literal_values()
    {
        // The built network, branch by branch kind: each named constant reaches the model exactly as its literal did.
        // A distribution branch may have been written as n identical parallel units (N204: R / n, X / n, B x n, rating x n,
        // n a whole number), so its unit values are checked through n, which the rating gives exactly.
        GridModel m = SyntheticGb.Build();
        Branch tie = m.Branches.First(b => b.Name == "NSC 400 tie");
        Assert.Equal((0.0003, 0.003, 0.35, 5500.0), (tie.R, tie.X, tie.B, tie.RateAMva));
        Branch corridor = m.Branches.First(b => b.Name == "NSC-SSC 400");
        Assert.Equal((0.0007, 0.007, 0.8, 5500.0), (corridor.R, corridor.X, corridor.B, corridor.RateAMva));

        static void Unit(Branch b, double r, double x, double tap, double unitMva)
        {
            double n = b.RateAMva / unitMva;
            Assert.True(n >= 1 && n == Math.Floor(n), $"{b.Name}: rating {b.RateAMva} is not a whole number of {unitMva} MVA units");
            Assert.Equal((r / n, x / n, tap), (b.R, b.X, b.TapRatio));
        }
        Branch sgt = m.Branches.First(b => b.Name!.EndsWith(" GSP 1 SGT", StringComparison.Ordinal));
        Unit(sgt, 0.001, 0.02, 0.99, 750.0);
        Oltc gspOltc = m.Oltcs.First(o => o.Transformer == sgt.Name);
        Assert.Equal((1.0, 0.9, 1.1, 0.00625, 0.01), (gspOltc.TargetVpu, gspOltc.MinTap, gspOltc.MaxTap, gspOltc.StepPu, gspOltc.DeadbandPu));
        Branch prim = m.Branches.First(b => b.Name!.EndsWith(" 132/33", StringComparison.Ordinal));
        Unit(prim, 0.004, 0.06, 0.97, 180.0);
        Oltc primOltc = m.Oltcs.First(o => o.Transformer == prim.Name);
        Assert.Equal((1.0, 0.9, 1.1, 0.0125, 0.015), (primOltc.TargetVpu, primOltc.MinTap, primOltc.MaxTap, primOltc.StepPu, primOltc.DeadbandPu));
        Unit(m.Branches.First(b => b.Name!.EndsWith(" 33/11", StringComparison.Ordinal)), 0.01, 0.22, 0.97, 30.0);
        Unit(m.Branches.First(b => b.Name!.EndsWith(" 11/6.6", StringComparison.Ordinal)), 0.04, 0.5, 0.99, 12.0);
    }

    [Fact]
    public void N345_the_cited_call_sites_use_the_names_not_the_literals()
    {
        // Source guard: the old quoted lines are gone and the named call sites are present (one sample per file).
        string Src(params string[] p) => File.ReadAllText(Path.Combine(new[] { CliProcess.Root() }.Concat(p).ToArray()));
        string td = Src("src", "GridSim.Core", "Assets", "Passive", "TransformerDynamics.cs");
        Assert.DoesNotContain("cls.Param(AssetParams.TapStepPu, 0.0125)", td, StringComparison.Ordinal);
        Assert.Contains("cls.Param(AssetParams.TapStepPu, DefaultTapStepPu)", td, StringComparison.Ordinal);
        string gfl = Src("src", "GridSim.Core", "Assets", "GridFollowingInverter.cs");
        Assert.Contains("cls.Param(AssetParams.RocofTripHzPerS, DefaultRocofTripHzPerS)", gfl, StringComparison.Ordinal);
        string gb = Src("src", "GridSim.Core", "Synthetic", "SyntheticGb.cs");
        Assert.DoesNotContain("branches.Add(new Branch(a, b, 0.0003, 0.003, 0.35", gb, StringComparison.Ordinal);
        Assert.Contains("branches.Add(new Branch(a, b, Ring400RPu, Ring400XPu, Ring400BPu,", gb, StringComparison.Ordinal);
        string program = Src("src", "GridSim.Cli", "Program.cs");
        Assert.DoesNotContain("new Pss1a(20.0, 10.0, 0.15, 0.05, 0.15, 0.05, -0.1, 0.1)", program, StringComparison.Ordinal);
        Assert.DoesNotContain("RmsExciter.FromIeeet1(50.0, 0.02, 1.0, 0.2, 0.05, 1.0, -10.0, 10.0", program, StringComparison.Ordinal);
        Assert.Contains("RmsExciter.FromIeeet1(GridSim.Cli.CliDefaults.Ieeet1RegulatorGain,", program, StringComparison.Ordinal);
        string cli = Src("src", "GridSim.Cli", "CliDefaults.cs");
        Assert.Contains("public const double Ieeet1RegulatorGain = 50.0, Ieeet1RegulatorTimeS = 0.02, Ieeet1ExciterConstant = 1.0,", cli, StringComparison.Ordinal);
        Assert.Contains("public const double Pss1aGain = 20.0, Pss1aWashoutS = 10.0, Pss1aLead1S = 0.15, Pss1aLag1S = 0.05,", cli, StringComparison.Ordinal);
    }

    [Theory]
    // the built CLI is read: the right value resolves, a wrong one and an unknown member are reported by name
    [InlineData("member:GridSim.Cli:GridSim.Cli.CliDefaults.SyntheticXdpPu", "0.3", null)]
    [InlineData("member:GridSim.Cli:GridSim.Cli.CliDefaults.Ieeet1RegulatorMinPu", "-10.0", null)]
    [InlineData("member:GridSim.Cli:GridSim.Cli.CliDefaults.SyntheticXdpPu", "0.31", "expected 0.31 but the code holds 0.3")]
    [InlineData("member:GridSim.Cli:GridSim.Cli.CliDefaults.NoSuchConstant", "1", "no static field or property 'NoSuchConstant'")]
    [InlineData("member:GridSim.Cli:GridSim.Cli.NoSuchType.X", "1", "names a type")]
    public void N345_the_resolver_reads_the_built_cli(string reference, string valueJson, string? fragment)
    {
        using JsonDocument v = JsonDocument.Parse(valueJson);
        string? why = CodeDefaultRefResolver.Mismatch(reference, v.RootElement);
        if (fragment is null) Assert.Null(why);
        else
        {
            Assert.NotNull(why);
            Assert.Contains(fragment, why, StringComparison.Ordinal);
        }
    }
}
