namespace GridSim.Core.Model;

/// <summary>
/// The fuel / technology kinds GridSim distinguishes. Every reader of a generator's free-text fuel or type string
/// (the GDA importer, the presentation layers, the DC-OPF merit order, carbon tracing) classifies it into one of these
/// through <see cref="FuelTypes.Classify"/>, and then looks its own values up by kind.
/// </summary>
public enum FuelKind
{
    /// <summary>Not recognised (or empty): every reader's fallback.</summary>
    Other = 0,
    Nuclear,
    Coal,
    /// <summary>Combined-cycle gas turbine.</summary>
    Ccgt,
    /// <summary>Open-cycle gas turbine (peaking plant).</summary>
    Ocgt,
    /// <summary>Gas-fired plant of unstated cycle.</summary>
    Gas,
    /// <summary>Combined heat and power.</summary>
    Chp,
    /// <summary>Biomass, biogas, landfill gas and energy from waste.</summary>
    Biomass,
    /// <summary>Pumped-storage hydro (a synchronous machine).</summary>
    PumpedStorage,
    /// <summary>Hydrogen fuel cells and electrolysers (converter-connected; burnt hydrogen is caught by the turbine kinds).</summary>
    Hydrogen,
    /// <summary>Conventional hydro (not pumped storage).</summary>
    Hydro,
    /// <summary>Tidal, wave and other marine generation.</summary>
    Tidal,
    WindOffshore,
    WindOnshore,
    Solar,
    /// <summary>Battery and other converter-connected storage.</summary>
    Storage,
    Interconnector,
    /// <summary>A grid infeed (an upstream network equivalent at a model boundary).</summary>
    GridInfeed,
    /// <summary>Reciprocating engines (diesel, oil and gas engines).</summary>
    Recip,
}

/// <summary>Which reader is classifying a string (see <see cref="FuelTypes"/>, "deferred keywords").</summary>
public enum FuelReader
{
    /// <summary>The GDA importer (<c>GridSim.Gda.FuelMap</c>): raw dataset strings to a canonical type and an inertia
    /// constant H.</summary>
    Gda,
    /// <summary>The presentation taxonomy (<c>GridSim.Core.Presentation.FuelTaxonomy</c>): name, colour, category and
    /// the synchronous / converter split the shells show.</summary>
    Presentation,
    /// <summary>Economics: the DC-OPF merit-order cost (<c>DcOptimalPowerFlow.FuelCostPerMwh</c>) and the carbon-tracing
    /// intensity (<c>FuelCarbon.IntensityTPerMwh</c>).</summary>
    Economics,
}

/// <summary>One keyword (or phrase) of the taxonomy: the kind it selects and which readers read it.</summary>
/// <param name="Phrase">One or more words, lower case, separated by single spaces.</param>
/// <param name="Kind">The kind a match selects.</param>
/// <param name="Readers">The readers that use this keyword. A reader not listed skips it and carries on down the list.</param>
/// <param name="Deferred">Null for a keyword every reader uses. Otherwise why a reader skips it: reading it there
/// would change a synchronous / inertia classification, which belongs to the DEFERRED frequency / inertia pass.</param>
public sealed record FuelKeyword(string Phrase, FuelKind Kind, IReadOnlyList<FuelReader> Readers, string? Deferred = null);

/// <summary>
/// THE fuel taxonomy (R305 rest, N020; October 2026). One ordered keyword list turns a generator's free-text fuel or
/// type string into a <see cref="FuelKind"/>; each reader then looks up its own value by kind (the GDA importer its
/// canonical type and inertia constant, the shells their name and colour, the DC-OPF its merit-order cost, carbon
/// tracing its intensity). It replaces four separate keyword lists (GDA <c>FuelMap</c>, presentation
/// <c>FuelTaxonomy</c>, <c>FuelCostPerMwh</c>, <c>FuelCarbon</c>) that disagreed with each other: the same plant could
/// be OCGT to the importer and Gas to the shells ("gas turbine"), wind to the converter test and mid-merit "unknown" to
/// the DC-OPF ("onshore", which data/uk and data/gb-synthetic use), hydro to the cost table ("hydrogen").
///
/// <para><b>Matching (N020).</b> The string is lower-cased and split into words at every character that is not a
/// letter or digit (so "Pump-Storage", "pump_storage" and "Pump Storage" read alike). A keyword matches when each of its
/// words is the START of consecutive words in the string: "hydro" matches "Hydroelectric" and "Run of river hydro";
/// "recip" matches "Reciprocating". It no longer matches inside a word, which the old substring test did: "Charcoal"
/// was coal (H = 4 s), "Boiler" an oil engine (H = 1 s), "Microwave" wave power. The first keyword in list order that
/// matches decides. On every type string in the bundled data and every fuel / plant-type string in the GDA lake the
/// word rule gives the same kind as the old substring rule did (pinned by tests).</para>
///
/// <para><b>Source.</b> The kinds follow the GB fuel-type categories Elexon publishes for BM units and the BMRS
/// generation-by-fuel-type data (<see cref="ElexonFuelType"/>: NUCLEAR, COAL, CCGT, OCGT, OIL, BIOMASS, NPSHYD, PS,
/// WIND, INT*, OTHER), refined where the GB datasets GridSim imports are finer: the NESO TEC and Embedded Capacity
/// registers' "Plant Type" (CCGT, CHP, Energy Storage System, PV Array, Wind Onshore, Tidal, Wave, Hydro, Biomass,
/// Waste, Gas Reciprocating, Thermal), the NESO "Fuel Type" (Bioenergy, Hydrogen, Natural Gas, Oil) and the DNO LTDS
/// "Fuel Type" columns (Photovoltaic, Onshore Wind, Offshore Wind, Small CHP, Waste incineration, Biomass &amp; energy
/// crops). Evidence on disk: the Elexon BM unit reference (Insights API <c>/reference/bmunits/all</c>, GDA lake copy
/// <c>DataSources\Elexon\Reference\bmunits_all.json</c>, 3,025 units, 2026-10-02) carries exactly the fuel types
/// listed on <see cref="ElexonFuelType"/>. [VERIFY]: Elexon's own fuel-type definitions document is not yet in
/// docs/reference (recorded for S3 / N136).</para>
///
/// <para><b>Deferred keywords.</b> Where the old lists disagreed in a way that changes whether a plant is synchronous
/// (or its inertia constant), the reader keeps its old reading until the frequency / inertia pass with Mark: the
/// keyword lists only the readers that already read it that way (<see cref="FuelKeyword.Deferred"/> says why). For
/// example the shells never knew "oil" (they showed oil plant as non-synchronous Other while the importer gave it
/// H = 1 s as a reciprocating engine), so "oil" is skipped by the presentation reader for now.</para>
/// </summary>
public static class FuelTypes
{
    private static readonly FuelReader[] All = [FuelReader.Gda, FuelReader.Presentation, FuelReader.Economics];
    private static readonly FuelReader[] GdaAndEconomics = [FuelReader.Gda, FuelReader.Economics];
    private static readonly FuelReader[] PresentationAndEconomics = [FuelReader.Presentation, FuelReader.Economics];

    private const string DeferredInertia = "DEFERRED (frequency / inertia pass): ";

    /// <summary>The ordered keyword list. The first keyword that matches (for the reader asking) decides.</summary>
    public static IReadOnlyList<FuelKeyword> Keywords { get; } =
    [
        new("nuclear", FuelKind.Nuclear, All),
        // Pumped storage before storage and hydro. The NESO TEC register writes "Pump Storage" (no "pumped").
        new("pumped", FuelKind.PumpedStorage, All),
        new("pump storage", FuelKind.PumpedStorage, GdaAndEconomics,
            DeferredInertia + "the shells read \"Pump Storage\" as converter Storage (non-synchronous); the importer as synchronous pumped storage."),
        new("ccgt", FuelKind.Ccgt, All),
        new("combined cycle", FuelKind.Ccgt, All),
        new("ocgt", FuelKind.Ocgt, All),
        new("open cycle", FuelKind.Ocgt, All),
        new("gas turbine", FuelKind.Ocgt, All),
        new("peaker", FuelKind.Ocgt, PresentationAndEconomics,
            DeferredInertia + "the importer reads \"peaker\" as Other (H = 0); the shells as synchronous OCGT."),
        new("chp", FuelKind.Chp, All),
        new("cogen", FuelKind.Chp, All),
        new("combined heat", FuelKind.Chp, PresentationAndEconomics,
            DeferredInertia + "the importer reads \"Combined Heat and Power\" without \"CHP\" as Other (H = 0)."),
        new("coal", FuelKind.Coal, All),
        new("lignite", FuelKind.Coal, [FuelReader.Economics],
            DeferredInertia + "the importer and the shells read \"lignite\" as Other (non-synchronous, H = 0)."),
        new("biomass", FuelKind.Biomass, All),
        // N157 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): the NESO "Fuel Type" column writes
        // biomass plant as "Bioenergy" (1,149 rows in the GDA lake), which no keyword read, so it fell to Other (H 0,
        // non-synchronous). Bioenergy plant is steam-turbine or engine plant on a synchronous machine, the same as the
        // Elexon BIOMASS fuel type (FuelTypes.ElexonFuelType), so it reads as biomass in every reader (H 4 s in GDA).
        new("bioenergy", FuelKind.Biomass, All),
        new("biogas", FuelKind.Biomass, All),
        new("anaerobic", FuelKind.Biomass, GdaAndEconomics,
            DeferredInertia + "the shells read \"anaerobic digestion\" as non-synchronous Other; the importer as synchronous biomass."),
        new("landfill", FuelKind.Biomass, All),
        new("waste", FuelKind.Biomass, All),
        // Hydrogen before hydro: "hydrogen" starts with "hydro" (R298). Burnt hydrogen (a hydrogen CCGT, gas turbine or
        // CHP) is caught by the turbine keywords above and stays synchronous.
        new("hydrogen", FuelKind.Hydrogen, All),
        new("fuel cell", FuelKind.Hydrogen, All),
        new("electrolys", FuelKind.Hydrogen, All),
        new("electrolyz", FuelKind.Hydrogen, All),
        new("hydro", FuelKind.Hydro, All),
        new("tidal", FuelKind.Tidal, All),
        new("wave", FuelKind.Tidal, All),
        new("marine", FuelKind.Tidal, All),
        // Offshore before onshore: "Wind Offshore", "wind-offshore" and a bare "offshore" are offshore wind.
        new("offshore", FuelKind.WindOffshore, All),
        new("wind", FuelKind.WindOnshore, All),
        new("onshore", FuelKind.WindOnshore, All),
        new("solar", FuelKind.Solar, All),
        new("pv", FuelKind.Solar, All),
        new("photovolt", FuelKind.Solar, All),
        new("storage", FuelKind.Storage, All),
        new("battery", FuelKind.Storage, All),
        new("bess", FuelKind.Storage, All),
        new("interconnector", FuelKind.Interconnector, All),
        new("hvdc", FuelKind.Interconnector, All),
        new("import", FuelKind.Interconnector, All),
        new("grid", FuelKind.GridInfeed, All),
        new("infeed", FuelKind.GridInfeed, All),
        // Gas after every specific gas technology (CCGT, OCGT, CHP, landfill, biogas), so "gas engine" and
        // "Gas Reciprocating" are gas plant, as both the importer and the shells always read them.
        new("gas", FuelKind.Gas, All),
        new("combined", FuelKind.Gas, [FuelReader.Presentation],
            DeferredInertia + "the shells read a bare \"combined\" as synchronous gas-fired plant (it used to say CCGT); " +
            "the importer and economics never read it."),
        new("reciprocating", FuelKind.Recip, All),
        new("recip", FuelKind.Recip, PresentationAndEconomics,
            DeferredInertia + "the importer reads the canonical type \"recip\" itself as Other (H = 0); it only ever sees raw dataset strings."),
        new("diesel", FuelKind.Recip, All),
        new("engine", FuelKind.Recip, All),
        new("oil", FuelKind.Recip, GdaAndEconomics,
            DeferredInertia + "the shells read \"oil\" as non-synchronous Other; the importer as a synchronous reciprocating engine (H = 1 s)."),
    ];

    /// <summary>Classify a free-text fuel / type string for <paramref name="reader"/>. Null, empty or unrecognised gives
    /// <see cref="FuelKind.Other"/>.</summary>
    public static FuelKind Classify(string? text, FuelReader reader)
    {
        string[] words = Words(text);
        if (words.Length == 0) return FuelKind.Other;
        foreach (FuelKeyword k in Keywords)
        {
            if (!Reads(k, reader)) continue;
            if (Matches(k.Phrase, words)) return k.Kind;
        }
        return FuelKind.Other;
    }

    private static bool Reads(FuelKeyword k, FuelReader reader)
    {
        foreach (FuelReader r in k.Readers) if (r == reader) return true;
        return false;
    }

    /// <summary>The words of a string: lower case (invariant culture), split at every character that is not a letter
    /// or digit, empty pieces dropped.</summary>
    public static string[] Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        string s = text.Trim().ToLowerInvariant();
        var words = new List<string>();
        int start = -1;
        for (int i = 0; i <= s.Length; i++)
        {
            bool wordChar = i < s.Length && char.IsLetterOrDigit(s[i]);
            if (wordChar && start < 0) start = i;
            else if (!wordChar && start >= 0) { words.Add(s[start..i]); start = -1; }
        }
        return words.ToArray();
    }

    /// <summary>
    /// N157 (B4, October 2026; Ruling R1: Mark delegated EE calls 2026-10-04): words that NEGATE the keyword right after
    /// them. The NGED LTDS "Fuel Type" column writes "Biomass &amp; energy crops (not CHP)" (126 rows) and "Waste
    /// incineration (not CHP)" (23 rows): the "chp" keyword matched the word after "not", so both read as CHP (H 2.5 s in
    /// GDA) although the string says they are not. A keyword whose first word directly follows one of these words no
    /// longer matches at that position, so the classifier carries on down the list ("biomass", "waste": Biomass, H 4 s).
    /// "non" covers "Non-CHP" (split into "non", "chp"). No bundled type string and no other lake string carries either
    /// word before a keyword (FuelTypesTaxonomyTests: every other lake string keeps its reading).
    /// </summary>
    public static IReadOnlyList<string> NegatingWords { get; } = ["not", "non"];

    /// <summary>True when every word of <paramref name="phrase"/> is the start of consecutive words of the string, and the
    /// word before the match (if any) is not a <see cref="NegatingWords"/> entry (N157).</summary>
    private static bool Matches(string phrase, string[] words)
    {
        string[] p = phrase.Split(' ');
        for (int i = 0; i + p.Length <= words.Length; i++)
        {
            // N157: "not chp" / "non chp" is the opposite of chp; skip this position, keep looking further on.
            if (i > 0 && IsNegating(words[i - 1])) continue;
            bool all = true;
            for (int j = 0; j < p.Length && all; j++) all = words[i + j].StartsWith(p[j], StringComparison.Ordinal);
            if (all) return true;
        }
        return false;
    }

    private static bool IsNegating(string word)
    {
        foreach (string n in NegatingWords) if (string.Equals(word, n, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// The canonical GridSim generator <c>type</c> for a kind: the vocabulary the GDA importer writes into
    /// generation.json and that <see cref="ConverterPlant"/> and <c>PoleMap</c> look up exactly. A hydrogen fuel cell or
    /// electrolyser is written as <c>storage</c> (converter-connected, no inertia; R298), as it always was.
    /// </summary>
    public static string CanonicalType(FuelKind kind) => kind switch
    {
        FuelKind.Nuclear => "nuclear",
        FuelKind.Coal => "coal",
        FuelKind.Ccgt => "ccgt",
        FuelKind.Ocgt => "ocgt",
        FuelKind.Gas => "gas",
        FuelKind.Chp => "chp",
        FuelKind.Biomass => "biomass",
        FuelKind.PumpedStorage => "pumped-storage",
        FuelKind.Hydrogen => "storage",
        FuelKind.Hydro => "hydro",
        FuelKind.Tidal => "tidal",
        FuelKind.WindOffshore => "wind-offshore",
        FuelKind.WindOnshore => "wind-onshore",
        FuelKind.Solar => "solar",
        FuelKind.Storage => "storage",
        FuelKind.Interconnector => "interconnector",
        FuelKind.GridInfeed => "grid",
        FuelKind.Recip => "recip",
        FuelKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown fuel kind."),
    };

    /// <summary>
    /// The Elexon BM unit / BMRS fuel-type code a kind falls under (the source categories, see the class summary).
    /// Interconnectors carry one code per link (INTFR, INTIRL, INTNED, INTEW, INTNEM, INTIFA2, INTNSL, INTELEC, INTVKL,
    /// INTGRNL), shown here as the prefix <c>INT</c>. Embedded or converter plant with no BM fuel type of its own (solar,
    /// battery storage, hydrogen, tidal, a grid infeed) and anything unrecognised fall under <c>OTHER</c>.
    /// </summary>
    public static string ElexonFuelType(FuelKind kind) => kind switch
    {
        FuelKind.Nuclear => "NUCLEAR",
        FuelKind.Coal => "COAL",
        FuelKind.Ccgt => "CCGT",
        FuelKind.Ocgt => "OCGT",
        FuelKind.Biomass => "BIOMASS",
        FuelKind.PumpedStorage => "PS",
        FuelKind.Hydro => "NPSHYD",
        FuelKind.WindOffshore or FuelKind.WindOnshore => "WIND",
        FuelKind.Interconnector => "INT",
        FuelKind.Recip => "OIL",
        FuelKind.Gas or FuelKind.Chp or FuelKind.Hydrogen or FuelKind.Tidal or FuelKind.Solar or FuelKind.Storage
            or FuelKind.GridInfeed or FuelKind.Other => "OTHER",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown fuel kind."),
    };
}
