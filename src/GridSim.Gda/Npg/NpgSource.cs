using System.Globalization;
using GridSim.Gda.Csv;
using GridSim.Gda.Ltds;

namespace GridSim.Gda.Npg;

/// <summary>
/// Reads Northern Powergrid's LTDS (NE England + Yorkshire) into the shared
/// <see cref="LtdsDataset"/>. NPg publishes both CSV and Parquet; this reads via the
/// format-agnostic <see cref="DataSourceTable"/> (preferring Parquet where present).
///
/// NPg's model is keyed by (busbar name, voltage): a circuit joins two same-voltage
/// busbars, and a transformer links a substation's primary- and secondary-voltage
/// busbars. Impedances are percent on 100 MVA  -  including transformers (unlike UKPN,
/// whose transformer percents are on rating).
/// </summary>
public static class NpgSource
{
    // The per-vintage observed maximum load column, e.g. "2024/25 Net (Observed) Maximum Load (MVA)" (R300).
    private static readonly System.Text.RegularExpressions.Regex DemandColumn = new(
        @"^(\d{4})/(\d{2}) Net \(Observed\) Maximum Load \(MVA\)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// N211 (October 2026, Ruling R1): S/S Group words NPg spells two ways, alias -> canonical (upper case, whole
    /// words). The Harrogate group is "KNAREBOROUGH" in the transformer table and "KNARESBOROUGH" in the circuit table;
    /// the canonical spelling is the town's, Knaresborough. Every group cell is read through
    /// <see cref="GdaModelBuilder.ApplyGroupWordAliases"/>, so the model carries one name per group; the alias is kept
    /// here (and on <see cref="LtdsDataset.GroupWordAliases"/>), so a <c>--gsp</c> carve typed with it still selects the
    /// group, and the build notes say how many cells were read through it. The join already met (N207's site-name join
    /// does not use the group), so no number moves; the group is now one name in listings and carves.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> GroupWordAliases =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["KNAREBOROUGH"] = "KNARESBOROUGH" };

    public static LtdsDataset Load(string root, bool preferParquet = true)
    {
        string npg = Path.Combine(root, "NorthernPowerGrid");
        if (!Directory.Exists(npg)) throw new DirectoryNotFoundException($"NPg data not found: {npg}");

        // N211: every S/S Group cell is read through the alias table; cells that changed are counted for the notes.
        int groupCellsRespelt = 0;
        var respeltGroups = new SortedSet<string>(StringComparer.Ordinal);
        string Group(IRow r)
        {
            string published = r["S/S Group"];
            string canonical = GdaModelBuilder.ApplyGroupWordAliases(published, GroupWordAliases);
            if (!string.Equals(published, canonical, StringComparison.Ordinal))
            {
                groupCellsRespelt++;
                respeltGroups.Add($"'{published}' -> '{canonical}'");
            }
            return canonical;
        }

        // GR-313: every dataset whose Parquet copy could not be read and whose RAWData CSV was read instead, one note
        // each, so the build notes say which source each table came from.
        var parquetFallbacks = new List<string>();
        DataSourceTable Open(string dataset) => OpenDataset(npg, dataset, preferParquet, parquetFallbacks);

        var nodes = new Dictionary<string, LtdsNode>(StringComparer.OrdinalIgnoreCase);
        void AddNode(string code, string name, string gsp)
        { if (code.Length > 0 && !nodes.ContainsKey(code)) nodes[code] = new LtdsNode("NPG", code, name, "", gsp, "Energised"); }

        // ---- circuits (Appendix 3) ----
        var circuits = new List<LtdsCircuit>();
        using (DataSourceTable t = Open("LTDS_Appendix_3_Circuit_Data"))
            foreach (IRow r in t.Rows())
            {
                string nm1 = r["S/S or Busbar Name Node 1"], nm2 = r["S/S or Busbar Name Node 2"];
                if (nm1.Length == 0 || nm2.Length == 0) continue;
                double kv = r.Double("Operating Voltage (kV)");
                string gsp = Group(r);   // N211
                string c1 = Key(nm1, kv), c2 = Key(nm2, kv);
                AddNode(c1, nm1, gsp); AddNode(c2, nm2, gsp);
                double rating = Num(r, "Rating Information (MVA)", "Rating (MVA)", "Rating Information");
                circuits.Add(new LtdsCircuit(
                    r["License"], gsp, "", c1, c2, "Energised", kv, "",
                    Num(r, "Positive Sequence Impedance R % on (100 MVA Base)", "Positive Sequence Impedance R (% on 100 MVA Base)"),
                    Num(r, "Positive Sequence Impedance X (% on 100 MVA Base)", "Positive Sequence Impedance X % on (100 MVA Base)"),
                    Num(r, "Susceptance B (% on 100 MVA Base)", "Susceptance B % on (100 MVA Base)"),
                    0, 0, rating, rating, r.Double("Circuit Length (km)"), "Circuit"));
            }

        // ---- transformers (Appendix 4) ----
        // N202 (October 2026, Ruling R1 follow-up): the circuit busbars indexed by site name, so a transformer winding
        // whose own (name, voltage) key names no busbar can join its site's busbar (see WindingNode). Built once, from
        // the circuit nodes only, before any transformer node is added.
        Dictionary<string, List<(string Code, double Kv)>> busbarSites = SiteIndex(nodes.Values);
        var tx2 = new List<LtdsTransformer2>();
        int windingsJoinedBySite = 0;   // N202: windings placed on their site's busbar, reported in the build notes
        using (DataSourceTable t = Open("LTDS_Appendix_4_Transformer_Data"))
            foreach (IRow r in t.Rows())
            {
                string nm = r["S/S or Busbar Name Node"];
                if (nm.Length == 0) continue;
                double pv = r.Double("Primary Voltage (kV)"), sv = r.Double("Secondary Voltage (kV)");
                if (pv <= 0 || sv <= 0 || pv == sv) continue;
                string gsp = Group(r);   // N211
                // N202: each winding joins the busbar its key names, else its site's busbar (WindingNode).
                string h = WindingNode(nm, pv, nodes, busbarSites), l = WindingNode(nm, sv, nodes, busbarSites);
                if (!string.Equals(h, Key(nm, pv), StringComparison.Ordinal)) windingsJoinedBySite++;
                if (!string.Equals(l, Key(nm, sv), StringComparison.Ordinal)) windingsJoinedBySite++;
                AddNode(h, nm, gsp); AddNode(l, nm, gsp);
                tx2.Add(new LtdsTransformer2(
                    r["License"], gsp, h, pv, l, sv, "Energised", r["Vector Group"],
                    Num(r, "Positive Sequence Impedance R (% on 100 MVA Base)", "Positive Sequence Impedance R % on (100 MVA Base)"),
                    Num(r, "Positive Sequence Impedance X (% on 100 MVA Base)", "Positive Sequence Impedance X % on (100 MVA Base)"),
                    Num(r, "Zero Sequence Reactance X (% on 100 MVA Base)"),
                    r.Double("Minimum Tap %"), r.Double("Maximum Tap %"),
                    Num(r, "Transformer Rating (MVA)", "Rating (MVA)"), 0));
            }

        // ---- load (Appendix 5) ----
        // N184: the circuit and transformer nodes indexed by canonical site name, for the demand rows whose exact
        // (name, voltage) key names no node (see DemandNode). Built once, from the nodes read above, in node order.
        Dictionary<string, List<(string Code, double Kv)>> sites = SiteIndex(nodes.Values);
        var demand = new List<LtdsDemand>();
        using (DataSourceTable t = Open("LTDS_Appendix_5_Load_Information"))
        {
            // R300: the newest "YYYY/YY Net (Observed) Maximum Load (MVA)" column, not a hard-coded 2024/25 one;
            // an unversioned name is the fallback. No demand column at all is an error, not zero demand.
            string mvaCol = VintageColumns.Newest(t.Headers, DemandColumn)?.Header
                ?? new[] { "Maximum Load (MVA)", "Net (Observed) Maximum Load (MVA)" }
                    .FirstOrDefault(c => t.Headers.Contains(c, StringComparer.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(
                    $"NPg LTDS_Appendix_5_Load_Information: no maximum-load (MVA) column (headers: {string.Join(", ", t.Headers)}).");
            string pfVintage = mvaCol.EndsWith("(MVA)", StringComparison.Ordinal) ? mvaCol[..^"(MVA)".Length] + "(PF)" : "";

            foreach (IRow r in t.Rows())
            {
                string nm = r["S/S or Busbar Name"];
                if (nm.Length == 0) continue;
                double kv = r.Double("Voltage Level (kV)");
                double mva = Num(r, mvaCol);
                if (mva <= 0) continue;
                double pf = Num(r, pfVintage, "Maximum Load (PF)");
                if (pf is <= 0 or > 1) pf = GdaBuildDefaults.DemandPowerFactor;
                demand.Add(new LtdsDemand(
                    Group(r), "", nm, DemandNode(nm, kv, nodes, sites), kv,
                    mva * pf, pf, Num(r, "Firm Capacity (MVA)")));
            }

            if (demand.Count == 0)
                throw new InvalidDataException(
                    $"NPg LTDS_Appendix_5_Load_Information: column {mvaCol} gave no positive demand on any row, so the " +
                    "replica would have no load.");
        }

        // ---- generation (Appendix 7) ----
        var gen = new List<LtdsGeneration>();
        DataSourceTable? gt = TryOpenDataset(npg, "LTDS_Appendix_7_Generation", preferParquet, parquetFallbacks);
        if (gt is not null)
            using (gt)
                foreach (IRow r in gt.Rows())
                {
                    string sub = r["Primary Substation"];
                    double cap = Num(r, "Installed Capacity (MVA)", "Installed Capacity");
                    if (sub.Length == 0 || cap <= 0) continue;
                    gen.Add(new LtdsGeneration(
                        // N211: the same alias table as the S/S Group cells (not counted: a different column).
                        GdaModelBuilder.ApplyGroupWordAliases(r["Grid Supply Point"], GroupWordAliases), r["Bulk Supply Point"], sub,
                        r.Double("Connection Voltage (kV)"), cap, r["Fuel Type"], r["Connected/Accepted"]));
                }

        // N202 (October 2026, Ruling R1 follow-up, Mark delegated 2026-10-04): NPg takes UKPN's infeed rule (N194),
        // one GSP infeed per GSP group per island, at every bus of the GSP site at its highest voltage there. With the
        // old rule (one infeed per group, at its busiest highest-voltage node) and the 86 demand rows the N184 join
        // places, the full-lake NPg build stopped converging: a 26-bus, 33 kV-only island (no transformer from 132 kV
        // in the published tables) carrying Harrogate 33kV (51.9 MW) and Wormald Green 33kV (70.8 MW) was fed from one
        // infeed at Coneythorpe T1 33kV and sagged to 0.519 pu. NGED keeps the old rule (it converges with it, and the
        // flag would move its numbers).
        return new LtdsDataset(
            "GB/NPg ex-GDA", ImpedanceConvention.PercentOn100Mva,
            nodes.Values.ToList(), circuits, tx2, Array.Empty<LtdsTransformer3>(),
            demand, gen, TransformerPercentOnRating: false, InfeedPerGspGroupPerIsland: true)
        {
            // N202: say how many transformer windings the site-name join placed (printed first in the build notes).
            // N211: then, only when some cell used an alias spelling, how many S/S Group cells were read as canonical.
            // GR-313: then one note per dataset read from its CSV because its Parquet copy could not be read.
            SourceNotes = SourceNotesOf(windingsJoinedBySite, groupCellsRespelt, respeltGroups).Concat(parquetFallbacks).ToArray(),
            GroupWordAliases = GroupWordAliases,
        };
    }

    /// <summary>The reader's notes, in order: N202 (windings joined by site), then N211 (group cells re-spelt). An empty
    /// list when neither happened, exactly as before N211 when no cell used an alias.</summary>
    private static string[] SourceNotesOf(int windingsJoinedBySite, int groupCellsRespelt, SortedSet<string> respeltGroups)
    {
        var notes = new List<string>();
        if (windingsJoinedBySite > 0)
            notes.Add($"Transformer windings: {windingsJoinedBySite} named no busbar by (name, voltage) and joined their site's " +
                      "circuit busbar by site name (for example 'Harrogate GT2B' at 33 kV joins 'Harrogate GT2B 33kV'; N202).");
        if (groupCellsRespelt > 0)
            notes.Add($"S/S Group: {groupCellsRespelt} cell(s) spell a group word two ways and were read in the canonical spelling " +
                      $"({string.Join(", ", respeltGroups)}; N211).");
        return notes.ToArray();
    }

    // Node identity = (busbar name, voltage). Names vary in case/spacing across the
    // circuit/transformer/load tables, so normalise hard (upper-case, collapse runs of
    // whitespace) to join them  -  otherwise the network fragments into islands.
    private static string Key(string name, double kv)
    {
        string norm = string.Join(' ', name.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return $"{norm}|{kv.ToString("0.#", CultureInfo.InvariantCulture)}";
    }

    // N184: a voltage token with its "kV" ("66/20kV", "66kV", "11 kV"), and a trailing transformer number (" T2").
    private static readonly System.Text.RegularExpressions.Regex SiteVoltageToken = new(
        @"\b\d+(\.\d+)?(\s*/\s*\d+(\.\d+)?)*\s*kv\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex TrailingTransformerNumber = new(
        @"\s+T\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// N184 (Ruling R1, Mark delegated 2026-10-04): the largest relative gap between a demand row's voltage and the node
    /// voltage it may join by site name. 5 %: NPg publishes transformer secondaries at 11.5 kV where the load table says
    /// 11.0 (a 4.3 % gap) for the same busbar. The nearest node voltage wins, so a site with nodes at two close voltages
    /// joins the nearer one. Measured on the full lake (October 2026, site name with the trailing " T&lt;n&gt;" dropped):
    /// exact voltage joins 62 rows (2,106.8 MVA), 5 % joins 86 (2,286.6 MVA); 10 % would join one more (87, 2,302.0
    /// MVA) by accepting a gap no busbar-voltage spelling explains, so 5 % is kept.
    /// </summary>
    public const double SiteJoinVoltageTolerance = 0.05;

    /// <summary>N184: a busbar name as a site: upper-case, voltage tokens with "kV" removed, whitespace collapsed
    /// ("Annfield 66/20kV" and "ANNFIELD" both give "ANNFIELD").</summary>
    public static string SiteName(string name) =>
        string.Join(' ', SiteVoltageToken.Replace(name, " ").ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>N184: the circuit / transformer nodes per site name, each with its voltage (from its key), in the given
    /// node order.</summary>
    private static Dictionary<string, List<(string Code, double Kv)>> SiteIndex(IEnumerable<LtdsNode> nodes)
    {
        var index = new Dictionary<string, List<(string Code, double Kv)>>(StringComparer.Ordinal);
        foreach (LtdsNode n in nodes)
        {
            int bar = n.Code.LastIndexOf('|');
            if (bar < 0) continue;
            double kv = double.Parse(n.Code[(bar + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture);
            string site = SiteName(n.Code[..bar]);
            if (!index.TryGetValue(site, out List<(string Code, double Kv)>? list)) index[site] = list = new();
            list.Add((n.Code, kv));
        }
        return index;
    }

    /// <summary>
    /// N184 (Ruling R1, Mark delegated 2026-10-04): the node a load row joins. Exactly as before when its own
    /// (name, voltage) key names a circuit or transformer node (34 of the 657 positive-load rows on the full lake, so
    /// those rows are unchanged). Otherwise the load's site name (<see cref="SiteName"/>, with a trailing " T&lt;n&gt;"
    /// also dropped) is looked up among the nodes' site names, and the node whose voltage is closest to the load's,
    /// within <see cref="SiteJoinVoltageTolerance"/>, is taken (ties: the node first read). That joins 52 more rows on
    /// the full lake (86 in all, 2,286.6 MVA): the most the published tables allow, since most other loads are 11 kV
    /// primaries whose 11 kV busbar is in neither Appendix 3 nor Appendix 4. A row with no such node keeps its own key,
    /// which names no node, so the builder reports it as outside the network exactly as before.
    /// </summary>
    private static string DemandNode(string name, double kv, Dictionary<string, LtdsNode> nodes,
        Dictionary<string, List<(string Code, double Kv)>> sites)
    {
        string own = Key(name, kv);
        if (nodes.ContainsKey(own)) return own;
        string site = TrailingTransformerNumber.Replace(SiteName(name), "");
        if (kv <= 0 || !sites.TryGetValue(site, out List<(string Code, double Kv)>? candidates)) return own;
        string? best = null;
        double bestGap = double.PositiveInfinity;
        foreach ((string code, double nodeKv) in candidates)
        {
            double gap = Math.Abs(nodeKv - kv);
            if (gap <= SiteJoinVoltageTolerance * kv && gap < bestGap) { best = code; bestGap = gap; }
        }
        return best ?? own;
    }

    /// <summary>
    /// N202 (October 2026, Ruling R1 follow-up, Mark delegated 2026-10-04): the node a transformer winding joins.
    ///
    /// <para>NPg's transformer table (Appendix 4) and circuit table (Appendix 3) spell one busbar differently: the
    /// transformer row says "Harrogate GT2B" (132 / 33 kV) or "Brighouse 33/11kV T1", while the circuits that connect it
    /// say "Harrogate GT2B 132kV", "Harrogate GT2B 33kV" and "Brighouse T1". Keyed by (name, voltage) alone, 254
    /// high-voltage and 54 low-voltage windings on the full lake named no busbar, so those transformers were built
    /// floating, connected to nothing: the grid transformers of Harrogate (GT2B, GT3B, GT4B, 132/33 kV) and Wormald Green
    /// (GT3, GT4) among them, which left the two sites' 33 kV networks as one island with no path to 132 kV and its
    /// 122.7 MW fed from a single infeed (the N202 non-convergence).</para>
    ///
    /// <para>The rule, the same as the N184 demand join (<see cref="DemandNode"/>) without its trailing-transformer-number
    /// step (here " T1" and " T2" are different units): a winding whose own key names a node keeps it, exactly as before.
    /// Otherwise its site name (<see cref="SiteName"/>: voltage tokens with "kV" removed) is looked up among the CIRCUIT
    /// busbars' site names, and the busbar whose voltage is nearest the winding's, within
    /// <see cref="SiteJoinVoltageTolerance"/> (NPg writes 11.5 kV secondaries onto 11 kV busbars), is taken when it is the
    /// only one at that distance. No such busbar, or two at the same distance: the winding keeps its own key, as before.</para>
    /// </summary>
    private static string WindingNode(string name, double kv, Dictionary<string, LtdsNode> nodes,
        Dictionary<string, List<(string Code, double Kv)>> busbarSites)
    {
        string own = Key(name, kv);
        if (nodes.ContainsKey(own)) return own;
        if (kv <= 0 || !busbarSites.TryGetValue(SiteName(name), out List<(string Code, double Kv)>? candidates)) return own;
        string? best = null;
        double bestGap = double.PositiveInfinity;
        bool tied = false;
        foreach ((string code, double nodeKv) in candidates)
        {
            double gap = Math.Abs(nodeKv - kv);
            if (gap > SiteJoinVoltageTolerance * kv) continue;
            if (gap < bestGap) { best = code; bestGap = gap; tied = false; }
            else if (gap == bestGap && !string.Equals(code, best, StringComparison.OrdinalIgnoreCase)) tied = true;
        }
        return best is not null && !tied ? best : own;
    }

    private static double Num(IRow r, params string[] names)
    {
        foreach (string n in names) if (r.Has(n)) return r.Double(n);
        return 0;
    }

    private static DataSourceTable OpenDataset(string dnoRoot, string dataset, bool preferParquet, List<string> fallbackNotes) =>
        TryOpenDataset(dnoRoot, dataset, preferParquet, fallbackNotes)
        ?? throw new FileNotFoundException($"NPg dataset not found: {dataset}");

    private static DataSourceTable? TryOpenDataset(string dnoRoot, string dataset, bool preferParquet, List<string> fallbackNotes)
    {
        string pq = Path.Combine(dnoRoot, "Parquet", dataset);
        string raw = Path.Combine(dnoRoot, "RAWData", dataset);
        bool pqHasData = Directory.Exists(pq) && Directory.EnumerateFiles(pq, "*.parquet", SearchOption.AllDirectories).Any();
        bool rawHasCsv = Directory.Exists(raw) && Directory.EnumerateFiles(raw, "*.csv", SearchOption.AllDirectories).Any();

        // Prefer parquet, but some tables carry nested geo columns Parquet.Net can't
        // parse  -  fall back to the populated CSV for those.
        if (preferParquet && pqHasData)
        {
            // GR-313: the fallback used to be silent, so a truncated or corrupt Parquet part (or an ambiguous multi-vintage
            // folder) quietly became a RAWData CSV read, possibly of another publication with other columns. It is now
            // stated in the build notes with the reason, and an error with no CSV to fall back to still throws.
            try { return DataSourceTable.Open(pq); }
            catch (Exception ex) when (rawHasCsv)
            {
                fallbackNotes.Add($"NPg {dataset}: the Parquet copy could not be read ({ex.GetType().Name}: {ex.Message}); " +
                                  "the RAWData CSV was read instead.");
            }
        }
        if (rawHasCsv) return DataSourceTable.Open(raw);
        if (pqHasData) return DataSourceTable.Open(pq);
        return null;
    }
}
