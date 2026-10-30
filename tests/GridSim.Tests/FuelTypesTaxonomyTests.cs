using System.Text.RegularExpressions;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.Presentation;
using GridSim.Gda;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R305 rest and N020 (October 2026): one fuel taxonomy in Core (<see cref="FuelTypes"/>) read by the GDA importer
/// (<see cref="FuelMap"/>), the shells (<see cref="FuelTaxonomy"/>), the DC-OPF merit order
/// (<see cref="DcOptimalPowerFlow.FuelCostPerMwh"/>) and carbon tracing (<see cref="FuelCarbon"/>), matching whole words
/// by their start instead of any substring.
/// <para>The expected values in the two big tables below were produced by the OLD four keyword lists (a line-for-line
/// port run over the same strings), so these tests prove what did not change; every expectation that differs from the
/// old reading says so in a comment, with the old value.</para>
/// </summary>
public class FuelTypesTaxonomyTests
{
    // ---------------------------------------------------------------- the GDA lake vocabulary: nothing moves

    /// <summary>Every distinct fuel / plant-type string in the GDA lake (NESO Embedded Capacity Register "Plant Type",
    /// NESO "Fuel Type", NGED LTDS "Fuel Type"; scanned 2026-10-04): the importer's canonical type and inertia constant
    /// are exactly what the old substring list gave, and so is the shells' name except the one display fix noted.</summary>
    [Theory]
    // Moved 2026-10 (B4 N157, Ruling R1: Mark delegated EE calls 2026-10-04; source: the Elexon BM fuel-type category
    // BIOMASS (FuelTypes.ElexonFuelType) that NESO's "Bioenergy" plant falls under; synchronous steam / engine plant):
    // before "other", H 0.0, "Other" (no keyword read "Bioenergy"); after "biomass", H 4.0, "Biomass".
    [InlineData("Bioenergy", "biomass", 4.0, "Biomass")]
    [InlineData("Biofuel (Landfill gas) - Gas turbine (OCGT)", "ocgt", 4.0, "OCGT")]
    [InlineData("Biomass", "biomass", 4.0, "Biomass")]
    // Moved 2026-10 (B4 N157, Ruling R1: Mark delegated EE calls 2026-10-04; source: the NGED LTDS string itself says "not
    // CHP"): before "chp", H 2.5, "CHP" (the "chp" keyword matched the word after "not"); after "biomass", H 4.0, "Biomass".
    [InlineData("Biomass & energy crops (not CHP)", "biomass", 4.0, "Biomass")]
    [InlineData("CCGT (Combined Cycle Gas Turbine)", "ccgt", 5.0, "CCGT")]
    // Display fix: the shells used to show these as CCGT (their bare 'combined' keyword); CHP is what they are. Both
    // are synchronous fossil, so no inertia figure moves.
    [InlineData("CHP (Combined Heat and Power)", "chp", 2.5, "CHP")]
    [InlineData("CHP (Combined Heat and Power);Demand", "chp", 2.5, "CHP")]
    [InlineData("CHP (Combined Heat and Power);PV Array (Photo Voltaic/solar)", "chp", 2.5, "CHP")]
    [InlineData("Demand;Energy Storage System", "storage", 0.0, "Storage")]
    [InlineData("Demand;Energy Storage System;Wind Onshore", "wind-onshore", 0.0, "Wind (onshore)")]
    [InlineData("Demand;Hydro", "hydro", 3.0, "Hydro")]
    [InlineData("Demand;PV Array (Photo Voltaic/solar);Wind Onshore", "wind-onshore", 0.0, "Wind (onshore)")]
    [InlineData("Demand;Tidal", "tidal", 0.0, "Tidal")]
    [InlineData("Demand;Wind Onshore", "wind-onshore", 0.0, "Wind (onshore)")]
    [InlineData("Energy Storage System", "storage", 0.0, "Storage")]
    [InlineData("Energy Storage System;PV Array (Photo Voltaic/solar)", "solar", 0.0, "Solar")]
    [InlineData("Energy Storage System;PV Array (Photo Voltaic/solar);Wind Onshore", "wind-onshore", 0.0, "Wind (onshore)")]
    [InlineData("Energy Storage System;Wind Onshore", "wind-onshore", 0.0, "Wind (onshore)")]
    [InlineData("Gas Reciprocating", "gas", 4.0, "Gas")]
    [InlineData("Hydro", "hydro", 3.0, "Hydro")]
    [InlineData("Hydro;PV Array (Photo Voltaic/solar)", "hydro", 3.0, "Hydro")]
    [InlineData("Hydrogen", "storage", 0.0, "Hydrogen")]
    [InlineData("Medium CHP (>5MW,<50MW)", "chp", 2.5, "CHP")]
    [InlineData("Mixed", "other", 0.0, "Other")]
    [InlineData("Natural Gas", "gas", 4.0, "Gas")]
    [InlineData("Offshore Wind", "wind-offshore", 0.0, "Wind (offshore)")]
    [InlineData("Oil", "recip", 1.0, "Other")]   // deferred: the shells keep reading oil as Other (see FuelTypes)
    [InlineData("Onshore Wind", "wind-onshore", 0.0, "Wind (onshore)")]
    [InlineData("Other Generation", "other", 0.0, "Other")]
    [InlineData("PV Array (Photo Voltaic/solar)", "solar", 0.0, "Solar")]
    [InlineData("PV Array (Photo Voltaic/solar);Wind Onshore", "wind-onshore", 0.0, "Wind (onshore)")]
    [InlineData("Photovoltaic", "solar", 0.0, "Solar")]
    [InlineData("Small CHP", "chp", 2.5, "CHP")]
    [InlineData("Thermal", "other", 0.0, "Other")]
    [InlineData("Tidal", "tidal", 0.0, "Tidal")]
    [InlineData("Waste", "biomass", 4.0, "Biomass")]
    // Moved 2026-10 (B4 N157, Ruling R1; as above): before "chp", H 2.5, "CHP"; after "biomass" (the "waste" keyword),
    // H 4.0, "Biomass".
    [InlineData("Waste incineration (not CHP)", "biomass", 4.0, "Biomass")]
    [InlineData("Wave", "tidal", 0.0, "Tidal")]
    [InlineData("Wind Onshore", "wind-onshore", 0.0, "Wind (onshore)")]
    public void Every_gda_lake_string_keeps_its_importer_type_and_inertia(string raw, string type, double h, string shown)
    {
        FuelMap.Kind k = FuelMap.Classify(raw);
        Assert.Equal(type, k.Type);
        Assert.Equal(h, k.H);
        Assert.Equal(shown, FuelTaxonomy.Classify(raw).Name);
    }

    // ---------------------------------------------------------------- the bundled type vocabulary

    /// <summary>Every generator type string in the bundled data (data/**/generation.json: 20 strings over 7,602
    /// units). The synchronous flag never changes; the name, cost and carbon factor change only for "onshore" and
    /// "offshore" (data/uk and data/gb-synthetic), which the old lists knew only partly.</summary>
    [Theory]
    [InlineData("other", "Other", false, DcOptimalPowerFlow.DefaultCostPerMwh, "unknown")]
    [InlineData("wind-onshore", "Wind (onshore)", false, 1.0, "zero")]
    [InlineData("grid", "Grid infeed", false, 60.0, "import")]
    [InlineData("solar", "Solar", false, 1.0, "zero")]
    [InlineData("hydro", "Hydro", true, 5.0, "zero")]
    [InlineData("gas", "Gas", true, 65.0, "gas")]
    [InlineData("chp", "CHP", true, 65.0, "chp")]
    [InlineData("biomass", "Biomass", true, 55.0, "biomass")]
    [InlineData("storage", "Storage", false, 30.0, "zero")]
    [InlineData("coal", "Coal", true, 45.0, "coal")]
    [InlineData("recip", "Reciprocating engine", true, 120.0, "oil")]
    [InlineData("nuclear", "Nuclear", true, 10.0, "zero")]
    [InlineData("ccgt", "CCGT", true, 65.0, "ccgt")]
    [InlineData("wind-offshore", "Wind (offshore)", false, 1.0, "zero")]
    [InlineData("interconnector", "Interconnector", false, 60.0, "import")]
    // Changed: the shells showed "onshore" as Other, the DC-OPF priced it at the 50 default and carbon tracing gave it
    // the unknown default; it is onshore wind (19 units in data/uk and data/gb-synthetic). Still non-synchronous.
    [InlineData("onshore", "Wind (onshore)", false, 1.0, "zero")]
    [InlineData("ocgt", "OCGT", true, 95.0, "ocgt")]
    [InlineData("tidal", "Tidal", false, 1.0, "zero")]
    // Changed: the DC-OPF priced "offshore" at the 50 default and carbon tracing gave it the unknown default (the
    // shells already showed offshore wind). 4 units in data/uk and data/gb-synthetic.
    [InlineData("offshore", "Wind (offshore)", false, 1.0, "zero")]
    [InlineData("pumped-storage", "Pumped storage", true, 30.0, "zero")]
    public void Every_bundled_type_reads_the_same_in_every_reader(string type, string name, bool synchronous, double cost, string carbon)
    {
        FuelDescriptor d = FuelTaxonomy.Classify(type);
        Assert.Equal(name, d.Name);
        Assert.Equal(synchronous, d.Synchronous);
        Assert.Equal(cost, DcOptimalPowerFlow.FuelCostPerMwh(type));
        Assert.Equal(Carbon(carbon), FuelCarbon.IntensityTPerMwh(type));
    }

    private static double Carbon(string key) => key switch
    {
        "zero" => 0.0,
        "unknown" => FuelCarbon.DefaultTPerMwh,
        "gas" => CarbonIntensityTable.TraceGenericGasTPerMwh,
        "chp" => CarbonIntensityTable.TraceChpTPerMwh,
        "biomass" => CarbonIntensityTable.TraceBiomassTPerMwh,
        "coal" => CarbonIntensityTable.TraceCoalTPerMwh,
        "oil" => CarbonIntensityTable.TraceOilTPerMwh,
        "ccgt" => CarbonIntensityTable.TraceCcgtTPerMwh,
        "ocgt" => CarbonIntensityTable.TraceOcgtTPerMwh,
        "import" => CarbonIntensityTable.TraceImportTPerMwh,
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    [Fact]
    public void The_synchronous_flag_of_every_canonical_type_agrees_with_the_importer_inertia()
    {
        // The canonical types the importer writes: a type with H > 0 is synchronous in the shells, and one with H = 0
        // is not. (The importer's own H table is unchanged; this pins that the two readers agree on the vocabulary.)
        foreach (FuelKind kind in Enum.GetValues<FuelKind>())
        {
            string type = FuelTypes.CanonicalType(kind);
            bool sync = FuelTaxonomy.Classify(type).Synchronous;
            Assert.True(sync == FuelMap.InertiaH(type) > 0, $"{kind} ('{type}'): shells synchronous {sync}, importer H {FuelMap.InertiaH(type)}");
        }
    }

    // ---------------------------------------------------------------- N020: whole words, by their start

    [Theory]
    [InlineData("Charcoal", FuelKind.Other)]          // was Coal (substring "coal")
    [InlineData("Boiler", FuelKind.Other)]            // was an oil engine (substring "oil") for the importer and economics
    [InlineData("Microwave", FuelKind.Other)]         // was Tidal (substring "wave")
    [InlineData("Hydroelectric", FuelKind.Hydro)]     // a word that STARTS with "hydro" still matches
    [InlineData("Run of river hydro", FuelKind.Hydro)]
    [InlineData("Hydrogen fuel cell", FuelKind.Hydrogen)]
    [InlineData("Hydrogen Gas Turbine", FuelKind.Ocgt)]
    [InlineData("Pump-Storage", FuelKind.PumpedStorage)]
    [InlineData("pump_storage", FuelKind.PumpedStorage)]
    [InlineData("Reciprocating", FuelKind.Recip)]
    [InlineData("wind_offshore", FuelKind.WindOffshore)]
    [InlineData("  OCGT peaker  ", FuelKind.Ocgt)]
    [InlineData("Landfill gas", FuelKind.Biomass)]
    [InlineData("Gas engine", FuelKind.Gas)]
    [InlineData("", FuelKind.Other)]
    [InlineData(null, FuelKind.Other)]
    public void Keywords_match_whole_words_by_their_start(string? text, FuelKind expected)
        => Assert.Equal(expected, FuelTypes.Classify(text, FuelReader.Economics));

    [Fact]
    public void The_substring_false_positives_read_as_other_in_every_reader()
    {
        foreach (string s in new[] { "Charcoal", "Boiler", "Microwave" })
        {
            Assert.Equal("other", FuelMap.Classify(s).Type);
            Assert.Equal(0.0, FuelMap.Classify(s).H);   // Charcoal was 4.0 (coal), Boiler 1.0 (recip), Microwave 0.0 (tidal)
            Assert.Equal("Other", FuelTaxonomy.Classify(s).Name);
            Assert.Equal(DcOptimalPowerFlow.DefaultCostPerMwh, DcOptimalPowerFlow.FuelCostPerMwh(s));
            Assert.Equal(FuelCarbon.DefaultTPerMwh, FuelCarbon.IntensityTPerMwh(s));
        }
    }

    [Fact]
    public void Words_split_at_every_non_alphanumeric_character()
        => Assert.Equal(new[] { "pv", "array", "photo", "voltaic", "solar" }, FuelTypes.Words("  PV Array (Photo Voltaic/solar) "));

    // ---------------------------------------------------------------- readers now agree

    [Fact]
    public void A_gas_turbine_is_ocgt_to_every_reader()
    {
        // Was: OCGT to the importer, Gas to the shells, 65 (gas) to the DC-OPF and generic gas to carbon tracing.
        Assert.Equal("ocgt", FuelMap.Classify("Gas turbine").Type);
        Assert.Equal("OCGT", FuelTaxonomy.Classify("Gas turbine").Name);
        Assert.Equal(95.0, DcOptimalPowerFlow.FuelCostPerMwh("Gas turbine"));
        Assert.Equal(CarbonIntensityTable.TraceOcgtTPerMwh, FuelCarbon.IntensityTPerMwh("Gas turbine"));
    }

    [Fact]
    public void Hydrogen_and_pumped_storage_are_no_longer_priced_as_hydro()
    {
        // Was 5 (the "hydro" substring) for both.
        Assert.Equal(30.0, DcOptimalPowerFlow.FuelCostPerMwh("hydrogen"));
        Assert.Equal(30.0, DcOptimalPowerFlow.FuelCostPerMwh("Pumped storage hydro"));
        Assert.Equal(0.0, FuelCarbon.IntensityTPerMwh("hydrogen"));
    }

    // ---------------------------------------------------------------- deferred keywords (frequency / inertia pass)

    [Fact]
    public void Every_keyword_a_reader_skips_says_why_and_every_other_keyword_is_read_by_all()
    {
        foreach (FuelKeyword k in FuelTypes.Keywords)
        {
            bool all = k.Readers.Count == 3 && k.Readers.Distinct().Count() == 3;
            if (all) Assert.Null(k.Deferred);
            else
            {
                Assert.NotNull(k.Deferred);
                Assert.StartsWith("DEFERRED (frequency / inertia pass): ", k.Deferred, StringComparison.Ordinal);
            }
            Assert.Equal(k.Phrase, k.Phrase.Trim().ToLowerInvariant());
            Assert.DoesNotContain("  ", k.Phrase, StringComparison.Ordinal);
        }
    }

    [Theory]
    // The importer's old readings, kept until the inertia pass (H in brackets).
    [InlineData("peaker", "other", 0.0)]
    [InlineData("recip", "other", 0.0)]
    [InlineData("Combined Heat and Power", "other", 0.0)]
    [InlineData("lignite", "other", 0.0)]
    [InlineData("Pump Storage", "pumped-storage", 3.0)]
    [InlineData("Anaerobic digestion", "biomass", 4.0)]
    [InlineData("Oil", "recip", 1.0)]
    public void Deferred_keywords_keep_the_importers_old_reading(string raw, string type, double h)
    {
        Assert.Equal(type, FuelMap.Classify(raw).Type);
        Assert.Equal(h, FuelMap.Classify(raw).H);
    }

    [Theory]
    // The shells' old readings, kept until the inertia pass.
    [InlineData("oil", "Other", false)]
    [InlineData("Oil engine", "Reciprocating engine", true)]       // "oil" skipped, "engine" read, as before
    [InlineData("Pump Storage", "Storage", false)]
    [InlineData("Anaerobic digestion", "Other", false)]
    [InlineData("Anaerobic digestion gas", "Gas", true)]
    [InlineData("lignite", "Other", false)]
    [InlineData("peaker", "OCGT", true)]
    [InlineData("recip", "Reciprocating engine", true)]
    [InlineData("combined", "Gas", true)]                          // was CCGT: same synchronous fossil class
    public void Deferred_keywords_keep_the_shells_old_synchronous_reading(string type, string name, bool synchronous)
    {
        FuelDescriptor d = FuelTaxonomy.Classify(type);
        Assert.Equal(name, d.Name);
        Assert.Equal(synchronous, d.Synchronous);
    }

    // ---------------------------------------------------------------- every reader covers every kind

    [Fact]
    public void Every_kind_has_a_canonical_type_a_descriptor_a_cost_a_carbon_factor_and_an_elexon_code()
    {
        var elexon = new HashSet<string> { "NUCLEAR", "COAL", "CCGT", "OCGT", "OIL", "BIOMASS", "NPSHYD", "PS", "WIND", "INT", "OTHER" };
        foreach (FuelKind kind in Enum.GetValues<FuelKind>())
        {
            Assert.False(string.IsNullOrEmpty(FuelTypes.CanonicalType(kind)));
            Assert.False(string.IsNullOrEmpty(FuelTaxonomy.Describe(kind).Name));
            Assert.Contains(FuelTypes.ElexonFuelType(kind), elexon);
            // Round trip through the canonical type: each kind's canonical string reads back as that kind (hydrogen is
            // written as "storage", R298, so it reads back as storage).
            FuelKind back = FuelTypes.Classify(FuelTypes.CanonicalType(kind), FuelReader.Economics);
            Assert.Equal(kind == FuelKind.Hydrogen ? FuelKind.Storage : kind, back);
            Assert.True(double.IsFinite(DcOptimalPowerFlow.FuelCostPerMwh(FuelTypes.CanonicalType(kind))));
            Assert.True(double.IsFinite(FuelCarbon.IntensityTPerMwh(FuelTypes.CanonicalType(kind))));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => FuelTypes.CanonicalType((FuelKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => FuelTaxonomy.Describe((FuelKind)99));
    }

    // ---------------------------------------------------------------- the map's JS mirror matches the same way

    /// <summary>The map's fuelColor (map-geo.js) now matches whole words by their start, like Core. This runs the JS
    /// algorithm in C# (lower case, split at non-alphanumerics, phrase words as word starts, first row wins) and checks
    /// it against <see cref="FuelTaxonomy"/> on the substring false positives and every bundled type, which the
    /// substring-based check in FuelTaxonomyJsAntiDriftTests does not cover.</summary>
    [Theory]
    [InlineData("Charcoal")] [InlineData("Microwave")] [InlineData("Boiler")] [InlineData("onshore")] [InlineData("offshore")]
    [InlineData("other")] [InlineData("wind-onshore")] [InlineData("grid")] [InlineData("solar")] [InlineData("hydro")]
    [InlineData("gas")] [InlineData("chp")] [InlineData("biomass")] [InlineData("storage")] [InlineData("coal")]
    [InlineData("recip")] [InlineData("nuclear")] [InlineData("ccgt")] [InlineData("wind-offshore")] [InlineData("interconnector")]
    [InlineData("ocgt")] [InlineData("tidal")] [InlineData("pumped-storage")] [InlineData("CHP (Combined Heat and Power)")]
    [InlineData("Gas turbine")] [InlineData("Combined")] [InlineData("Pump Storage")] [InlineData("Oil engine")]
    public void The_map_js_mirror_matches_words_like_core(string type)
    {
        string js = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Web", "wwwroot", "js", "map-geo.js");
        Match table = Regex.Match(js, @"const FUEL_RULES = \[(.*?)\n\s*\];", RegexOptions.Singleline);
        Assert.True(table.Success);
        var rules = new List<(string[] Keys, string Color)>();
        foreach (Match row in Regex.Matches(table.Groups[1].Value, @"\[\[([^\]]*)\],\s*'(#[0-9A-Fa-f]{6})'\]"))
            rules.Add((Regex.Matches(row.Groups[1].Value, @"'([^']*)'").Select(k => k.Groups[1].Value).ToArray(), row.Groups[2].Value));
        string other = Regex.Match(js, @"const FUEL_OTHER = '(#[0-9A-Fa-f]{6})';").Groups[1].Value;
        Assert.Contains("phraseMatches(k, words)", js, StringComparison.Ordinal);   // the JS really matches by words

        string[] words = Regex.Split(type.Trim().ToLowerInvariant(), "[^0-9a-z]+").Where(w => w.Length > 0).ToArray();
        bool Phrase(string phrase)
        {
            string[] p = phrase.Split(' ');
            for (int i = 0; i + p.Length <= words.Length; i++)
            {
                bool all = true;
                for (int j = 0; j < p.Length && all; j++) all = words[i + j].StartsWith(p[j], StringComparison.Ordinal);
                if (all) return true;
            }
            return false;
        }
        string color = other;
        foreach ((string[] keys, string c) in rules)
            if (keys.Any(Phrase)) { color = c; break; }
        Assert.Equal(FuelTaxonomy.Classify(type).Color, color);
    }
}
