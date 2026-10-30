using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using GridSim.Core.Harmonics;
using GridSim.Core.IO;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N075 (October 2026): <see cref="HarmonicPenetration.Solve"/> no longer drops spectrum components at unanalysed orders
/// silently for non-CLI callers: the result lists them (<see cref="HarmonicPenetrationResult.ComponentsOutsideOrders"/>)
/// and <see cref="HarmonicPenetration.RefuseComponentsOutsideOrders"/> is the fail-loud form. Voltages are unchanged.
///
/// <para>N036 (October 2026): <see cref="ConverterHarmonicSources"/>, the Core rule giving every converter-interfaced unit
/// its G5 Issue 5 Table B.3 profile (S139 pp.99-100) at its rated or dispatched current. Additive: the default study
/// (one 1/h six-pulse source, <see cref="HarmonicModelOptions.Study"/>) is untouched.</para>
/// </summary>
public sealed class N036N075ConverterHarmonicSourcesTests
{
    // ================================================================== N075

    private static HarmonicSource ArcFurnaceAt5() => HarmonicSourceLibrary.Make("arc-furnace", 5, 0.4);

    [Fact]
    public void Solve_lists_the_components_it_did_not_analyse()
    {
        // The built-in arc-furnace spectrum has orders 2, 3, 4, 5, 7; the default orders are odd 3..49, so 2 and 4
        // are not analysed. Default (not balanced) options so the 3rd is carried.
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(IeeeCases.Case9(), new[] { ArcFurnaceAt5() });
        Assert.Equal(new[]
        {
            "bus 5 (arc-furnace) order 2 at 5 %",
            "bus 5 (arc-furnace) order 4 at 3 %",
        }, pen.ComponentsOutsideOrders);
    }

    [Fact]
    public void The_report_changes_no_voltage()
    {
        // The same study with the unanalysed components removed from the spectrum by hand gives bit-identical
        // voltages at every order and bus: the report is information only.
        GridModel m = IeeeCases.Case9();
        HarmonicSource full = ArcFurnaceAt5();
        HarmonicSource trimmed = full with { Spectrum = full.Spectrum.Where(c => c.Order is not (2 or 4)).ToArray() };
        HarmonicPenetrationResult a = HarmonicPenetration.Solve(m, new[] { full });
        HarmonicPenetrationResult b = HarmonicPenetration.Solve(m, new[] { trimmed });
        Assert.Empty(b.ComponentsOutsideOrders);
        Assert.Equal(b.Orders, a.Orders);
        for (int oi = 0; oi < a.Orders.Count; oi++)
            for (int bi = 0; bi < a.BusIds.Count; bi++)
                Assert.Equal(b.Voltage[oi][bi], a.Voltage[oi][bi]);
    }

    [Fact]
    public void The_default_library_source_on_the_study_orders_reports_nothing()
    {
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(IeeeCases.Case9(),
            new[] { HarmonicSourceLibrary.Make("6-pulse", 5, 0.5) }, HarmonicModelOptions.Study);
        Assert.Empty(pen.ComponentsOutsideOrders);
    }

    [Fact]
    public void A_deliberate_subset_of_orders_is_reported_not_refused()
    {
        // Single-order studies (many existing tests) still run; the result says what the subset left out.
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(IeeeCases.Case9(),
            new[] { HarmonicSourceLibrary.Make("6-pulse", 5, 0.5) }, new HarmonicModelOptions { Orders = new[] { 5 } });
        Assert.Equal(7, pen.ComponentsOutsideOrders.Count);   // 7, 11, 13, 17, 19, 23, 25
        Assert.StartsWith("bus 5 (6-pulse) order 7 at ", pen.ComponentsOutsideOrders[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Refuse_throws_naming_every_component_and_passes_a_clean_set()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            HarmonicPenetration.RefuseComponentsOutsideOrders(new[] { ArcFurnaceAt5() }, new HarmonicModelOptions()));
        Assert.Contains("2 harmonic source component(s)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bus 5 (arc-furnace) order 2 at 5 %", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bus 5 (arc-furnace) order 4 at 3 %", ex.Message, StringComparison.Ordinal);
        HarmonicPenetration.RefuseComponentsOutsideOrders(new[] { HarmonicSourceLibrary.Make("6-pulse", 5, 0.5) }, HarmonicModelOptions.Study);
    }

    // ================================================================== N036

    /// <summary>case9 with: unit 2 (bus 3) a wind farm; a battery at bus 5 at zero output (50 MVA); a line-commutated
    /// HVDC link at bus 7 (fault current 0, 100 MVA, 20 MW); a power-flow-only converter at bus 9.</summary>
    private static GridModel MixedFleet()
    {
        GridModel m = IeeeCases.Case9();
        var gens = m.Generators.ToList();
        gens[2] = gens[2] with { Type = "wind", Name = "WF" };
        gens.Add(new Generator(5, Pg: 0, MBase: 50, Type: "battery", Name: "BESS"));
        gens.Add(new Generator(7, Pg: 20, Qg: 0, MBase: 100, Type: "hvdc", ConverterFaultCurrentPu: 0.0, Name: "LCC"));
        gens.Add(new Generator(9, Pg: 0, Type: "wind", PowerFlowOnly: true));
        return m.CopyWith(generators: gens);
    }

    [Fact]
    public void Profile_follows_the_technology()
    {
        Assert.Null(ConverterHarmonicSources.ProfileFor(new Generator(1, 10)));                                   // synchronous
        Assert.Null(ConverterHarmonicSources.ProfileFor(new Generator(1, 10, Type: "ccgt")));
        Assert.Equal("afe-g5", ConverterHarmonicSources.ProfileFor(new Generator(1, 10, Type: "wind_offshore")));
        Assert.Equal("afe-g5", ConverterHarmonicSources.ProfileFor(new Generator(1, 10, Type: "solar")));
        Assert.Equal("afe-g5", ConverterHarmonicSources.ProfileFor(new Generator(1, 10, Type: "interconnector")));   // VSC by default
        Assert.Equal("afe-g5", ConverterHarmonicSources.ProfileFor(new Generator(1, 10, ConverterInterfaced: true)));
        Assert.Equal("afe-g5", ConverterHarmonicSources.ProfileFor(new Generator(1, 10, Type: "hvdc", ConverterFaultCurrentPu: 1.1)));
        Assert.Equal("12-pulse-g5", ConverterHarmonicSources.ProfileFor(new Generator(1, 10, Type: "hvdc", ConverterFaultCurrentPu: 0.0)));
        Assert.Null(ConverterHarmonicSources.ProfileFor(new Generator(1, 10, Type: "wind", PowerFlowOnly: true)));
        Assert.Null(ConverterHarmonicSources.ProfileFor(new Generator(1, 10, Type: "wind", ConverterInterfaced: false)));
    }

    [Fact]
    public void Rated_level_gives_one_source_per_converter_at_mbase_over_basemva()
    {
        ConverterHarmonicSourceSet set = ConverterHarmonicSources.FromModel(MixedFleet());
        Assert.Equal(3, set.Sources.Count);
        HarmonicSource wf = set.Sources[0], bess = set.Sources[1], lcc = set.Sources[2];
        Assert.Equal((3, "afe-g5", 128.0 / 100.0, "WF"), (wf.Bus, wf.SourceType, wf.FundamentalCurrentPu, wf.Name));
        Assert.Equal((5, "afe-g5", 50.0 / 100.0, "BESS"), (bess.Bus, bess.SourceType, bess.FundamentalCurrentPu, bess.Name));
        Assert.Equal((7, "12-pulse-g5", 100.0 / 100.0, "LCC"), (lcc.Bus, lcc.SourceType, lcc.FundamentalCurrentPu, lcc.Name));
        Assert.All(set.Sources, s => Assert.Equal(GridSim.Core.Diagnostics.ProvenanceTag.CuratedPlaceholder, s.Provenance));
        Assert.Equal(new[] { "unit 5 (wind) at bus 9: a power-flow-only device, not physical plant" }, set.UnitsNotModelled);
    }

    [Fact]
    public void Each_component_is_the_g5_table_value_times_i1_at_phase_zero()
    {
        ConverterHarmonicSourceSet set = ConverterHarmonicSources.FromModel(MixedFleet());
        HarmonicSource wf = set.Sources[0];
        // G5 Table B.3 active front end, 5th: 0.034 pu -> 3.4 %; I1 = 1.28 pu.
        Assert.Equal(Complex.FromPolarCoordinates(3.4 / 100.0 * 1.28, 0.0), wf.InjectionAt(5));
        // Every kept component is exactly the library's.
        var lib = HarmonicSourceLibrary.ActiveFrontEndG5.ToDictionary(c => c.Order);
        Assert.All(wf.Spectrum, c => Assert.Equal(lib[c.Order], c));
        // Twelve-pulse kept whole (it has no triplen).
        Assert.Equal(HarmonicSourceLibrary.TwelvePulseG5, set.Sources[2].Spectrum);
    }

    [Fact]
    public void Balanced_study_leaves_out_and_lists_every_triplen()
    {
        ConverterHarmonicSourceSet set = ConverterHarmonicSources.FromModel(MixedFleet());
        int[] afeTriplens = HarmonicSourceLibrary.ActiveFrontEndG5.Where(c => c.Order % 3 == 0).Select(c => c.Order).ToArray();
        Assert.Equal(new[] { 3, 6, 9, 12, 15, 21, 27 }, afeTriplens);
        Assert.All(set.Sources, s => Assert.DoesNotContain(s.Spectrum, c => c.Order % 3 == 0));
        Assert.Equal(2 * afeTriplens.Length, set.OmittedComponents.Count);   // two AFE units
        Assert.Equal("unit 2 'WF' (wind) at bus 3 (afe-g5) order 3 at 0.5 %: a triplen (zero sequence), not carried by the balanced study",
            set.OmittedComponents[0]);
        Assert.Equal(HarmonicSourceLibrary.ActiveFrontEndG5.Count - afeTriplens.Length, set.Sources[0].Spectrum.Count);
    }

    [Fact]
    public void Unbalanced_form_keeps_every_table_component()
    {
        ConverterHarmonicSourceSet set = ConverterHarmonicSources.FromModel(MixedFleet(), balanced: false);
        Assert.Empty(set.OmittedComponents);
        Assert.Equal(HarmonicSourceLibrary.ActiveFrontEndG5, set.Sources[0].Spectrum);
    }

    [Fact]
    public void Dispatched_level_uses_the_unit_output_and_lists_a_unit_at_zero()
    {
        ConverterHarmonicSourceSet set = ConverterHarmonicSources.FromModel(MixedFleet(), ConverterHarmonicLevel.Dispatched);
        Assert.Equal(2, set.Sources.Count);
        Assert.Equal(Math.Sqrt(85.0 * 85.0 + -10.95 * -10.95) / 100.0, set.Sources[0].FundamentalCurrentPu);
        Assert.Equal(20.0 / 100.0, set.Sources[1].FundamentalCurrentPu);
        Assert.Equal(new[]
        {
            "unit 3 'BESS' (battery) at bus 5: zero output at this operating point (dispatched level)",
            "unit 5 (wind) at bus 9: a power-flow-only device, not physical plant",
        }, set.UnitsNotModelled);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Rated_level_refuses_a_unit_with_no_usable_rating(double mbase)
    {
        GridModel m = IeeeCases.Case9();
        var gens = m.Generators.ToList();
        gens[2] = gens[2] with { Type = "pv", MBase = mbase };
        var ex = Assert.Throws<ArgumentException>(() => ConverterHarmonicSources.FromModel(m.CopyWith(generators: gens)));
        Assert.Contains("unit 2 (pv) at bus 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispatched_level_refuses_a_non_finite_output()
    {
        GridModel m = IeeeCases.Case9();
        var gens = m.Generators.ToList();
        gens[2] = gens[2] with { Type = "pv", Qg = double.NaN };
        Assert.Throws<ArgumentException>(() => ConverterHarmonicSources.FromModel(m.CopyWith(generators: gens), ConverterHarmonicLevel.Dispatched));
    }

    [Fact]
    public void A_model_with_no_converter_gives_no_source()
    {
        ConverterHarmonicSourceSet set = ConverterHarmonicSources.FromModel(IeeeCases.Case9());
        Assert.Empty(set.Sources);
        Assert.Empty(set.OmittedComponents);
        Assert.Empty(set.UnitsNotModelled);
    }

    [Fact]
    public void Study_options_are_the_default_study_over_orders_2_to_50()
    {
        HarmonicModelOptions s = ConverterHarmonicSources.StudyOptions, d = HarmonicModelOptions.Study;
        Assert.Equal(Enumerable.Range(2, 49), s.Orders);
        Assert.Equal(d with { Orders = s.Orders }, s);
        // The default study itself is untouched: still the default odd 3..49 orders, the same list object.
        Assert.Same(HarmonicModelOptions.DefaultOrders, d.Orders);
    }

    [Fact]
    public void The_per_converter_study_runs_and_analyses_every_kept_component()
    {
        // End to end on the balanced study model: no triplen refusal (they were left out), nothing outside the
        // orders, and a finite non-zero voltage at a converter bus at the 5th.
        GridModel m = MixedFleet();
        ConverterHarmonicSourceSet set = ConverterHarmonicSources.FromModel(m);
        HarmonicPenetrationResult pen = HarmonicPenetration.Solve(m, set.Sources, ConverterHarmonicSources.StudyOptions);
        Assert.Empty(pen.ComponentsOutsideOrders);
        int bus3 = pen.BusIds.ToList().IndexOf(3);
        Complex v5 = pen.VoltageAt(5, bus3);
        Assert.True(double.IsFinite(v5.Magnitude) && v5.Magnitude > 0.0, $"|V5| at bus 3 = {v5.Magnitude}");
        // Run twice: bit-identical (the parallel per-order solve writes by index).
        HarmonicPenetrationResult again = HarmonicPenetration.Solve(m, set.Sources, ConverterHarmonicSources.StudyOptions);
        for (int oi = 0; oi < pen.Orders.Count; oi++)
            for (int bi = 0; bi < pen.BusIds.Count; bi++)
                Assert.Equal(pen.Voltage[oi][bi], again.Voltage[oi][bi]);
    }

    // ------------------------------------------------------------------ the CLI flag (built CLI, real process)

    [Fact]
    public void Cli_converter_sources_on_the_uk_case_names_the_sources_and_the_omissions()
    {
        // data/uk: 16 converter units (3 onshore, 2 offshore, 1 solar, 10 interconnectors), of which Moyle, IFA and
        // BritNed carry converterFaultCurrentPu 0 (line-commutated). 13 AFE units x 7 triplens = 91 omitted.
        CliProcess.Result r = CliProcess.RunBuilt("harmonics", "uk", "--converter-sources", "rated");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("16 source(s), orders 49", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("converter sources: 13 self-commutated (afe-g5), 3 line-commutated (12-pulse-g5), level rated", r.StdOut, StringComparison.Ordinal);
        Assert.Contains("omitted          : 91 triplen component(s)", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_converter_sources_on_a_case_with_no_converter_exits_1()
    {
        CliProcess.Result r = CliProcess.RunBuilt("harmonics", "case9", "--converter-sources", "rated");
        Assert.True(r.ExitCode == 1, r.ToString());
        Assert.Contains("has no converter-interfaced unit that injects", r.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--converter-sources", "nominal")]
    [InlineData("--converter-sources", "rated", "--synth-sources")]
    public void Cli_converter_sources_usage_errors_exit_2(params string[] flags)
    {
        CliProcess.Result r = CliProcess.RunBuilt(new[] { "harmonics", "uk" }.Concat(flags).ToArray());
        Assert.True(r.ExitCode == 2, r.ToString());
    }

    [Fact]
    public void Cli_default_harmonics_output_does_not_mention_converter_sources()
    {
        // Additive: without the flag the verb prints what it always printed (one synthetic source).
        CliProcess.Result r = CliProcess.RunBuilt("harmonics", "uk");
        Assert.True(r.ExitCode == 0, r.ToString());
        Assert.Contains("1 source(s), orders 24", r.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("converter sources", r.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_units_at_one_bus_add_linearly()
    {
        // G5 Issue 5 s.6.3.3 p.36: no declared diversity -> linear addition. Two identical converters at one bus give
        // exactly twice one converter's harmonic voltage (a linear solve, phase 0 sources).
        GridModel m = IeeeCases.Case9();
        var one = m.Generators.ToList();
        one.Add(new Generator(5, Pg: 0, MBase: 40, Type: "battery"));
        var two = one.ToList();
        two.Add(new Generator(5, Pg: 0, MBase: 40, Type: "battery"));
        GridModel m1 = m.CopyWith(generators: one), m2 = m.CopyWith(generators: two);
        HarmonicPenetrationResult p1 = HarmonicPenetration.Solve(m1, ConverterHarmonicSources.FromModel(m1).Sources, ConverterHarmonicSources.StudyOptions);
        HarmonicPenetrationResult p2 = HarmonicPenetration.Solve(m2, ConverterHarmonicSources.FromModel(m2).Sources, ConverterHarmonicSources.StudyOptions);
        int b = p1.BusIds.ToList().IndexOf(5);
        foreach (int h in new[] { 5, 7, 11 })
        {
            Complex v1 = p1.VoltageAt(h, b), v2 = p2.VoltageAt(h, b);
            Assert.True(Math.Abs(v2.Magnitude - 2.0 * v1.Magnitude) <= 1e-12 * v1.Magnitude,
                $"h{h}: |V| two units {v2.Magnitude:R} vs 2 x one unit {2.0 * v1.Magnitude:R}");
        }
    }
}
