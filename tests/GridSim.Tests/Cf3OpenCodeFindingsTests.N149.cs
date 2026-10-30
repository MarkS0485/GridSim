using System;
using System.Linq;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N149 (CF3, October 2026; Ruling R1): ENA EREC G5 Issue 5 s.3.29 p.17 (docs/reference S139): active-front-end
/// converters "may be classified as resonant plant due to their internal impedance". With
/// <see cref="HarmonicModelOptions.ActiveFrontEndReactancePu"/> set, every unit of the active-front-end harmonic class is
/// stamped in Y(h) as a shunt R + jhX (on its rating, converted to the system base): a Norton equivalent instead of an
/// ideal current source. Line-commutated units, power-flow-only devices and every study without the option are untouched,
/// bit for bit.
/// </summary>
public sealed partial class Cf3OpenCodeFindingsTests
{
    /// <summary>case9 plus three converter units: an AFE solar plant at bus 5 (MBase 50), a line-commutated link at bus 7
    /// (fault current 0) and a power-flow-only converter at bus 9.</summary>
    private static GridModel N149Model()
    {
        GridModel m = IeeeCases.Case9();
        return m.CopyWith(generators: m.Generators
            .Append(new Generator(5, Pg: 10, MBase: 50, Name: "afe", Type: "solar"))
            .Append(new Generator(7, Pg: 20, MBase: 200, Name: "lcc", Type: "interconnector", ConverterFaultCurrentPu: 0.0))
            .Append(new Generator(9, Pg: 5, MBase: 80, Name: "pfo", Type: "solar", PowerFlowOnly: true))
            .ToList());
    }

    private static int Diag(ComplexCsr y, GridModel m, int busId)
        => y.DiagPtr[Enumerable.Range(0, m.BusCount).First(i => m.Buses[i].Id == busId)];

    [Fact]
    public void N149_only_the_active_front_end_class_is_resonant_plant()
    {
        GridModel m = N149Model();
        Assert.True(FrequencyDependentYBus.IsActiveFrontEndPlant(m.Generators.Single(g => g.Name == "afe")));
        Assert.False(FrequencyDependentYBus.IsActiveFrontEndPlant(m.Generators.Single(g => g.Name == "lcc")));
        Assert.False(FrequencyDependentYBus.IsActiveFrontEndPlant(m.Generators.Single(g => g.Name == "pfo")));
        Assert.All(m.Generators.Where(g => !g.IsConverterInterfaced), g => Assert.False(FrequencyDependentYBus.IsActiveFrontEndPlant(g)));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(13)]
    public void N149_the_stamp_is_exactly_the_norton_admittance_at_the_afe_bus_and_nothing_else_moves(int h)
    {
        GridModel m = N149Model();
        HarmonicModelOptions off = HarmonicModelOptions.Study;
        HarmonicModelOptions on = off with { ActiveFrontEndReactancePu = 0.15, ActiveFrontEndResistancePu = 0.004 };
        ComplexCsr a = FrequencyDependentYBus.Build(m, h, off);
        ComplexCsr b = FrequencyDependentYBus.Build(m, h, on);
        Assert.Equal(a.RowPtr, b.RowPtr);
        Assert.Equal(a.ColIdx, b.ColIdx);

        // The expected admittance, with the same arithmetic: 0.15 and 0.004 pu on 50 MVA, on the 100 MVA system base.
        double scale = m.BaseMva / 50.0;
        double r = 0.004 * scale, hx = h * (0.15 * scale);
        double den = r * r + hx * hx;
        int p = Diag(b, m, 5);
        Assert.Equal(a.G[p] + r / den, b.G[p]);
        Assert.Equal(a.B[p] + -hx / den, b.B[p]);
        // Every other entry is bit-identical: the line-commutated bus 7 and the power-flow-only bus 9 included.
        for (int k = 0; k < a.G.Length; k++)
        {
            if (k == p) continue;
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.G[k]), BitConverter.DoubleToInt64Bits(b.G[k]));
            Assert.Equal(BitConverter.DoubleToInt64Bits(a.B[k]), BitConverter.DoubleToInt64Bits(b.B[k]));
        }
    }

    [Fact]
    public void N149_without_the_option_or_without_afe_units_every_study_is_bit_identical()
    {
        // A model whose only converter is line-commutated: the option changes nothing, voltages bit for bit.
        GridModel m = IeeeCases.Case9();
        GridModel lccOnly = m.CopyWith(generators: m.Generators
            .Append(new Generator(7, Pg: 20, MBase: 200, Name: "lcc", Type: "interconnector", ConverterFaultCurrentPu: 0.0)).ToList());
        HarmonicSource full = HarmonicSourceLibrary.Make(ConverterHarmonicSources.LineCommutatedProfile, 7, 2.0);
        // The balanced study carries no triplen and analyses odd orders: keep exactly the components it carries.
        HarmonicSource src = full with
        {
            Spectrum = full.Spectrum.Where(c => !HarmonicSequence.IsTriplen(c.Order) && HarmonicModelOptions.Study.Orders.Contains(c.Order)).ToArray(),
        };
        HarmonicPenetrationResult a = HarmonicPenetration.Solve(lccOnly, new[] { src }, HarmonicModelOptions.Study);
        HarmonicPenetrationResult b = HarmonicPenetration.Solve(lccOnly, new[] { src },
            HarmonicModelOptions.Study with { ActiveFrontEndReactancePu = 0.15 });
        for (int oi = 0; oi < a.Orders.Count; oi++)
            for (int bi = 0; bi < a.BusIds.Count; bi++)
                Assert.Equal(a.Voltage[oi][bi], b.Voltage[oi][bi]);
        // The Study defaults carry no converter impedance.
        Assert.Null(HarmonicModelOptions.Study.ActiveFrontEndReactancePu);
        Assert.Null(ConverterHarmonicSources.StudyOptions.ActiveFrontEndReactancePu);
        Assert.Equal(0.0, HarmonicModelOptions.Study.ActiveFrontEndResistancePu);
    }

    [Fact]
    public void N149_the_afe_shunt_changes_the_study_where_the_afe_unit_sits()
    {
        // With the converter as a source at its own bus, its internal impedance now carries part of its current to
        // ground there: the 5th-order voltage at bus 5 moves (it was an ideal current source before).
        GridModel m = N149Model();
        HarmonicSource src = HarmonicSourceLibrary.Make(ConverterHarmonicSources.SelfCommutatedProfile, 5, 0.5);
        var opt = ConverterHarmonicSources.StudyOptions;
        HarmonicSource kept = src with { Spectrum = src.Spectrum.Where(c => !HarmonicSequence.IsTriplen(c.Order) && opt.Orders.Contains(c.Order)).ToArray() };
        HarmonicPenetrationResult ideal = HarmonicPenetration.Solve(m, new[] { kept }, opt);
        HarmonicPenetrationResult norton = HarmonicPenetration.Solve(m, new[] { kept }, opt with { ActiveFrontEndReactancePu = 0.15 });
        int oi = ideal.Orders.ToList().IndexOf(5), bi = ideal.BusIds.ToList().IndexOf(5);
        Assert.NotEqual(ideal.Voltage[oi][bi], norton.Voltage[oi][bi]);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(-0.1, 0.0)]
    [InlineData(double.NaN, 0.0)]
    [InlineData(double.PositiveInfinity, 0.0)]
    [InlineData(0.15, -0.01)]
    [InlineData(0.15, double.NaN)]
    public void N149_an_impossible_impedance_is_refused(double x, double r)
    {
        GridModel m = N149Model();
        Assert.Throws<ArgumentException>(() => FrequencyDependentYBus.Build(m, 5,
            HarmonicModelOptions.Study with { ActiveFrontEndReactancePu = x, ActiveFrontEndResistancePu = r }));
    }

    [Fact]
    public void N149_an_afe_unit_without_a_rating_fails_loud_naming_it()
    {
        GridModel m = IeeeCases.Case9();
        GridModel bad = m.CopyWith(generators: m.Generators.Append(new Generator(5, Pg: 10, MBase: 0, Name: "norating", Type: "solar")).ToList());
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FrequencyDependentYBus.Build(bad, 5, HarmonicModelOptions.Study with { ActiveFrontEndReactancePu = 0.15 }));
        Assert.Contains("'norating' at bus 5", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the CLI flags (built CLI, real process)

    [Fact]
    public void N149_cli_afe_reactance_on_uk_stamps_the_13_afe_units()
    {
        // data/uk: 16 converter units, 3 of them line-commutated (N036 test): 13 active-front-end units.
        CliProcess.Result r = CliProcess.RunBuilt("harmonics", "uk", "--converter-sources", "rated", "--afe-reactance", "0.15", "--afe-resistance", "0.005");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("  AFE impedance    : 13 active-front-end unit(s) stamped as resonant plant (G5 Issue 5 s.3.29), R + jhX = 0.005 + j0.15 pu on each unit's rating [VERIFY value]",
            r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void N149_cli_without_the_flag_prints_no_afe_line()
    {
        CliProcess.Result r = CliProcess.RunBuilt("harmonics", "uk", "--converter-sources", "rated");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.DoesNotContain("AFE impedance", r.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2, "--afe-resistance", "0.01")]
    [InlineData(2, "--afe-reactance", "0")]
    [InlineData(2, "--afe-reactance", "-0.1")]
    [InlineData(2, "--afe-reactance", "0.15", "--afe-resistance", "-1")]
    [InlineData(1, "--afe-reactance", "0.15")]
    public void N149_cli_refusals(int exit, params string[] flags)
    {
        // case9 has no converter: a valid --afe-reactance has nothing to stamp (exit 1).
        CliProcess.Result r = CliProcess.RunBuilt(new[] { "harmonics", "case9" }.Concat(flags).ToArray());
        Assert.True(r.ExitCode == exit, r.ToString());
    }
}
