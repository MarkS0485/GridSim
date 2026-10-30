using System.Globalization;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Presentation;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests.Ui;

/// <summary>
/// The ASSETS quirk fixes (ASSETS plan section 4.3, decided in section 8 decision 4: "copied as is for parity, then each
/// fixed as its own slice in both apps with its exact number change recorded"). The page models are shared by the WPF
/// ASSETS tab (thin adapters) and the Avalonia one, so each fix is tested here once on the shared model, on every
/// bundled case. UiAssetPagesParityTests still pins every other value against the verbatim old WPF code, with one named
/// exception per fix.
///
/// <para>The frames here carry the bundled model and an unsolved, empty result: what is tested (the kV chips, the OLTC
/// count) depends on the model alone, and an empty result keeps even gb-full and the continental case
/// cheap enough for this laptop. The parity test drives the same pages with live solved frames. The one exception is S5
/// (the home phasor card's angle spread), which needs solved angles: each bundled case is solved once there, by the
/// steady-state solver the live host uses (test changed 2026-10-20, ASSETS fix S5).</para>
/// </summary>
public sealed class UiAssetFixesTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------------------------------------------------------
    // The bundled cases
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Every bundled case: the MATPOWER case9 and every data folder carrying a system.json.</summary>
    public static TheoryData<string> BundledCases => new()
    {
        "case9", "uk", "gb-synthetic", "gb-full",
        "intl/continental-europe", "intl/france", "intl/iberia", "intl/ireland-all-island",
    };

    private static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Load one bundled case (fails loud when the file is missing).</summary>
    private static GridModel Load(string name)
    {
        if (name == "case9")
        {
            string file = Path.Combine(DataDir, "matpower", "lib", "case9.m");
            Assert.True(File.Exists(file), $"bundled case not found: {file}");
            return MatpowerParser.ParseFile(file);
        }
        string dir = Path.Combine(DataDir, name.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(Path.Combine(dir, "system.json")), $"bundled case not found: {dir}");
        return JsonGridLoader.LoadDirectory(dir);
    }

    /// <summary>A frame of the model with an empty, unsolved result (no bus voltages, no flows).</summary>
    private static SimFrame FrameOf(GridModel m) => new(m, new PowerFlowResult
    {
        Converged = false,
        Iterations = 0,
        MaxMismatch = double.NaN,
        BusIds = Array.Empty<int>(),
        Vm = Array.Empty<double>(),
        VaDeg = Array.Empty<double>(),
        PInjMw = Array.Empty<double>(),
        QInjMvar = Array.Empty<double>(),
        TotalLossMw = 0,
        TotalLossMvar = 0,
        LineFlows = Array.Empty<LineFlow>(),
    }, Hour: 0, Tick: 0, FrequencyHz: 50, GenerationMw: 0, DemandMw: 0);

    // ---------------------------------------------------------------------------------------------------------------
    // S4: the substation fleet's kV chips are rebuilt on a model swap, exactly as the circuit page's band chips are
    // ---------------------------------------------------------------------------------------------------------------

    private static SubstationFleetModel<KvFilterChipModel, SubstationFleetRowModel> NewSubstationFleet() =>
        new(new AssetExplorerModel());

    /// <summary>A model's voltage levels, worked out here independently of the page: distinct positive base kV,
    /// highest first.</summary>
    private static List<double> LevelsOf(GridModel m) =>
        m.Buses.Select(b => b.BaseKv).Where(kv => kv > 0).Distinct().OrderByDescending(kv => kv).ToList();

    /// <summary>
    /// The chip row is exactly the model's: "ALL" (NaN) then one chip per level in order, labelled "{kV:F0} kV" (the
    /// machine culture, as the page formats it), lit on exactly the expected chip (NaN = "ALL"); and the register is
    /// filtered by exactly that selection.
    /// </summary>
    private static void AssertChipsAre(SubstationFleetModel<KvFilterChipModel, SubstationFleetRowModel> page, GridModel m,
        double selected, string at)
    {
        List<double> levels = LevelsOf(m);
        Assert.True(page.Filters.Count == levels.Count + 1, $"{at}: want {levels.Count + 1} chips, got {page.Filters.Count}");
        Assert.Equal("ALL", page.Filters[0].Label);
        Assert.True(double.IsNaN(page.Filters[0].Kv), $"{at}: the first chip is not ALL (NaN)");
        Assert.True(page.Filters[0].Active == double.IsNaN(selected), $"{at}: ALL lit {page.Filters[0].Active}");
        for (int k = 0; k < levels.Count; k++)
        {
            KvFilterChipModel chip = page.Filters[k + 1];
            Assert.True(BitConverter.DoubleToInt64Bits(chip.Kv) == BitConverter.DoubleToInt64Bits(levels[k]),
                $"{at}: chip {k + 1} is {chip.Kv:R} kV, want {levels[k]:R}");
            Assert.Equal($"{levels[k]:F0} kV", chip.Label);
            Assert.True(chip.Active == (levels[k] == selected), $"{at}: chip {chip.Label} lit {chip.Active}");
        }
        Assert.Equal(1, page.Filters.Count(c => c.Active));

        // The register follows the selection: every bus (capped at 200 rows) for ALL, else exactly the buses within
        // 0.5 kV of the selected level (the page's own NearKv rule).
        int want = double.IsNaN(selected) ? m.Buses.Count : m.Buses.Count(b => Math.Abs(b.BaseKv - selected) < 0.5);
        Assert.True(page.Rows.Count == Math.Min(200, want), $"{at}: {page.Rows.Count} rows, want {Math.Min(200, want)}");
        if (!double.IsNaN(selected))
            Assert.All(page.Rows, r => Assert.True(Math.Abs(r.SortKv - selected) < 0.5, $"{at}: row {r.Name} at {r.SortKv} kV"));
    }

    /// <summary>The case sequences swapped under an open substation fleet page: back and forth, GB to GB, GB to
    /// international, levels kept, levels lost, no level in common.</summary>
    public static TheoryData<string[]> Swaps => new()
    {
        new[] { "uk", "case9", "uk" },
        new[] { "gb-synthetic", "uk" },
        new[] { "uk", "gb-synthetic", "uk" },
        new[] { "case9", "gb-synthetic", "case9" },
        new[] { "gb-full", "gb-synthetic", "gb-full" },
        new[] { "intl/france", "intl/iberia", "intl/continental-europe" },
        new[] { "uk", "intl/ireland-all-island", "gb-full" },
    };

    [Theory]
    [MemberData(nameof(Swaps))]
    public void S4_after_every_model_swap_the_kv_chips_are_exactly_the_new_models_levels(string[] cases)
    {
        GridModel[] models = cases.Select(Load).ToArray();

        // With nothing picked: after each swap the chips are the new model's, ALL lit.
        var page = NewSubstationFleet();
        for (int i = 0; i < models.Length; i++)
        {
            page.Refresh(FrameOf(models[i]));
            AssertChipsAre(page, models[i], double.NaN, $"{string.Join(" -> ", cases.Take(i + 1))} (ALL)");
        }

        // Each level of the first model picked, then every swap: the pick is kept while the running model still has
        // exactly that level, and falls back to ALL (for good, as on the circuit page) once a model lacks it.
        foreach (double pick in LevelsOf(models[0]))
        {
            var p = NewSubstationFleet();
            p.Refresh(FrameOf(models[0]));
            KvFilterChipModel chip = p.Filters.Single(c => c.Kv == pick);
            chip.OnToggle();   // re-filters at once on the last frame
            AssertChipsAre(p, models[0], pick, $"{cases[0]} with {pick} kV picked");
            double selected = pick;
            for (int i = 1; i < models.Length; i++)
            {
                if (!LevelsOf(models[i]).Contains(selected)) selected = double.NaN;
                p.Refresh(FrameOf(models[i]));
                AssertChipsAre(p, models[i], selected, $"{string.Join(" -> ", cases.Take(i + 1))} with {pick} kV picked first");
            }
        }
    }

    [Fact]
    public void S4_the_swaps_cover_a_kept_pick_a_lost_pick_and_no_common_level()
    {
        // The theory above is not vacuous: its swaps include a pick that survives (400 kV, gb-synthetic -> uk), a pick
        // that vanishes (275 kV, gb-synthetic -> uk) and a swap with no level in common (uk -> ireland-all-island).
        List<double> gbs = LevelsOf(Load("gb-synthetic")), uk = LevelsOf(Load("uk")), ie = LevelsOf(Load("intl/ireland-all-island"));
        Assert.Contains(400.0, gbs);
        Assert.Contains(400.0, uk);
        Assert.Contains(275.0, gbs);
        Assert.DoesNotContain(275.0, uk);
        Assert.Empty(uk.Intersect(ie));
    }

    [Theory]
    [MemberData(nameof(BundledCases))]
    public void S4_an_unchanged_model_keeps_its_chip_objects_and_the_pick(string name)
    {
        GridModel m = Load(name);
        var page = NewSubstationFleet();
        page.Refresh(FrameOf(m));
        AssertChipsAre(page, m, double.NaN, $"{name} first frame");
        KvFilterChipModel[] before = page.Filters.ToArray();

        // The same model again, and a freshly loaded copy of it (a different object with the same levels): nothing is
        // rebuilt, so the chips are the same objects and their bindings stay live.
        page.Refresh(FrameOf(m));
        page.Refresh(FrameOf(Load(name)));
        Assert.Equal(before.Length, page.Filters.Count);
        for (int k = 0; k < before.Length; k++) Assert.Same(before[k], page.Filters[k]);

        // A pick survives frames of an unchanged model.
        double lowest = LevelsOf(m)[^1];
        page.Filters[^1].OnToggle();
        page.Refresh(FrameOf(m));
        AssertChipsAre(page, m, lowest, $"{name} lowest level picked, then a frame");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // S12: "OLTC FITTED" counts transformers with a mapped OLTC record, not OLTC records
    // ---------------------------------------------------------------------------------------------------------------

    private static TransformerFleetModel<TransformerFleetRowModel> NewTransformerFleet() => new(new AssetExplorerModel());

    /// <summary>The fixed tile, worked out here independently of the page: transformer branches that at least one OLTC
    /// record maps to by the solver's own rule, "of" every transformer branch.</summary>
    private static string FittedOf(GridModel m)
    {
        int[] map = SteadyStateSolver.MapOltcRecordsToBranches(m);
        int fitted = map.Where(i => i >= 0 && m.Branches[i].IsTransformer).Distinct().Count();
        return $"{fitted} of {m.Branches.Count(b => b.IsTransformer)}";
    }

    /// <summary>
    /// The tile on every bundled case: exactly the mapped-transformer count, and the exact value before the fix (OLTC
    /// records of transformers) and after, pinned. Every bundled case maps each record to its own transformer, so the
    /// value does not change on any of them; the built case below shows the difference.
    /// </summary>
    [Theory]
    [InlineData("case9", 0, "0 of 0", "0 of 0")]
    [InlineData("uk", 10, "10 of 10", "10 of 10")]
    [InlineData("gb-synthetic", 357, "357 of 2168", "357 of 2168")]
    [InlineData("gb-full", 1383, "1383 of 2620", "1383 of 2620")]
    [InlineData("intl/continental-europe", 0, "0 of 682", "0 of 682")]
    [InlineData("intl/france", 0, "0 of 242", "0 of 242")]
    [InlineData("intl/iberia", 0, "0 of 112", "0 of 112")]
    [InlineData("intl/ireland-all-island", 0, "0 of 5", "0 of 5")]
    public void S12_oltc_fitted_on_every_bundled_case_counts_transformers_with_a_mapped_oltc(
        string name, int records, string before, string after)
    {
        GridModel m = Load(name);
        Assert.Equal(records, m.Oltcs.Count);
        // The value before the fix: the old rule, records of transformers.
        Assert.Equal(before, $"{m.Oltcs.Count} of {m.Branches.Count(b => b.IsTransformer)}");
        Assert.Equal(after, FittedOf(m));

        var page = NewTransformerFleet();
        page.Refresh(FrameOf(m));
        Assert.Equal(after, page.OltcFitted);
        // The tile agrees with the whole fleet's own "OLTC" tags when every transformer is listed (under the 200-row cap).
        if (page.Rows.Count == m.Branches.Count(b => b.IsTransformer))
            Assert.Equal(after, $"{page.Rows.Count(r => r.HasOltc)} of {page.Rows.Count}");
    }

    [Fact]
    public void S12_two_records_on_one_transformer_and_a_record_on_no_branch_count_once_and_not_at_all()
    {
        // The uk case (10 transformers, 10 OLTC records, one each) with two records added: a second record for the
        // first record's transformer and controlled bus (it maps to the same branch), and a record naming a transformer
        // that is not in the model (it maps to no branch, -1).
        GridModel uk = Load("uk");
        Oltc first = uk.Oltcs[0];
        var oltcs = uk.Oltcs.ToList();
        oltcs.Add(first with { TargetVpu = first.TargetVpu + 0.01 });
        oltcs.Add(new Oltc("no such transformer", first.ControlsBus));
        var built = new GridModel { BaseMva = uk.BaseMva, Buses = uk.Buses, Generators = uk.Generators, Branches = uk.Branches, Oltcs = oltcs };

        int[] map = SteadyStateSolver.MapOltcRecordsToBranches(built);
        Assert.Equal(map[0], map[^2]);   // the two records share a branch
        Assert.Equal(-1, map[^1]);       // the last record maps to no branch
        Assert.True(built.Branches[map[0]].IsTransformer);

        var page = NewTransformerFleet();
        page.Refresh(FrameOf(built));
        // Before the fix the tile read the record count, "12 of 10" (more fitted than there are transformers); the
        // shared record counts once and the unmapped record not at all, so it reads "10 of 10".
        Assert.Equal("12 of 10", $"{built.Oltcs.Count} of {built.Branches.Count(b => b.IsTransformer)}");
        Assert.Equal("10 of 10", page.OltcFitted);
        Assert.Equal(FittedOf(built), page.OltcFitted);

        // And with the first record's transformer left with no record at all (its record renamed away), the fleet has
        // one transformer fewer fitted: "9 of 10" where the record count still says 10.
        oltcs[0] = first with { Transformer = "renamed away" };
        oltcs.RemoveAt(oltcs.Count - 2);
        var lost = new GridModel { BaseMva = uk.BaseMva, Buses = uk.Buses, Generators = uk.Generators, Branches = uk.Branches, Oltcs = oltcs };
        page.Refresh(FrameOf(lost));
        Assert.Equal(11, lost.Oltcs.Count);
        Assert.Equal("9 of 10", page.OltcFitted);
        Assert.Equal(FittedOf(lost), page.OltcFitted);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // S5: the home GENERATOR PHASORS card's angle spread is over the generator buses, as the phasor page's is
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>One solved frame of a bundled case: the steady-state solver the live host uses (Newton with the OLTC
    /// outer loop), its regulated model as the frame's model, as a live frame carries it.</summary>
    private static SimFrame SolvedFrameOf(string name)
    {
        GridModel m = Load(name);
        var solver = new SteadyStateSolver();
        PowerFlowResult r = solver.Solve(m);
        Assert.True(r.VaDeg.Length > 0, $"{name}: the solve returned no bus angles");
        return new SimFrame(solver.RegulatedModel, r, Hour: 0, Tick: 1, FrequencyHz: 50, GenerationMw: 0, DemandMw: 0);
    }

    /// <summary>The spread before the fix, worked out here (the old card's rule): max minus min over EVERY bus angle of
    /// the result; 0 when there are none.</summary>
    private static double AllBusSpread(PowerFlowResult r) =>
        r.VaDeg.Length > 0 ? r.VaDeg.Max() - r.VaDeg.Min() : 0;

    /// <summary>The card's figure as shown: "{spread:F1} deg", or " - " at no spread (Metric then MetricUnit).</summary>
    private static string CardText(double spread) => spread > 0 ? $"{spread:F1} deg" : " - ";

    /// <summary>
    /// Every bundled case, solved: the card's figure before the fix (the spread over all buses) and after (the spread
    /// over the generator buses), pinned as the card shows them under the invariant culture; and after the fix the card
    /// shows exactly the phasor page's figure, with its colour and bar worked from that same spread. The exact doubles
    /// (round-trip) are written to the test output for the record.
    /// </summary>
    [Theory]
    // The exact spreads (round-trip doubles, 2026-10-20, this build's solve), before (all buses) -> after (generator
    // buses): case9 13.268810754494263 -> 9.280005481642787; uk 5.758768276821117 -> 3.382592293049997; gb-synthetic
    // 52.67146233680553 -> 45.156203364973564; gb-full 60.425663531394456 -> 51.301288444300965; continental-europe
    // 187.69108329419657 -> 187.51744454959086; france 192.69659895669983 -> 192.69659895669983 (no change: its extreme
    // buses carry generators); iberia 102.65339633559068 -> 99.59668243648616; ireland-all-island 8.943392486654346 ->
    // 8.78879813743572. The colour band (15 / 30 deg) changes on no bundled case; the bar (60 deg = full 220) moves
    // wherever the spread moves below 60 deg, and on gb-full from full (220) to 51.30... / 60 * 220.
    [InlineData("case9", "13.3 deg", "9.3 deg")]
    [InlineData("uk", "5.8 deg", "3.4 deg")]
    [InlineData("gb-synthetic", "52.7 deg", "45.2 deg")]
    [InlineData("gb-full", "60.4 deg", "51.3 deg")]
    [InlineData("intl/continental-europe", "187.7 deg", "187.5 deg")]
    [InlineData("intl/france", "192.7 deg", "192.7 deg")]
    [InlineData("intl/iberia", "102.7 deg", "99.6 deg")]
    [InlineData("intl/ireland-all-island", "8.9 deg", "8.8 deg")]
    public void S5_home_phasor_card_spread_equals_the_phasor_pages_spread_on_every_bundled_case(
        string name, string before, string after)
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            SimFrame f = SolvedFrameOf(name);

            // The landing page as the explorer shows it (the explorer refreshes its visible landing page on a frame).
            var x = new AssetExplorerModel();
            x.Update(f, "00:00");
            var home = Assert.IsType<AssetHomeModel<AssetClassCardModel>>(x.Home);
            AssetClassCardModel card = home.Classes.Single(c => c.Title == "GENERATOR PHASORS");
            Assert.Equal("ANGLE SPREAD", card.MetricLabel);   // the label is unchanged by S5

            // The phasor page the card opens, on the same frame.
            var page = new GeneratorPhasorFleetModel<PhasorTileModel>(x);
            page.Refresh(f);

            // The generator-bus spread, worked out independently of both pages (the parity test's own copy of the rule).
            double? genSpread = UiAssetPagesParityTests.GeneratorBusSpread(f.Model, f.Result);
            double allSpread = AllBusSpread(f.Result);
            output.WriteLine($"{name}: before (all {f.Result.VaDeg.Length} buses) {allSpread:R} deg -> \"{CardText(allSpread)}\"; " +
                $"after (generator buses) {(genSpread is double g ? g.ToString("R", CultureInfo.InvariantCulture) : "none")} deg -> \"{CardText(genSpread ?? 0)}\"; " +
                $"page \"{page.AngleSpread}\"");

            // The phasor page is unchanged by S5: its figure is the generator-bus spread.
            Assert.Equal(genSpread is double gs ? $"{gs:F1} deg spread" : " - ", page.AngleSpread);

            // The card now shows exactly the page's figure (the page says "x deg spread", the card "x" then " deg").
            double spread = genSpread ?? 0;
            Assert.Equal(CardText(spread), card.Metric + card.MetricUnit);
            if (spread > 0) Assert.Equal(page.AngleSpread, $"{card.Metric}{card.MetricUnit} spread");
            UiColour colour = spread > 30 ? ColourRamps.Loading(0.9) : spread > 15 ? ColourRamps.Loading(0.6) : ColourRamps.Voltage(1.0);
            Assert.Equal(colour, card.MetricColour);
            Assert.Equal(BitConverter.DoubleToInt64Bits(Math.Min(1.0, spread / 60.0) * AssetClassCardModel.BarTrack),
                BitConverter.DoubleToInt64Bits(card.BarWidth));

            // The exact change on this case, pinned: before (all buses) and after (generator buses).
            Assert.Equal(before, CardText(allSpread));
            Assert.Equal(after, card.Metric + card.MetricUnit);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // S10: circuit voltage bands, the GB ladder when within 0.5 kV of a level, the circuit's own kV otherwise
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Count per band, highest band first, as "band:count" joined by single spaces.</summary>
    private static string Counts(IEnumerable<int> bands) =>
        string.Join(" ", bands.GroupBy(b => b).OrderByDescending(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"));

    /// <summary>
    /// Every bundled case: every circuit's band before the fix (the nearest GB ladder level) and after (the ladder level
    /// within 0.5 kV, else its own kV), as counts per band, pinned. The GB cases (uk, gb-synthetic, gb-full) have every
    /// circuit on the ladder, so not one circuit's band changes there; on case9 (MATPOWER, 345 kV) and the international
    /// cases the real levels appear. Then the pages and the tree follow the one rule: the band chips are exactly the
    /// distinct new bands, highest first, labelled "{band} kV"; every fleet row (the 200 most loaded) carries its
    /// circuit's new band; the first circuit's page names its new band; and the ASSETS tree's CABLES &amp; LINES groups
    /// (which read the same CircuitInfo.Band) are labelled exactly as the chips.
    /// </summary>
    [Theory]
    // Circuits whose band changes: case9 9 of 9 (345 kV: 400 -> 345); uk 0 of 14; gb-synthetic 0 of 100; gb-full 0 of
    // 3298; continental-europe 7397 of 7397 (750, 500 and 380 kV: 400 -> their own; 300 and 220 kV: 275 -> their own);
    // france 2374 of 2376 (its two 132 kV circuits stay 132); iberia 2009 of 2022 (its 13 at 132 kV stay);
    // ireland-all-island 77 of 77.
    [InlineData("case9", "400:9", "345:9")]
    [InlineData("uk", "400:14", "400:14")]
    [InlineData("gb-synthetic", "400:42 275:31 132:27", "400:42 275:31 132:27")]
    [InlineData("gb-full", "400:268 275:311 132:2719", "400:268 275:311 132:2719")]
    [InlineData("intl/continental-europe", "400:2975 275:4422", "750:30 500:1 380:2944 300:597 220:3825")]
    [InlineData("intl/france", "400:869 275:1505 132:2", "380:869 300:11 220:1494 132:2")]
    [InlineData("intl/iberia", "400:592 275:1417 132:13", "380:592 220:1417 132:13")]
    [InlineData("intl/ireland-all-island", "400:3 275:74", "380:3 220:74")]
    public void S10_circuit_bands_on_every_bundled_case_before_and_after(string name, string before, string after)
    {
        GridModel m = Load(name);
        List<(int Index, double Kv)> circuits = Enumerable.Range(0, m.Branches.Count)
            .Where(i => !m.Branches[i].IsTransformer)
            .Select(i => (i, UiAssetPagesParityTests.CircuitKv(m, m.Branches[i])))
            .ToList();
        Assert.NotEmpty(circuits);

        // Every circuit, before and after: the page's rule (CircuitInfo.Band) is the new rule worked out independently.
        var oldBands = circuits.Select(c => UiAssetPagesParityTests.OldCircuitBand(c.Kv)).ToList();
        var newBands = circuits.Select(c => UiAssetPagesParityTests.NewCircuitBand(c.Kv)).ToList();
        foreach ((int index, double kv) in circuits)
            Assert.True(CircuitInfo.Band(kv) == UiAssetPagesParityTests.NewCircuitBand(kv), $"{name}: circuit {index} at {kv:R} kV banded {CircuitInfo.Band(kv)}");
        int moved = Enumerable.Range(0, circuits.Count).Count(k => oldBands[k] != newBands[k]);
        output.WriteLine($"{name}: {circuits.Count} circuits, {moved} change band; before {Counts(oldBands)}; after {Counts(newBands)}");
        Assert.Equal(before, Counts(oldBands));
        Assert.Equal(after, Counts(newBands));

        // The GB cases: not one circuit changes band.
        if (name is "uk" or "gb-synthetic" or "gb-full") Assert.Equal(0, moved);

        // The fleet page: the chip row and every row it lists.
        var page = new CircuitFleetModel<BandChipModel, CircuitFleetRowModel>(new AssetExplorerModel());
        page.Refresh(FrameOf(m));
        List<int> distinct = newBands.Distinct().OrderByDescending(b => b).ToList();
        Assert.Equal(new[] { "ALL" }.Concat(distinct.Select(b => $"{b} kV")), page.Bands.Select(c => c.Label));
        Assert.Equal(new[] { 0 }.Concat(distinct), page.Bands.Select(c => c.Band));
        Assert.NotEmpty(page.Circuits);
        foreach (CircuitFleetRowModel row in page.Circuits)
        {
            int want = UiAssetPagesParityTests.NewCircuitBand(UiAssetPagesParityTests.CircuitKv(m, m.Branches[row.BranchIndex]));
            Assert.Equal(want, row.Band);
            Assert.Equal($"{want} kV", row.BandLabel);
        }

        // The circuit page of the first circuit names its new band.
        (int first, double firstKv) = circuits[0];
        Branch b0 = m.Branches[first];
        var detail = new CircuitDetailModel<CircuitStatusRowModel>(first, b0.From, b0.To, b0.Name, new AssetExplorerModel());
        detail.Refresh(FrameOf(m));
        Assert.EndsWith($"  {UiAssetPagesParityTests.NewCircuitBand(firstKv)} kV", detail.Sub, StringComparison.Ordinal);

        // The ASSETS tree groups its circuits by the same band, labelled as the chips.
        AssetTree tree = AssetTree.Build(m);
        AssetTreeNode cables = tree.Roots.Single(r => r.Page.Kind == AssetPageKind.CircuitFleet);
        Assert.Equal(distinct.Select(b => $"{b} kV"), cables.Children.Select(g => g.Label));
    }

    [Fact]
    public void S10_the_band_rule_at_its_edges()
    {
        // On the ladder, and within (strictly) 0.5 kV of it: the ladder level, as before.
        Assert.Equal(400, CircuitInfo.Band(400));
        Assert.Equal(400, CircuitInfo.Band(399.9));
        Assert.Equal(400, CircuitInfo.Band(400.49));
        Assert.Equal(132, CircuitInfo.Band(131.6));
        Assert.Equal(11, CircuitInfo.Band(11));
        // Exactly 0.5 kV away is off the ladder: its own kV, rounded half away from zero.
        Assert.Equal(133, CircuitInfo.Band(132.5));
        Assert.Equal(400, CircuitInfo.Band(399.5));   // off the ladder by exactly 0.5 kV, and rounds to 400 itself
        Assert.Equal(399, CircuitInfo.Band(398.6));
        // The real non-GB levels read as themselves (before the fix: 275, 275, 400, 400, 400, 400, 11).
        Assert.Equal(220, CircuitInfo.Band(220));
        Assert.Equal(300, CircuitInfo.Band(300));
        Assert.Equal(345, CircuitInfo.Band(345));
        Assert.Equal(380, CircuitInfo.Band(380));
        Assert.Equal(500, CircuitInfo.Band(500));
        Assert.Equal(750, CircuitInfo.Band(750));
        Assert.Equal(7, CircuitInfo.Band(6.6));
        Assert.Equal("380 kV", CircuitInfo.Label(CircuitInfo.Band(380)));
        // No usable kV (rounds below 1 kV, or NaN): the old nearest-level snap, so band 0 (the "ALL" chip) never appears.
        Assert.Equal(11, CircuitInfo.Band(0));
        Assert.Equal(11, CircuitInfo.Band(0.4));
        Assert.Equal(11, CircuitInfo.Band(-5));
        Assert.Equal(400, CircuitInfo.Band(double.NaN));
        Assert.Equal(1, CircuitInfo.Band(0.5));
        // The test's own copies of both rules agree with the page at every one of these values.
        foreach (double kv in new[] { 400, 399.9, 400.49, 131.6, 11, 132.5, 399.5, 398.6, 220, 300, 345, 380, 500, 750, 6.6, 0, 0.4, -5, double.NaN, 0.5, 22, 25 })
            Assert.Equal(UiAssetPagesParityTests.NewCircuitBand(kv), CircuitInfo.Band(kv));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // S3: one per-frame lookup, first-wins (the substation fleet's voltage table was last-wins)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every bundled case: the bus ids that appear more than once (the only place the substation fleet's old last-wins
    /// voltage table and the shared first-wins table can differ), listed and pinned: none on any bundled case. The
    /// model's bus list is what the solved result's BusIds are built from, so a case with no repeated model bus id has no
    /// repeated result id either; the parity walk (UiAssetPagesParityTests) checks the solved results of case9, uk and
    /// gb-synthetic too (its S3 exception never applied there). With an unsolved frame the page shows no voltages, so the
    /// table's rule cannot show there; what is checked on every case is that both rules build the same table from the
    /// model's own ids, so nothing displayed can change.
    /// </summary>
    [Theory]
    [MemberData(nameof(BundledCases))]
    public void S3_no_bundled_case_has_a_repeated_bus_id(string name)
    {
        GridModel m = Load(name);
        List<int> repeated = UiAssetPagesParityTests.RepeatedIds(m.Buses.Select(b => b.Id));
        output.WriteLine($"{name}: {m.Buses.Count} buses, repeated bus ids: {(repeated.Count == 0 ? "none" : string.Join(", ", repeated))}");
        Assert.Empty(repeated);

        // A result laid out as the solver lays it out (one entry per model bus, in model order), each bus a different
        // voltage: the old rule (last-wins) and the shared lookup (first-wins) give the same table, entry for entry.
        int[] ids = m.Buses.Select(b => b.Id).ToArray();
        double[] vm = Enumerable.Range(0, ids.Length).Select(i => 0.9 + i * 1e-6).ToArray();
        var r = new PowerFlowResult
        {
            Converged = true, Iterations = 1, MaxMismatch = 0, BusIds = ids, Vm = vm, VaDeg = new double[ids.Length],
            PInjMw = new double[ids.Length], QInjMvar = new double[ids.Length], TotalLossMw = 0, TotalLossMvar = 0,
            LineFlows = Array.Empty<LineFlow>(),
        };
        Dictionary<int, double> shared = FrameLookup.ByBusId(r.BusIds, r.Vm);
        Dictionary<int, double> last = UiAssetPagesParityTests.LastWinsVm(r);
        Assert.Equal(last.Count, shared.Count);
        foreach ((int id, double v) in last)
            Assert.True(BitConverter.DoubleToInt64Bits(v) == BitConverter.DoubleToInt64Bits(shared[id]), $"{name}: bus {id}");
    }

    /// <summary>
    /// The shared lookup is first-wins, by behaviour: for a repeated id it keeps the FIRST entry's value and the FIRST
    /// position, for every other id the one entry; and it is exactly the old inline TryAdd loops the home page, the phasor
    /// page and the transformer pages carried (so those pages are unchanged).
    /// </summary>
    [Fact]
    public void S3_the_shared_lookup_keeps_the_first_entry_for_a_repeated_id()
    {
        int[] ids = { 7, 3, 7, 9, 3, 7 };
        double[] values = { 1.01, 0.97, 0.88, 1.04, 1.10, 0.92 };
        Dictionary<int, double> byId = FrameLookup.ByBusId(ids, values);
        Assert.Equal(new[] { 7, 3, 9 }, byId.Keys);
        Assert.Equal(1.01, byId[7]);   // not 0.88 or 0.92 (the later id-7 entries)
        Assert.Equal(0.97, byId[3]);   // not 1.10
        Assert.Equal(1.04, byId[9]);
        Dictionary<int, int> at = FrameLookup.IndexByBusId(ids);
        Assert.Equal(0, at[7]);
        Assert.Equal(1, at[3]);
        Assert.Equal(3, at[9]);

        // The old inline loops, worked out here: identical tables.
        var old = new Dictionary<int, double>();
        for (int i = 0; i < ids.Length; i++) old.TryAdd(ids[i], values[i]);
        Assert.Equal(old, byId);
        var oldIdx = new Dictionary<int, int>();
        for (int i = 0; i < ids.Length; i++) oldIdx.TryAdd(ids[i], i);
        Assert.Equal(oldIdx, at);

        // Empty in, empty out; a missing argument fails loud.
        Assert.Empty(FrameLookup.ByBusId(Array.Empty<int>(), Array.Empty<double>()));
        Assert.Empty(FrameLookup.IndexByBusId(Array.Empty<int>()));
        Assert.Throws<ArgumentNullException>(() => FrameLookup.ByBusId(null!, values));
        Assert.Throws<ArgumentNullException>(() => FrameLookup.ByBusId(ids, null!));
        Assert.Throws<ArgumentNullException>(() => FrameLookup.IndexByBusId(null!));
    }

    /// <summary>
    /// A built case with a repeated bus id (two buses carry id 2; the result's first id-2 entry is 1.02 pu, the second
    /// 0.90 pu): the substation fleet now reads the FIRST entry, as every other page does. The exact change, under the
    /// invariant culture: OUT OF BAND "2" before (both id-2 buses read 0.90 pu, below the 0.94 pu band) and "0" after;
    /// the "Centre 400 B" row's voltage "0.900" before and "1.020" after, its colour ColourRamps.Voltage(0.90) before and
    /// ColourRamps.Voltage(1.02) after. The substation page of bus 2 already read 1.02 pu (its own first-match scan): the
    /// fleet now agrees with it.
    /// </summary>
    [Fact]
    public void S3_a_repeated_bus_id_reads_the_first_entry_on_the_substation_fleet()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            SimFrame f = UiAssetPagesParityTests.RepeatedBusIdFrame();
            Assert.Equal(new[] { 2 }, UiAssetPagesParityTests.RepeatedIds(f.Model.Buses.Select(b => b.Id)));
            Assert.Equal(new[] { 2 }, UiAssetPagesParityTests.RepeatedIds(f.Result.BusIds));

            // Before (the old rule, worked out here) and after (the page).
            Dictionary<int, double> last = UiAssetPagesParityTests.LastWinsVm(f.Result);
            Dictionary<int, double> first = UiAssetPagesParityTests.FirstWinsVm(f.Result);
            Assert.Equal(0.90, last[2]);
            Assert.Equal(1.02, first[2]);
            Assert.Equal(2, UiAssetPagesParityTests.OutOfBandBy(f.Model, last));
            Assert.Equal(0, UiAssetPagesParityTests.OutOfBandBy(f.Model, first));

            var page = NewSubstationFleet();
            page.Refresh(f);
            Assert.Equal("0", page.OutOfBand);
            SubstationFleetRowModel b = page.Rows.Single(x => x.Name == "Centre 400 B");
            Assert.Equal("1.020", b.Vm);
            Assert.Equal(ColourRamps.Voltage(1.02), b.VmColour);
            Assert.NotEqual(ColourRamps.Voltage(0.90), b.VmColour);
            // The other rows: unchanged by S3 (each id is single, or the row is not the one the page keeps for id 2).
            Assert.Equal("1.000", page.Rows.Single(x => x.Name == "North 400").Vm);
            Assert.Equal("0.970", page.Rows.Single(x => x.Name == "South 132").Vm);
            Assert.Equal(" - ", page.Rows.Single(x => x.Name == "Centre 400 A").Vm);

            // The fleet now agrees with the substation page of bus 2 (first-wins by its own linear scan).
            var detail = new SubstationDetailModel<PlantRowModel, ConnectionRowModel>(2, new AssetExplorerModel());
            detail.Refresh(f);
            output.WriteLine($"substation page of bus 2: Voltage \"{detail.Voltage}\"");
            Assert.StartsWith("1.020", detail.Voltage, StringComparison.Ordinal);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // S7: the transformer PROTECTION panel shows the case's protection settings where it has them
    // ---------------------------------------------------------------------------------------------------------------

    private static TransformerDetailModel<TransformerReadingModel, TransformerEventModel> TransformerPage(GridModel m, int index) =>
        new(index, m.Branches[index].From, m.Branches[index].To, m.Branches[index].Name, new AssetExplorerModel());

    /// <summary>A panel's rows as (label, value, status) and its chip colours.</summary>
    private static List<(string Label, string Value, string Status)> RowsOf(IEnumerable<TransformerReadingModel> rows) =>
        rows.Select(r => (r.Label, r.Value, r.Status)).ToList();

    /// <summary>The panel is exactly the five demo rows (text and chip colour), and the tag flag says so.</summary>
    private static void AssertDemoPanel(TransformerDetailModel<TransformerReadingModel, TransformerEventModel> page, string at)
    {
        Assert.True(page.ProtectionIsDemo, $"{at}: the STATIC DEMO tag flag is off on the demo panel");
        Assert.Equal(UiAssetPagesParityTests.DemoProtectionRows.ToList(), RowsOf(page.Protection));
        Assert.All(page.Protection, r => Assert.Equal(ColourRamps.Loading(0.0), r.ChipColour));
    }

    /// <summary>
    /// The page's unit table covers exactly the parameters a protection class may carry (the loader refuses any other
    /// key, so no protection parameter can show without its unit), and agrees with the test's own table.
    /// </summary>
    [Fact]
    public void S7_every_protection_parameter_has_its_unit()
    {
        var accepted = GridSim.Core.Assets.AssetClassSupport.AcceptedKeys(GridSim.Core.Assets.AssetKind.Protection);
        Assert.Equal(accepted.OrderBy(k => k, StringComparer.Ordinal), TransformerProtection.Units.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(UiAssetPagesParityTests.ProtectionUnits.OrderBy(kv => kv.Key, StringComparer.Ordinal),
            TransformerProtection.Units.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every bundled case, every transformer: where the case has protection settings for the transformer the PROTECTION
    /// panel lists exactly them (the test's own rule, UiAssetPagesParityTests.ExpectedProtectionRows) with the STATIC DEMO
    /// tag off; everywhere else it is the five demo rows exactly as before, tag on. Only gb-synthetic carries protections
    /// (two devices, both monitoring bus 1), and exactly one of its transformers ("NSC GSP 2 SGT", bus 1 to bus 96) is
    /// linked to them; every other transformer of every bundled case keeps the demo panel byte for byte.
    /// </summary>
    [Theory]
    // Transformers whose panel shows real rows: gb-synthetic 1 (NSC GSP 2 SGT); every other bundled case 0 (no
    // protections.json).
    [InlineData("case9", 0)]
    [InlineData("uk", 0)]
    [InlineData("gb-synthetic", 1)]
    [InlineData("gb-full", 0)]
    [InlineData("intl/continental-europe", 0)]
    [InlineData("intl/france", 0)]
    [InlineData("intl/iberia", 0)]
    [InlineData("intl/ireland-all-island", 0)]
    public void S7_protection_panel_on_every_transformer_of_every_bundled_case(string name, int withSettings)
    {
        GridModel m = Load(name);
        SimFrame f = FrameOf(m);
        Assert.Equal(name == "gb-synthetic", m.Protections is not null);
        int real = 0, transformers = 0;
        for (int i = 0; i < m.Branches.Count; i++)
        {
            if (!m.Branches[i].IsTransformer) continue;
            transformers++;
            var page = TransformerPage(m, i);
            page.Refresh(f);
            var want = UiAssetPagesParityTests.ExpectedProtectionRows(m, m.Branches[i], page.HvBusId, page.LvBusId);
            if (want.Count == 0)
            {
                AssertDemoPanel(page, $"{name} transformer {i}");
                continue;
            }
            real++;
            Assert.False(page.ProtectionIsDemo, $"{name} transformer {i}: the tag flag is on over real settings");
            Assert.Equal(want, RowsOf(page.Protection));
            Assert.All(page.Protection, r => Assert.Equal(GridSim.Ui.Dashboard.DashboardColours.Gray, r.ChipColour));
            output.WriteLine($"{name}: transformer {i} \"{m.Branches[i].Name}\" (HV bus {page.HvBusId}, LV bus {page.LvBusId}):");
            foreach (TransformerReadingModel r in page.Protection) output.WriteLine($"  {r.Label} | {r.Value} | {r.Status}");
        }
        output.WriteLine($"{name}: {transformers} transformers, {real} with protection settings");
        Assert.Equal(withSettings, real);
    }

    /// <summary>
    /// gb-synthetic's "NSC GSP 2 SGT": the panel's rows pinned word for word, from the case's protections.json and
    /// asset-classes.json (prot-lom-legacy and prot-lfdd-stage1), as Core holds them. Before the fix the panel read the
    /// five demo rows (Buchholz relay ... Pressure relief (63), each "healthy" / "OK") under a STATIC DEMO tag.
    /// </summary>
    [Fact]
    public void S7_gb_synthetic_NSC_GSP_2_SGT_lists_its_two_devices_exactly()
    {
        CultureInfo saved = CultureInfo.CurrentCulture;
        try
        {
            // A decimal-comma culture: the settings are written as Core holds them, whatever the machine culture.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            GridModel m = Load("gb-synthetic");
            int i = Enumerable.Range(0, m.Branches.Count).Single(k => m.Branches[k].IsTransformer && m.Branches[k].Name == "NSC GSP 2 SGT");
            var page = TransformerPage(m, i);
            page.Refresh(FrameOf(m));
            Assert.Equal(1, page.HvBusId);
            Assert.Equal(96, page.LvBusId);
            const string bus1 = "North Scotland A 400";
            Assert.Equal(bus1, m.Buses.First(b => b.Id == 1).Name);
            var want = new List<(string Label, string Value, string Status)>
            {
                ("embedded-dg-lom", "prot-lom-legacy", "HV BUS"),
                ("Monitored bus", $"{bus1} (bus 1)", "HV END"),
                ("Capacity", "800 MW", "AUTHORED"),
                ("Mode", "rocof / loss-of-mains (legacy)", "AUTHORED"),
                ("rocofTripHzPerS", "0.125 Hz/s", "Recorded"),
                ("rocofTripConfirmS", "0 s", "CuratedPlaceholder"),
                ("localRocofUpliftFactor", "1.16 x", "DerivedFromRecorded"),
                ("system-lfdd-1", "prot-lfdd-stage1", "HV BUS"),
                ("Monitored bus", $"{bus1} (bus 1)", "HV END"),
                ("Capacity", "632 MW", "AUTHORED"),
                ("Mode", "low-frequency demand disconnection", "AUTHORED"),
                ("lfddStageHz", "48.8 Hz", "Recorded"),
                ("lfddDwellS", "0 s", "CuratedPlaceholder"),
                ("lfddBlockMw", "632 MW", "DerivedFromRecorded"),
            };
            Assert.Equal(want, RowsOf(page.Protection));
            Assert.False(page.ProtectionIsDemo);
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    /// <summary>A protection class built in code (kind protection), every parameter tagged Recorded.</summary>
    private static GridSim.Core.Assets.AssetClass ProtClass(string name, string mode, params (string Key, double Value)[] ps) =>
        new(name, GridSim.Core.Assets.AssetKind.Protection, mode,
            ps.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            ps.ToDictionary(p => p.Key, _ => new GridSim.Core.Assets.AssetClassProvenance(GridSim.Core.Diagnostics.ProvenanceTag.Recorded, "built in the test"), StringComparer.Ordinal));

    /// <summary>
    /// Built cases on the uk model: each way a device links to a transformer, and the way back to the demo panel.
    /// <list type="bullet">
    /// <item>a device monitoring the transformer's LV bus ("LV BUS", "LV END");</item>
    /// <item>a recloser monitoring a bus elsewhere whose target branch is the transformer ("OPENS THIS UNIT", "OTHER BUS",
    /// "Target branch ... THIS UNIT", the recloser delay in s);</item>
    /// <item>a device elsewhere (not linked): not listed;</item>
    /// <item>a device whose class is missing from the library: one "MISSING" row in the alarm colour;</item>
    /// <item>the same page then fed a model with no protections: the five demo rows again, exactly, tag on; and fed the
    /// protected model again: the real rows again, tag off.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void S7_each_link_to_a_transformer_and_back_to_the_demo_panel()
    {
        GridModel uk = Load("uk");
        int tx = Enumerable.Range(0, uk.Branches.Count).First(i => uk.Branches[i].IsTransformer && uk.Branches[i].Name is not null);
        Branch b = uk.Branches[tx];
        var probe = TransformerPage(uk, tx);
        probe.Refresh(FrameOf(uk));
        int hv = probe.HvBusId, lv = probe.LvBusId;
        int elsewhere = uk.Buses.First(x => x.Id != hv && x.Id != lv).Id;
        AssertDemoPanel(probe, "uk as bundled");

        var lib = new GridSim.Core.Assets.AssetClassLibrary(new[]
        {
            ProtClass("uv-relay", "under-voltage", (GridSim.Core.Assets.AssetParams.UvTripPu, 0.8), (GridSim.Core.Assets.AssetParams.UvTripDwellS, 0.5)),
            ProtClass("recloser", "", (GridSim.Core.Assets.AssetParams.OverloadTripFraction, 1.2), (GridSim.Core.Assets.AssetParams.AutoRecloseDelayS, 3)),
        });
        var devices = new[]
        {
            new GridSim.Core.Assets.Protection.ProtectionInstance("lv-uv", "uv-relay", lv, 10),
            new GridSim.Core.Assets.Protection.ProtectionInstance("ar-1", "recloser", elsewhere, 0, TargetBranch: b.Name),
            new GridSim.Core.Assets.Protection.ProtectionInstance("far-uv", "uv-relay", elsewhere, 5),
            new GridSim.Core.Assets.Protection.ProtectionInstance("ghost", "no-such-class", hv, 1.5),
        };
        var prot = new GridModel
        {
            Name = uk.Name, BaseMva = uk.BaseMva, Buses = uk.Buses, Generators = uk.Generators, Branches = uk.Branches, Oltcs = uk.Oltcs,
            Protections = new GridSim.Core.Assets.Protection.ProtectionModel(lib, devices),
        };

        string Bus(int id) => uk.Buses.First(x => x.Id == id).Name ?? $"bus {id}";
        var want = new List<(string Label, string Value, string Status)>
        {
            ("lv-uv", "uv-relay", "LV BUS"),
            ("Monitored bus", $"{Bus(lv)} (bus {lv})", "LV END"),
            ("Capacity", "10 MW", "AUTHORED"),
            ("Mode", "under-voltage", "AUTHORED"),
            ("uvTripPu", "0.8 pu", "Recorded"),
            ("uvTripDwellS", "0.5 s", "Recorded"),
            ("ar-1", "recloser", "OPENS THIS UNIT"),
            ("Monitored bus", $"{Bus(elsewhere)} (bus {elsewhere})", "OTHER BUS"),
            ("Capacity", "0 MW", "AUTHORED"),
            ("Target branch", b.Name!, "THIS UNIT"),
            ("overloadTripFraction", "1.2 x RateA", "Recorded"),
            ("autoRecloseDelayS", "3 s", "Recorded"),
            ("ghost", "no-such-class", "HV BUS"),
            ("Monitored bus", $"{Bus(hv)} (bus {hv})", "HV END"),
            ("Capacity", "1.5 MW", "AUTHORED"),
            ("Class", "no-such-class is not in the class library", "MISSING"),
        };
        Assert.Equal(want, UiAssetPagesParityTests.ExpectedProtectionRows(prot, b, hv, lv));

        var page = TransformerPage(prot, tx);
        page.Refresh(FrameOf(prot));
        Assert.False(page.ProtectionIsDemo);
        Assert.Equal(want, RowsOf(page.Protection));
        Assert.Equal(ColourRamps.Loading(1.2), page.Protection[^1].ChipColour);
        Assert.All(page.Protection.Take(want.Count - 1), r => Assert.Equal(GridSim.Ui.Dashboard.DashboardColours.Gray, r.ChipColour));

        // The same frame again: the row objects are kept (nothing changed).
        TransformerReadingModel firstRow = page.Protection[0];
        page.Refresh(FrameOf(prot));
        Assert.Same(firstRow, page.Protection[0]);

        // A model with no protections: the demo rows again, exactly as before, tag on; and back.
        page.Refresh(FrameOf(uk));
        AssertDemoPanel(page, "uk after the protected model");
        page.Refresh(FrameOf(prot));
        Assert.False(page.ProtectionIsDemo);
        Assert.Equal(want, RowsOf(page.Protection));
    }
}
