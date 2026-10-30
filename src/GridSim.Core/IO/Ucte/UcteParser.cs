using System.Globalization;
using GridSim.Core.Model;
using GridSim.Core.Numerics;

namespace GridSim.Core.IO.Ucte;

/// <summary>How <see cref="UcteParser"/> reads a file (Tranche A, October 2026).</summary>
public sealed record UcteReadOptions
{
    /// <summary>The system's nominal frequency (Hz). A UCTE-DEF file carries none; null: <see cref="UcteParser.AssumedFrequencyHz"/>
    /// (the frequency of the European interconnected system the format was made for), noted.</summary>
    public double? FrequencyHz { get; init; }

    /// <summary>False (the default): a file holding special transformer tables (##TT, tap-dependent impedances GridSim does
    /// not model) for a transformer in operation is refused. True: the tables are read past, counted in a note.</summary>
    public bool AllowUnmodelled { get; init; }
}

/// <summary>What a UCTE-DEF node carries that <see cref="GridModel"/> has no place for.</summary>
/// <param name="Code">The 8-character node code as written (country, name, voltage level code, busbar).</param>
/// <param name="GeographicalName">The geographical name, trimmed.</param>
/// <param name="Country">The country block (##Z) the node was given in, or "" when the file names none.</param>
/// <param name="Equivalent">True for an equivalent node (status 1).</param>
/// <param name="XNode">True for an X-node (a boundary node, code starting with X).</param>
public sealed record UcteNodeMeta(string Code, string GeographicalName, string Country, bool Equivalent, bool XNode);

/// <summary>Which UCTE-DEF record a model branch came from.</summary>
/// <param name="Kind">"line", "coupler" or "transformer".</param>
/// <param name="Node1">Node 1 as written (for a transformer, the non-regulated winding).</param>
/// <param name="Node2">Node 2 as written (for a transformer, the regulated winding).</param>
/// <param name="Order">The order code.</param>
public sealed record UcteBranchKey(string Kind, string Node1, string Node2, string Order);

/// <summary>The outcome of reading a UCTE-DEF file: the model, the notes, and what the model has no place for.</summary>
public sealed record UcteParseResult(GridModel Model, IReadOnlyList<string> Notes)
{
    /// <summary>Each node's metadata, keyed by the model bus id (1, 2, ... in file order).</summary>
    public IReadOnlyDictionary<int, UcteNodeMeta> Nodes { get; init; } = new Dictionary<int, UcteNodeMeta>();

    /// <summary>The record of each model branch, position k for <see cref="GridModel.Branches"/>[k].</summary>
    public IReadOnlyList<UcteBranchKey> BranchKeys { get; init; } = Array.Empty<UcteBranchKey>();
}

/// <summary>
/// Reads a UCTE-DEF file (UCTE data exchange format for load flow, version 02: the ##N, ##L, ##T, ##R, ##TT and ##E
/// blocks) into a <see cref="GridModel"/> (Tranche A, October 2026).
/// </summary>
/// <remarks>
/// <para><b>Layout.</b> Fixed columns (the 1-based columns of the format description). Node (##N): code 1-8, geographical
/// name 10-21, status 23, type 25, voltage reference (kV) 27-32, active and reactive load 34-40 and 42-48, active and
/// reactive generation 50-56 and 58-64, minimum and maximum permissible generation 66-72 and 74-80, minimum and maximum
/// permissible reactive generation 82-88 and 90-96, then primary control, short-circuit power, X/R and plant type
/// (carried by no GridSim element; read past). Line (##L): nodes 1-8 and 10-17, order 19, status 21, R (ohm) 23-28,
/// X (ohm) 30-35, B (microsiemens) 37-44, current limit (A) 46-51, name 53 on. Transformer (##T): node 1 (non-regulated
/// winding) 1-8, node 2 (regulated winding) 10-17, order 19, status 21, rated voltages U1 and U2 (kV) 23-27 and 29-33,
/// nominal power (MVA) 35-39, R and X (ohm) 41-46 and 48-53, B and G (microsiemens) 55-62 and 64-69, current limit (A)
/// 71-76, name 78 on. Regulation (##R): the transformer's nodes and order, then phase regulation (du % 21-25, n 27-28,
/// n' 30-32, U kV 34-38) and angle regulation (du % 40-44, theta 46-50, n 52-53, n' 55-57, P MW 59-63, type 65-68).
/// ##C comment lines and ##Z country headers are taken as written; a number field holding anything but a number is
/// refused, naming the line, block and field.</para>
/// <para><b>Per unit.</b> System base 100 MVA. Each node's base voltage is its nominal voltage from the 7th character of
/// its code (0: 750, 1: 380, 2: 220, 3: 150, 4: 120, 5: 110, 6: 70, 7: 27, 8: 330, 9: 500 kV; any other character is
/// refused). Generation is written with the load sign (a producing unit has a negative value): the model's Pg, Qg and
/// limits are their negatives, as PowSyBl's UCTE importer takes them. A PV (type 2) or slack (type 3) node holds its
/// voltage reference in per unit of its base. Type 1 (Q and angle held) has no GridSim equivalent and is refused.</para>
/// <para><b>Lines</b> join nodes of one nominal voltage (otherwise refused): Z pu = Z ohm / Zbase, B pu = B x Zbase,
/// rating sqrt(3) x U x I. A busbar coupler in operation (status 2) and a line written with R = X = 0 get X = 0.0001 pu,
/// as the PSS/E reader does, with a note. Out-of-operation elements (status 8, 9, 7) are skipped and counted.</para>
/// <para><b>Transformers.</b> R, X, G and B are referred to the non-regulated winding (node 1), as PowSyBl's importer
/// reads them (its side 2 is UCTE's node 1 and carries the impedance). The model branch runs from node 2 (the regulated
/// winding, which carries the tap) to node 1: impedance in per unit of node 1's base, off-nominal ratio
/// t = (U2 (1 + n' du / 100) / Ub2) / (U1 / Ub1), and for angle regulation the complex step 1 + n' du / 100 (cos theta +
/// j sin theta) (asymmetric; symmetric: ratio 1, angle 2 atan2(dy, 2 (1 + dx))) gives the ratio's size and the phase
/// shift. G and B are a shunt at node 1. A transformer with both kinds of regulation stepped off neutral is refused; the
/// regulated voltage or flow targets are not applied (the taps stay at n'), with a note.</para>
/// <para><b>X-nodes</b> (boundary nodes) are buses with their written load and generation as fixed injections, with a
/// note. ##E exchange schedules are read past (scheduling data, not network), with a note.</para>
/// </remarks>
public static class UcteParser
{
    /// <summary>The nominal frequency assumed when the caller gives none: the European interconnected system's.</summary>
    public const double AssumedFrequencyHz = 50.0;

    /// <summary>The system base (MVA). UCTE-DEF data is in physical units, so the base is GridSim's choice.</summary>
    public const double BaseMva = 100.0;

    /// <summary>The reactance (pu) given to a busbar coupler in operation or a line written with R = X = 0.</summary>
    public const double ZeroImpedanceXPu = 0.0001;

    /// <summary>The nominal voltage (kV) of each voltage level code (the 7th character of a node code).</summary>
    public static readonly IReadOnlyDictionary<char, double> NominalKv = new Dictionary<char, double>
    {
        ['0'] = 750, ['1'] = 380, ['2'] = 220, ['3'] = 150, ['4'] = 120, ['5'] = 110, ['6'] = 70, ['7'] = 27, ['8'] = 330, ['9'] = 500,
    };

    /// <summary>Read UCTE-DEF text into a model. See <see cref="ParseWithDiagnostics"/>.</summary>
    public static GridModel Parse(string text, string? name = null, UcteReadOptions? options = null)
        => ParseWithDiagnostics(text, name, options).Model;

    /// <summary>Read a UCTE-DEF file from disk. The case name defaults to the file stem.</summary>
    public static UcteParseResult ParseFile(string path, UcteReadOptions? options = null)
        => ParseWithDiagnostics(File.ReadAllText(path), Path.GetFileNameWithoutExtension(path), options);

    private sealed record Line(int Number, string Block, string Text)
    {
        public InvalidDataException Error(string what) => new($"UCTE-DEF line {Number} ({Block} block): {what}.");

        /// <summary>The 1-based columns from..to, trimmed ("" when the line is shorter).</summary>
        public string Col(int from, int to)
        {
            int start = from - 1;
            if (start >= Text.Length) return "";
            return Text.Substring(start, Math.Min(to, Text.Length) - start).Trim();
        }

        public string Rest(int from) => from - 1 >= Text.Length ? "" : Text[(from - 1)..].Trim();

        public double? OptReal(int from, int to, string field)
        {
            string s = Col(from, to);
            if (s.Length == 0) return null;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
                throw Error($"{field} (columns {from}-{to}) is '{s}'; it must be a finite number");
            return v;
        }

        public double Real(int from, int to, string field, double? dflt = null)
            => OptReal(from, to, field) ?? dflt ?? throw Error($"{field} (columns {from}-{to}) is empty and has no default");

        public int? OptInt(int from, int to, string field)
        {
            string s = Col(from, to);
            if (s.Length == 0) return null;
            if (!int.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
                throw Error($"{field} (columns {from}-{to}) is '{s}'; it must be a whole number");
            return v;
        }

        public int Int(int from, int to, string field)
            => OptInt(from, to, field) ?? throw Error($"{field} (columns {from}-{to}) is empty");
    }

    private static readonly string[] BlockOrder = { "##C", "##N", "##L", "##T", "##R", "##TT", "##E" };

    /// <summary>Read UCTE-DEF text, returning the model with the notes and the metadata the model has no place for.</summary>
    public static UcteParseResult ParseWithDiagnostics(string text, string? name = null, UcteReadOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Empty UCTE-DEF text.");
        options ??= new UcteReadOptions();
        if (options.FrequencyHz is double fGiven && !(fGiven > 0 && double.IsFinite(fGiven)))
            throw new ArgumentOutOfRangeException(nameof(options), options.FrequencyHz, "The nominal frequency must be a positive, finite number of hertz.");
        var notes = new List<string>();

        // ---- Split into blocks, in the format's order -------------------------------------------------------------
        var blocks = new Dictionary<string, List<Line>>(StringComparer.Ordinal);
        foreach (string b in BlockOrder) blocks[b] = new List<Line>();
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string? cur = null;
        int order = -1;
        string country = "";
        var countryOf = new Dictionary<int, string>();
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            if (raw.StartsWith("##", StringComparison.Ordinal))
            {
                string head = raw.TrimEnd();
                if (cur == "##N" && head.StartsWith("##Z", StringComparison.Ordinal))
                {
                    country = head[3..].Trim();
                    continue;
                }
                string key = head.StartsWith("##C", StringComparison.Ordinal) ? "##C" : head;
                int at = Array.IndexOf(BlockOrder, key);
                if (at < 0) throw new Line(i + 1, cur ?? "header", raw).Error($"'{head}' is not a UCTE-DEF block (##C, ##N, ##L, ##T, ##R, ##TT, ##E)");
                if (at <= order) throw new Line(i + 1, key, raw).Error($"block {key} comes after {BlockOrder[order]}; UCTE-DEF blocks come in the order {string.Join(", ", BlockOrder)}, each once");
                order = at;
                cur = key;
                continue;
            }
            if (raw.Trim().Length == 0) continue;
            if (cur is null) throw new Line(i + 1, "header", raw).Error("data before the first block header; a UCTE-DEF file starts with ##C (comments) or ##N (nodes)");
            if (cur == "##N") countryOf[i + 1] = country;
            blocks[cur].Add(new Line(i + 1, cur, raw));
        }
        if (blocks["##N"].Count == 0) throw new InvalidDataException("UCTE-DEF text has no node (##N) data.");
        string caseName = name ?? "ucte";

        // ---- Nodes ------------------------------------------------------------------------------------------------
        var busOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var baseKv = new List<double>();
        var meta = new Dictionary<int, UcteNodeMeta>();
        var nodeLines = new List<Line>();
        foreach (Line l in blocks["##N"])
        {
            if (l.Text.Length < 8) throw l.Error("a node record must start with its 8-character node code");
            string code = l.Text[..8];
            if (code.Trim().Length == 0) throw l.Error("the node code is blank");
            if (busOf.ContainsKey(code)) throw l.Error($"node '{code}' is defined twice");
            if (!NominalKv.TryGetValue(code[6], out double kv))
                throw l.Error($"node '{code}': voltage level code '{code[6]}' (the 7th character) is not one of 0-9");
            int status = l.Int(23, 23, "status");
            if (status is not (0 or 1)) throw l.Error($"status is {status}; a node is 0 (real) or 1 (equivalent)");
            busOf[code] = busOf.Count + 1;
            baseKv.Add(kv);
            nodeLines.Add(l);
            meta[busOf[code]] = new UcteNodeMeta(code, l.Col(10, 21), countryOf[l.Number], status == 1, code[0] == 'X');
        }

        var buses = new List<Bus>();
        var gens = new List<Generator>();
        var gs = new double[nodeLines.Count];
        var bs = new double[nodeLines.Count];
        int slack = 0, pv = 0, xnodes = 0, equivalents = 0;

        // ---- Lines ------------------------------------------------------------------------------------------------
        var branches = new List<Branch>();
        var keys = new List<UcteBranchKey>();
        var seen = new Dictionary<(string, string, string), int>();
        int off = 0, couplers = 0, zeroZ = 0;
        (int Bus, string Code) NodeOf(Line l, int from, string field)
        {
            string code = l.Text.Length >= from + 7 ? l.Text.Substring(from - 1, 8) : throw l.Error($"{field} (columns {from}-{from + 7}) is missing");
            return busOf.TryGetValue(code, out int b) ? (b, code) : throw l.Error($"{field} '{code}' is not a node of this file");
        }
        void Unique(Line l, string a, string b, string ord)
        {
            if (seen.TryGetValue((a, b, ord), out int prior) || seen.TryGetValue((b, a, ord), out prior))
                throw l.Error($"element {a} - {b} order '{ord}' is defined twice (also at line {prior})");
            seen[(a, b, ord)] = l.Number;
        }
        foreach (Line l in blocks["##L"])
        {
            (int i, string ci) = NodeOf(l, 1, "node 1");
            (int j, string cj) = NodeOf(l, 10, "node 2");
            if (i == j) throw l.Error($"both ends are node '{ci}'");
            string ord = l.Col(19, 19);
            Unique(l, ci, cj, ord);
            int status = l.Int(21, 21, "status");
            if (status is not (0 or 1 or 2 or 7 or 8 or 9)) throw l.Error($"status is {status}; a line is 0, 1, 2, 7, 8 or 9");
            if (status is 7 or 8 or 9) { off++; continue; }
            double kvi = baseKv[i - 1], kvj = baseKv[j - 1];
            if (kvi != kvj) throw l.Error($"a line joins nodes of different nominal voltages ({kvi} kV and {kvj} kV); a transformer belongs in ##T");
            double zb = kvi * kvi / BaseMva;
            double r = l.Real(23, 28, "R", 0), x = l.Real(30, 35, "X", 0), b = l.Real(37, 44, "B", 0);
            if (status == 0 && (r < 0 || x < 0)) throw l.Error("R and X of a real element must not be negative");
            double? limit = l.OptReal(46, 51, "current limit");
            string nm = l.Rest(53);
            if (status == 2)
            {
                couplers++;
                branches.Add(new Branch(i, j, 0, ZeroImpedanceXPu, Name: nm.Length > 0 ? nm : null));
                keys.Add(new UcteBranchKey("coupler", ci, cj, ord));
                continue;
            }
            double rpu = r / zb, xpu = x / zb;
            if (rpu == 0 && xpu == 0) { xpu = ZeroImpedanceXPu; zeroZ++; }
            branches.Add(new Branch(i, j, rpu, xpu, b * 1e-6 * zb,
                RateAMva: limit is double a && a > 0 ? Math.Sqrt(3.0) * kvi * a / 1000.0 : 0, Name: nm.Length > 0 ? nm : null));
            keys.Add(new UcteBranchKey("line", ci, cj, ord));
        }

        // ---- Regulation records, by transformer ---------------------------------------------------------------------
        var regulation = new Dictionary<(string, string, string), Line>();
        foreach (Line l in blocks["##R"])
        {
            (_, string ci) = NodeOf(l, 1, "node 1");
            (_, string cj) = NodeOf(l, 10, "node 2");
            string ord = l.Col(19, 19);
            if (!regulation.TryAdd((ci, cj, ord), l)) throw l.Error($"transformer {ci} - {cj} order '{ord}' has a second regulation record");
        }

        // ---- Transformers -------------------------------------------------------------------------------------------
        var tableUsers = new HashSet<(string, string, string)>();
        foreach (Line l in blocks["##TT"])
            if (l.Text.Length >= 19 && busOf.ContainsKey(l.Text[..8]) && busOf.ContainsKey(l.Text.Substring(9, 8)))
                tableUsers.Add((l.Text[..8], l.Text.Substring(9, 8), l.Col(19, 19)));
        int xfOff = 0, regulated = 0, tablesUsed = 0, tablesFirst = 0;
        var used = new HashSet<(string, string, string)>();
        foreach (Line l in blocks["##T"])
        {
            (int n1, string c1) = NodeOf(l, 1, "node 1");
            (int n2, string c2) = NodeOf(l, 10, "node 2");
            if (n1 == n2) throw l.Error($"both windings are on node '{c1}'");
            string ord = l.Col(19, 19);
            Unique(l, c1, c2, ord);
            int status = l.Int(21, 21, "status");
            if (status is not (0 or 1 or 8 or 9)) throw l.Error($"status is {status}; a transformer is 0, 1, 8 or 9");
            used.Add((c1, c2, ord));
            if (status is 8 or 9) { xfOff++; continue; }
            double u1 = l.Real(23, 27, "rated voltage 1"), u2 = l.Real(29, 33, "rated voltage 2");
            if (!(u1 > 0) || !(u2 > 0)) throw l.Error($"rated voltages must be above 0 kV (U1 {u1}, U2 {u2})");
            double r = l.Real(41, 46, "R", 0), x = l.Real(48, 53, "X", 0), b = l.Real(55, 62, "B", 0), g = l.Real(64, 69, "G", 0);
            if (status == 0 && (r < 0 || x < 0)) throw l.Error("R and X of a real element must not be negative");
            if (r == 0 && x == 0) throw l.Error("R and X are both 0; a transformer must have an impedance");
            double? limit = l.OptReal(71, 76, "current limit");
            string nm = l.Rest(78);
            if (tableUsers.Contains((c1, c2, ord))) { tablesUsed++; if (tablesFirst == 0) tablesFirst = l.Number; }

            double kv1 = baseKv[n1 - 1], kv2 = baseKv[n2 - 1];
            double zb1 = kv1 * kv1 / BaseMva;
            double ratio = 1.0, angle = 0.0;
            if (regulation.TryGetValue((c1, c2, ord), out Line? reg))
            {
                (ratio, angle) = Regulation(reg);
                if (ratio != 1.0 || angle != 0.0) regulated++;
            }
            double t = (u2 * ratio / kv2) / (u1 / kv1);
            if (g != 0 || b != 0) { gs[n1 - 1] += g * 1e-6 * kv1 * kv1; bs[n1 - 1] += b * 1e-6 * kv1 * kv1; }
            branches.Add(new Branch(n2, n1, r / zb1, x / zb1, 0, TapRatio: t, PhaseShiftDeg: angle,
                RateAMva: limit is double a && a > 0 ? Math.Sqrt(3.0) * kv1 * a / 1000.0 : 0, IsTransformer: true,
                Name: nm.Length > 0 ? nm : null));
            keys.Add(new UcteBranchKey("transformer", c1, c2, ord));
        }
        foreach ((string, string, string) k in regulation.Keys)
            if (!used.Contains(k)) throw regulation[k].Error($"the regulation record names transformer {k.Item1} - {k.Item2} order '{k.Item3}', which ##T does not define");
        if (tablesUsed > 0)
        {
            if (!options.AllowUnmodelled)
                throw new InvalidDataException($"UCTE-DEF case '{caseName}' has {tablesUsed} transformer(s) in operation with special transformer tables (##TT, first at line {tablesFirst}); GridSim does not model tap-dependent impedances, so its power flow would differ from the file's. Read it with unmodelled devices allowed (gridsim: --allow-unmodelled) to use the transformers' own impedances.");
            notes.Add($"{tablesUsed} transformer(s) have special transformer tables (##TT) that are read past and NOT modelled (unmodelled devices allowed); their own impedances are used.");
        }

        // ---- Buses and generators -----------------------------------------------------------------------------------
        for (int k = 0; k < nodeLines.Count; k++)
        {
            Line l = nodeLines[k];
            int id = k + 1;
            int type = l.Int(25, 25, "node type");
            if (type == 1) throw l.Error("node type 1 (Q and angle held) has no GridSim equivalent; only 0 (PQ), 2 (PV) and 3 (slack) are read");
            if (type is not (0 or 2 or 3)) throw l.Error($"node type is {type}; it must be 0, 1, 2 or 3");
            double kv = baseKv[k];
            double pl = l.Real(34, 40, "active load", 0), ql = l.Real(42, 48, "reactive load", 0);
            double? pg = l.OptReal(50, 56, "active generation"), qg = l.OptReal(58, 64, "reactive generation");
            double? pMinW = l.OptReal(66, 72, "minimum permissible generation"), pMaxW = l.OptReal(74, 80, "maximum permissible generation");
            double? qMinW = l.OptReal(82, 88, "minimum permissible reactive generation"), qMaxW = l.OptReal(90, 96, "maximum permissible reactive generation");
            double? vref = l.OptReal(27, 32, "voltage reference");
            if (meta[id].XNode) xnodes++;
            if (meta[id].Equivalent) equivalents++;
            bool hasGen = type != 0 || (pg is double p0 && p0 != 0) || (qg is double q0 && q0 != 0);
            BusType bt = type == 3 ? BusType.Slack : type == 2 ? BusType.Pv : BusType.Pq;
            double vm = 1.0;
            if (type != 0)
            {
                if (vref is not double v || !(v > 0)) throw l.Error($"a type {type} node holds its voltage, so its voltage reference must be above 0 kV");
                vm = v / kv;
            }
            if (type == 3) slack++;
            if (type == 2) pv++;
            if (hasGen)
            {
                // Load sign: production is negative in the file. The limits follow PowSyBl: minP = -(minimum), maxP = -(maximum).
                // 0.0 - v rather than -v, so a written 0 is +0 in the model, not -0.
                double pmin = pMinW is double a ? 0.0 - a : double.NegativeInfinity, pmax = pMaxW is double c ? 0.0 - c : double.PositiveInfinity;
                double qmin = qMinW is double e ? 0.0 - e : double.NegativeInfinity, qmax = qMaxW is double f ? 0.0 - f : double.PositiveInfinity;
                // Bug hunt 2026-10-29, finding 54 (decided, documented): limits the wrong way round are refused, not swapped.
                // Some TSO exports do invert the two (powsybl swaps them with a warning), but which value was meant as
                // the minimum is a guess, so the case is refused naming both values and the remedy.
                if (pmin > pmax) throw l.Error($"the permissible generation gives a minimum {pmin} MW above the maximum {pmax} MW (production is negative in UCTE-DEF); some exports invert the two columns, so swap them in the file if that is what was meant");
                if (qmin > qmax) throw l.Error($"the permissible reactive generation gives a minimum {qmin} MVAr above the maximum {qmax} MVAr (production is negative in UCTE-DEF)");
                gens.Add(new Generator(id, Pg: 0.0 - (pg ?? 0), Qg: 0.0 - (qg ?? 0), Vg: vm, Qmin: qmin, Qmax: qmax, Pmin: pmin, Pmax: pmax,
                    MBase: BaseMva, Name: meta[id].Code.Trim()));
            }
            buses.Add(new Bus(id, bt, Pd: pl, Qd: ql, Gs: gs[k], Bs: bs[k], BaseKv: kv, Vm: vm, VaDeg: 0,
                Name: meta[id].Code.Trim()));
        }
        if (slack == 0) throw new InvalidDataException($"UCTE-DEF case '{caseName}' has no slack (type 3) node; the power flow would be singular.");
        if (slack > 1) notes.Add($"{slack} slack (type 3) nodes; treating as a multi-island system (the format allows one).");
        if (xnodes > 0) notes.Add($"{xnodes} X-node(s) (boundary nodes) are buses with their written load and generation as fixed injections.");
        if (equivalents > 0) notes.Add($"{equivalents} equivalent node(s) (status 1) are read as ordinary nodes.");
        if (off > 0 || xfOff > 0) notes.Add($"skipped {off} line(s) or coupler(s) and {xfOff} transformer(s) out of operation.");
        if (couplers > 0) notes.Add($"{couplers} busbar coupler(s) in operation are branches of X = {ZeroImpedanceXPu.ToString("R", CultureInfo.InvariantCulture)} pu.");
        if (zeroZ > 0) notes.Add($"{zeroZ} line(s) have R = X = 0; given X = {ZeroImpedanceXPu.ToString("R", CultureInfo.InvariantCulture)} pu.");
        if (regulated > 0) notes.Add($"{regulated} transformer(s) are at a tap off neutral (##R); the taps stay at n', and any regulated voltage or flow target is not applied.");
        if (blocks["##E"].Count > 0) notes.Add($"{blocks["##E"].Count} exchange schedule record(s) (##E) read past (scheduling data, not network).");

        double freq = options.FrequencyHz ?? AssumedFrequencyHz;
        if (options.FrequencyHz is null) notes.Add(FormattableString.Invariant($"UCTE-DEF carries no system frequency; assumed {AssumedFrequencyHz:0.##} Hz (the European interconnected system)."));

        var model = new GridModel
        {
            Name = caseName,
            BaseMva = BaseMva,
            FrequencyHz = freq,
            Buses = buses,
            Generators = gens,
            Branches = branches,
        };
        var errors = model.Validate().Where(d => d.Severity == ValidationSeverity.Error).Select(d => d.Message).ToList();
        if (errors.Count > 0)
            throw new InvalidDataException($"UCTE-DEF case '{caseName}' is invalid:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", errors));
        return new UcteParseResult(model, notes) { Nodes = meta, BranchKeys = keys };
    }

    /// <summary>A regulation record's ratio factor on the regulated winding and phase shift (degrees).</summary>
    private static (double Ratio, double AngleDeg) Regulation(Line l)
    {
        double? du1 = l.OptReal(21, 25, "phase regulation du");
        int? n1 = l.OptInt(27, 28, "phase regulation n"), p1 = l.OptInt(30, 32, "phase regulation n'");
        double? du2 = l.OptReal(40, 44, "angle regulation du"), theta = l.OptReal(46, 50, "angle regulation theta");
        int? n2 = l.OptInt(52, 53, "angle regulation n"), p2 = l.OptInt(55, 57, "angle regulation n'");
        string type = l.Col(65, 68);

        bool phase = du1 is not null || n1 is not null || p1 is not null;
        bool angle = du2 is not null || theta is not null || n2 is not null || p2 is not null;
        if (!phase && !angle) throw l.Error("the regulation record gives neither phase nor angle regulation");
        double ratio = 1.0, deg = 0.0;
        if (phase)
        {
            if (du1 is null || n1 is null || p1 is null) throw l.Error("phase regulation needs du, n and n' (columns 21-25, 27-28, 30-32)");
            if (n1 < 0 || Math.Abs(p1.Value) > n1) throw l.Error($"phase regulation tap n' {p1} is outside -n..n (n {n1})");
            ratio = 1.0 + p1.Value * du1.Value / 100.0;
            if (!(ratio > 0)) throw l.Error($"phase regulation gives a ratio factor of {ratio}; it must be above 0");
        }
        if (angle)
        {
            if (du2 is null || theta is null || n2 is null || p2 is null) throw l.Error("angle regulation needs du, theta, n and n' (columns 40-44, 46-50, 52-53, 55-57)");
            if (n2 < 0 || Math.Abs(p2.Value) > n2) throw l.Error($"angle regulation tap n' {p2} is outside -n..n (n {n2})");
            if (type.Length > 0 && type is not ("ASYM" or "SYMM")) throw l.Error($"angle regulation type is '{type}'; it must be ASYM or SYMM");
            if (phase && p1 != 0 && p2 != 0)
                throw l.Error("both phase and angle regulation are stepped off neutral; GridSim's transformer cannot combine the two");
            double step = p2.Value * du2.Value / 100.0, rad = theta.Value * Math.PI / 180.0;
            double dx = step * DetMath.Cos(rad), dy = step * DetMath.Sin(rad);
            if (type == "SYMM")
                deg = 2.0 * DetMath.Atan2(dy, 2.0 * (1.0 + dx)) * 180.0 / Math.PI;
            else if (p2 != 0)
            {
                ratio *= Math.Sqrt(dy * dy + (1.0 + dx) * (1.0 + dx));
                deg = DetMath.Atan2(dy, 1.0 + dx) * 180.0 / Math.PI;
            }
        }
        return (ratio, deg);
    }
}
