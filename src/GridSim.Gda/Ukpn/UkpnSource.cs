using System.Globalization;
using GridSim.Gda.Csv;
using GridSim.Gda.Ltds;

namespace GridSim.Gda.Ukpn;

/// <summary>
/// Reads UK Power Networks' LTDS (EPN / LPN / SPN  -  East, London, South East) into
/// the shared <see cref="LtdsDataset"/>. UKPN's schema differs from NGED's: impedances
/// are <b>percent</b> (circuits on 100 MVA, transformers on rating), ratings are in
/// amps, there's no node dictionary (nodes are the circuit endpoints), and demand /
/// generation are keyed by substation  -  so we synthesise nodes and resolve each
/// substation to its lowest-voltage node. The convention tag lets the common builder
/// do the right per-unit conversion.
/// <para>
/// N185 (October 2026): where to find each table. The reader always looks first at the published CSV under
/// <c>UkPowerNetworks/RAWData</c>; a CSV that has at least one data row is read exactly as before (same code, same
/// row type, so every build that worked before is bit-identical). Only when the CSV is header-only (or missing) does
/// the reader look for the GDA lake's Parquet conversion of the same table under <c>UkPowerNetworks/Parquet</c>
/// (same file name with <c>.parquet</c>, the same 'ukpn-' prefix rule). A Parquet copy with rows is then read through
/// <see cref="CsvEquivalentRow"/>, which gives every cell the meaning the CSV reader would give the same text (see
/// there for what "the same" can and cannot promise). When neither has rows, the reader falls back to the header-only
/// CSV, so the refusal is the old one (no demand), now naming both places that were searched.
/// </para>
/// </summary>
public static class UkpnSource
{
    // The per-vintage observed maximum demand column, e.g. Maximum_Demand_24_25_MW (R300).
    private static readonly System.Text.RegularExpressions.Regex DemandColumn = new(
        @"^Maximum_Demand_(\d{2})_(\d{2})_MW$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Load UKPN from a GDA lake root. Same as <see cref="Load(string, Action{string}?)"/> with no notes.</summary>
    public static LtdsDataset Load(string root) => Load(root, null);

    /// <summary>
    /// Load UKPN from a GDA lake root. <paramref name="note"/>, when given, receives one plain-English line for every
    /// table that was read from its Parquet copy instead of its CSV (and one line on numbers whose CSV text cannot be
    /// known exactly; see <see cref="CsvEquivalentRow"/>). When every table is read from CSV nothing is reported, so
    /// the output of a CSV build is unchanged.
    /// </summary>
    public static LtdsDataset Load(string root, Action<string>? note)
    {
        string raw = Path.Combine(root, "UkPowerNetworks", "RAWData");
        string parquetDir = Path.Combine(root, "UkPowerNetworks", "Parquet");
        // N185: a lake that has only the Parquet conversion (no RAWData folder at all) is readable too. With neither
        // folder the old refusal stands, word for word.
        if (!Directory.Exists(raw) && !Directory.Exists(parquetDir))
            throw new DirectoryNotFoundException($"UKPN data not found: {raw}");

        // Counts the double cells read from Parquet whose shortest exact text has more than 15 significant digits
        // (see CsvEquivalentRow.Double). Shared by all four tables so one note covers the whole load.
        var longDoubles = new LongDoubleTally();

        TableSource circuitSrc = Resolve(raw, parquetDir, "ltds-table-1-circuit-data.csv", note)!;
        TableSource txSrc = Resolve(raw, parquetDir, "ltds-table-2a-transformer-2w.csv", note)!;
        TableSource demandSrc = Resolve(raw, parquetDir, "ltds-table-3a-load-data-observed.csv", note)!;
        TableSource? genSrc = Resolve(raw, parquetDir, "ltds-table-5-generation.csv", note, required: false);
        // N194: the three-winding table is optional (an older lake or a test fixture may not have it); when present,
        // its units are built like NGED's three-winding units.
        TableSource? tx3Src = Resolve(raw, parquetDir, "ltds-table-2b-transformer-data-3w.csv", note, required: false);

        // N194: node codes are not unique across UKPN's three licence areas (see SplitNodeCodes). Work out, before
        // anything is built, which codes name different substations in different licence areas; those are keyed
        // "<code>@<licence>" from here on, every other code exactly as published.
        HashSet<string> splitCodes = SplitNodeCodes(circuitSrc, txSrc, tx3Src, longDoubles, out int sharedBoundaryCodes);
        string Key(string code, string licence) =>
            code.Length > 0 && splitCodes.Contains(code) ? $"{code}@{LicenceTag(licence)}" : code;
        // N194 notes go into the dataset (the build report prints them with the builder's own notes), not to the
        // `note` callback, whose lines are only about where each table was read from (N185).
        var buildNotes = new List<string>();
        if (splitCodes.Count > 0)
            buildNotes.Add($"UKPN: {splitCodes.Count} node code(s) name different substations in different licence areas (for " +
                 $"example {string.Join(", ", splitCodes.Order(StringComparer.Ordinal).Take(NoteExampleLimit))}); each is " +
                 $"kept apart per licence area as <code>@<area>. {sharedBoundaryCodes} code(s) used in more than one area " +
                 "name the same site in each (boundary substations) and stay one node.");

        var nodeInfo = new Dictionary<string, (string sub, string gsp, double kv)>(StringComparer.OrdinalIgnoreCase);
        var subToNode = new Dictionary<string, (string node, double kv)>(StringComparer.OrdinalIgnoreCase);
        void See(string node, string sub, string gsp, double kv)
        {
            if (node.Length == 0) return;
            if (!nodeInfo.ContainsKey(node)) nodeInfo[node] = (sub, gsp, kv);
            if (sub.Length > 0 && (!subToNode.TryGetValue(sub, out var e) || kv < e.kv)) subToNode[sub] = (node, kv); // lowest-V node = where load connects
        }

        // N194: for every substation, the LV-winding nodes of the transformers that feed it, each with the winter MVA
        // rating of the windings landing there. UKPN's LTDS has no busbar-coupler records, so a substation whose board
        // is split into sections (one per transformer or per pair) appears as several unconnected nodes. The demand
        // row is shared over these nodes below instead of being put wholly on one of them.
        var feedersBySub = new Dictionary<string, SortedDictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        // N194: for every transformer HV node, the LV substations it feeds (used to find aggregate demand rows).
        var lvSubsByHvNode = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void AddFeeder(string sub, string node, double ratingMva, string hvNode)
        {
            if (sub.Length == 0 || node.Length == 0) return;
            if (hvNode.Length > 0)
            {
                if (!lvSubsByHvNode.TryGetValue(hvNode, out HashSet<string>? fed))
                    lvSubsByHvNode[hvNode] = fed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                fed.Add(sub);
            }
            if (!feedersBySub.TryGetValue(sub, out SortedDictionary<string, double>? f))
                feedersBySub[sub] = f = new SortedDictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            // A rating that is blank, zero, negative or not a number adds nothing (weight 0); see ShareDemand for
            // what happens when every feeder of a substation has no rating.
            f[node] = f.GetValueOrDefault(node) + (double.IsFinite(ratingMva) && ratingMva > 0 ? ratingMva : 0.0);
        }

        // N067 (October 2026): data rows read per table, counted only to explain a refusal (below). Nothing is decided
        // on them, so every build that succeeded before is unchanged.
        int circuitRows = 0, txRows = 0, demandRows = 0;

        // ---- circuits ----
        var circuits = new List<LtdsCircuit>();
        using (UkpnTable t = UkpnTable.Open(circuitSrc, longDoubles))
            foreach (IRow r in t.Rows())
            {
                circuitRows++;
                string lic = r["LicenceArea"];
                string n1 = Key(r["FromNode"], lic), n2 = Key(r["ToNode"], lic);
                if (n1.Length == 0 || n2.Length == 0) continue;
                double kv = r.Double("OperatingVoltage (kV)");
                string gsp = r["GridSupplyPoint"];
                See(n1, r["FromSubstation"], gsp, kv);
                See(n2, r["ToSubstation"], gsp, kv);
                double winterA = r.Double("Rating_Amps_Winter");
                double summerA = r.Double("Rating_Amps_Summer");
                circuits.Add(new LtdsCircuit(
                    r["LicenceArea"], gsp, "", n1, n2, "Energised", kv, r["LineName"],
                    r.Double("Positive_Sequence_Impedance_R_Percent"),
                    r.Double("Positive_Sequence_Impedance_X_Percent"),
                    B(r), 0, 0,
                    AmpsToMva(winterA, kv), AmpsToMva(summerA, kv),
                    r.Double("Circuit_Length (km)"), "Circuit"));
            }

        // ---- two-winding transformers ----
        var tx2 = new List<LtdsTransformer2>();
        using (UkpnTable t = UkpnTable.Open(txSrc, longDoubles))
            foreach (IRow r in t.Rows())
            {
                txRows++;
                string lic = r["LicenceArea"];
                string hv = Key(r["HV_Node"], lic), lv = Key(r["LV_Node"], lic);
                if (hv.Length == 0 || lv.Length == 0) continue;
                double kvH = r.Double("Voltage_HV (kV)"), kvL = r.Double("Voltage_LV (kV)");
                string gsp = r["GridSupplyPoint"];
                See(hv, r["HV_Substation"], gsp, kvH);
                See(lv, r["LV_Substation"], gsp, kvL);
                tx2.Add(new LtdsTransformer2(
                    r["LicenceArea"], gsp, hv, kvH, lv, kvL, "Energised", r["Vector_Group"],
                    r.Double("Positive_Sequence_Impedance_R_Percent"),
                    r.Double("Positive_Sequence_Impedance_X_Percent"),
                    r.Double("Zero_Sequence_Impedance_X_Percent"),
                    TapOffsetPercent(r, "Tap_Range_Min_Percent"), TapOffsetPercent(r, "Tap_Range_Max_Percent"),
                    r.Double("Transformer_Rating_MVA_Winter"), r.Double("Transformer_Rating_MVA_Summer")));
                AddFeeder(r["LV_Substation"], lv, r.Double("Transformer_Rating_MVA_Winter"), hv);
            }

        // ---- three-winding transformers (N194) ----
        // UKPN publishes its three-winding units (one HV winding, two LV windings) in LTDS Table 2b, which this reader
        // used to ignore: on the full lake that left 143 transformers out of the replica (Carnaby St's three 66/11/22 kV
        // units, for example), so their substations were fed through whatever 2W unit or cable happened to remain and
        // the power flow collapsed. Read after Table 2a, so every node first seen in the circuits or 2W table keeps
        // exactly the substation, GSP and voltage it had before.
        var tx3 = new List<LtdsTransformer3>();
        if (tx3Src is not null)
            using (UkpnTable t = UkpnTable.Open(tx3Src, longDoubles))
                foreach (IRow r in t.Rows())
                {
                    string lic = r["LicenceArea"];
                    string hv = Key(r["HV_Node"], lic), lv1 = Key(r["LV_Node_1"], lic), lv2 = Key(r["LV_Node_2"], lic);
                    if (hv.Length == 0 || lv1.Length == 0) continue;   // same rule as Table 2a: both main ends needed
                    double kvH = r.Double("Voltage_HV (kV)"), kv1 = r.Double("Voltage_LV_1 (kV)"), kv2 = r.Double("Voltage_LV_2 (kV)");
                    string gsp = r["GridSupplyPoint"];
                    See(hv, r["HV_Substation"], gsp, kvH);
                    See(lv1, r["LV_Substation_1"], gsp, kv1);
                    See(lv2, r["LV_Substation_2"], gsp, kv2);   // an empty LV2 node is ignored by See
                    // Mapping onto the shared record (the builder turns it into a star equivalent):
                    //   Node1 = HV, Node2 = LV1, Node3 = LV2 ("tertiary" in NGED's wording);
                    //   X HV-LV = X_HV_LV1, X LV-Tert = X_LV1_LV2, X HV-Tert = X_HV_LV2; R = R_HV_LV1 (the builder
                    //   puts the one R on the HV leg, as it does for NGED);
                    //   tap range = the HV winding's (the LV windings publish 100 / 100, fixed); ratings = the HV
                    //   winding's winter (nominal) and summer figures, the same pairing Table 2a uses.
                    // Impedances are percent on 100 MVA, like Table 2a (see the dataset flag below).
                    tx3.Add(new LtdsTransformer3(
                        r["LicenceArea"], gsp, hv, kvH, lv1, kv1, lv2, kv2, "Energised",
                        $"{r["Vector_Group_HV"]}{r["Vector_Group_LV1"]}{r["Vector_Group_LV2"]}",
                        r.Double("Positive_Sequence_Impedance_R_HV_LV1_Percent"),
                        r.Double("Positive_Sequence_Impedance_X_HV_LV1_Percent"),
                        r.Double("Positive_Sequence_Impedance_X_LV1_LV2_Percent"),
                        r.Double("Positive_Sequence_Impedance_X_HV_LV2_Percent"),
                        TapOffsetPercent(r, "Tap_Range_Min_Percent_HV"), TapOffsetPercent(r, "Tap_Range_Max_Percent_HV"),
                        r.Double("Transformer_Rating_MVA_Winter_HV"), r.Double("Transformer_Rating_MVA_Summer_HV")));
                    AddFeeder(r["LV_Substation_1"], lv1, r.Double("Transformer_Rating_MVA_Winter_LV1"), hv);
                    AddFeeder(r["LV_Substation_2"], lv2, r.Double("Transformer_Rating_MVA_Winter_LV2"), hv);
                }

        // ---- demand (one row per substation: the worst season) ----
        var demandBySub = new Dictionary<string, LtdsDemand>(StringComparer.OrdinalIgnoreCase);
        string demandFile = demandSrc.Path;
        using (UkpnTable t = UkpnTable.Open(demandSrc, longDoubles))
        {
            // R300: find the newest Maximum_Demand_YY_YY_MW column instead of hard-coding 24_25, which would
            // read 0 for every row of a renamed 2025/26 table and silently build a network with no demand.
            (string Header, int StartYear) demandCol = VintageColumns.Newest(t.Headers, DemandColumn)
                ?? throw new InvalidDataException(
                    $"UKPN {Path.GetFileName(demandFile)}: no Maximum_Demand_YY_YY_MW column (headers: {string.Join(", ", t.Headers)}).");
            string pfCol = demandCol.Header[..^"_MW".Length] + "_PF";

            // Rows whose substation names no node in the circuit and transformer tables cannot be placed. They used to
            // be skipped with no count, so a spelling difference between tables (for example "St Johns Wood" against
            // "St John's Wood") lost that load silently while the build reported full demand. They are now counted
            // and summed (the worst season per substation, the same rule the placed rows follow) and reported in the
            // source notes, as NGED and NPg report their off-network rows.
            var unplacedBySub = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            int blankSubRows = 0;
            double blankSubMw = 0.0;
            foreach (IRow r in t.Rows())
            {
                demandRows++;
                string sub = r["Substation"];
                if (sub.Length == 0)
                {
                    double bmw = r.Double(demandCol.Header);
                    if (bmw > 0) { blankSubRows++; blankSubMw += bmw; }
                    continue;
                }
                if (!subToNode.TryGetValue(sub, out var nd))
                {
                    double umw = r.Double(demandCol.Header);
                    if (umw > 0 && (!unplacedBySub.TryGetValue(sub, out double prev) || umw > prev)) unplacedBySub[sub] = umw;
                    continue;
                }
                double mw = r.Double(demandCol.Header);
                if (mw <= 0) continue;
                if (demandBySub.TryGetValue(sub, out LtdsDemand? cur) && cur.MaxDemandMw >= mw) continue;
                demandBySub[sub] = new LtdsDemand(
                    r["GridSupplyPoint"], "", sub, nd.node, nd.kv, mw,
                    r.Double(pfCol, 1.0), r.Double("Firm_Capacity_MW"));
            }

            if (unplacedBySub.Count > 0)
            {
                double unplacedMw = 0.0;
                foreach (string s in unplacedBySub.Keys.Order(StringComparer.OrdinalIgnoreCase)) unplacedMw += unplacedBySub[s];
                buildNotes.Add(FormattableString.Invariant(
                    $"WARNING UKPN: {unplacedBySub.Count} demand substation(s), {unplacedMw:F1} MW at their worst season, name no ") +
                    "node in the circuit or transformer tables, so their load is NOT in the replica (largest: " +
                    string.Join(", ", unplacedBySub.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Take(NoteExampleLimit).Select(kv => FormattableString.Invariant($"'{kv.Key}' {kv.Value:F1} MW"))) +
                    "). A spelling difference between the tables is the usual cause.");
            }
            if (blankSubRows > 0)
                buildNotes.Add(FormattableString.Invariant(
                    $"WARNING UKPN: {blankSubRows} demand row(s), {blankSubMw:F1} MW, have no substation name and are NOT in the replica."));

            // A table that yields no demand at all is broken data, not a zero-load network.
            // N067: the message now also says how many data rows each table had, and when a table had none, that
            // the GDA lake's conversion may have left only its header in RAWData. On the full lake in October 2026
            // all three tables were header-only stubs, so the old text ("no positive demand on any known substation")
            // pointed at the demand column instead of the missing rows.
            // N185: the reader now reads a header-only table's Parquet copy, so a table can reach this point empty
            // only when no Parquet copy with rows was found either; the message says where it looked.
            if (demandBySub.Count == 0)
            {
                string why = "";
                if (circuitRows == 0 || txRows == 0 || demandRows == 0)
                {
                    var empty = new List<string>();
                    if (circuitRows == 0) empty.Add(Path.GetFileName(circuitSrc.Path));
                    if (txRows == 0) empty.Add(Path.GetFileName(txSrc.Path));
                    if (demandRows == 0) empty.Add(Path.GetFileName(demandFile));
                    why = $" {string.Join(", ", empty)} had no data rows (header only)" +
                          (Directory.Exists(parquetDir)
                              ? $", and no Parquet copy with rows was found for {(empty.Count == 1 ? "it" : "them")} under {parquetDir} " +
                                "(the UKPN reader looks there only for the same table name ending .parquet, with or without the " +
                                "'ukpn-' prefix; any other file, which the UKPN reader does not read, does not count). Put the full " +
                                "CSVs back under RAWData or the Parquet copies under Parquet."
                              : ".");
                }
                throw new InvalidDataException(
                    $"UKPN {Path.GetFileName(demandFile)}: column {demandCol.Header} gave no positive demand on any " +
                    $"known substation, so the replica would have no load (data rows read: circuits {circuitRows}, " +
                    $"transformers {txRows}, demand {demandRows}).{why}");
            }
        }

        // ---- generation ----
        var gen = new List<LtdsGeneration>();
        if (genSrc is not null)
            using (UkpnTable t = UkpnTable.Open(genSrc, longDoubles))
                foreach (IRow r in t.Rows())
                {
                    string sub = r["Substation"];
                    if (sub.Length == 0) continue;
                    double cap = r.Double("InstalledCapacity_MVA");
                    if (cap <= 0) continue;
                    gen.Add(new LtdsGeneration(
                        r["GridSupplyPoint"], "", sub, r.Double("Connection_Voltage_kV"),
                        cap, r["FuelType"], r["Connected_Accepted"]));
                }

        // N185: say when numbers came from Parquet with more digits than a double's guaranteed 15 (see
        // CsvEquivalentRow.Double for why those, and only those, cannot be promised equal to a CSV read).
        if (note is not null && longDoubles.Count > 0)
            note($"UKPN: {longDoubles.Count} number(s) read from Parquet have more than 15 significant digits " +
                 $"({string.Join(", ", longDoubles.Columns.Select(kv => $"{kv.Key} {kv.Value}"))}). The Parquet " +
                 "converter parsed the original CSV text with pandas' default parser, which is not always correctly " +
                 "rounded for 16 or 17 digits, so these may differ in the last binary place from a read of the original CSV.");

        var nodes = nodeInfo.Select(kv => new LtdsNode(
            "UKPN", kv.Key, kv.Value.sub, "", kv.Value.gsp, "Energised")).ToList();

        // N194: leave out the rows that are the sum of other rows (see AggregateRows), then share each remaining
        // substation's demand over the transformer LV nodes that feed it (see ShareDemand).
        HashSet<string> aggregates = AggregateRows(demandBySub, feedersBySub, lvSubsByHvNode, circuits,
            out Dictionary<string, HashSet<string>> fedByAggregate);
        if (aggregates.Count > 0)
        {
            double aggMw = 0;
            foreach (string s in aggregates) aggMw += demandBySub[s].MaxDemandMw;
            // GR-333: state the MW the replica does not carry. For each aggregate row, its published figure less the sum of
            // the demand rows it feeds; a positive difference is load that may be supplied directly from the board (or
            // diversity: maximum demands do not add), and it is not in the replica. Rows summed in a fixed (ordinal) order,
            // so the figure is the same on every run.
            double residualMw = 0;
            int withResidual = 0;
            foreach (string s in aggregates.Order(StringComparer.OrdinalIgnoreCase))
            {
                double behind = 0;
                foreach (string other in fedByAggregate[s].Order(StringComparer.OrdinalIgnoreCase))
                    behind += demandBySub[other].MaxDemandMw;
                double excess = demandBySub[s].MaxDemandMw - behind;
                if (excess > 0) { residualMw += excess; withResidual++; }
            }
            buildNotes.Add(FormattableString.Invariant(
                $"UKPN: {withResidual} of the {aggregates.Count} aggregate demand row(s) publish more than the rows they feed add up to; ") +
                FormattableString.Invariant(
                $"the excess, {residualMw:F1} MW in all, is not in the replica (direct load on the board, or diversity between maximum demands; GR-333)."));
            buildNotes.Add(FormattableString.Invariant(
                $"UKPN: {aggregates.Count} demand row(s), {aggMw:F1} MW, carry no load because each is the load of a board that ") +
                "feeds other substations with their own demand rows (a grid substation's row is the sum of the primaries " +
                "behind it), so counting it as well would count that load twice (for example " +
                string.Join(", ", aggregates.Order(StringComparer.OrdinalIgnoreCase).Take(NoteExampleLimit).Select(s => $"'{s}'")) +
                "). Load supplied directly from such a board, if any, is not in the replica; generation connected there still joins the board.");
        }
        var demand = new List<LtdsDemand>(demandBySub.Count);
        int sharedRows = 0, sharedParts = 0, movedRows = 0;
        foreach (LtdsDemand d in demandBySub.Values)
        {
            if (aggregates.Contains(d.PrimarySubstation))
            {
                // Kept as a join-only row: no load, but generation that names this grid board still finds it. It is
                // placed on the board's last transformer LV node in node-code order (the node the builder's
                // last-listed rule would pick if the row were shared), or on its own node if no transformer feeds it.
                string joinNode = feedersBySub.TryGetValue(d.PrimarySubstation, out SortedDictionary<string, double>? fb) && fb.Count > 0
                    ? fb.Keys.Last() : d.Node;
                demand.Add(d with { Node = joinNode, Kv = nodeInfo[joinNode].kv, JoinOnly = true });
                continue;
            }
            IReadOnlyList<LtdsDemand> parts = ShareDemand(d, feedersBySub.GetValueOrDefault(d.PrimarySubstation),
                node => nodeInfo[node].kv);
            if (parts.Count > 1) { sharedRows++; sharedParts += parts.Count; }
            else if (!ReferenceEquals(parts[0], d)) movedRows++;
            demand.AddRange(parts);
        }
        // Pedantic gate: every row being an aggregate of another is impossible for a real network (the rows at the
        // bottom of the chain feed no other row), so an empty result means the data or this rule is broken.
        if (!demand.Any(x => !x.JoinOnly))
            throw new InvalidDataException(
                $"UKPN {Path.GetFileName(demandFile)}: all {demandBySub.Count} demand row(s) were judged to be sums of " +
                "other rows, which no real network can produce; refusing to build a replica with no load.");
        if (sharedRows > 0 || movedRows > 0)
            buildNotes.Add($"UKPN: {sharedRows} substation demand row(s) were shared over {sharedParts} transformer LV nodes (busbar " +
                 $"sections with no coupler in the LTDS), in proportion to the winter rating of the windings feeding each; " +
                 $"{movedRows} more were moved onto the one transformer LV node that feeds the substation.");

        // N194: UKPN's transformer impedances are percent on the 100 MVA system base, not on the unit's rating (the
        // shared record's default). Evidence: Table 2a positive-sequence X runs 13.2 % to 237.3 % (median 98.6 %),
        // falls as the rating rises (log-log correlation -0.86 over 2,171 units) and X x rating / 100 (the percent on
        // the unit's own rating, if the published figure is on 100 MVA) has median 23.0 % (5 % to 95 %: 11.3 % to
        // 32.4 %), the normal range for 33/11 and 132/33 kV units. Read as percent on rating, a 24 MVA 33/11 kV unit
        // publishing 100 % was 4.17 pu on 100 MVA, 100 % on its own rating. NPg's LTDS headers for the same table say
        // "% on 100 MVA Base" outright, and NPg is already read that way.
        return new LtdsDataset(
            "GB/UKPN ex-GDA", ImpedanceConvention.PercentOn100Mva,
            nodes, circuits, tx2, tx3,
            demand, gen, TransformerPercentOnRating: false, InfeedPerGspGroupPerIsland: true)
        {
            SourceNotes = buildNotes,
        };
    }

    /// <summary>
    /// N194 (October 2026): the demand rows that are the sum of other demand rows, by the network's own structure.
    /// <para>
    /// UKPN's LTDS Table 3a lists grid substations (the 33 kV or 66 kV board behind the 132 kV transformers) as well
    /// as the primaries those boards feed, and a grid row is the load of its whole board, primaries included. Built
    /// as published, every primary's load was counted twice: once at the primary, once again at its grid board. On
    /// the full lake (LTDS of 28 November 2025) the 156 rows named "... 33kV" / "... 66kV" add up to 9.42 GW of the
    /// 23.71 GW in the table; for grid boards whose 33 kV network reaches no other grid site the primaries behind
    /// them sum to the grid row almost exactly (Back Hill 21.7 / 21.7 MW, Smallfield 45.9 / 45.9, Hastings 13.5 /
    /// 13.5, Maidstone 63.6 / 63.5, Chessington 117.0 / 116.1), and UKPN's whole demand cannot be the 22.7 GW the old
    /// replica carried (about half of GB's peak).
    /// </para>
    /// <para>
    /// Rule: a row is an aggregate when, walking the circuits only (never through a transformer) from its
    /// substation's transformer LV nodes (or, if no transformer feeds it, from its own node), the walk reaches the HV
    /// node of a transformer that feeds ANOTHER substation which has its own demand row. On the full lake this flags
    /// exactly the 156 rows named 33 kV / 66 kV that have nodes, plus four London 20 / 22 kV boards whose fed rows
    /// add up to their own figure (St Johns Wood 22kV 19.8 / 19.8 MW, Deptford West 22kV 34.4 / 34.4, Moscow Rd 22kV
    /// 72.0 / 73.6, Bankside D 20kV 56.7 / 35.5). The name is never used, only the network.
    /// </para>
    /// </summary>
    private static HashSet<string> AggregateRows(
        Dictionary<string, LtdsDemand> demandBySub,
        Dictionary<string, SortedDictionary<string, double>> feedersBySub,
        Dictionary<string, HashSet<string>> lvSubsByHvNode,
        List<LtdsCircuit> circuits,
        out Dictionary<string, HashSet<string>> fedByAggregate)
    {
        // Circuit adjacency (every circuit, including zero-impedance ties, which join one board).
        var adj = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Link(string a, string b)
        {
            if (!adj.TryGetValue(a, out List<string>? la)) adj[a] = la = new List<string>();
            la.Add(b);
        }
        foreach (LtdsCircuit c in circuits) { Link(c.Node1, c.Node2); Link(c.Node2, c.Node1); }

        var aggregates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        fedByAggregate = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string sub, LtdsDemand d) in demandBySub)
        {
            IEnumerable<string> start = feedersBySub.TryGetValue(sub, out SortedDictionary<string, double>? f) && f.Count > 0
                ? f.Keys : new[] { d.Node };
            var seen = new HashSet<string>(start, StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>(seen);
            // GR-333: the walk now runs to the end of the circuit network (it used to stop at the first other demand row
            // found), collecting every other demand row it reaches, so the note can state the residual MW. Which rows are
            // aggregates is unchanged: a row is one exactly when the set is not empty.
            var fedRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (stack.Count > 0)
            {
                string x = stack.Pop();
                if (lvSubsByHvNode.TryGetValue(x, out HashSet<string>? fed))
                    foreach (string other in fed)
                        if (!string.Equals(other, sub, StringComparison.OrdinalIgnoreCase) && demandBySub.ContainsKey(other))
                            fedRows.Add(other);
                if (adj.TryGetValue(x, out List<string>? next))
                    foreach (string y in next)
                        if (seen.Add(y)) stack.Push(y);
            }
            if (fedRows.Count > 0)
            {
                aggregates.Add(sub);
                fedByAggregate[sub] = fedRows;
            }
        }
        return aggregates;
    }

    /// <summary>How many example codes a note names.</summary>
    private const int NoteExampleLimit = 5;

    /// <summary>
    /// The short tag of a UKPN licence area for a split node key: the text in brackets ("London Power Networks (LPN)"
    /// gives "LPN"), else the whole trimmed name, else "blank".
    /// </summary>
    private static string LicenceTag(string licence)
    {
        string s = licence.Trim();
        int open = s.LastIndexOf('('), close = s.LastIndexOf(')');
        if (open >= 0 && close > open + 1) return s[(open + 1)..close].Trim();
        return s.Length > 0 ? s : "blank";
    }

    /// <summary>Characters that end the first word of a name (see <see cref="SiteWord"/>).</summary>
    private static readonly System.Buffers.SearchValues<char> SiteWordBreaks = System.Buffers.SearchValues.Create(" \t/");

    /// <summary>First word of a substation or GSP name, lower case ("Barking 132kV GIS" gives "barking").</summary>
    private static string SiteWord(string name)
    {
        string s = name.Trim();
        int cut = s.AsSpan().IndexOfAny(SiteWordBreaks);
        return (cut < 0 ? s : s[..cut]).ToLowerInvariant();
    }

    /// <summary>
    /// N194 (October 2026): find the node codes that UKPN uses for different substations in different licence areas.
    /// <para>
    /// UKPN's LTDS node codes (four letters of the site plus a voltage digit and a suffix) are unique only within a
    /// licence area. On the full lake (LTDS published 28 November 2025) 112 of the 5,927 codes in Tables 1, 2a and 2b
    /// appear in more than one of EPN, LPN and SPN. Most are different places: BROG31 is Brogborough Primary 33kV
    /// (EPN, Sundon GSP), Bromley Grid 33kV (LPN, Hurst) and Broad Oak Grid 33kV (SPN, Ninfield); BURG31 is Bury Grid
    /// (EPN) and Burgess Hill (SPN). Keyed by code alone, the builder joined each such set into one bus, wiring
    /// substations up to 150 km apart together, which drove the power flow to collapse. A few are one boundary site
    /// published by both areas (BARG11 Barking, BEDD11 Beddington, CHSI1A Chessington, BRIM11 Brimsdown, ...), and those
    /// must stay one node.
    /// </para>
    /// <para>
    /// Rule: a code used in more than one licence area is the same site when every area's rows share both the first
    /// word of the substation name and the first word of the GSP name (Barking / Barking, Beddington / Beddington);
    /// otherwise it is split, one node per licence area. Codes used in one area only are untouched, so a network
    /// with no such collision builds exactly as before. Placeholder names such as "DNO-UKPN" or "EPN 0043" never
    /// match a real site word in another area, so such a pair is split (the safe direction: a missed boundary tie
    /// separates two pieces that each keep their own GSP infeed, while a false tie wires two distant networks
    /// together). Within one licence area a code is never split (there the same code under two names is, on this
    /// lake, a placeholder and a real name for one node, with one exception left as an open item: SHLZ31 in SPN).
    /// </para>
    /// </summary>
    private static HashSet<string> SplitNodeCodes(
        TableSource circuitSrc, TableSource txSrc, TableSource? tx3Src, LongDoubleTally tally, out int sharedBoundaryCodes)
    {
        // code -> licence -> (substation site words, GSP site words)
        var uses = new Dictionary<string, Dictionary<string, (HashSet<string> Subs, HashSet<string> Gsps)>>(StringComparer.OrdinalIgnoreCase);
        void Use(string code, string licence, string substation, string gsp)
        {
            if (code.Length == 0) return;
            if (!uses.TryGetValue(code, out var byLic)) uses[code] = byLic = new(StringComparer.OrdinalIgnoreCase);
            if (!byLic.TryGetValue(licence, out var u)) byLic[licence] = u = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
            u.Subs.Add(SiteWord(substation));
            u.Gsps.Add(SiteWord(gsp));
        }

        using (UkpnTable t = UkpnTable.Open(circuitSrc, tally))
            foreach (IRow r in t.Rows())
            {
                string lic = r["LicenceArea"], gsp = r["GridSupplyPoint"];
                Use(r["FromNode"], lic, r["FromSubstation"], gsp);
                Use(r["ToNode"], lic, r["ToSubstation"], gsp);
            }
        using (UkpnTable t = UkpnTable.Open(txSrc, tally))
            foreach (IRow r in t.Rows())
            {
                string lic = r["LicenceArea"], gsp = r["GridSupplyPoint"];
                Use(r["HV_Node"], lic, r["HV_Substation"], gsp);
                Use(r["LV_Node"], lic, r["LV_Substation"], gsp);
            }
        if (tx3Src is not null)
            using (UkpnTable t = UkpnTable.Open(tx3Src, tally))
                foreach (IRow r in t.Rows())
                {
                    string lic = r["LicenceArea"], gsp = r["GridSupplyPoint"];
                    Use(r["HV_Node"], lic, r["HV_Substation"], gsp);
                    Use(r["LV_Node_1"], lic, r["LV_Substation_1"], gsp);
                    Use(r["LV_Node_2"], lic, r["LV_Substation_2"], gsp);
                }

        var split = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        sharedBoundaryCodes = 0;
        foreach ((string code, var byLic) in uses)
        {
            if (byLic.Count < 2) continue;
            HashSet<string>? subs = null, gsps = null;
            foreach ((HashSet<string> s, HashSet<string> g) in byLic.Values)
            {
                if (subs is null) { subs = new HashSet<string>(s, StringComparer.Ordinal); gsps = new HashSet<string>(g, StringComparer.Ordinal); }
                else { subs.IntersectWith(s); gsps!.IntersectWith(g); }
            }
            subs!.Remove(""); gsps!.Remove("");
            if (subs.Count > 0 && gsps.Count > 0) sharedBoundaryCodes++;
            else split.Add(code);
        }
        return split;
    }

    /// <summary>
    /// N194 (October 2026): where one UKPN demand row goes. <paramref name="feeders"/> maps each LV-winding node of the
    /// transformers whose LV substation is the row's substation to the summed winter rating (MVA) of the windings
    /// landing on it (null or empty when no transformer names this substation as its LV side).
    /// <list type="bullet">
    /// <item>No feeder, or one feeder node that is already the row's node: the row itself is returned, untouched (so a
    /// substation the old rule placed correctly builds exactly as before).</item>
    /// <item>One feeder node that is not the row's node (the old rule picked a lowest-voltage node of the substation that
    /// no transformer feeds, such as a cable end): the row moves to the feeder node, demand unchanged.</item>
    /// <item>Several feeder nodes: one row per node, in node-code order, each carrying share = weight / total of the MW
    /// and of the firm capacity (power factor unchanged). If every weight is 0 (no usable rating), the shares are equal.</item>
    /// </list>
    /// Why: UKPN's LTDS has no busbar couplers, so a substation whose board is split into sections shows up as
    /// several unconnected nodes; the old rule put the whole demand on one of them, so one or two transformers of a
    /// four-transformer site carried the lot (Townmead B 11kV: 34.9 MW through one 33 MVA unit and an 11 kV cable;
    /// Bulwer St 11kV: all of it through two of four units), driving those sections to collapse. Load at a split
    /// board is shared by the transformers in proportion to their capacity, which is what the rating weights do.
    /// </summary>
    private static IReadOnlyList<LtdsDemand> ShareDemand(
        LtdsDemand d, SortedDictionary<string, double>? feeders, Func<string, double> nodeKv)
    {
        if (feeders is null || feeders.Count == 0) return new[] { d };
        if (feeders.Count == 1)
        {
            string only = feeders.Keys.First();
            return string.Equals(only, d.Node, StringComparison.OrdinalIgnoreCase)
                ? new[] { d }
                : new[] { d with { Node = only, Kv = nodeKv(only) } };
        }
        double total = 0;
        foreach (double w in feeders.Values) total += w;
        var parts = new List<LtdsDemand>(feeders.Count);
        foreach ((string node, double w) in feeders)
        {
            double share = total > 0 ? w / total : 1.0 / feeders.Count;
            parts.Add(d with { Node = node, Kv = nodeKv(node), MaxDemandMw = d.MaxDemandMw * share, FirmCapacityMva = d.FirmCapacityMva * share });
        }
        return parts;
    }

    /// <summary>
    /// N195 (October 2026): read one end of a UKPN transformer tap range and return it in the convention the shared
    /// builder expects, an OFFSET from the nominal ratio in percent (-10 means a ratio of 0.9).
    /// <para>
    /// UKPN's LTDS Table 2a publishes <c>Tap_Range_Min_Percent</c> / <c>Tap_Range_Max_Percent</c> as the ratio at the
    /// end tap in percent OF the nominal ratio, not as an offset from it. Evidence (lake Parquet copy of
    /// ukpn-ltds-table-2a-transformer-2w, LTDS published 28 November 2025, 2,171 rows): every minimum lies in 64..100
    /// and every maximum in 100..120, so every row brackets 100; the commonest pair is 90 / 110 (845 rows), then
    /// 79 / 110 (308) and 81 / 104 (173); exactly two rows are 100 / 100 (a fixed-ratio unit). The 82 / 104 pair (154
    /// rows on 33/11 kV units) is the "-18 % to +4 % (on load) for 33/11 kV" that SSEN's LTDS states in offset form
    /// (docs/reference S262, section 4.1.1), and NGED ('Min Tap' / 'Max Tap') and NPg ('Minimum Tap %', e.g. -15 / 5)
    /// publish offsets. Read as offsets, 2,170 of the 2,171 UKPN units were refused as "range excludes the nominal
    /// 0 % tap" and got no tap changer.
    /// </para>
    /// <para>
    /// The conversion is value - 100. For the whole numbers UKPN publishes this is exact in binary (90 - 100 is
    /// exactly -10), so a published 90 / 110 becomes -10 / +10 and builds exactly the limits 0.9 / 1.1 that an NGED or
    /// NPg -10 % / +10 % unit gets. 100 / 100 becomes 0 / 0, which the builder treats as "no on-load tap changer".
    /// A blank or unreadable cell is returned as NaN (not 0, which would turn into -100 % and look like a real
    /// range), so the builder's tap-range check refuses that unit loudly and names it in a WARNING note.
    /// </para>
    /// </summary>
    private static double TapOffsetPercent(IRow r, string column)
    {
        double percentOfNominal = r.Double(column, double.NaN);
        return percentOfNominal - 100.0;   // NaN stays NaN
    }

    private static double AmpsToMva(double amps, double kv) => amps > 0 && kv > 0 ? Math.Sqrt(3) * kv * amps / 1000.0 : 0;

    // The susceptance column header carries a stray space ("Susceptance_B_ Percent").
    private static double B(IRow r)
    {
        foreach (string name in new[] { "Susceptance_B_ Percent", "Susceptance_B_Percent", "Susceptance_B Percent" })
            if (r.Has(name)) return r.Double(name);
        return 0;
    }

    // ---- N185: choosing CSV or Parquet per table --------------------------------------------------------------

    /// <summary>Where one table is read from: a CSV file, or a Parquet file standing in for a header-only CSV.</summary>
    private sealed record TableSource(string Path, bool IsParquet);

    /// <summary>
    /// Choose the file for one table. In order: (1) the RAWData CSV when it has at least one data row (the pre-N185
    /// behaviour, untouched); (2) otherwise the Parquet copy when it has at least one row; (3) otherwise the CSV even
    /// though it is header-only (so the existing refusals, which count the rows, explain what is missing); (4) no CSV
    /// and no Parquet copy: the old "file not found" refusal for a required table (now also naming the Parquet
    /// folder), or null for the optional generation table.
    /// </summary>
    private static TableSource? Resolve(string raw, string parquetDir, string csvName, Action<string>? note, bool required = true)
    {
        string? csv = Directory.Exists(raw) ? TryFind(raw, csvName) : null;
        if (csv is not null && CsvHasDataRow(csv)) return new TableSource(csv, IsParquet: false);

        // Only reached when the CSV is missing or header-only, so a lake with full CSVs never even lists the
        // Parquet folder: its builds cannot change.
        string parquetName = System.IO.Path.ChangeExtension(csvName, ".parquet");
        string? pq = Directory.Exists(parquetDir) ? TryFind(parquetDir, parquetName) : null;
        if (pq is not null)
        {
            int rows = ParquetRowCount(pq);
            if (rows > 0)
            {
                note?.Invoke(csv is not null
                    // GR-835: the row count is formatted in the invariant culture (N0 groups digits by culture).
                    ? FormattableString.Invariant($"UKPN: {System.IO.Path.GetFileName(csv)} under RAWData has no data rows (header only); read {rows:N0} rows from its Parquet copy {pq} instead.")
                    : FormattableString.Invariant($"UKPN: no {csvName} under {raw}; read {rows:N0} rows from the Parquet copy {pq} instead."));
                return new TableSource(pq, IsParquet: true);
            }
        }

        if (csv is not null) return new TableSource(csv, IsParquet: false);
        if (!required) return null;
        // Old text first (word for word), then where else it looked.
        throw new FileNotFoundException(
            $"UKPN file not found under {raw}: {csvName} (or a 'ukpn-'-prefixed variant)" +
            (Directory.Exists(parquetDir) ? $"; no Parquet copy with rows ({parquetName}) under {parquetDir} either" : ""));
    }

    /// <summary>True when the CSV has at least one data row, by the CSV reader's own rule (fully blank lines skipped).</summary>
    private static bool CsvHasDataRow(string path)
    {
        using CsvTable t = CsvTable.Open(path);
        return t.Rows().Any();
    }

    private static int ParquetRowCount(string path)
    {
        using var t = (ParquetDataTable)DataSourceTable.Open(path);
        return t.RowCount;
    }

    /// <summary>
    /// One opened table, CSV or Parquet, yielding <see cref="IRow"/>. The CSV branch yields the very same
    /// <see cref="CsvRow"/> values the reader used before N185 (only boxed as the interface it already implemented),
    /// so every CSV cell reads through the same code and gives the same bits.
    /// </summary>
    private sealed class UkpnTable : IDisposable
    {
        private readonly CsvTable? _csv;
        private readonly ParquetDataTable? _pq;
        private readonly string _path;
        private readonly LongDoubleTally _tally;

        private UkpnTable(CsvTable? csv, ParquetDataTable? pq, string path, LongDoubleTally tally)
        { _csv = csv; _pq = pq; _path = path; _tally = tally; }

        public static UkpnTable Open(TableSource src, LongDoubleTally tally) => src.IsParquet
            ? new UkpnTable(null, (ParquetDataTable)DataSourceTable.Open(src.Path), src.Path, tally)
            : new UkpnTable(CsvTable.Open(src.Path), null, src.Path, tally);

        public IReadOnlyList<string> Headers => _csv is not null ? _csv.Headers : _pq!.Headers;

        public IEnumerable<IRow> Rows()
        {
            if (_csv is not null)
            {
                foreach (CsvRow r in _csv.Rows()) yield return r;
                yield break;
            }
            for (int i = 0; i < _pq!.RowCount; i++) yield return new CsvEquivalentRow(_pq, i, _path, _tally);
        }

        public void Dispose()
        {
            _csv?.Dispose();
            _pq?.Dispose();
        }
    }

    /// <summary>Counts, per column, the Parquet double cells read whose exact text needs more than 15 digits.</summary>
    private sealed class LongDoubleTally
    {
        private readonly HashSet<(string File, int Row, string Column)> _seen = new();
        public SortedDictionary<string, int> Columns { get; } = new(StringComparer.Ordinal);
        public int Count => _seen.Count;

        public void Add(string file, int row, string column)
        {
            // A cell can be read more than once (B(r) asks Has then Double); count it once.
            if (!_seen.Add((file, row, column))) return;
            Columns[column] = Columns.TryGetValue(column, out int n) ? n + 1 : 1;
        }
    }

    /// <summary>
    /// N185: a Parquet row read with the CSV reader's meaning. The GDA lake's converter
    /// (UkPowerNetworks/Processors/parquet_data_conversion.py) runs <c>pandas.read_csv(skipinitialspace=True)</c> on the
    /// published CSV and writes the frame with pyarrow, keeping the column names. What that changes, and how this row
    /// undoes it:
    /// <list type="bullet">
    /// <item>Text cells: pandas strips leading blanks but not trailing ones; <see cref="CsvRow"/> trims both. This row
    /// trims with the same <c>string.Trim()</c>, so the text is identical. A cell that pandas turned into a number,
    /// a date or anything other than text cannot be turned back into its original text exactly ("007" became 7,
    /// "1.50" became 1.5), so reading such a cell as text is refused, naming the file, row and column. Never guessed.</item>
    /// <item>Empty cells: pandas writes them as null; null reads as "" and as the fallback number, as an empty CSV
    /// cell does. (pandas also turns the texts NA, N/A, null, NaN and a few others into null; a CSV read would have
    /// kept the text. On the October 2026 lake no text column read here has a single null, and the one row with
    /// null numbers (Thorpe Grid, "Two Sites Sharing the Same Switch") is read as empty, which is what an empty
    /// cell gives.)</item>
    /// <item>Numbers stored as text (pandas keeps a column as text when any cell is not a number, e.g. "Not
    /// Available"): parsed by exactly <see cref="CsvRow.Double"/>'s rule.</item>
    /// <item>Integers (int64): converted to double, which rounds exactly as parsing their decimal text does.</item>
    /// <item>Doubles: pandas' parser produced them. A double whose shortest exact text has at most 15 significant
    /// digits equals the correctly rounded value of any decimal text of at most 15 digits within one unit in the
    /// last place of it, which is what a CSV read (<c>double.TryParse</c>, correctly rounded) gives; so for those the
    /// value is the CSV's value. pandas' default parser is not correctly rounded for 16 or 17 digits (measured
    /// N185: 47 of 230 such strings near the lake's values were one unit off), so a double needing more than 15
    /// digits might differ in its last bit from a CSV read; those are counted and reported in a note, not hidden.</item>
    /// </list>
    /// </summary>
    private readonly struct CsvEquivalentRow : IRow
    {
        private readonly ParquetDataTable _t;
        private readonly int _i;
        private readonly string _file;
        private readonly LongDoubleTally _tally;

        public CsvEquivalentRow(ParquetDataTable t, int i, string file, LongDoubleTally tally)
        { _t = t; _i = i; _file = file; _tally = tally; }

        public string this[string column]
        {
            get
            {
                object? v = _t.Cell(_i, column);
                if (v is null) return string.Empty;
                if (v is string s) return s.Trim();
                throw new InvalidDataException(
                    $"UKPN {System.IO.Path.GetFileName(_file)}: row {_i + 1}, column '{column}' holds a {v.GetType().Name} " +
                    $"({Convert.ToString(v, CultureInfo.InvariantCulture)}) where text is expected. The Parquet converter " +
                    "turned the CSV text into a number, which cannot be turned back into the original text exactly, so " +
                    "this Parquet copy cannot be read as the CSV. Use the CSV.");
            }
        }

        // CsvRow.Has: the cell exists and is not blank. A number always had non-blank text; null was an empty cell.
        public bool Has(string column) => _t.Cell(_i, column) switch
        {
            null => false,
            string s => s.Trim().Length > 0,
            _ => true,
        };

        public double Double(string column, double fallback = 0.0)
        {
            object? v = _t.Cell(_i, column);
            switch (v)
            {
                case null:
                    return fallback;   // empty CSV cell -> fallback
                case string s:
                {
                    // Exactly CsvRow.Double on the trimmed text.
                    s = s.Trim();
                    if (s.Length == 0) return fallback;
                    // GR-379: the GDA number rule (GdaNumber), exactly as CsvRow.Double.
                    return GdaNumber.TryParse(s, column, out double parsed) ? parsed : fallback;
                }
                case double d:
                    if (SignificantDigits(d) > 15) _tally.Add(_file, _i, CsvTable.Normalize(column));
                    return d;
                case long l:
                    return l;   // long -> double is correctly rounded, as parsing the integer text is
                case int n:
                    return n;
                case bool:
                    return fallback;   // CSV text True / False does not parse as a number -> fallback
                default:
                    throw new InvalidDataException(
                        $"UKPN {System.IO.Path.GetFileName(_file)}: row {_i + 1}, column '{column}' holds a {v.GetType().Name} " +
                        $"({Convert.ToString(v, CultureInfo.InvariantCulture)}), which the CSV reader would not have read as " +
                        "this number. Use the CSV.");
            }
        }
    }

    /// <summary>
    /// Significant digits in the shortest text that reads back as exactly <paramref name="d"/> (.NET's "R" format is
    /// the shortest round-trip form). Non-finite values count as 0: they have no decimal text to compare.
    /// </summary>
    public static int SignificantDigits(double d)
    {
        if (!double.IsFinite(d)) return 0;
        string s = d.ToString("R", CultureInfo.InvariantCulture);
        int e = s.IndexOfAny(['E', 'e']);
        if (e >= 0) s = s[..e];
        s = s.TrimStart('-').Replace(".", "").TrimStart('0').TrimEnd('0');
        return s.Length;
    }

    private static string? TryFind(string dir, string file)
    {
        // The published archive prefixes every DNO file with 'ukpn-' (e.g.
        // ukpn-ltds-table-1-circuit-data.csv); accept both the prefixed and the bare name.
        // The '*' suffix pattern is anchored to the end, so it won't match sibling tables
        // such as ...-observed-transposed.csv.
        //
        // R297: enumeration order is filesystem-dependent, so "the first match" could be a different file on
        // Windows and on the Linux server. Candidates are ordered deterministically, and more than one equally
        // good match is an error naming them, never a silent pick.
        List<string> exact = Directory.EnumerateFiles(dir, file, SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (exact.Count > 1) throw Ambiguous(dir, file, exact);
        if (exact.Count == 1) return exact[0];

        List<string> suffixed = Directory.EnumerateFiles(dir, "*" + file, SearchOption.AllDirectories)
            .OrderBy(f => Path.GetFileName(f).Length)   // closest (shortest) match first
            .ThenBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (suffixed.Count == 0) return null;
        int best = Path.GetFileName(suffixed[0]).Length;
        List<string> tied = suffixed.Where(f => Path.GetFileName(f).Length == best).ToList();
        if (tied.Count > 1) throw Ambiguous(dir, file, tied);
        return suffixed[0];

        static InvalidDataException Ambiguous(string dir, string file, List<string> hits) => new(
            $"UKPN: {hits.Count} equally good matches for {file} under {dir}; which one is read would depend on the " +
            "filesystem. Keep one. Matches: " + string.Join(", ", hits.Select(h => Path.GetRelativePath(dir, h))));
    }
}
