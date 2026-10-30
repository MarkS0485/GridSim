using System.Globalization;
using System.Text.RegularExpressions;
using GridSim.Core.Model;
using GridSim.Gda.Ltds;

namespace GridSim.Gda;

/// <summary>How a DNO's published impedances are expressed  -  they differ by operator.</summary>
public enum ImpedanceConvention
{
    /// <summary>Ohms referred to the GSP voltage; B in microsiemens. GDA's normaliser read NGED this way, and GridSim did
    /// until N347 (CF4, October 2026); no shipped reader uses it now (NGED is percent on 100 MVA, see
    /// <see cref="GdaModelBuilder.NgedCircuitConvention"/>). Kept for a caller-built dataset that really is ohmic.</summary>
    OhmicReferredToGsp,
    /// <summary>Percent on 100 MVA (circuits, B in percent too); transformers on their rating or on 100 MVA as the dataset
    /// says. UKPN, NPg and (since N347) NGED.</summary>
    PercentOn100Mva,
}

/// <summary>
/// A DNO's network as the shared LTDS record types, plus its impedance convention.
/// Every operator publishes the same physical content in its own schema; a per-DNO
/// source normalises into this, and the builder assembles it the same way.
/// </summary>
public sealed record LtdsDataset(
    string Name,
    ImpedanceConvention Convention,
    IReadOnlyList<LtdsNode> Nodes,
    IReadOnlyList<LtdsCircuit> Circuits,
    IReadOnlyList<LtdsTransformer2> Transformers2,
    IReadOnlyList<LtdsTransformer3> Transformers3,
    IReadOnlyList<LtdsDemand> Demand,
    IReadOnlyList<LtdsGeneration> Generation,
    bool TransformerPercentOnRating = true,   // percent on the unit's rating; UKPN (N194) and NPg pass false: % on 100 MVA
    bool InfeedPerGspGroupPerIsland = false)
{
    /// <summary>
    /// N194 (October 2026): plain-English notes from the reader about what it did to the published tables (UKPN:
    /// node codes kept apart per licence area, aggregate demand rows, demand shared over busbar sections). The
    /// builder puts them first in <see cref="GdaDiagnostics.Notes"/>. Empty for NGED, so its report is unchanged; NPg
    /// gives one line since N202 (transformer windings joined to their busbar by site name).
    /// </summary>
    public IReadOnlyList<string> SourceNotes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// N184 (Ruling R1, Mark delegated 2026-10-04): join embedded generation to demand primaries on the canonical
    /// primary-name key (<see cref="GdaModelBuilder.CanonicalPrimaryKey"/>) instead of the whitespace-and-case key.
    /// NGED's generation table names a primary "Wigston Magna 33 11 kV S Stn" where its demand table says
    /// "Wigston Magna 11", so the plain key placed 3 of its 897 generation rows; the canonical key places 308. Set by
    /// <see cref="GdaModelBuilder.LoadNged"/> only. Default false, so NPg and UKPN (and every dataset a caller builds
    /// itself) join exactly as before: on NPg the canonical key would fold 47 distinct demand names onto shared keys for
    /// one extra name match (393 against 392 of 993 generation rows), so it does not apply there.
    /// </summary>
    public bool CanonicalPrimaryJoin { get; init; }

    /// <summary>
    /// N252 (CF3, October 2026; Ruling R1): the transformer impedances (two- and three-winding R1 / X1) are percent on
    /// 100 MVA whatever <see cref="Convention"/> says for the circuits. Set by <see cref="GdaModelBuilder.LoadNged"/>
    /// only; default false, so every other dataset builds exactly as before (UKPN and NPg already read their
    /// transformers on 100 MVA through <see cref="TransformerPercentOnRating"/> false).
    /// <para>Evidence (NGED LTDS tabular model, the GDA lake, read 2026-10-10): the Table 2a / 2b columns carry no unit,
    /// and GDA's normaliser calls them ohms. Read as percent on 100 MVA, the median unit is 11.4 % on its own rating
    /// (33/11 kV, 1,860 units), 10.4 % (132/33 kV, 384), 12.3 % (132/11 kV, 230): the normal range; X falls with rating
    /// (log-log slope -0.83), as a fixed-base percent does. Decisive: NGED's own Table 4 gives the Thevenin impedance at
    /// Banbury Road 33/11 kV (BANR5) as X1 0.6457 pu on 100 MVA, and its two 12 MVA units publish X1 110.846 and
    /// 111.939: 1.108 and 1.119 pu on 100 MVA, 0.557 in parallel, plus the 33 kV source behind them, which is the
    /// published 0.6457. Read as ohms referred to the 132 kV GSP (the old reading), the pair would be 0.320 pu, about
    /// half of what NGED's own fault table implies. W:\Code\Profiles tools/build_transformers.py records the same
    /// finding across NGED, UKPN and NPg (slopes -0.74, -0.61, -0.76).</para>
    /// </summary>
    public bool TransformerPercentOn100Mva { get; init; }

    /// <summary>
    /// N211 (October 2026, Ruling R1): words of a GSP / S/S group name that the publisher spells two ways, as
    /// alias -> canonical (upper case, whole words). The reader already wrote every row's group in the canonical
    /// spelling; the builder applies the same table to a <see cref="GdaBuildOptions.GspGroup"/> carve, so a user who
    /// types the alias still selects the group. Empty (the default) for every dataset but NPg
    /// (<see cref="Npg.NpgSource.GroupWordAliases"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string> GroupWordAliases { get; init; } = new Dictionary<string, string>();

    // InfeedPerGspGroupPerIsland (N194, October 2026; default false, so NGED builds exactly as before; UKPN sets it, and
    // NPg since N202, Ruling R1 follow-up, where the old single infeed per group left a 33 kV-only island unsolvable): place
    // the GSP infeeds (slacks) per GSP group per connected island, at the GSP site itself. UKPN publishes a GSP's
    // board as several unconnected section nodes (Elstree GIS 132kV: ELSG10, ELSG11, ELSG12, ELSG13; Redbridge
    // Supergrid 33kV: REBR30, REBR32) with no couplers, so one GSP group falls into several islands, and the busiest
    // node of a group is often a grid site several circuits from the GSP. With one infeed per group at its busiest
    // node, GSP sections were fed only through a neighbouring group's circuits (or the safety-net slack), which
    // overloaded those circuits several times over and drove the UKPN power flow to collapse. Under the flag, every
    // bus at a GSP site (name begins with the group's name as whole words) at the site's highest voltage in that
    // island is an infeed, since each section has its own supergrid transformers; a group piece with no site bus
    // keeps the old single choice (highest kV, then most branches). See the slack section of the builder.
}

/// <summary>Options controlling how a GDA dataset is assembled into a GridModel.</summary>
public sealed class GdaBuildOptions
{
    /// <summary>System apparent-power base (MVA) for per-unit conversion. 100 is standard.</summary>
    public double BaseMva { get; set; } = 100.0;

    /// <summary>
    /// Reference voltage (kV) the LTDS ohmic impedances are referred to. NGED refers
    /// every element (including 33/11 kV transformers) to the GSP voltage, so the
    /// network builds as a single-Vbase referred model. 0 = auto-detect per element
    /// from its GSP-group name (e.g. "Coventry 132kV" -> 132). Ignored for the percent convention.
    /// </summary>
    public double RefKv { get; set; } = 0.0;

    /// <summary>
    /// Keep only circuits at/above this kV (0 = everything down to 11 kV). 132 ~= DNO transmission interface.
    /// The network below the cut is not built, but its demand (and the embedded generation at
    /// those primaries) is folded onto the nearest retained boundary bus, so the replica still
    /// carries the whole load; see the R047 notes in the build.
    /// </summary>
    public double MinKv { get; set; } = 0.0;

    /// <summary>Restrict to one GSP group (carves a smaller, single-island subnetwork).</summary>
    public string? GspGroup { get; set; }

    /// <summary>Restrict to one licence area (e.g. NGED EMIDS/WMIDS/SWEST/SWALES, or a UKPN area).</summary>
    public string? Licence { get; set; }

    /// <summary>LTDS B1 susceptance unit -> siemens (ohmic convention). LTDS publishes S, so 1e-6.</summary>
    public double SusceptanceToSiemens { get; set; } = 1e-6;

    /// <summary>Drop out-of-service / future / decommissioned elements.</summary>
    public bool EnergisedOnly { get; set; } = true;

    /// <summary>Attach an OLTC to every step-down transformer (regulates its LV bus to 1.0 p.u.).</summary>
    public bool CreateOltcs { get; set; } = true;

    /// <summary>Fold embedded generation into negative demand instead of emitting Generator records.</summary>
    public bool EmbeddedGenAsNegativeLoad { get; set; } = false;

    /// <summary>
    /// Admit only embedded generation whose connection status says it is CONNECTED (default
    /// true). Plant that is merely accepted, offered or contracted to connect is not built
    /// yet, so it must not be dispatched in a replica of today's network; it is excluded and
    /// its MW reported in the notes. A row whose status cannot be classified (blank or an
    /// unrecognised word) is excluded too and flagged as a WARNING. False restores the old
    /// behaviour: every generation row enters the model whatever its status.
    /// </summary>
    public bool ConnectedGenerationOnly { get; set; } = true;

    /// <summary>
    /// Path to a substation-geolocation table (CSV/Parquet with <c>name</c>,
    /// <c>longitude</c>, <c>latitude</c>) used to give buses real GB coordinates.
    /// Null (default) leaves bus X/Y untouched.
    /// </summary>
    public string? SubstationsPath { get; set; }

    /// <summary>
    /// When a <see cref="SubstationsPath"/> is supplied and the file exists, project
    /// matched buses onto GB geography. Has no effect when SubstationsPath is null.
    /// </summary>
    public bool Geolocate { get; set; } = true;
}

/// <summary>Counts and notes describing what was built  -  printed as the structural report (no data values leak).</summary>
public sealed record GdaDiagnostics(
    int RawNodes, int Buses, int BusbarTiesMerged,
    int SeriesBranches, int Transformers2, int Transformers3, int StarNodes,
    int DemandPoints, double TotalDemandMw, int UnmatchedDemand,
    int Generators, double TotalGenCapacityMw, int UnmatchedGen,
    int Islands, int LargestIslandBuses,
    IReadOnlyList<KeyValuePair<int, int>> BusesByKv,
    IReadOnlyList<string> Notes)
{
    /// <summary>Step-down transformers given no OLTC because they publish a 0 %/0 % tap range (off-load units).</summary>
    public int OltcsWithoutTapRange { get; init; }

    /// <summary>Step-down transformers given no OLTC because their published tap range is impossible or implausible (named in the notes).</summary>
    public int OltcsWithInvalidTapRange { get; init; }

    /// <summary>Zero-impedance circuits merged as ties although their Equipment is not a busbar or switch (likely missing R/X).</summary>
    public int ZeroImpedanceNonTieCircuits { get; init; }

    /// <summary>Transformers whose published reactance was degenerate and was floored to the minimum (named in the notes).</summary>
    public int DegenerateTransformers { get; init; }

    /// <summary>Matched generation rows left out because their status says they are not connected yet (accepted, offered, ...).</summary>
    public int GenNotConnected { get; init; }

    /// <summary>Export capacity (MW) of the <see cref="GenNotConnected"/> rows.</summary>
    public double GenNotConnectedMw { get; init; }

    /// <summary>Matched generation rows left out because their status is blank or unrecognised (flagged as a WARNING note).</summary>
    public int GenUnknownStatus { get; init; }

    /// <summary>Export capacity (MW) of the <see cref="GenUnknownStatus"/> rows.</summary>
    public double GenUnknownStatusMw { get; init; }

    /// <summary>Demand rows below the <see cref="GdaBuildOptions.MinKv"/> cut folded onto their nearest boundary bus (included in <see cref="DemandPoints"/> and <see cref="TotalDemandMw"/>).</summary>
    public int AggregatedDemand { get; init; }

    /// <summary>MW of the <see cref="AggregatedDemand"/> rows.</summary>
    public double AggregatedDemandMw { get; init; }

    /// <summary>Live demand rows whose node is outside the built network and was not folded (other carve areas, or nodes on no in-service element).</summary>
    public int OutOfScopeDemand { get; init; }

    /// <summary>MW of the <see cref="OutOfScopeDemand"/> rows.</summary>
    public double OutOfScopeDemandMw { get; init; }

    /// <summary>Generation rows joined by primary name alone because their GSP group did not list the primary but the name is unique to one group.</summary>
    public int GenJoinedByUniqueName { get; init; }

    /// <summary>Generation rows left unplaced (and counted in <see cref="UnmatchedGen"/>) because their primary name exists under several GSP groups, none of them theirs.</summary>
    public int GenAmbiguousPrimary { get; init; }

    /// <summary>Export capacity (MW) of the <see cref="GenAmbiguousPrimary"/> rows.</summary>
    public double GenAmbiguousPrimaryMw { get; init; }

    /// <summary>(GSP group, primary) keys whose demand rows sit on more than one bus; generation there joins the last-listed bus
    /// (the whitespace key), or, under the canonical key (NGED, N205), the bus of its own voltage.</summary>
    public int PrimariesOnSeveralBuses { get; init; }

    /// <summary>N205: generation rows (canonical key only) whose site has demand on several buses and that joined the one
    /// bus whose demand voltage is the generator's own (see <see cref="GdaModelBuilder.CanonicalPrimaryVoltageKv"/>).</summary>
    public int GenJoinedBySiteVoltage { get; init; }

    /// <summary>N205: generation rows (canonical key only) left unplaced (and counted in <see cref="UnmatchedGen"/>)
    /// because their site has demand on several buses and their voltage does not pick exactly one.</summary>
    public int GenAmbiguousSiteVoltage { get; init; }

    /// <summary>Export capacity (MW) of the <see cref="GenAmbiguousSiteVoltage"/> rows.</summary>
    public double GenAmbiguousSiteVoltageMw { get; init; }
}

public sealed record GdaBuildResult(GridModel Model, GdaDiagnostics Diagnostics);

/// <summary>
/// Assembles a complete electrical <see cref="GridModel"/> from a DNO LTDS dataset  - 
/// every node, cable, transformer, demand point and embedded generator in a real
/// footprint. Busbars and disconnectors (zero-impedance ties) are collapsed by
/// union-find so each electrical node becomes one bus; impedances are converted to
/// per-unit according to the dataset's <see cref="ImpedanceConvention"/>; and every
/// Grid Supply Point is modelled as a transmission infeed (slack).
/// </summary>
public static class GdaModelBuilder
{
    // GR-330: "not in service", "de-energised" and "not energised" are dead too; they used to read as alive (a circuit
    // "Not in service" was built as a live branch).
    private static readonly string[] DeadStatuses =
        { "out of service", "decommiss", "removed", "future", "planned", "abandon", "proposed",
          "not in service", "de-energ", "deenerg", "not energ", "un-energ", "unenerg" };

    /// <summary>
    /// N347 (CF4, October 2026; Ruling R1): THE one place that says how NGED's LTDS circuit table is read. NGED's
    /// Table 1 R1 / X1 / B1 are PERCENT ON 100 MVA (B1 the percent charging), not ohms referred to the GSP voltage with B
    /// in microsiemens as GDA's normaliser (and GridSim before this) read them.
    /// <para><b>Proof</b> (as N252 proved the transformers): NGED's own Table 4 (ltds-table-4-fault-level.csv, the GDA
    /// lake copy of the NGED LTDS tabular model, read 2026-10-11) gives the Thevenin impedance R1 + jX1 in pu on 100 MVA
    /// at each busbar. For a site fed radially by one circuit from one source site (and nothing else at that voltage),
    /// Z_th(site) = Z_th(source) + Z_circuit. Reading the circuit as percent on 100 MVA matches the published difference
    /// in BOTH R and X to the 4 published decimals, at 33, 66 and 132 kV; reading it as GSP-referred ohms (Zbase =
    /// kV^2 / 100) misses by the factor Zbase / 100 (10.89 at 33 kV, 1.7424 at 132 kV):</para>
    /// <list type="bullet">
    /// <item>33 kV, EMIDS RATS3 fed from TOTO3 (one circuit, R1 12.28, X1 15.15): Table 4 dZ = 0.1228 + j0.1515 pu;
    /// percent reading 0.1228 + j0.1515; ohmic reading 1.1275 + j1.3915.</item>
    /// <item>33 kV, EMIDS GECW3 from WIGS3: dZ 0.0583 + j0.0953; percent 0.0583 + j0.0954; ohmic 0.5352 + j0.8757.</item>
    /// <item>33 kV, WMIDS FROG3 from BRIS3: dZ 0.0514 + j0.0790; percent 0.0513 + j0.0790; ohmic 0.4713 + j0.7252.</item>
    /// <item>66 kV, WMIDS TEWK6 from BROG6: dZ 0.0767 + j0.1161; percent 0.0767 + j0.1161; ohmic 0.1760 + j0.2666.</item>
    /// <item>132 kV, EMIDS HAGC1 from NOGR1: dZ 0.0156 + j0.0424; percent 0.0156 + j0.0425; ohmic 0.0089 + j0.0244.</item>
    /// </list>
    /// <para>The B1 column is read on the same basis (the LTDS form gives R, X and B of a circuit together on one base);
    /// as percent the 132 kV per-km median B1 0.0827 is 4.7 uS/km (a normal overhead-line / cable mix), as microsiemens it
    /// was 0.083 uS/km, about 35 times less charging than a 132 kV overhead line alone carries. A probe found
    /// the radial pairs.</para>
    /// </summary>
    public const ImpedanceConvention NgedCircuitConvention = ImpedanceConvention.PercentOn100Mva;

    /// <summary>Load the NGED LTDS tabular model from disk into a dataset: circuits and transformers percent on 100 MVA
    /// (N347 <see cref="NgedCircuitConvention"/>, N252 <see cref="LtdsDataset.TransformerPercentOn100Mva"/>).</summary>
    public static LtdsDataset LoadNged(GdaPaths paths) => new(
        "GB/NGED ex-GDA", NgedCircuitConvention,
        LtdsReader.Nodes(paths.LtdsNodes),
        LtdsReader.Circuits(paths.LtdsCircuits),
        LtdsReader.TwoWindingTransformers(paths.LtdsTwoWinding),
        LtdsReader.ThreeWindingTransformers(paths.LtdsThreeWinding),
        LtdsReader.Demand(paths.LtdsDemand),
        paths.Has(p => p.LtdsGeneration) ? LtdsReader.Generation(paths.LtdsGeneration) : Array.Empty<LtdsGeneration>())
    {
        // N184: NGED's two tables spell primaries differently; join them on the canonical primary key.
        CanonicalPrimaryJoin = true,
        // N252: NGED's transformer impedances are percent on 100 MVA (proof on the property), not GSP-referred ohms.
        TransformerPercentOn100Mva = true,
    };

    /// <summary>Build from the NGED LTDS tabular model on disk (percent on 100 MVA throughout; N252, N347).</summary>
    public static GdaBuildResult Build(GdaPaths paths, GdaBuildOptions? options = null)
        => Build(LoadNged(paths), options);

    public static GdaBuildResult Build(LtdsDataset ds, GdaBuildOptions? options = null)
    {
        var opt = options ?? new GdaBuildOptions();
        bool percent = ds.Convention == ImpedanceConvention.PercentOn100Mva;
        var notes = new List<string>();
        notes.AddRange(ds.SourceNotes);   // N194: the reader's own notes first (none for NGED; one for NPg since N202)
        // N184: the primary-name key of the generation-to-demand join. The canonical key only when the dataset asks for
        // it (NGED); otherwise the whitespace-and-case key every build used before, so those builds are unchanged.
        Func<string, string> primaryKey = ds.CanonicalPrimaryJoin ? CanonicalPrimaryKey : NormKey;

        IReadOnlyList<LtdsNode> nodes = ds.Nodes;
        IReadOnlyList<LtdsCircuit> circuits = ds.Circuits;
        IReadOnlyList<LtdsTransformer2> tx2 = ds.Transformers2;
        IReadOnlyList<LtdsTransformer3> tx3 = ds.Transformers3;
        IReadOnlyList<LtdsDemand> demand = ds.Demand;
        IReadOnlyList<LtdsGeneration> gen = ds.Generation;

        int rawNodes = nodes.Count;

        // ---- region / status / voltage filters --------------------------------
        // GSP-group carving compares canonical keys (GspKey) so the user's circuit-table group
        // name also matches the generation table's differently-spelled name for the same group.
        // N211: a carve typed with a published alias spelling ("KNAREBOROUGH") selects the canonical group.
        string? gspKey = opt.GspGroup is null ? null : GspKey(ApplyGroupWordAliases(opt.GspGroup, ds.GroupWordAliases));
        bool RegionOkC(string licence, string gsp) =>
            (opt.Licence is null || licence.Equals(opt.Licence, StringComparison.OrdinalIgnoreCase)) &&
            (gspKey is null || GspKey(gsp) == gspKey);
        bool Alive(string status) =>
            !opt.EnergisedOnly || !DeadStatuses.Any(d => status.Contains(d, StringComparison.OrdinalIgnoreCase));

        // Region and status first, then the voltage cut. The region/status-filtered lists are
        // kept because the elements removed ONLY by the voltage cut are the network below the
        // cut, whose demand is folded onto the boundary (see the demand fold, R047). Where()
        // preserves order, so the retained lists are element-for-element what they were.
        static double MaxKv3(LtdsTransformer3 t) => Math.Max(t.Kv1, Math.Max(t.Kv2, t.Kv3));
        List<LtdsCircuit> circuitsInRegion = circuits.Where(c => RegionOkC(c.Licence, c.GspGroup) && Alive(c.Status)).ToList();
        List<LtdsTransformer2> tx2InRegion = tx2.Where(t => RegionOkC(t.Licence, t.GspGroup) && Alive(t.Status)).ToList();
        List<LtdsTransformer3> tx3InRegion = tx3.Where(t => RegionOkC(t.Licence, t.GspGroup) && Alive(t.Status)).ToList();
        circuits = circuitsInRegion.Where(c => c.KvBase >= opt.MinKv).ToList();
        tx2 = tx2InRegion.Where(t => Math.Max(t.Kv1, t.Kv2) >= opt.MinKv).ToList();
        tx3 = tx3InRegion.Where(t => MaxKv3(t) >= opt.MinKv).ToList();

        // ---- node voltage dictionary ------------------------------------------
        var nodeKv = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        // GR-783: a node code published at two different voltages still takes the higher one (unchanged), but is now
        // counted and named in a WARNING note instead of being resolved without a word.
        var kvConflicts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        void NoteKv(string code, double kv)
        {
            if (code.Length == 0 || !(kv > 0)) return;
            if (nodeKv.TryGetValue(code, out double e))
            {
                if (kv != e) kvConflicts.Add(code);
                if (kv > e) nodeKv[code] = kv;
            }
            else nodeKv[code] = kv;
        }
        foreach (LtdsCircuit c in circuits) { NoteKv(c.Node1, c.KvBase); NoteKv(c.Node2, c.KvBase); }
        foreach (LtdsTransformer2 t in tx2) { NoteKv(t.Node1, t.Kv1); NoteKv(t.Node2, t.Kv2); }
        foreach (LtdsTransformer3 t in tx3) { NoteKv(t.Node1, t.Kv1); NoteKv(t.Node2, t.Kv2); NoteKv(t.Node3, t.Kv3); }
        if (kvConflicts.Count > 0)
            notes.Add($"WARNING node-kv: {kvConflicts.Count} node code(s) are published at more than one voltage; the higher " +
                $"voltage is used: {string.Join(", ", kvConflicts.Take(10))}{(kvConflicts.Count > 10 ? ", ..." : "")}.");

        var codeToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (LtdsNode nd in nodes) if (!codeToName.ContainsKey(nd.Code)) codeToName[nd.Code] = nd.Name;

        // ---- union-find: collapse busbar/disconnector zero-impedance ties -----
        var dsu = new Dsu();
        foreach (string code in nodeKv.Keys) dsu.Add(code);
        int ties = 0;
        // A zero-impedance row can only be modelled as a tie (a zero-impedance branch would
        // make the admittance matrix singular), so it is merged either way. But a row whose
        // Equipment says it is a real cable or line, not a busbar or switch, has most likely
        // lost its R/X (NGED's '-' placeholder cells parse as 0), and merging it collapses
        // two substations into one bus. That must not happen silently (R097, October 2026
        // review): count every such row and name the first few in the notes.
        int zeroImpedanceNonTies = 0;
        var zeroImpedanceNonTieExamples = new List<string>();
        // GR-286: transformers whose published reactance is degenerate and was floored (counted and named, like the
        // zero-impedance circuits above); and demand rows left out for a zero or negative maximum demand (GR-315).
        int degenerateTransformers = 0;
        var degenerateTransformerExamples = new List<string>();
        int demandZeroRows = 0, demandNegativeRows = 0;
        double demandNegativeMw = 0;
        foreach (LtdsCircuit c in circuits)
            if (!c.IsSeries && dsu.Contains(c.Node1) && dsu.Contains(c.Node2))
            {
                if (!IsTieEquipment(c.Equipment))
                {
                    zeroImpedanceNonTies++;
                    if (zeroImpedanceNonTieExamples.Count < NoteExampleLimit)
                        zeroImpedanceNonTieExamples.Add($"{c.Node1}-{c.Node2} '{c.Equipment}'");
                }
                if (dsu.Union(c.Node1, c.Node2)) ties++;
            }

        var rootKv = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach ((string code, double kv) in nodeKv)
        {
            string r = dsu.Find(code);
            if (!rootKv.TryGetValue(r, out double e) || kv > e) rootKv[r] = kv;
        }

        // ---- buses: assign ids lazily to roots that carry a real branch -------
        var busIdByRoot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var buses = new List<MutableBus>();
        int GetBus(string code)
        {
            string r = dsu.Find(code);
            if (busIdByRoot.TryGetValue(r, out int id)) return id;
            id = buses.Count;
            busIdByRoot[r] = id;
            double kv = rootKv.GetValueOrDefault(r, 0);
            buses.Add(new MutableBus { Id = id, Kv = kv, Name = codeToName.GetValueOrDefault(r, r) });
            return id;
        }

        // per-unit helpers (depend on the dataset's convention)
        double pctScale = opt.BaseMva / 100.0;
        double CircuitPu(double raw, string gsp) => percent ? raw / 100.0 * pctScale : raw / RefZBase(gsp, opt);
        // Susceptance is an ADMITTANCE: converting pu-on-100MVA to the model
        // base multiplies by S_old/S_new = 100/BaseMva - the INVERSE of the
        // impedance factor CircuitPu correctly uses (at BaseMva=100 the two
        // coincide, which is how the inversion hid).
        double CircuitBPu(double raw, string gsp) => percent ? raw / 100.0 / pctScale : raw * opt.SusceptanceToSiemens * RefZBase(gsp, opt);
        double TxPu(double raw, double ratingMva, string gsp)
        {
            // N252: a dataset whose transformers are percent on 100 MVA whatever its circuits' convention (NGED; since N347
            // its circuits are percent on 100 MVA too, and this gives the same value the percent branch below would with
            // a 100 MVA transformer base).
            if (ds.TransformerPercentOn100Mva) return raw / 100.0 * (opt.BaseMva / 100.0);
            if (!percent) return raw / RefZBase(gsp, opt);
            double txBase = ds.TransformerPercentOnRating
                ? (ratingMva > 0 ? ratingMva : GdaBuildDefaults.TransformerRatingFallbackMva)
                : 100.0;   // the percent-on-100-MVA convention itself (a definition, not a fallback)
            return raw / 100.0 * (opt.BaseMva / txBase);
        }

        // ---- branches ---------------------------------------------------------
        var branches = new List<Branch>();
        var oltcs = new List<Oltc>();
        int oltcsNoTapRange = 0, oltcsInvalidTapRange = 0;
        var invalidTapRangeExamples = new List<string>();
        int seriesCount = 0;
        var nameSeen = new Dictionary<string, int>();
        string Uniq(string baseName) { int k = nameSeen.GetValueOrDefault(baseName); nameSeen[baseName] = k + 1; return k == 0 ? baseName : $"{baseName}#{k + 1}"; }

        foreach (LtdsCircuit c in circuits)
        {
            if (!c.IsSeries) continue;
            if (!dsu.Contains(c.Node1) || !dsu.Contains(c.Node2)) continue;
            int f = GetBus(c.Node1), t = GetBus(c.Node2);
            if (buses[f].Gsp.Length == 0) buses[f].Gsp = c.GspGroup;
            if (buses[t].Gsp.Length == 0) buses[t].Gsp = c.GspGroup;
            if (f == t) continue; // internal to a merged bus
            if (!percent && RefZBase(c.GspGroup, opt) <= 0) continue;
            double rpu = CircuitPu(c.R1Ohm, c.GspGroup);
            double xpu = CircuitPu(c.X1Ohm, c.GspGroup);
            double bpu = CircuitBPu(c.B1MicroS, c.GspGroup);
            string nm = Uniq($"{Short(c.Node1)}-{Short(c.Node2)} {c.KvBase:F0}kV");
            branches.Add(new Branch(f, t, rpu, xpu, bpu, RateAMva: c.WinterRatingMva, Name: nm, IsTransformer: false));
            seriesCount++;
        }

        foreach (LtdsTransformer2 t in tx2)
        {
            if (!dsu.Contains(t.Node1) || !dsu.Contains(t.Node2)) continue;
            int f = GetBus(t.Node1), tt = GetBus(t.Node2);
            if (buses[f].Gsp.Length == 0) buses[f].Gsp = t.GspGroup;
            if (buses[tt].Gsp.Length == 0) buses[tt].Gsp = t.GspGroup;
            if (f == tt) continue;
            if (!percent && RefZBase(t.GspGroup, opt) <= 0) continue;
            // Impedances normalise to the system base; the voltage ratio is carried by
            // the per-unit bases, so the transformer is a series impedance at tap 1.0.
            double rpu = TxPu(t.R1Ohm, t.NominalRatingMva, t.GspGroup);
            double xpu = TxPu(t.X1Ohm, t.NominalRatingMva, t.GspGroup);
            if (double.IsNaN(rpu) || double.IsNaN(xpu))
                throw new InvalidDataException($"GDA build: transformer {t.Node1}/{t.Node2} has a NaN impedance (R1 {t.R1Ohm}, X1 {t.X1Ohm}).");
            if (Math.Abs(xpu) < GdaBuildDefaults.DegenerateTransformerXPu)
            {
                // Guard a degenerate winding, but say so: a renamed X column reads 0 on every row, and every transformer
                // then became an almost perfect short circuit between voltage levels with no word in the report.
                degenerateTransformers++;
                if (degenerateTransformerExamples.Count < NoteExampleLimit)
                    degenerateTransformerExamples.Add($"{t.Node1}/{t.Node2} X {xpu:R} pu");
                xpu = GdaBuildDefaults.MinTransformerXPu;
            }
            string nm = Uniq($"TX {Short(t.Node1)}/{Short(t.Node2)} {t.Kv1:F0}/{t.Kv2:F0}");

            // Orient every transformer branch HV -> LV (R014, October 2026 review).
            // The Branch tap sits on the From end (MATPOWER convention), so V_to ~ V_from / tap,
            // and SteadyStateSolver's OLTC loop LOWERS the tap to RAISE the bus it controls.
            // That rule is only right when the controlled bus is the To (untapped) end. The
            // LTDS lists the windings in either order (an NGED row may put the 11 kV node in
            // Node 1), so a branch built Node1 -> Node2 could leave the LV bus on the From end,
            // where each OLTC step moves the voltage the WRONG way and the tap runs to its
            // limit. Swapping the ends here puts the LV bus on the To end for every unit.
            //
            // Why this changes no unregulated number: the branch has no shunt (B = 0) and is
            // built at tap 1.0 with no phase shift, so its admittance stamp is symmetric in
            // From and To; y/tap^2 with tap = 1.0 is exactly y. The Ybus entries, and their
            // accumulation order (branch order is unchanged), are identical either way. What
            // changes is (a) which end reports "from" flows and (b) the OLTC now regulates.
            // Bus ids are still assigned in the listing order above (GetBus(Node1) first), so
            // the bus numbering of every existing build is unchanged.
            bool lvListedFirst = t.Kv1 < t.Kv2;
            int hvEnd = lvListedFirst ? tt : f;
            int lvEnd = lvListedFirst ? f : tt;
            branches.Add(new Branch(hvEnd, lvEnd, rpu, xpu, 0, RateAMva: t.NominalRatingMva, Name: nm, IsTransformer: true));
            if (opt.CreateOltcs && t.Kv1 != t.Kv2)
            {
                // The controlled bus is the LV end, which is now always the branch's To end.
                // Pedantic gate: if a future edit ever breaks that invariant, fail loud here
                // rather than emit an OLTC that the solver would drive the wrong way.
                if (branches[^1].To != lvEnd)
                    throw new InvalidOperationException(
                        $"GDA build invariant broken: OLTC on '{nm}' would control bus {lvEnd}, which is not the To end of its branch.");

                // Tap limits come from the PUBLISHED tap range (R097, October 2026 review), not
                // a hard-coded 0.9..1.1 for every unit. MinTap / MaxTap here are OFFSETS from the
                // nominal ratio in percent: NGED 'Min Tap'/'Max Tap' and NPg 'Minimum/Maximum Tap %'
                // publish them so; UKPN 'Tap_Range_*_Percent' publishes percent OF nominal (90 / 110)
                // and UkpnSource converts it to an offset (N195). The tap is on the HV (From) end, so a +5 % tap is a
                // ratio of 1.05, which LOWERS the LV voltage; that is the same sense the solver
                // uses (it raises the tap when the controlled bus is high). Hence ratio = 1 + pct/100.
                // A published -10 %/+10 % gives exactly the old 0.9/1.1 (1.0 + -0.1 rounds to the
                // same double as the literal 0.9, and 1.0 + 0.1 to 1.1), so units with the common
                // symmetric range build bit-identically to before.
                switch (ClassifyTapRange(t.MinTap, t.MaxTap, out string why))
                {
                    case TapRangeVerdict.Valid:
                        double minTap = 1.0 + t.MinTap / 100.0;
                        double maxTap = 1.0 + t.MaxTap / 100.0;
                        oltcs.Add(new Oltc(nm, lvEnd, TargetVpu: 1.0, MinTap: minTap, MaxTap: maxTap,
                            StepPu: GdaBuildDefaults.OltcStepPu, DeadbandPu: GdaBuildDefaults.OltcDeadbandPu));
                        break;
                    case TapRangeVerdict.NoTapChanger:
                        // 0 %/0 %: an off-load (or fixed-ratio) unit. It has nothing to move on
                        // load, so it gets no OLTC; counted and reported in the notes.
                        oltcsNoTapRange++;
                        break;
                    default:
                        // Impossible or implausible range: refuse to invent limits for it.
                        // Counted, the first few named, and flagged as a WARNING in the notes.
                        oltcsInvalidTapRange++;
                        if (invalidTapRangeExamples.Count < NoteExampleLimit)
                            invalidTapRangeExamples.Add(FormattableString.Invariant($"'{nm}' ({t.MinTap:R} %/{t.MaxTap:R} %: {why})"));
                        break;
                }
            }
        }

        int starNodes = 0;
        var starBusIds = new HashSet<int>();   // synthesized 3-winding star (T) nodes - never real GSP infeeds
        foreach (LtdsTransformer3 t in tx3)
        {
            if (!dsu.Contains(t.Node1) || !dsu.Contains(t.Node2)) continue;
            int h = GetBus(t.Node1), l = GetBus(t.Node2);
            if (buses[h].Gsp.Length == 0) buses[h].Gsp = t.GspGroup;
            if (buses[l].Gsp.Length == 0) buses[l].Gsp = t.GspGroup;
            bool hasTert = t.Node3.Length > 0 && dsu.Contains(t.Node3) && t.Kv3 > 0;
            double hv = Math.Max(t.Kv1, Math.Max(t.Kv2, t.Kv3));
            if (!percent && RefZBase(t.GspGroup, opt) <= 0) continue;

            // Star (T) equivalent from the three measured short-circuit reactances.
            double xHL = t.X1HvLvOhm, xLT = t.X1LvTertOhm, xHT = t.X1HvTertOhm;
            double xH = 0.5 * (xHL + xHT - xLT);
            double xL = 0.5 * (xHL + xLT - xHT);
            double xT = 0.5 * (xHT + xLT - xHL);
            double Floor(double x) => x <= 0 ? GdaBuildDefaults.StarLegFloorFraction * Math.Abs(xHL) + GdaBuildDefaults.StarLegFloorAbs : x;

            int star = buses.Count;
            buses.Add(new MutableBus { Id = star, Kv = hv, Name = $"STAR {Short(t.Node1)}" });
            starBusIds.Add(star);
            starNodes++;
            double rpu = TxPu(t.R1Ohm, t.NominalRatingMva, t.GspGroup);
            branches.Add(new Branch(h, star, rpu, TxPu(Floor(xH), t.NominalRatingMva, t.GspGroup), 0, RateAMva: t.NominalRatingMva, Name: Uniq($"TX3 H {Short(t.Node1)}"), IsTransformer: true));
            branches.Add(new Branch(star, l, 0, TxPu(Floor(xL), t.NominalRatingMva, t.GspGroup), 0, RateAMva: t.NominalRatingMva, Name: Uniq($"TX3 L {Short(t.Node2)}"), IsTransformer: true));
            if (hasTert)
            {
                int tert = GetBus(t.Node3);
                branches.Add(new Branch(star, tert, 0, TxPu(Floor(xT), t.NominalRatingMva, t.GspGroup), 0, RateAMva: t.NominalRatingMva, Name: Uniq($"TX3 T {Short(t.Node3)}"), IsTransformer: true));
            }
        }

        // ---- below-cut owner map (R047) ---------------------------------------
        // With MinKv > 0 the network below the cut is not built, but its demand is real load
        // drawn through the boundary. It used to be dropped silently (the replica solved
        // with a fraction of its demand while reporting every point "matched"). Now every
        // node below the cut is assigned to its NEAREST retained boundary bus, by a
        // simultaneous breadth-first search from all boundary buses over the in-region,
        // in-service elements that only the voltage cut removed. Seeds are enqueued in
        // ascending bus-id order and neighbours in data order, so the search is fully
        // deterministic and a node equidistant (in hops) from two boundary buses goes to the
        // lower bus id. Losses and voltage drop below the cut are not modelled.
        Dictionary<string, int>? belowCutOwner = null;
        if (opt.MinKv > 0)
        {
            var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            void Link(string a, string b)
            {
                if (a.Length == 0 || b.Length == 0) return;
                if (!adj.TryGetValue(a, out List<string>? la)) adj[a] = la = new List<string>();
                if (!adj.TryGetValue(b, out List<string>? lb)) adj[b] = lb = new List<string>();
                la.Add(b);
                lb.Add(a);
            }
            foreach (LtdsCircuit c in circuitsInRegion) if (c.KvBase < opt.MinKv) Link(c.Node1, c.Node2);
            foreach (LtdsTransformer2 t in tx2InRegion) if (Math.Max(t.Kv1, t.Kv2) < opt.MinKv) Link(t.Node1, t.Node2);
            foreach (LtdsTransformer3 t in tx3InRegion)
                if (MaxKv3(t) < opt.MinKv)
                {
                    Link(t.Node1, t.Node2);
                    if (t.Node3.Length > 0) { Link(t.Node1, t.Node3); Link(t.Node2, t.Node3); }
                }

            // Boundary seeds: below-cut graph nodes that are also built buses. (Dsu.Find adds a
            // missing code, so membership is tested with Contains first.)
            var seeds = new List<(int Bus, string Code)>();
            foreach (string code in adj.Keys)
                if (dsu.Contains(code) && busIdByRoot.TryGetValue(dsu.Find(code), out int seedBus))
                    seeds.Add((seedBus, code));
            seeds.Sort((x, y) => x.Bus != y.Bus ? x.Bus.CompareTo(y.Bus) : StringComparer.OrdinalIgnoreCase.Compare(x.Code, y.Code));

            belowCutOwner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<string>();
            foreach ((int seedBus, string code) in seeds) { belowCutOwner[code] = seedBus; queue.Enqueue(code); }
            while (queue.Count > 0)
            {
                string u = queue.Dequeue();
                int owner = belowCutOwner[u];
                foreach (string v in adj[u])
                    if (!belowCutOwner.ContainsKey(v)) { belowCutOwner[v] = owner; queue.Enqueue(v); }
            }
        }

        // ---- demand fold ------------------------------------------------------
        int unmatchedDemand = 0, matchedDemand = 0;
        double totalDemand = 0;
        int aggregatedDemand = 0, outOfScopeDemand = 0;
        double aggregatedDemandMw = 0, outOfScopeDemandMw = 0;
        // (canonical GSP key, normalised primary name) -> bus. Both parts are already
        // case-folded (GspKey and NormKey lower-case), so ordinal comparison is exact.
        var primaryToBus = new Dictionary<(string Gsp, string Primary), int>();
        var primaryKeysOnSeveralBuses = new HashSet<(string Gsp, string Primary)>();
        // N205 (October 2026, Ruling R1): under the canonical key (NGED) the key folds one site's primaries of different
        // voltages together ("Courthouse Green 11" and "Courthouse Green 6.6" both give "courthousegreen"). Every bus a
        // key reaches is kept here with the voltage its demand row's NAME carries (null when the name carries none), in
        // data order, so a generator at such a site can join the bus of its own voltage instead of the last-listed one.
        // Not used for the whitespace key: those builds keep the R048 last-writer rule exactly.
        var primaryBusesByVoltage = new Dictionary<(string Gsp, string Primary), List<(double? Kv, int Bus)>>();
        void RegisterVoltage((string Gsp, string Primary) key, string primaryName, int busId)
        {
            if (!ds.CanonicalPrimaryJoin) return;
            if (!primaryBusesByVoltage.TryGetValue(key, out List<(double? Kv, int Bus)>? list))
                primaryBusesByVoltage[key] = list = new List<(double? Kv, int Bus)>();
            list.Add((CanonicalPrimaryVoltageKv(primaryName), busId));
        }
        foreach (LtdsDemand d in demand)
        {
            if (d.JoinOnly)
            {
                // N194: a join-only row (UKPN grid board, whose figure is the sum of other rows) adds no load and
                // is not counted as a demand point; it only puts its substation name on its bus so generation
                // connected at that board still joins. Only a live row on a built bus registers; anything else is
                // skipped silently because no load was ever going to be placed for it. No other reader sets the
                // flag, so this branch never runs for NGED or NPg and their builds are unchanged.
                if (!Alive(d.Status) || !IsJoinKey(d.PrimarySubstation) || !dsu.Contains(d.Node)) continue;
                if (!busIdByRoot.TryGetValue(dsu.Find(d.Node), out int joinBus)) continue;
                var joinKey = (GspKey(d.GspGroup), primaryKey(d.PrimarySubstation));
                if (primaryToBus.TryGetValue(joinKey, out int earlierJoin) && earlierJoin != joinBus) primaryKeysOnSeveralBuses.Add(joinKey);
                primaryToBus[joinKey] = joinBus;
                RegisterVoltage(joinKey, d.PrimarySubstation, joinBus);
                continue;
            }
            // A NaN demand (a 'NaN' cell, a NaN Parquet double) passed the "<= 0" gate into Pd; it is refused. Zero and
            // negative (net-exporting) rows are left out as before, but counted for the notes.
            if (double.IsNaN(d.MaxDemandMw))
                throw new InvalidDataException($"GDA build: demand row for node {d.Node} (primary '{d.PrimarySubstation}') has a NaN maximum demand.");
            if (d.MaxDemandMw <= 0)
            {
                if (d.MaxDemandMw < 0) { demandNegativeRows++; demandNegativeMw += d.MaxDemandMw; }
                else demandZeroRows++;
                continue;
            }
            if (!Alive(d.Status)) continue;                            // 'Removed'/decommissioned substations must not fold demand in
            int bus;
            if (!dsu.Contains(d.Node))
            {
                // Not on the built network. Below the voltage cut: fold onto its boundary bus.
                // Otherwise it is outside this build (another carve, or a node on no in-scope
                // element): counted and reported, never silently lost.
                if (belowCutOwner is not null && belowCutOwner.TryGetValue(d.Node, out int boundaryBus))
                {
                    bus = boundaryBus;
                    aggregatedDemand++;
                    aggregatedDemandMw += d.MaxDemandMw;
                }
                else
                {
                    outOfScopeDemand++;
                    outOfScopeDemandMw += d.MaxDemandMw;
                    continue;
                }
            }
            else
            {
                if (!busIdByRoot.ContainsKey(dsu.Find(d.Node))) { unmatchedDemand++; continue; } // in scope but a dangling leaf
                bus = GetBus(d.Node);
            }
            double pf = d.PowerFactor is > 0 and <= 1 ? d.PowerFactor : GdaBuildDefaults.DemandPowerFactor;
            // D1 (October 2026): tan(acos(pf)) through GridSim's deterministic maths (Core's DetMath), not the OS libm,
            // so a GDA-built case has the same reactive demand bits on every operating system.
            double q = d.MaxDemandMw * GridSim.Core.Numerics.DetMath.Tan(GridSim.Core.Numerics.DetMath.Acos(pf));
            buses[bus].Pd += d.MaxDemandMw;
            buses[bus].Qd += q;
            totalDemand += d.MaxDemandMw;
            matchedDemand++;
            // Only a real substation name may seed the generation join key; '-'/blank must not
            // (see IsJoinKey) or every dash-generation row lands on the last dash-demand bus.
            // The key is (GSP group, primary name), not the name alone (R048): names are not
            // unique across GSPs or licence areas. Within one GSP group the last-listed row
            // still wins, as before; how often that happens is reported below.
            if (IsJoinKey(d.PrimarySubstation) && primaryKey(d.PrimarySubstation).Length > 0)
            {
                // N184: a name the canonical key empties (only voltage and station words) is not a join key either.
                var key = (GspKey(d.GspGroup), primaryKey(d.PrimarySubstation));
                if (primaryToBus.TryGetValue(key, out int earlier) && earlier != bus) primaryKeysOnSeveralBuses.Add(key);
                primaryToBus[key] = bus;
                RegisterVoltage(key, d.PrimarySubstation, bus);
            }
        }
        int demandConsidered = matchedDemand + unmatchedDemand;

        // Dataset-wide primary register: for every primary name, the GSP groups it appears
        // under in the WHOLE demand table (every live row, regardless of this build's carve or
        // voltage cut). It decides how a generation row joins, independent of what this
        // build happens to contain, so a licence carve cannot make a colliding name look unique.
        var primaryGspGroups = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (LtdsDemand d in demand)
        {
            if (!Alive(d.Status) || !IsJoinKey(d.PrimarySubstation)) continue;
            string name = primaryKey(d.PrimarySubstation);
            if (name.Length == 0) continue;   // N184: never empty under NormKey after IsJoinKey; guards the canonical key
            if (!primaryGspGroups.TryGetValue(name, out SortedSet<string>? groups))
                primaryGspGroups[name] = groups = new SortedSet<string>(StringComparer.Ordinal);
            groups.Add(GspKey(d.GspGroup));
        }

        // ---- generation -------------------------------------------------------
        var generators = new List<Generator>();
        int unmatchedGen = 0;
        double totalGenCap = 0;
        // Connection-status filter (R015, October 2026 review). Every reader captures a
        // status (NGED 'Connection Status', UKPN 'Connected_Accepted', NPg
        // 'Connected/Accepted'), but it used to be ignored, so plant that is only ACCEPTED to
        // connect was dispatched as if built. Counted only for rows that would otherwise
        // have been placed, so the reported MW is exactly what left the model.
        int genNotConnected = 0, genUnknownStatus = 0;
        double genNotConnectedMw = 0, genUnknownStatusMw = 0;
        var notConnectedStatuses = new SortedSet<string>(StringComparer.Ordinal);
        var unknownStatuses = new SortedSet<string>(StringComparer.Ordinal);
        int genJoinedByUniqueName = 0, genAmbiguousPrimary = 0;
        double genAmbiguousPrimaryMw = 0;
        var ambiguousPrimaryExamples = new List<string>();
        int genJoinedBySiteVoltage = 0, genAmbiguousSiteVoltage = 0;   // N205
        double genAmbiguousSiteVoltageMw = 0;
        var ambiguousSiteVoltageExamples = new List<string>();
        foreach (LtdsGeneration g in gen)
        {
            if (double.IsNaN(g.ExportCapacityMw))
                throw new InvalidDataException($"GDA build: generation row at '{g.PrimarySubstation}' has a NaN export capacity.");
            if (g.ExportCapacityMw <= 0) continue;
            // When carving a GSP group, restrict generation to it  -  substation names
            // aren't globally unique, so a pure name match would let the national
            // fleet flood a single GSP (13 GW into a 0.9 GW network). Compare canonical
            // GspKeys because the generation table spells the group differently.
            if (gspKey is not null && GspKey(g.GspGroup) != gspKey) continue;
            // A '-'/blank primary substation is an NGED null marker, not a joinable name.
            if (!IsJoinKey(g.PrimarySubstation)) { unmatchedGen++; continue; }

            // Join on (GSP group, primary), R048. It used to be the primary name alone, last
            // writer wins, so a generator whose primary shared a name with one in another GSP
            // or licence area could land in the wrong place, or in a carve it did not belong to.
            //  1. The generator's own GSP group lists this primary: join there.
            //  2. Its GSP group does not (the generation and demand tables can name groups
            //     differently), but the name appears under exactly ONE group dataset-wide: the
            //     name is unambiguous, so join there; counted in the notes.
            //  3. The name appears under several groups and none is the generator's: it cannot
            //     be placed reliably. Left unplaced (unmatched) and reported as a WARNING.
            string primary = primaryKey(g.PrimarySubstation);
            if (primary.Length == 0 || !primaryGspGroups.TryGetValue(primary, out SortedSet<string>? groupsOfName)) { unmatchedGen++; continue; }
            string genGsp = GspKey(g.GspGroup);
            string ownerGsp;
            if (groupsOfName.Contains(genGsp)) ownerGsp = genGsp;
            else if (groupsOfName.Count == 1) { ownerGsp = groupsOfName.Min!; genJoinedByUniqueName++; }
            else
            {
                genAmbiguousPrimary++;
                genAmbiguousPrimaryMw += g.ExportCapacityMw;
                if (ambiguousPrimaryExamples.Count < NoteExampleLimit) ambiguousPrimaryExamples.Add($"'{g.PrimarySubstation}' ({g.GspGroup})");
                unmatchedGen++;
                continue;
            }
            // The owning primary exists, but not on this build's network (another carve area,
            // or a row with no bus): unmatched, exactly as a name miss always was.
            if (!primaryToBus.TryGetValue((ownerGsp, primary), out int bus)) { unmatchedGen++; continue; }
            // N205 (October 2026, Ruling R1): the canonical key names a SITE; where that site's demand rows sit on more
            // than one bus (its primaries of different voltages: "Courthouse Green 11" and "... 6.6"), the generator
            // joins by site AND voltage: the one bus whose demand name carries the generator's voltage (the lowest
            // voltage in its own name, "33 11 kV" -> 11, the primary's lower voltage as the demand table names it;
            // failing that, its connection voltage). A voltage that picks no bus, or several, leaves the row unplaced
            // with a WARNING (the R048 rule for an ambiguous name), never on the last-listed bus by data order.
            if (primaryBusesByVoltage.TryGetValue((ownerGsp, primary), out List<(double? Kv, int Bus)>? atSite)
                && atSite.Select(c => c.Bus).Distinct().Count() > 1)
            {
                double? genKv = CanonicalPrimaryVoltageKv(g.PrimarySubstation) ?? (g.Kv > 0 ? g.Kv : null);
                int[] ofVoltage = genKv is double kv
                    ? atSite.Where(c => c.Kv == kv).Select(c => c.Bus).Distinct().ToArray()
                    : Array.Empty<int>();
                if (ofVoltage.Length != 1)
                {
                    genAmbiguousSiteVoltage++;
                    genAmbiguousSiteVoltageMw += g.ExportCapacityMw;
                    if (ambiguousSiteVoltageExamples.Count < NoteExampleLimit)
                        ambiguousSiteVoltageExamples.Add(FormattableString.Invariant(
                            $"'{g.PrimarySubstation}' ({g.GspGroup}; {(genKv is double v ? v.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + " kV" : "no voltage")})"));
                    unmatchedGen++;
                    continue;
                }
                bus = ofVoltage[0];
                genJoinedBySiteVoltage++;
            }
            if (opt.ConnectedGenerationOnly)
            {
                GenConnection state = ClassifyGenStatus(g.Status);
                if (state == GenConnection.NotConnected)
                {
                    genNotConnected++;
                    genNotConnectedMw += g.ExportCapacityMw;
                    notConnectedStatuses.Add(g.Status.Trim());
                    continue;
                }
                if (state == GenConnection.Unknown)
                {
                    genUnknownStatus++;
                    genUnknownStatusMw += g.ExportCapacityMw;
                    unknownStatuses.Add(g.Status.Trim());
                    continue;
                }
            }
            totalGenCap += g.ExportCapacityMw;
            FuelMap.Kind kind = FuelMap.Classify(g.FuelType);
            double cap = g.ExportCapacityMw;
            double output = cap * FuelMap.CapacityFactor(kind.Type); // realistic dispatch, not nameplate
            if (opt.EmbeddedGenAsNegativeLoad)
            {
                buses[bus].Pd -= output;
            }
            else
            {
                generators.Add(new Generator(
                    Bus: bus, Pg: output, Qg: 0, Vg: 1.0,
                    Qmin: -GdaBuildDefaults.EmbeddedGenQRatio * cap, Qmax: GdaBuildDefaults.EmbeddedGenQRatio * cap, Pmin: 0, Pmax: cap,
                    MBase: cap, H: kind.H, Name: $"{g.PrimarySubstation} {kind.Type}", Type: kind.Type));
            }
        }

        // ---- slack & bus types ------------------------------------------------
        int n = buses.Count;
        var degree = new int[n];
        foreach (Branch b in branches) { degree[b.From]++; degree[b.To]++; }

        int[] comp = ComponentIds(n, branches);
        int islands = comp.Length == 0 ? 0 : comp.Max() + 1;
        var compSize = new int[Math.Max(1, islands)];
        for (int i = 0; i < n; i++) compSize[comp[i]]++;
        int largest = compSize.Length > 0 ? compSize.Max() : 0;
        int largestComp = 0;
        for (int c = 0; c < compSize.Length; c++) if (compSize[c] == largest) { largestComp = c; break; }

        // One infeed (slack) per GSP GROUP  -  a DNO network is fed from the transmission
        // system at *every* Grid Supply Point, not once per island. Forcing a whole
        // licence area through a single infeed collapses its far feeders; modelling each
        // GSP as its own infeed reflects the real supergrid -> 132 kV connections.
        var gspBus = new Dictionary<string, int>(StringComparer.Ordinal);
        var gspKv2 = new Dictionary<string, double>(StringComparer.Ordinal);
        var gspDeg = new Dictionary<string, int>(StringComparer.Ordinal);
        var siteBuses = new Dictionary<string, List<int>>(StringComparer.Ordinal);   // N194, used only under InfeedPerGspGroupPerIsland
        // N194: under InfeedPerGspGroupPerIsland, the GSP site name of every group (the group's name without a
        // trailing voltage word), longest first, so a bus AT a GSP site can be recognised whatever group the
        // first branch seen at it happened to carry. Empty (never used) when the flag is off.
        List<(string Group, string Site)> gspSites = ds.InfeedPerGspGroupPerIsland
            ? buses.Select(b => b.Gsp).Where(g => g.Length > 0).Distinct(StringComparer.Ordinal)
                .Select(g => (Group: g, Site: SiteName(g))).Where(x => x.Site.Length > 0)
                .OrderByDescending(x => x.Site.Length).ThenBy(x => x.Group, StringComparer.Ordinal).ToList()
            : new List<(string Group, string Site)>();
        for (int i = 0; i < n; i++)
        {
            // A synthesized 3-winding star node has no GSP and sits at the highest winding kV,
            // so it would win the "_isl{comp}" fallback group and pin an internal transformer
            // node as a phantom slack (and get a fake 'Grid infeed' generator on export). It is
            // never a real supergrid infeed, so it must not be a slack candidate here.
            if (starBusIds.Contains(i)) continue;
            if (ds.InfeedPerGspGroupPerIsland)
            {
                // N194 (UKPN; see LtdsDataset.InfeedPerGspGroupPerIsland). Two changes, both only under the flag:
                //  1. One infeed per GSP group IN EACH ISLAND (the key carries the island).
                //  2. A bus at a GSP site (its name begins with a group's site name as whole words: "West Weybridge
                //     132kV(NEW)" for group "West Weybridge", "Elstree GIS 132kV" for "Elstree") belongs to THAT group
                //     for this choice, whatever group its first branch carried. When a group has site buses in an
                //     island, EVERY site bus at the highest voltage among them is an infeed (each is a section of the
                //     GSP board with its own supergrid transformers: Redbridge Supergrid 33kV is published as REBR30
                //     and REBR32, unconnected); only when it has none does the old single choice (higher kV, then
                //     more branches) apply. Without this the infeed went to the busiest 132 kV node of the group,
                //     often a grid site several circuits away (Leatherhead for West Weybridge), a GSP section tagged
                //     with a neighbour's group (Chessington's CHSI10, reached first through a West Weybridge circuit)
                //     got no infeed at all, and a second section of a GSP (REBR32) was fed only through the 33 kV
                //     network from the first.
                string? siteGroup = null;
                string busName = WordName(buses[i].Name);
                foreach ((string group, string site) in gspSites)
                    if (busName.Length >= site.Length && busName.StartsWith(site, StringComparison.Ordinal) &&
                        (busName.Length == site.Length || !char.IsLetter(busName[site.Length])))
                    { siteGroup = group; break; }
                string group0 = siteGroup ?? buses[i].Gsp;
                string k = group0.Length > 0 ? NormKey(group0) + "|isl" + comp[i] : "_isl" + comp[i];
                if (siteGroup is not null)
                {
                    // Site buses: keep every one at the highest voltage seen so far for this key.
                    if (!siteBuses.TryGetValue(k, out List<int>? list)) siteBuses[k] = list = new List<int>();
                    if (list.Count == 0 || buses[i].Kv > buses[list[0]].Kv + 1e-6) { list.Clear(); list.Add(i); }
                    else if (Math.Abs(buses[i].Kv - buses[list[0]].Kv) < 1e-6) list.Add(i);
                    continue;
                }
                // Other buses: the old single best (higher kV, then more branches), used only if the key has no
                // site bus at all.
                if (!gspBus.TryGetValue(k, out _) || buses[i].Kv > gspKv2[k] ||
                    (Math.Abs(buses[i].Kv - gspKv2[k]) < 1e-6 && degree[i] > gspDeg[k]))
                { gspBus[k] = i; gspKv2[k] = buses[i].Kv; gspDeg[k] = degree[i]; }
                continue;
            }
            string key = buses[i].Gsp.Length > 0 ? NormKey(buses[i].Gsp) : "_isl" + comp[i];
            if (!gspBus.TryGetValue(key, out _) || buses[i].Kv > gspKv2[key] ||
                (Math.Abs(buses[i].Kv - gspKv2[key]) < 1e-6 && degree[i] > gspDeg[key]))
            { gspBus[key] = i; gspKv2[key] = buses[i].Kv; gspDeg[key] = degree[i]; }
        }
        var slackSet = new HashSet<int>();
        if (ds.InfeedPerGspGroupPerIsland)
        {
            // N194: a key with site buses is fed at all of them; a key without, at its single best bus.
            foreach ((string k, List<int> list) in siteBuses) foreach (int s in list) slackSet.Add(s);
            foreach ((string k, int s) in gspBus) if (!siteBuses.ContainsKey(k)) slackSet.Add(s);
        }
        else
        {
            slackSet.UnionWith(gspBus.Values);
        }

        // Safety net: every island must contain at least one slack.
        var islandHasSlack = new bool[Math.Max(1, islands)];
        foreach (int s in slackSet) islandHasSlack[comp[s]] = true;
        var islandBest = new int[Math.Max(1, islands)];
        Array.Fill(islandBest, -1);
        for (int i = 0; i < n; i++)
        {
            int c = comp[i];
            if (islandHasSlack[c]) continue;
            if (starBusIds.Contains(i)) continue;   // prefer a real bus over a transformer star node
            if (islandBest[c] < 0 || buses[i].Kv > buses[islandBest[c]].Kv) islandBest[c] = i;
        }
        foreach (int b in islandBest) if (b >= 0) slackSet.Add(b);

        var finalBuses = new List<Bus>(n);
        for (int i = 0; i < n; i++)
        {
            MutableBus mb = buses[i];
            BusType type = slackSet.Contains(i) ? BusType.Slack : BusType.Pq;
            finalBuses.Add(new Bus(
                Id: mb.Id, Type: type, Pd: mb.Pd, Qd: mb.Qd, BaseKv: mb.Kv,
                Vm: 1.0, VaDeg: 0, Name: mb.Name));
        }

        if (generators.Count == 0)
            notes.Add("No embedded generators matched; GSP infeeds carry all supply.");
        int primarySlack = slackSet.Where(s => comp[s] == largestComp)
            .DefaultIfEmpty(-1).OrderByDescending(s => s < 0 ? -1 : buses[s].Kv).First();
        notes.Add($"{slackSet.Count} GSP infeed(s) modelled as slack buses; primary = bus {primarySlack} " +
                  $"({(primarySlack >= 0 ? buses[primarySlack].Name : "n/a")}, {(primarySlack >= 0 ? buses[primarySlack].Kv : 0):F0} kV).");
        if (oltcsNoTapRange > 0)
            notes.Add($"OLTC: {oltcsNoTapRange} transformer(s) publish a 0 %/0 % tap range (no on-load tap changer); no OLTC created for them.");
        if (oltcsInvalidTapRange > 0)
            notes.Add($"WARNING OLTC: {oltcsInvalidTapRange} transformer(s) publish an invalid tap range and got NO OLTC: " +
                      string.Join("; ", invalidTapRangeExamples) + (oltcsInvalidTapRange > invalidTapRangeExamples.Count ? "; ..." : "") + ".");
        if (degenerateTransformers > 0)
            notes.Add($"WARNING transformers: {degenerateTransformers} transformer(s) publish a reactance below " +
                      $"{GdaBuildDefaults.DegenerateTransformerXPu:R} pu and were built at {GdaBuildDefaults.MinTransformerXPu:R} pu " +
                      $"(check for a missing X column): {string.Join("; ", degenerateTransformerExamples)}" +
                      (degenerateTransformers > degenerateTransformerExamples.Count ? "; ..." : "") + ".");
        if (demandZeroRows > 0 || demandNegativeRows > 0)
            notes.Add($"Demand: left out {demandZeroRows} row(s) with zero maximum demand and {demandNegativeRows} with negative " +
                      $"(net-exporting) maximum demand totalling {demandNegativeMw:F1} MW; the generation behind them is still built.");
        if (zeroImpedanceNonTies > 0)
            notes.Add($"WARNING ties: {zeroImpedanceNonTies} zero-impedance circuit(s) whose Equipment is not a busbar or switch were merged as busbar ties " +
                      $"(check for missing R/X cells): {string.Join("; ", zeroImpedanceNonTieExamples)}" +
                      (zeroImpedanceNonTies > zeroImpedanceNonTieExamples.Count ? "; ..." : "") + ".");
        if (ds.CanonicalPrimaryJoin)
            notes.Add("Generation join key: canonical primary name (voltage tokens such as '33 11 kV' or '6.6' and station words such as " +
                      "'S Stn' dropped), so the generation and demand tables' spellings of one primary meet (N184).");
        if (genJoinedByUniqueName > 0)
            notes.Add($"Generation join: {genJoinedByUniqueName} row(s) joined by primary name alone: their GSP group does not list that primary " +
                      "in the demand table, but the name is unique to one GSP group.");
        if (genAmbiguousPrimary > 0)
            notes.Add(FormattableString.Invariant(
                $"WARNING generation join: {genAmbiguousPrimary} row(s), {genAmbiguousPrimaryMw:F1} MW export capacity, name a primary that exists under ") +
                $"several GSP groups, none of them the generator's own; left unplaced: {string.Join("; ", ambiguousPrimaryExamples)}" +
                (genAmbiguousPrimary > ambiguousPrimaryExamples.Count ? "; ..." : "") + ".");
        if (primaryKeysOnSeveralBuses.Count > 0)
            notes.Add($"Demand: {primaryKeysOnSeveralBuses.Count} primary name(s) have demand rows on more than one bus within one GSP group; " +
                      (ds.CanonicalPrimaryJoin
                          ? "generation at those sites joins the bus of its own voltage (N205)."
                          : "generation at those primaries joins the last-listed bus."));
        if (genJoinedBySiteVoltage > 0)
            notes.Add($"Generation join: {genJoinedBySiteVoltage} row(s) at a site with demand on several buses joined by site and voltage (N205).");
        if (genAmbiguousSiteVoltage > 0)
            notes.Add(FormattableString.Invariant(
                $"WARNING generation join: {genAmbiguousSiteVoltage} row(s), {genAmbiguousSiteVoltageMw:F1} MW export capacity, are at a site whose ") +
                $"demand sits on several buses and their voltage picks none or several of them (N205); left unplaced: " +
                string.Join("; ", ambiguousSiteVoltageExamples) + (genAmbiguousSiteVoltage > ambiguousSiteVoltageExamples.Count ? "; ..." : "") + ".");
        // Invariant culture: a note is a report, and its numbers must read the same on every machine.
        if (genNotConnected > 0)
            notes.Add(FormattableString.Invariant(
                $"Generation: {genNotConnected} matched row(s), {genNotConnectedMw:F1} MW export capacity, are not connected yet ") +
                $"and were left out (statuses: {QuoteList(notConnectedStatuses)}).");
        if (genUnknownStatus > 0)
            notes.Add(FormattableString.Invariant(
                $"WARNING generation: {genUnknownStatus} matched row(s), {genUnknownStatusMw:F1} MW export capacity, have a blank or ") +
                $"unrecognised connection status and were left out (statuses: {QuoteList(unknownStatuses)}).");
        if (aggregatedDemand > 0)
            notes.Add(FormattableString.Invariant(
                $"Demand: {aggregatedDemand} row(s), {aggregatedDemandMw:F1} MW below the {opt.MinKv:F0} kV cut were folded onto their nearest boundary bus ") +
                "(losses and voltage drop below the cut are not modelled).");
        if (outOfScopeDemand > 0)
        {
            // With no carve at all, a demand row off the network is a data defect: say so loudly.
            // Under a GSP/licence carve most such rows simply belong to other areas.
            bool carved = opt.GspGroup is not null || opt.Licence is not null;
            notes.Add(FormattableString.Invariant(
                $"{(carved ? "Demand" : "WARNING demand")}: {outOfScopeDemand} row(s), {outOfScopeDemandMw:F1} MW are on nodes outside this build's network ") +
                (carved ? "(other areas of the carve, or nodes on no in-scope element) and were not included." : "(nodes on no in-service element) and were not included."));
        }
        if (opt.MinKv > 0) notes.Add($"Filtered to >={opt.MinKv:F0} kV.");
        if (opt.GspGroup is not null) notes.Add($"GSP group = {opt.GspGroup}.");
        if (opt.Licence is not null) notes.Add($"Licence = {opt.Licence}.");

        var byKv = finalBuses.GroupBy(b => (int)Math.Round(b.BaseKv))
            .OrderByDescending(g => g.Key).Select(g => new KeyValuePair<int, int>(g.Key, g.Count())).ToList();

        var model = new GridModel
        {
            Name = $"{ds.Name}{(opt.GspGroup is not null ? $" [{opt.GspGroup}]" : "")}{(opt.MinKv > 0 ? $" >={opt.MinKv:F0}kV" : "")}",
            BaseMva = opt.BaseMva,
            FrequencyHz = GdaBuildDefaults.SystemFrequencyHz,
            Buses = finalBuses,
            Generators = generators,
            Branches = branches,
            Oltcs = oltcs,
        };

        // ---- optional geolocation: project matched buses onto GB geography ----
        if (opt.Geolocate && opt.SubstationsPath is { Length: > 0 } subsPath && File.Exists(subsPath)
            && new FileInfo(subsPath).Length > 0)
        {
            // Geolocation is optional cosmetics; a malformed/empty substations file (the real
            // archive ships a zero-byte substations.csv) must degrade to "no coordinates", never
            // fail the whole build.
            try
            {
                (GridModel located, int matched, int total) = Geo.GeoLocator.Apply(model, subsPath);
                model = located;
                notes.Add($"Geolocated {matched}/{total} buses from {Path.GetFileName(subsPath)}.");
            }
            catch (Exception ex)
            {
                notes.Add($"Geolocation skipped ({Path.GetFileName(subsPath)}): {ex.Message}");
            }
        }
        else if (opt.Geolocate && opt.SubstationsPath is { Length: > 0 } emptySubs && File.Exists(emptySubs))
        {
            notes.Add($"Geolocation skipped: {Path.GetFileName(emptySubs)} is empty.");
        }

        // Validate WARN-ONLY: a real data-merge pipeline legitimately merges nodes and
        // spans many islands, so it must never be hard-blocked. Fold every finding into the
        // structural notes and carry on.
        foreach (GridDiagnostic d in model.Validate())
            notes.Add($"validation: {d}");

        var diag = new GdaDiagnostics(
            rawNodes, n, ties, seriesCount, tx2.Count, tx3.Count, starNodes,
            demandConsidered, totalDemand, unmatchedDemand,
            generators.Count, totalGenCap, unmatchedGen,
            islands, largest, byKv, notes)
        {
            OltcsWithoutTapRange = oltcsNoTapRange,
            OltcsWithInvalidTapRange = oltcsInvalidTapRange,
            ZeroImpedanceNonTieCircuits = zeroImpedanceNonTies,
            DegenerateTransformers = degenerateTransformers,
            GenNotConnected = genNotConnected,
            GenNotConnectedMw = genNotConnectedMw,
            GenUnknownStatus = genUnknownStatus,
            GenUnknownStatusMw = genUnknownStatusMw,
            AggregatedDemand = aggregatedDemand,
            AggregatedDemandMw = aggregatedDemandMw,
            OutOfScopeDemand = outOfScopeDemand,
            OutOfScopeDemandMw = outOfScopeDemandMw,
            GenJoinedByUniqueName = genJoinedByUniqueName,
            GenAmbiguousPrimary = genAmbiguousPrimary,
            GenAmbiguousPrimaryMw = genAmbiguousPrimaryMw,
            PrimariesOnSeveralBuses = primaryKeysOnSeveralBuses.Count,
            GenJoinedBySiteVoltage = genJoinedBySiteVoltage,
            GenAmbiguousSiteVoltage = genAmbiguousSiteVoltage,
            GenAmbiguousSiteVoltageMw = genAmbiguousSiteVoltageMw,
        };

        return new GdaBuildResult(model, diag);
    }

    /// <summary>How many offending rows a note names before it says "...".</summary>
    private const int NoteExampleLimit = 5;

    private enum GenConnection { Connected, NotConnected, Unknown }

    /// <summary>
    /// Status words that mean the plant is NOT physically connected today. Checked FIRST,
    /// because several not-connected statuses also contain "connected" ("Accepted not yet
    /// Connected", "Not Connected", "Disconnected") and "commissioned" sits inside
    /// "decommissioned". Includes the demand/branch dead statuses.
    /// </summary>
    private static readonly string[] NotConnectedGenWords =
    {
        "accept", "offer", "not yet", "not connected", "unconnected", "disconnect", "pending",
        "contract", "applied", "application", "enquir", "quot", "consent", "scoping", "construct",
        "withdrawn", "terminat", "out of service", "decommiss", "removed", "future", "planned",
        "abandon", "proposed",
        // GR-330: negated forms of the connected words below ("De-energised" contains "energised", "Not in service"
        // contains "in service"); checked first, so they are not connected.
        "not in service", "de-energ", "deenerg", "not energ", "un-energ", "unenerg", "not commission", "not operational",
    };

    /// <summary>Status words that mean the plant is connected and running today (checked after the not-connected words).</summary>
    private static readonly string[] ConnectedGenWords =
        { "connected", "energised", "energized", "in service", "commissioned", "operational" };

    /// <summary>
    /// Classify an embedded generator's published connection status. Anything that matches
    /// neither list (including a blank cell) is <see cref="GenConnection.Unknown"/>: the
    /// builder does not guess, it leaves the row out and says so loudly.
    /// </summary>
    private static GenConnection ClassifyGenStatus(string status)
    {
        if (NotConnectedGenWords.Any(w => status.Contains(w, StringComparison.OrdinalIgnoreCase))) return GenConnection.NotConnected;
        if (ConnectedGenWords.Any(w => status.Contains(w, StringComparison.OrdinalIgnoreCase))) return GenConnection.Connected;
        return GenConnection.Unknown;
    }

    /// <summary>Render a set of status strings for a note: quoted, comma-separated, a blank shown as '(blank)'.</summary>
    private static string QuoteList(IEnumerable<string> items) =>
        string.Join(", ", items.Select(s => s.Length == 0 ? "(blank)" : $"'{s}'"));

    /// <summary>
    /// Largest tap excursion (percent of nominal ratio) accepted as a published tap range.
    /// Real on-load ranges run to about +/-20 %; anything beyond 50 % cannot be a percent
    /// range at all (tap POSITIONS such as 1..17 land here, or fail the 0 % check).
    /// </summary>
    private const double MaxPlausibleTapPercent = 50.0;

    private enum TapRangeVerdict { Valid, NoTapChanger, Invalid }

    /// <summary>
    /// Judge a published transformer tap range (percent of nominal ratio). A valid range is
    /// finite, ordered (min &lt;= max), contains the nominal 0 % tap (the build starts every
    /// OLTC at ratio 1.0, so 1.0 must lie inside its limits) and stays within
    /// +/-<see cref="MaxPlausibleTapPercent"/>. 0 %/0 % is an off-load or fixed-ratio unit.
    /// </summary>
    private static TapRangeVerdict ClassifyTapRange(double minPct, double maxPct, out string reason)
    {
        reason = "";
        if (!double.IsFinite(minPct) || !double.IsFinite(maxPct)) { reason = "not a finite number"; return TapRangeVerdict.Invalid; }
        if (minPct == 0 && maxPct == 0) return TapRangeVerdict.NoTapChanger;
        if (minPct > maxPct) { reason = "minimum above maximum"; return TapRangeVerdict.Invalid; }
        if (minPct > 0 || maxPct < 0) { reason = "range excludes the nominal 0 % tap"; return TapRangeVerdict.Invalid; }
        if (Math.Abs(minPct) > MaxPlausibleTapPercent || Math.Abs(maxPct) > MaxPlausibleTapPercent)
        {
            reason = $"beyond +/-{MaxPlausibleTapPercent:F0} %, not a percent range";
            return TapRangeVerdict.Invalid;
        }
        return TapRangeVerdict.Valid;
    }

    /// <summary>Equipment words that name a genuine zero-impedance tie (busbar, switchgear) rather than a cable or line.</summary>
    private static readonly string[] TieEquipmentWords =
        { "busbar", "bus bar", "bus-bar", "bus section", "coupler", "disconnector", "isolator", "switch", "breaker", "jumper", "link" };

    /// <summary>
    /// The tie words, each matched only at the START of a word (GR-834): "Switchgear", "Link box" and "Busbars" are
    /// ties, but "Interlink cable" (where "link" sits inside a word) is a cable. A plain substring match used to count
    /// such cables as ties and so hid them from the zero-impedance warning. Built from <see cref="TieEquipmentWords"/>.
    /// </summary>
    private static readonly Regex TieEquipmentWordStart = new(
        "(?<![A-Za-z0-9])(?:" + string.Join("|", TieEquipmentWords.Select(Regex.Escape)) + ")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True if an LTDS Equipment string describes a zero-impedance tie (busbar or switchgear).</summary>
    private static bool IsTieEquipment(string equipment) => TieEquipmentWordStart.IsMatch(equipment);

    private static double ZBase(double kv, double baseMva) => kv > 0 && baseMva > 0 ? kv * kv / baseMva : 0;

    /// <summary>Base impedance for an element, referred to its GSP-group voltage (NGED LTDS convention).</summary>
    private static double RefZBase(string gspGroup, GdaBuildOptions opt)
    {
        double kv = opt.RefKv > 0 ? opt.RefKv : ParseGspKv(gspGroup);
        return ZBase(kv, opt.BaseMva);
    }

    private static readonly int[] SystemKvs = { 400, 275, 132, 66, 33, 25, 11 };

    /// <summary>Extract the GSP reference voltage from a group name like "Coventry 132kV" (->132). Defaults to 132.</summary>
    public static double ParseGspKv(string gspGroup)
    {
        if (!string.IsNullOrEmpty(gspGroup))
            foreach (int kv in SystemKvs) // prefer the highest plausible system voltage present
                foreach (Match m in Regex.Matches(gspGroup, "\\d+"))
                    if (int.TryParse(m.Value, out int v) && v == kv)
                        return kv;
        return GdaBuildDefaults.DefaultGspKv;
    }

    private static string Short(string node)
    {
        int us = node.IndexOf('_');
        return us > 0 ? node[..us] : node;
    }

    /// <summary>Whitespace/case-insensitive key for joining strings across tables (spacing varies, e.g. "Exeter 132 kV" vs "Exeter 132kV").</summary>
    /// <summary>A trailing voltage word on a GSP group name ("Hurst 132kV", "Willesden Grid 66 kV").</summary>
    private static readonly Regex GspVoltageSuffix = new(@"\s*\d+(\.\d+)?\s*kV\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// N194: a GSP group's site name for <see cref="WordName"/> matching: the group name without a trailing voltage
    /// word ("Hurst 132kV" gives "hurst", "Willesden Grid 66 kV" gives "willesden grid"), in WordName form.
    /// </summary>
    private static string SiteName(string gspGroup) => WordName(GspVoltageSuffix.Replace(gspGroup, ""));

    /// <summary>N194: a name lower-cased with every run of whitespace turned into one space and the ends trimmed.</summary>
    private static string WordName(string name) =>
        string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string NormKey(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        int n = 0;
        foreach (char c in s) if (!char.IsWhiteSpace(c)) buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    // N184: the pieces of the canonical primary key (see CanonicalPrimaryKey).
    private static readonly Regex PrimaryDigitDotDigit =
        new(@"(?<=\d) \. (?=\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PrimaryVoltageToken =
        new(@"\b\d+(\.\d+)?(\s*/\s*\d+(\.\d+)?)*\s*(kv)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // N205 (October 2026): "s \. stn" added first, because step 1 below splits "S.Stn" into "s . stn" and the old list then
    // removed only "stn", leaving a stray "s" ("St. Austell 33/11kV S.Stn" gave "staustells", which no demand spelling
    // "St Austell 11" ("staustell") could meet). Every other name keys as before.
    private static readonly Regex PrimaryStationWord =
        new(@"\b(s \. stn|s stn|stn|substation|primary|prim|p s|ps|s s|ss|grid|bsp|main|t)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // N205: one number inside a voltage token ("33", "6.6", "66/11" gives two).
    private static readonly Regex PrimaryVoltageNumber =
        new(@"\d+(\.\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// N205 (October 2026, Ruling R1): the voltage a primary substation NAME carries, in kV, as the canonical key's
    /// companion: the lowest number among the voltage tokens <see cref="CanonicalPrimaryKey"/> removes, which is the
    /// primary's lower (secondary) voltage, the one the NGED demand table names a primary by. "Wigston Magna 33 11 kV
    /// S Stn" and "Wigston Magna 11" give 11; "Courthouse Green 6.6" gives 6.6; "St. Austell 33/11kV S.Stn" gives 11; a
    /// name with no voltage token ("Harbury S Stn", "Dillotford Avenue (T2)") gives null. Numbers parse with the invariant
    /// culture, so the same text gives the same double everywhere and two spellings of one voltage compare exactly.
    /// </summary>
    public static double? CanonicalPrimaryVoltageKv(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        // The same first two steps as CanonicalPrimaryKey, so a voltage token here is exactly one it removes there.
        string t = name.ToLowerInvariant().Replace(".", " . ", StringComparison.Ordinal);
        t = PrimaryDigitDotDigit.Replace(t, ".");
        var kept = new char[t.Length];
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            kept[i] = char.IsLetterOrDigit(c) || c == '.' || c == '/' ? c : ' ';
        }
        double? lowest = null;
        foreach (Match token in PrimaryVoltageToken.Matches(new string(kept)))
            foreach (Match number in PrimaryVoltageNumber.Matches(token.Value))
            {
                double kv = double.Parse(number.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (lowest is null || kv < lowest) lowest = kv;
            }
        return lowest;
    }

    /// <summary>
    /// N184 (Ruling R1, Mark delegated 2026-10-04): the canonical key of a primary substation name, so the two NGED
    /// vocabularies meet. The generation table writes "Wigston Magna 33 11 kV S Stn" or "Harbury 11 kV S Stn"; the demand
    /// table writes "Courthouse Green 11", "Cox Street 6.6" or "Pyle 33kV". Steps, in order:
    /// <list type="number">
    /// <item>lower-case (invariant); every '.' becomes " . ", then a '.' between two digits is joined back ("6.6" stays
    /// one token, "S.Stn" comes apart);</item>
    /// <item>every character that is not a letter, a digit, '.' or '/' becomes a space;</item>
    /// <item>voltage tokens are removed: a number, optionally with a decimal part and "/"-separated further numbers,
    /// optionally followed by "kv" ("33 11 kv" goes as two tokens, "66/11kv", "11", "6.6");</item>
    /// <item>station words are removed: "s stn", "stn", "substation", "primary", "prim", "p s", "ps", "s s", "ss",
    /// "grid", "bsp", "main" and a lone "t";</item>
    /// <item>every character that is not a letter or digit is dropped.</item>
    /// </list>
    /// So "Wigston Magna 33 11 kV S Stn" and "Wigston Magna 11" both give "wigstonmagna". The key names a SITE and so
    /// folds a site's primaries of different voltages together ("Courthouse Green 11" and "Courthouse Green 6.6" give
    /// one key); since N205 (October 2026) the join tells them apart by the voltage each name carries
    /// (<see cref="CanonicalPrimaryVoltageKv"/>): a generator at a site whose demand sits on several buses joins the bus
    /// of its own voltage, or is left unplaced with a warning, never the last-listed bus. The "primary name on more than
    /// one bus" note still counts such sites. A name the key empties is not a join key. Measured on the full NGED lake (October 2026, 897 generation
    /// rows, 375 with a '-' primary): the whitespace key placed 3 rows (8.2 MW export capacity), this key 308 rows
    /// (1,239.5 MW), 163 of them by unique name, none ambiguous.
    /// </summary>
    public static string CanonicalPrimaryKey(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string t = name.ToLowerInvariant().Replace(".", " . ", StringComparison.Ordinal);
        t = PrimaryDigitDotDigit.Replace(t, ".");
        var kept = new char[t.Length];
        for (int i = 0; i < t.Length; i++)
        {
            char c = t[i];
            kept[i] = char.IsLetterOrDigit(c) || c == '.' || c == '/' ? c : ' ';
        }
        t = PrimaryVoltageToken.Replace(new string(kept), " ");
        t = PrimaryStationWord.Replace(t, " ");
        var key = new System.Text.StringBuilder(t.Length);
        foreach (char c in t) if (char.IsLetterOrDigit(c)) key.Append(c);
        return key.ToString();
    }

    /// <summary>
    /// True if a substation string is a real key rather than an NGED null marker. The LTDS uses
    /// '-' (and blanks) for "no primary substation": 375 generation and 605 demand rows carry it.
    /// Left unguarded, '-' becomes a join key and collapses ~13.7 GW of generation onto whichever
    /// bus the last dash-demand row happened to map. A real key has at least one letter or digit.
    /// </summary>
    private static bool IsJoinKey(string s)
    {
        foreach (char c in s) if (char.IsLetterOrDigit(c)) return true;
        return false;
    }

    private static readonly Regex GspVoltageToken =
        new(@"\b\d{2,3}( \d{2})?\s*kv\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GspStationWord =
        new(@"\b(s g p|g s p|gsp|sgp|s stn|stn|grid supply point|power station|grid|substation|bsp|main)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Canonical key for matching a GSP group across the two NGED vocabularies. The circuit/demand
    /// tables name a group like "Berkswell 132kV"; the generation table names the same group
    /// "Berkswell 132 kV S Stn" / "Abham S.G.P." / "Aberthaw Power Station". Plain whitespace/case
    /// folding overlaps on only ~4 of 51 groups, so a GSP-carved build drops ~92% of embedded
    /// generation. Strip the station-type suffixes and voltage tokens so the same site resolves to
    /// one key on both sides.
    /// </summary>
    /// <summary>The canonical key a <see cref="GdaBuildOptions.GspGroup"/> carve compares (see <see cref="GspKey"/>):
    /// two names with the same key select the same elements.</summary>
    public static string GspGroupKey(string name) => GspKey(name);

    /// <summary>Whole-word letters of a group name (a run of letters, matched case-insensitively).</summary>
    private static readonly Regex GroupWord = new(@"\p{L}+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// N211 (October 2026, Ruling R1): <paramref name="group"/> with every whole word that is an alias in
    /// <paramref name="aliases"/> (keys upper case) replaced by its canonical spelling, in the case style of the word it
    /// replaces (all upper, all lower, or capitalised). Every other character is kept exactly, so a name with no alias
    /// word comes back equal to itself, character for character.
    /// </summary>
    public static string ApplyGroupWordAliases(string group, IReadOnlyDictionary<string, string> aliases)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(aliases);
        if (aliases.Count == 0) return group;
        return GroupWord.Replace(group, m =>
        {
            if (!aliases.TryGetValue(m.Value.ToUpperInvariant(), out string? canonical)) return m.Value;
            // The word is letters only (\p{L}+), so "every letter upper" / "every letter lower" is its case style.
            if (m.Value.All(char.IsUpper)) return canonical.ToUpperInvariant();
            if (m.Value.All(char.IsLower)) return canonical.ToLowerInvariant();
            return canonical[..1].ToUpperInvariant() + canonical[1..].ToLowerInvariant();
        });
    }

    /// <summary>Every distinct GSP group name (as spelt in the data) on the dataset's circuits and transformers, in
    /// ordinal order: the names a <c>--gsp</c> carve can select.</summary>
    public static IReadOnlyList<string> GspGroups(LtdsDataset ds)
    {
        ArgumentNullException.ThrowIfNull(ds);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (LtdsCircuit c in ds.Circuits) if (c.GspGroup.Length > 0) names.Add(c.GspGroup);
        foreach (LtdsTransformer2 t in ds.Transformers2) if (t.GspGroup.Length > 0) names.Add(t.GspGroup);
        foreach (LtdsTransformer3 t in ds.Transformers3) if (t.GspGroup.Length > 0) names.Add(t.GspGroup);
        return names.ToList();
    }

    /// <summary>True when <paramref name="name"/> selects at least one element of the dataset (its key equals the key
    /// of some group name on a circuit or transformer). An empty key (a name of only punctuation, voltage and station
    /// words) never matches.</summary>
    public static bool IsKnownGspGroup(LtdsDataset ds, string name)
    {
        ArgumentNullException.ThrowIfNull(ds);
        string key = GspKey(ApplyGroupWordAliases(name, ds.GroupWordAliases));   // N211: an alias spelling selects too
        return key.Length > 0 && GspGroups(ds).Any(g => GspKey(g) == key);
    }

    /// <summary>
    /// Up to <paramref name="count"/> group names closest to a mistyped <paramref name="name"/>, for the "did you
    /// mean" line of a refused <c>--gsp</c>. Closeness is the Levenshtein edit distance between canonical keys; ties
    /// break by name (ordinal), so the suggestion list is deterministic. Each key is offered once, under its first
    /// spelling.
    /// </summary>
    public static IReadOnlyList<string> SuggestGspGroups(LtdsDataset ds, string name, int count = 3)
    {
        string key = GspKey(name);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scored = new List<(int Distance, string Name)>();
        foreach (string g in GspGroups(ds))
        {
            string gk = GspKey(g);
            if (!seen.Add(gk)) continue;
            scored.Add((EditDistance(key, gk), g));
        }
        return scored.OrderBy(s => s.Distance).ThenBy(s => s.Name, StringComparer.Ordinal)
                     .Take(Math.Max(0, count)).Select(s => s.Name).ToList();
    }

    // Classic two-row Levenshtein distance (insert, delete, substitute all cost 1).
    private static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private static string GspKey(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        Span<char> buf = stackalloc char[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char c = char.ToLowerInvariant(s[i]);
            buf[i] = char.IsLetterOrDigit(c) ? c : ' ';   // punctuation -> space (folds "s.g.p." -> "s g p")
        }
        string t = new string(buf);
        t = GspVoltageToken.Replace(t, " ");
        t = GspStationWord.Replace(t, " ");
        Span<char> outb = stackalloc char[t.Length];
        int n = 0;
        foreach (char c in t) if (!char.IsWhiteSpace(c)) outb[n++] = c;
        return new string(outb[..n]);
    }

    /// <summary>Label each bus with its connected-component (island) id via BFS over the branch graph.</summary>
    private static int[] ComponentIds(int n, List<Branch> branches)
    {
        var comp = new int[n];
        if (n == 0) return comp;
        Array.Fill(comp, -1);
        var adj = new List<int>[n];
        for (int i = 0; i < n; i++) adj[i] = new List<int>();
        foreach (Branch b in branches) { adj[b.From].Add(b.To); adj[b.To].Add(b.From); }
        int c = 0;
        var stack = new Stack<int>();
        for (int s = 0; s < n; s++)
        {
            if (comp[s] != -1) continue;
            comp[s] = c; stack.Push(s);
            while (stack.Count > 0)
            {
                int u = stack.Pop();
                foreach (int v in adj[u]) if (comp[v] == -1) { comp[v] = c; stack.Push(v); }
            }
            c++;
        }
        return comp;
    }

    private sealed class MutableBus
    {
        public int Id;
        public double Kv;
        public double Pd;
        public double Qd;
        public string Name = "";
        public string Gsp = "";   // GSP group this bus belongs to (for per-GSP infeed/slack assignment)
    }
}

/// <summary>Tiny disjoint-set (union-find) over string node codes, with path compression.</summary>
internal sealed class Dsu
{
    private readonly Dictionary<string, string> _parent = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string x) => _parent.TryAdd(x, x);
    public bool Contains(string x) => _parent.ContainsKey(x);

    public string Find(string x)
    {
        if (!_parent.TryGetValue(x, out string? p)) { _parent[x] = x; return x; }
        while (!ReferenceEquals(p, x) && p != x)
        {
            string gp = _parent[p];
            _parent[x] = gp;
            x = p; p = gp;
        }
        return x;
    }

    public bool Union(string a, string b)
    {
        string ra = Find(a), rb = Find(b);
        if (ra == rb) return false;
        _parent[ra] = rb;
        return true;
    }
}
