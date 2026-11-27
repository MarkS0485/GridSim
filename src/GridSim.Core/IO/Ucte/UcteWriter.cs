using System.Globalization;
using System.Text;
using GridSim.Core.Model;
using F = GridSim.Core.IO.Ucte.UcteColumns;

namespace GridSim.Core.IO.Ucte;

/// <summary>How <see cref="UcteWriter"/> writes a model.</summary>
public sealed record UcteWriteOptions
{
    /// <summary>The UCTE-DEF read the model came from, or null. Given, the writer keeps its node codes, geographical
    /// names, country blocks, node status, busbar couplers, order codes, each branch's status code (real, equivalent or
    /// coupler, and out of operation when the model has the branch out of service) and the out-of-operation records the
    /// reader skipped (written back as the file had them), and, for each transformer the model still has
    /// as read (same ends, same ratio and shift), the file's rated voltages, nominal power and regulation record; and the
    /// file's G and B of the transformers at a node whose shunt is as read. So a UCTE case is written back with its own
    /// names and reads back to the same model. The model must then have the read's buses (same ids, same order) and
    /// branches (same count).</summary>
    public UcteParseResult? Source { get; init; }

    /// <summary>The country of the node codes the writer synthesises (for buses with no source node): one of the
    /// two-letter names of <see cref="UcteWriter.CountryCodes"/>. It is written as the ##Z block and as the first
    /// character of each synthesised code. Required when a code is synthesised. X (UCTE's code for X-nodes, the boundary
    /// nodes) is not one: a synthesised node is an ordinary bus.</summary>
    public string? Country { get; init; }
}

/// <summary>What <see cref="UcteWriter"/> wrote.</summary>
/// <param name="Text">The UCTE-DEF text.</param>
/// <param name="Notes">What the format could not carry, or carries only to its column precision.</param>
/// <param name="NodeCodes">The 8-character node code of each model bus, in model order (the read-back numbers the buses
/// 1, 2, ... in this order).</param>
/// <param name="BranchOrder">The model branch index of each branch the reader rebuilds, in its order (lines, then
/// transformers; out-of-service branches are written out of operation, status 8, 9 or 7, which the reader skips).</param>
public sealed record UcteWriteResult(string Text, IReadOnlyList<string> Notes, IReadOnlyList<string> NodeCodes, IReadOnlyList<int> BranchOrder);

/// <summary>
/// Writes a <see cref="GridModel"/> as UCTE-DEF text (RC5 WP34): the format of "UCTE data exchange format for load flow and
/// three phase short circuit studies", version 02 (2007-05-01), the public UCTE (now ENTSO-E) description, in the columns
/// <see cref="UcteParser"/> reads (<see cref="UcteColumns"/>).
/// </summary>
/// <remarks>
/// <para><b>Blocks.</b> ##C (the version line 2007.05.01 and one comment), ##N (nodes, under ##Z country headers), ##L
/// (lines and busbar couplers), ##T (transformers) and ##R (regulation, for a transformer off nominal ratio or with a
/// phase shift). No ##TT tables (GridSim models none) and no ##E exchange schedules (GridSim carries none).</para>
/// <para><b>Node codes.</b> From the source read when one is given, so an X-node (a boundary node) of the file stays one.
/// Otherwise synthesised as the country's code character (<see cref="UcteWriteOptions.Country"/>), the bus id in five
/// digits, the voltage level code and busbar 1 (<c>F</c>, <c>00012</c>, <c>5</c>, <c>1</c> for bus 12 at 110 kV in
/// France), under that country's ##Z block. A synthesised code never starts with X: UCTE reserves X for X-nodes, which
/// other UCTE tools take as boundary points (PowSyBl, for one, turns them into dangling lines), and a GridSim bus with no
/// source node is an ordinary bus. Every character UCTE uses for a country is a country, so the writer cannot choose one
/// itself: with no country given, a synthesised code is refused, naming the option. The voltage level code is the code's
/// 7th character, as the reader takes it; the WP34 specification wrote the code as X, level, id, suffix, which would put a
/// digit of the id where the reader looks for the level, so the reader's layout is followed. A bus id above 99999 is
/// refused, as is a bus whose base voltage is not one of the ten UCTE levels (0 kV included).</para>
/// <para><b>Status codes.</b> A branch in service is written with its source record's status: 0 (real element), 1
/// (equivalent element) or 2 (busbar coupler); with no source read, 0, except that a branch with a negative R or X is
/// written as an equivalent element (1), the only kind UCTE-DEF lets carry a negative impedance, and named in the
/// notes. A branch out of service is written out of
/// operation with the matching code: 8 for a real element, 9 for an equivalent one, 7 for a busbar coupler. The reader
/// reads an element of status 1 as it reads one of status 0, except that it accepts a negative R or X for it, so the
/// status is kept and such an element reads back. The records the source read skipped (status 7, 8 or 9) are written
/// back as the file had them, each where it stood among the branches, with a skipped transformer's ##R record; the reader
/// skips them again, so they change no number.</para>
/// <para><b>Units and signs.</b> The inverse of the reader's: Z ohm = Z pu x Zbase, B microsiemens = B pu / Zbase / 1e-6,
/// current limit = rating / (sqrt(3) x U), generation and its limits with the load sign (the negative of the model's),
/// voltage reference = set point x base kV. A transformer is written from its to bus (node 1, the non-regulated winding,
/// which carries the impedance) to its from bus (node 2). A transformer the source read gave, still with the read's
/// ends, ratio and shift, is written with the file's own rated voltages, nominal power and ##R record, as text, so the
/// reader makes the same ratio and shift of them (rated voltages off the bus levels and multi-step taps included). Any
/// other transformer is written at the rated voltages of the two buses' levels, with nominal power = rating A (the
/// system base when rating A is 0); a ratio other than 1 is a ##R phase regulation of one step, du = 100 (t - 1); a
/// phase shift is a ##R angle regulation of one step (SYMM at 90 degrees when the ratio is 1, otherwise ASYM with du and
/// theta the size and angle of t e^(j phi) - 1). A bus shunt is written as the G and B of the first in-service
/// transformer whose node 1 is that bus (the reader puts a transformer's G and B at node 1); a bus shunt with no such
/// transformer is refused, since UCTE-DEF has no node shunt. With a source read, a bus whose shunt is as read and whose
/// node 1 transformers are all in service and as read keeps the file's G and B on each of them, so the reader adds the
/// same terms in the same order.</para>
/// <para><b>Precision.</b> UCTE-DEF numbers are fixed-width text: each is written with as many decimals as its columns
/// hold, so it carries an absolute error of at most half a unit in its last written decimal (for R, six columns: 0.00005
/// ohm below 10 ohm). A UCTE case read and written again with its source read gives its reader the same model bit for
/// bit, given that the file writes its numbers as plain decimals (no exponent, a digit before the point): each number
/// of a node, line or transformer impedance is written back with at least the decimals it came with, and the fields
/// the model folds into a transformer's ratio or a bus shunt are written back as the file's own text. Exceptions,
/// which come back to the column precision: a number the file wrote in another form (1e-3, .5), and a transformer or
/// bus shunt changed after the read, which is written as from another format. A case from elsewhere (MATPOWER, RAW)
/// comes back to the column precision, not bit for bit.</para>
/// <para><b>Refused</b>, naming the element: HVDC links, FACTS devices, static var compensators, induction machines, a
/// voltage-dependent (ZIP) load, a branch with R = X = 0, a second in-service unit at one bus, a PV or slack bus with no
/// in-service unit, a number too large for its columns. Listed in the notes: what UCTE-DEF has no field for (start
/// voltages of PQ buses, bus voltage limits, ratings B and C, unit data beyond the power-flow record, tap changer and
/// flow control targets, sequence data, layout).</para>
/// </remarks>
public static class UcteWriter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Write <paramref name="model"/> as UCTE-DEF text.</summary>
    public static UcteWriteResult Write(GridModel model, UcteWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        options ??= new UcteWriteOptions();
        UcteParseResult? source = options.Source;
        var notes = new List<string>();

        // ---- What UCTE-DEF cannot hold ---------------------------------------------------------------------------
        static void Refuse(int count, string what)
        {
            if (count > 0) throw new InvalidDataException($"UCTE-DEF writer: the model has {count} {what}, which UCTE-DEF cannot hold; the written case's power flow would differ.");
        }
        Refuse(model.LccLinks.Count, "two-terminal LCC DC link(s)");
        Refuse(model.VscLinks.Count, "VSC DC link(s)");
        Refuse(model.MtdcLinks.Count, "multi-terminal DC link(s)");
        Refuse(model.FactsDevices.Count, "FACTS device(s)");
        Refuse(model.StaticVarCompensators.Count, "static var compensator(s)");
        Refuse(model.InductionMachines.Count, "induction machine(s)");
        foreach (Bus b in model.Buses)
            if (b.Zip is { VariesWithVoltage: true })
                throw new InvalidDataException($"UCTE-DEF writer: bus {b.Id} has a voltage-dependent (ZIP) load; UCTE-DEF loads are constant power.");
        if (source is not null)
        {
            if (source.Model.Buses.Count != model.Buses.Count || Enumerable.Range(0, model.Buses.Count).Any(i => source.Model.Buses[i].Id != model.Buses[i].Id)
                || source.BranchKeys.Count != model.Branches.Count)
                throw new InvalidDataException("UCTE-DEF writer: the source read does not match the model (its buses, in order, and its branch count must be the model's); write without the source to synthesise node codes.");
            if (source.BranchStatus.Count != source.BranchKeys.Count)
                throw new InvalidDataException($"UCTE-DEF writer: the source read has {source.BranchStatus.Count} branch status code(s) for {source.BranchKeys.Count} branch(es); give a read made by UcteParser.");
            foreach (int st in source.BranchStatus)
                if (st is not (0 or 1 or 2))
                    throw new InvalidDataException(FormattableString.Invariant($"UCTE-DEF writer: the source read has a branch of status {st}; a branch in the model is 0 (real), 1 (equivalent) or 2 (busbar coupler)."));
        }
        if (options.Country is not null && !CountryCodes.ContainsKey(options.Country))
            throw new ArgumentOutOfRangeException(nameof(options), options.Country, CountryRefusal(options.Country));

        // ---- Node codes ------------------------------------------------------------------------------------------
        int n = model.Buses.Count;
        var codes = new string[n];
        var kv = new double[n];
        var position = new Dictionary<int, int>();
        var synthesise = new List<(int Index, char Level)>();
        for (int i = 0; i < n; i++)
        {
            Bus b = model.Buses[i];
            if (!position.TryAdd(b.Id, i)) throw new InvalidDataException($"UCTE-DEF writer: bus {b.Id} appears twice.");
            char level = LevelCode(b.BaseKv, $"bus {b.Id}");
            kv[i] = b.BaseKv;
            if (source is not null && source.Nodes.TryGetValue(b.Id, out UcteNodeMeta? meta))
            {
                if (meta.Code.Length != 8 || meta.Code[6] != level)
                    throw new InvalidDataException($"UCTE-DEF writer: bus {b.Id}'s source node code '{meta.Code}' does not carry its base voltage's level code '{level}'.");
                codes[i] = meta.Code;
            }
            else
            {
                if (b.Id < 0 || b.Id > 99999)
                    throw new InvalidDataException($"UCTE-DEF writer: bus {b.Id} cannot be given a synthesised node code (country, five digits, level, busbar): ids 0 to 99999 only.");
                codes[i] = "";   // synthesised below, once the model has passed every other check
                synthesise.Add((i, level));
            }
        }
        if (Enumerable.Range(0, n).Any(i => model.Buses[i].Id != i + 1)) notes.Add("the read-back numbers the buses 1, 2, ... in the order written (see NodeCodes).");
        if (model.BaseMva != UcteParser.BaseMva) notes.Add(FormattableString.Invariant($"the model is on a {model.BaseMva} MVA base; UCTE-DEF is in physical units and the read-back is on {UcteParser.BaseMva} MVA."));

        // ---- Branches: which are lines, which transformers ---------------------------------------------------------
        var lines = new List<int>();
        var xfmrs = new List<int>();
        for (int k = 0; k < model.Branches.Count; k++)
        {
            Branch br = model.Branches[k];
            if (!position.TryGetValue(br.From, out int fromPos) || !position.TryGetValue(br.To, out int toPos))
                throw new InvalidDataException($"UCTE-DEF writer: branch {k + 1} ({br.From}-{br.To}) names a bus the model does not have.");
            if (br.R == 0 && br.X == 0)
                throw new InvalidDataException($"UCTE-DEF writer: branch {k + 1} ({br.From}-{br.To}) has R = X = 0; the reader would give it X = {UcteParser.ZeroImpedanceXPu.ToString("R", Inv)} pu, so it is not written.");
            double tap = br.TapRatio == 0 ? 1.0 : br.TapRatio;
            bool sameLevel = kv[fromPos] == kv[toPos];
            bool coupler = source is not null && source.BranchKeys[k].Kind == "coupler";
            if (coupler || (!br.IsTransformer && tap == 1.0 && br.PhaseShiftDeg == 0.0 && sameLevel)) lines.Add(k);
            else
            {
                if (br.B != 0)
                    throw new InvalidDataException($"UCTE-DEF writer: branch {k + 1} ({br.From}-{br.To}) is written as a transformer (it is one, or has a ratio, a phase shift or ends at two voltage levels) and has line charging B = {br.B.ToString("R", Inv)}; a UCTE transformer's B is a shunt at node 1, not line charging.");
                xfmrs.Add(k);
            }
        }

        // A transformer the source read gave, with the read's ends, ratio and shift: written with the file's own text.
        var kept = new Dictionary<int, UcteTransformerRecord>();   // transformer branch index -> its source record
        if (source is not null)
            foreach (int k in xfmrs)
            {
                Branch br = model.Branches[k];
                UcteBranchKey key = source.BranchKeys[k];
                if (source.Transformers.TryGetValue(k, out UcteTransformerRecord? rec)
                    && source.Nodes.TryGetValue(br.To, out UcteNodeMeta? end1) && end1.Code == key.Node1
                    && source.Nodes.TryGetValue(br.From, out UcteNodeMeta? end2) && end2.Code == key.Node2
                    && br.TapRatio == rec.TapRatio && br.PhaseShiftDeg == rec.PhaseShiftDeg)
                    kept[k] = rec;
            }

        // Bus shunts ride on the first in-service transformer whose node 1 (the model's to bus) is the bus.
        var shuntCarrier = new Dictionary<int, int>();   // bus index -> transformer branch index
        foreach (int k in xfmrs)
        {
            Branch br = model.Branches[k];
            if (br.InService) shuntCarrier.TryAdd(position[br.To], k);
        }
        // With a source read: a bus whose shunt is as read, and whose node 1 transformers are all in service and kept,
        // gets the file's own G and B on each of them (the reader adds them up in file order, as it did).
        var keptShunt = new HashSet<int>();
        if (source is not null)
            for (int i = 0; i < n; i++)
            {
                Bus b = model.Buses[i], read = source.Model.Buses[i];
                List<int> at = xfmrs.Where(k => position[model.Branches[k].To] == i).ToList();
                if (at.Count > 0 && b.Gs == read.Gs && b.Bs == read.Bs && at.All(k => model.Branches[k].InService && kept.ContainsKey(k)))
                    keptShunt.Add(i);
            }
        for (int i = 0; i < n; i++)
        {
            Bus b = model.Buses[i];
            if ((b.Gs != 0 || b.Bs != 0) && !shuntCarrier.ContainsKey(i))
                throw new InvalidDataException($"UCTE-DEF writer: bus {b.Id} has a shunt (Gs {b.Gs.ToString("R", Inv)} MW, Bs {b.Bs.ToString("R", Inv)} Mvar) and is node 1 of no in-service transformer; UCTE-DEF has no node shunt, so it cannot be written.");
        }
        int shuntsCarried = Enumerable.Range(0, n).Count(i => model.Buses[i].Gs != 0 || model.Buses[i].Bs != 0);
        if (shuntsCarried > 0) notes.Add($"{shuntsCarried} bus shunt(s) written as the G and B of a transformer whose node 1 is the bus (UCTE-DEF has no node shunt).");

        // ---- Synthesised node codes (after every other check, so a model refused for another reason says so first) --
        if (synthesise.Count > 0)
        {
            if (options.Country is null)
                throw new InvalidDataException($"UCTE-DEF writer: {synthesise.Count} bus(es) (bus {model.Buses[synthesise[0].Index].Id} first) have no source node code, so one is synthesised, and a UCTE node code starts with its country's code; give the country (UcteWriteOptions.Country; gridsim convert --ucte-country), one of {string.Join(", ", CountryCodes.Keys)}.");
            string first = new string(CountryCodes[options.Country], 1);
            foreach ((int idx, char lv) in synthesise) codes[idx] = first + model.Buses[idx].Id.ToString("D5", Inv) + lv + "1";
            notes.Add($"{synthesise.Count} node code(s) synthesised as the country code '{first}' ({options.Country}) + the bus id in five digits + the voltage level code + busbar 1, under ##Z{options.Country}; they are ordinary nodes, not X-nodes.");
        }
        if (codes.Distinct(StringComparer.Ordinal).Count() != n) throw new InvalidDataException("UCTE-DEF writer: two buses have the same node code.");

        // ---- Text ------------------------------------------------------------------------------------------------
        var sb = new StringBuilder();
        sb.Append("##C 2007.05.01\n");
        sb.Append("Written by GridSim (UcteWriter): ").Append(OneLine(model.Name)).Append('\n');
        sb.Append("##N\n");

        // ---- Nodes -----------------------------------------------------------------------------------------------
        var gensAt = model.AllGenerators.GroupBy(u => u.Bus).ToDictionary(grp => grp.Key, grp => grp.ToList());
        int outGens = model.AllGenerators.Count(u => !u.InService), pqStart = 0, pqUnitVg = 0, genExtras = 0, truncated = 0, limits = 0, pvStart = 0;
        string? country = null;
        for (int i = 0; i < n; i++)
        {
            Bus b = model.Buses[i];
            string who = $"bus {b.Id}";
            string zone = source is not null && source.Nodes.TryGetValue(b.Id, out UcteNodeMeta? m0) ? m0.Country : options.Country!;
            if (zone != country)
            {
                if (zone.Length > 0) sb.Append("##Z").Append(zone).Append('\n');
                else if (country is not null) throw new InvalidDataException($"UCTE-DEF writer: {who}'s node has no country block but follows one; a ##Z block cannot be closed.");
                country = zone;
            }
            List<Generator> units = gensAt.TryGetValue(b.Id, out List<Generator>? all) ? all.Where(u => u.InService).ToList() : new List<Generator>();
            if (units.Count > 1)
                throw new InvalidDataException($"UCTE-DEF writer: {who} has {units.Count} in-service units; a UCTE-DEF node holds one generation.");
            Generator? g = units.Count == 1 ? units[0] : null;
            int type = b.Type switch { BusType.Slack => 3, BusType.Pv => 2, _ => 0 };
            if (type != 0 && g is null) throw new InvalidDataException($"UCTE-DEF writer: {who} is a {(type == 3 ? "slack" : "PV")} bus with no in-service unit.");
            if (b.VaDeg != 0 || (type == 0 && b.Vm != 1.0)) pqStart++;
            if (type == 0 && g is not null && g.Vg != 1.0) pqUnitVg++;
            if (type != 0 && g is not null && b.Vm != g.Vg) pvStart++;
            if (b.VmaxPu is not null || b.VminPu is not null) limits++;

            var row = new Row();
            row.Left(F.NodeCode, codes[i]);
            string name = source is not null && source.Nodes.TryGetValue(b.Id, out UcteNodeMeta? m1) ? m1.GeographicalName : OneLine(b.Name ?? "");
            if (name.Length > F.NodeName.Width) { name = name[..F.NodeName.Width]; truncated++; }
            row.Left(F.NodeName, name);
            row.Left(F.NodeStatus, source is not null && source.Nodes.TryGetValue(b.Id, out UcteNodeMeta? m2) && m2.Equivalent ? "1" : "0");
            row.Left(F.NodeType, type.ToString(Inv));
            double level = kv[i];
            if (g is not null && type != 0) row.Num(F.NodeVoltage, g.Vg * level, $"{who} voltage reference (kV)");
            row.Num(F.NodeLoadP, b.Pd, $"{who} active load (MW)");
            row.Num(F.NodeLoadQ, b.Qd, $"{who} reactive load (Mvar)");
            if (g is not null)
            {
                if (type == 0 && g.Pg == 0 && g.Qg == 0)
                    throw new InvalidDataException($"UCTE-DEF writer: {who} is a PQ bus whose unit has zero output; the reader makes a unit at a PQ node only from non-zero generation, so it would be lost.");
                // Load sign: production is negative. 0.0 - x keeps a zero +0.
                row.Num(F.NodeGenP, 0.0 - g.Pg, $"{who} active generation (MW)");
                row.Num(F.NodeGenQ, 0.0 - g.Qg, $"{who} reactive generation (Mvar)");
                if (!double.IsNegativeInfinity(g.Pmin)) row.Num(F.NodeGenPMin, 0.0 - g.Pmin, $"{who} minimum permissible generation (MW)");
                if (!double.IsPositiveInfinity(g.Pmax)) row.Num(F.NodeGenPMax, 0.0 - g.Pmax, $"{who} maximum permissible generation (MW)");
                if (!double.IsNegativeInfinity(g.Qmin)) row.Num(F.NodeGenQMin, 0.0 - g.Qmin, $"{who} minimum permissible reactive generation (Mvar)");
                if (!double.IsPositiveInfinity(g.Qmax)) row.Num(F.NodeGenQMax, 0.0 - g.Qmax, $"{who} maximum permissible reactive generation (Mvar)");
                if (g.MBase != UcteParser.BaseMva || g.H != 0 || g.Type.Length > 0 || g.SubtransientXdPu > 0 || g.NegativeSeqXPu != 0 || g.Cost is not null
                    || g.ConverterInterfaced is not null || g.UnitTransformerXPu != 0 || g.GridForming || g.PowerFlowOnly || g.DynamicsClass is not null)
                    genExtras++;
            }
            sb.Append(row).Append('\n');
        }
        if (outGens > 0) notes.Add($"{outGens} out-of-service unit(s) not written: a UCTE-DEF node has no unit status, and the power flow does not use them.");
        if (pqStart > 0) notes.Add($"{pqStart} bus(es) have a start angle other than 0, or (PQ buses) a start voltage other than 1 pu; UCTE-DEF has no field for them, so the read-back starts PQ buses at 1 pu and every bus at 0 degrees.");
        if (pqUnitVg > 0) notes.Add($"{pqUnitVg} unit(s) at PQ buses have a voltage set point other than 1 pu; a PQ node carries no voltage reference, so the read-back gives them 1 pu.");
        if (pvStart > 0) notes.Add($"{pvStart} PV or slack bus(es) start at a voltage other than their set point; the read-back starts at the set point.");
        if (limits > 0) notes.Add($"{limits} bus(es) carry voltage limits, which UCTE-DEF cannot hold; not written.");
        if (truncated > 0) notes.Add($"{truncated} bus name(s) cut to the 12 columns of the geographical name; the read-back names buses by node code.");
        if (genExtras > 0) notes.Add($"{genExtras} unit(s) carry a machine base, dynamics, fault, converter or cost data that UCTE-DEF cannot hold; the read-back gives them a 100 MVA base and none of the rest.");

        // ---- Lines -----------------------------------------------------------------------------------------------
        var orders = new Dictionary<(string, string), int>();
        string Order(int k, string a, string b)
        {
            if (source is not null) return source.BranchKeys[k].Order;
            (string, string) key = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
            int c = orders.TryGetValue(key, out int prior) ? prior + 1 : 1;
            orders[key] = c;
            const string symbols = "123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            if (c > symbols.Length) throw new InvalidDataException($"UCTE-DEF writer: more than {symbols.Length} elements join nodes {a} and {b}; the order code is one character.");
            return symbols[(c - 1)..c];
        }
        int open = 0, ratesBc = 0, branchExtras = 0;
        var order = new List<int>();
        // The status code of branch k: its source record's (0, 1 or 2; 0 with no source), or, out of service, the matching
        // out-of-operation code (8 real, 9 equivalent, 7 busbar coupler). A real element with a negative R or X is written
        // as an equivalent one (1, or 9 out of service): in UCTE-DEF only an equivalent element may carry a negative
        // impedance, and the reader refuses a real one; each such branch is named in the notes.
        var negativeAsEquivalent = new List<string>();
        string StatusOf(int k)
        {
            Branch b = model.Branches[k];
            int code = source is null ? 0 : source.BranchStatus[k];
            if (code == 0 && (b.R < 0 || b.X < 0))
            {
                code = 1;
                negativeAsEquivalent.Add(FormattableString.Invariant($"branch {k + 1} ({b.From}-{b.To}, R {b.R:R} pu, X {b.X:R} pu)"));
            }
            int written = b.InService ? code : code switch { 1 => 9, 2 => 7, _ => 8 };
            return written.ToString(Inv);
        }
        // The source's out-of-operation records, written back as the file had them, each before the model branch it stood
        // before (the rest at the end of its block).
        List<UcteSkippedRecord> skippedLines = source?.Skipped.Where(r => r.Block == "##L").ToList() ?? new List<UcteSkippedRecord>();
        List<UcteSkippedRecord> skippedXfmrs = source?.Skipped.Where(r => r.Block == "##T").ToList() ?? new List<UcteSkippedRecord>();
        int nextLine = 0, nextXfmr = 0;
        var regulation = new List<string>();
        void SkippedLinesUpTo(int k)
        {
            while (nextLine < skippedLines.Count && skippedLines[nextLine].Before <= k) sb.Append(skippedLines[nextLine++].Text).Append('\n');
        }
        void SkippedXfmrsUpTo(int k)
        {
            while (nextXfmr < skippedXfmrs.Count && skippedXfmrs[nextXfmr].Before <= k)
            {
                UcteSkippedRecord rec = skippedXfmrs[nextXfmr++];
                sb.Append(rec.Text).Append('\n');
                if (rec.Regulation is not null) regulation.Add(rec.Regulation);
            }
        }
        sb.Append("##L\n");
        foreach (int k in lines)
        {
            SkippedLinesUpTo(k);
            Branch br = model.Branches[k];
            string who = $"branch {k + 1} ({br.From}-{br.To})";
            int i = position[br.From], j = position[br.To];
            double zb = kv[i] * kv[i] / model.BaseMva;
            bool coupler = source is not null && source.BranchKeys[k].Kind == "coupler";
            var row = new Row();
            row.Left(F.Node1, codes[i]);
            row.Left(F.Node2, codes[j]);
            row.Left(F.Order, Order(k, codes[i], codes[j]));
            row.Left(F.Status, StatusOf(k));
            if (coupler)
            {
                if (!(br.R == 0 && br.X == UcteParser.ZeroImpedanceXPu && br.B == 0))
                    throw new InvalidDataException($"UCTE-DEF writer: {who} was a busbar coupler in the source but is no longer the coupler's {UcteParser.ZeroImpedanceXPu.ToString("R", Inv)} pu reactance.");
                row.Num(F.LineR, 0, who + " R");
                row.Num(F.LineX, 0, who + " X");
                row.Num(F.LineB, 0, who + " B");
            }
            else
            {
                row.Num(F.LineR, br.R * zb, who + " R (ohm)");
                row.Num(F.LineX, br.X * zb, who + " X (ohm)");
                row.Num(F.LineB, br.B / zb / 1e-6, who + " B (microsiemens)");
            }
            if (br.RateAMva > 0) row.Num(F.LineCurrent, br.RateAMva * 1000.0 / (Math.Sqrt(3.0) * kv[i]), who + " current limit (A)");
            row.Rest(F.LineNameFrom, OneLine(br.Name ?? ""));
            sb.Append(row).Append('\n');
            if (br.InService) order.Add(k); else open++;
            if (br.RateBMva != 0 || br.RateCMva != 0) ratesBc++;
            if (br.R0 != 0 || br.X0 != 0 || br.LengthKm != 0 || br.Equipment is not null || br.DynamicsClass is not null) branchExtras++;
        }
        SkippedLinesUpTo(int.MaxValue);

        // ---- Transformers and their regulation -------------------------------------------------------------------
        int regulated = 0, keptCount = 0;
        sb.Append("##T\n");
        foreach (int k in xfmrs)
        {
            SkippedXfmrsUpTo(k);
            Branch br = model.Branches[k];
            string who = $"transformer {k + 1} ({br.From}-{br.To})";
            int n1 = position[br.To], n2 = position[br.From];
            double kv1 = kv[n1], kv2 = kv[n2];
            double zb1 = kv1 * kv1 / model.BaseMva;
            string ord = Order(k, codes[n1], codes[n2]);
            var row = new Row();
            row.Left(F.Node1, codes[n1]);
            row.Left(F.Node2, codes[n2]);
            row.Left(F.Order, ord);
            row.Left(F.Status, StatusOf(k));
            UcteTransformerRecord? rec = kept.TryGetValue(k, out UcteTransformerRecord? r0) ? r0 : null;
            if (rec is not null)
            {
                // The file's own text: the reader folds U1, U2 and the ##R record into the ratio, so the same text gives
                // the same ratio. The nominal power is not read into the model; it is kept as the file had it.
                row.Text(F.TransformerU1, rec.RatedVoltage1, who + " rated voltage 1");
                row.Text(F.TransformerU2, rec.RatedVoltage2, who + " rated voltage 2");
                row.Text(F.TransformerS, rec.NominalPower, who + " nominal power");
            }
            else
            {
                row.Num(F.TransformerU1, kv1, who + " rated voltage 1 (kV)");
                row.Num(F.TransformerU2, kv2, who + " rated voltage 2 (kV)");
                row.Num(F.TransformerS, br.RateAMva > 0 ? br.RateAMva : model.BaseMva, who + " nominal power (MVA)");
            }
            row.Num(F.TransformerR, br.R * zb1, who + " R (ohm)");
            row.Num(F.TransformerX, br.X * zb1, who + " X (ohm)");
            if (rec is not null && keptShunt.Contains(n1))
            {
                row.Text(F.TransformerB, rec.Susceptance, who + " B");
                row.Text(F.TransformerG, rec.Conductance, who + " G");
            }
            else if (br.InService && shuntCarrier.TryGetValue(n1, out int carrier) && carrier == k)
            {
                Bus b1 = model.Buses[n1];
                // The reader: shunt += G x 1e-6 x U1^2 (MW at 1 pu), likewise B.
                row.Num(F.TransformerB, b1.Bs / (1e-6 * kv1 * kv1), who + " B (microsiemens, the bus shunt at node 1)");
                row.Num(F.TransformerG, b1.Gs / (1e-6 * kv1 * kv1), who + " G (microsiemens, the bus shunt at node 1)");
            }
            else
            {
                row.Num(F.TransformerB, 0, who + " B");
                row.Num(F.TransformerG, 0, who + " G");
            }
            if (br.RateAMva > 0) row.Num(F.TransformerCurrent, br.RateAMva * 1000.0 / (Math.Sqrt(3.0) * kv1), who + " current limit (A)");
            row.Rest(F.TransformerNameFrom, OneLine(br.Name ?? ""));
            sb.Append(row).Append('\n');
            if (br.InService) order.Add(k); else open++;
            if (br.RateBMva != 0 || br.RateCMva != 0) ratesBc++;
            if (br.R0 != 0 || br.X0 != 0 || br.FromWinding is not null || br.ToWinding is not null || br.DynamicsClass is not null) branchExtras++;

            if (rec is not null)
            {
                keptCount++;
                if (rec.Regulation is not null)
                {
                    var kr = new Row();
                    kr.Left(F.Node1, codes[n1]);
                    kr.Left(F.Node2, codes[n2]);
                    kr.Left(F.Order, ord);
                    kr.Rest(F.PhaseDu.From, rec.Regulation);
                    regulation.Add(kr.ToString());
                }
                continue;
            }

            double tap = br.TapRatio == 0 ? 1.0 : br.TapRatio;
            double phi = br.PhaseShiftDeg;
            if (tap == 1.0 && phi == 0.0) continue;
            regulated++;
            var reg = new Row();
            reg.Left(F.Node1, codes[n1]);
            reg.Left(F.Node2, codes[n2]);
            reg.Left(F.Order, ord);
            if (phi == 0.0)
            {
                // Phase regulation, one step: the reader's ratio factor is 1 + n' du / 100 with n' = 1.
                reg.Num(F.PhaseDu, 100.0 * (tap - 1.0), who + " phase regulation du (%)");
                reg.Left(F.PhaseN, "1");
                reg.Left(F.PhaseTap, "1");
            }
            else
            {
                double rad = phi * Math.PI / 180.0;
                double du, thetaDeg;
                string kind;
                if (tap == 1.0)
                {
                    // SYMM at theta 90: the reader's shift is 2 atan(s / 2), so s = 2 tan(phi / 2).
                    du = 100.0 * 2.0 * DetMath.Tan(rad / 2.0);
                    thetaDeg = 90.0;
                    kind = "SYMM";
                }
                else
                {
                    // ASYM: the reader's complex factor is 1 + s e^(j theta); make it t e^(j phi).
                    double re = tap * DetMath.Cos(rad) - 1.0, im = tap * DetMath.Sin(rad);
                    du = 100.0 * Math.Sqrt(re * re + im * im);
                    thetaDeg = DetMath.Atan2(im, re) * 180.0 / Math.PI;
                    kind = "ASYM";
                }
                reg.Num(F.AngleDu, du, who + " angle regulation du (%)");
                reg.Num(F.AngleTheta, thetaDeg, who + " angle regulation theta (degrees)");
                reg.Left(F.AngleN, "1");
                reg.Left(F.AngleTap, "1");
                reg.Left(F.AngleType, kind);
            }
            regulation.Add(reg.ToString());
        }
        SkippedXfmrsUpTo(int.MaxValue);
        if (regulation.Count > 0)
        {
            sb.Append("##R\n");
            foreach (string r in regulation) sb.Append(r).Append('\n');
        }

        if (open > 0) notes.Add($"{open} out-of-service branch(es) written out of operation (status 8, 9 for an equivalent element, 7 for a busbar coupler); the reader skips them.");
        foreach (string what in negativeAsEquivalent)
            notes.Add($"{what} has a negative R or X and is written as an equivalent element (status 1, or 9 out of service): UCTE-DEF lets only an equivalent element carry a negative impedance.");
        if (skippedLines.Count + skippedXfmrs.Count > 0)
            notes.Add($"{skippedLines.Count + skippedXfmrs.Count} out-of-operation record(s) of the source (status 7, 8 or 9) written back as the file had them; the reader skips them.");
        if (regulated > 0) notes.Add($"{regulated} transformer(s) off nominal ratio or with a phase shift written as one-step ##R regulation; the ratio and shift come back to the precision of the du and theta columns.");
        if (keptCount > 0) notes.Add($"{keptCount} transformer(s) written with the source file's rated voltages, nominal power and regulation record as it had them, so their ratio and shift read back as read.");
        if (source is not null && xfmrs.Count > keptCount)
            notes.Add($"{xfmrs.Count - keptCount} transformer(s) differ from the source read (ends, ratio or phase shift) and are written as from another format: rated voltages of the bus levels, nominal power from rating A (the system base when it is 0), one-step ##R regulation.");
        if (ratesBc > 0) notes.Add($"{ratesBc} branch(es) carry ratings B or C, which UCTE-DEF cannot hold (one current limit); not written.");
        if (branchExtras > 0) notes.Add($"{branchExtras} branch(es) carry sequence data, winding connections, a length, an equipment type or a dynamics class, which UCTE-DEF cannot hold; not written.");
        if (model.Oltcs.Count > 0 || model.TransformerFlowControls.Count > 0)
            notes.Add($"{model.Oltcs.Count + model.TransformerFlowControls.Count} tap changer(s) or flow control(s) written at their present tap only; the reader applies no regulation target.");
        if (model.ZeroSequenceMutuals.Count > 0) notes.Add($"{model.ZeroSequenceMutuals.Count} zero-sequence mutual coupling(s) not written.");
        if (model.Boundaries.Count > 0) notes.Add($"{model.Boundaries.Count} boundary(ies) not written: UCTE-DEF boundaries are X-nodes, which GridSim does not build from them.");
        if (model.Buses.Any(x => x.X != 0 || x.Y != 0)) notes.Add("bus layout coordinates not written.");
        if (model.FrequencyHz != UcteParser.AssumedFrequencyHz)
            notes.Add(FormattableString.Invariant($"the model's frequency is {model.FrequencyHz} Hz; UCTE-DEF carries none, so give it again when reading the file back."));
        if (model.AssetDynamics is not null || model.Protections is not null) notes.Add("the asset-dynamics and protection bundles are not written: UCTE-DEF has no place for them.");

        return new UcteWriteResult(sb.ToString(), notes, codes, order);
    }

    /// <summary>
    /// <paramref name="model"/> as the reader rebuilds it from <paramref name="written"/>, apart from the column precision:
    /// buses numbered 1, 2, ... in model order, in-service units only, branches in <see cref="UcteWriteResult.BranchOrder"/>
    /// (in-service lines, then in-service transformers), and tap changer and flow control buses renumbered to match. A
    /// read-back is compared with this, not with <paramref name="model"/> as given.
    /// </summary>
    public static GridModel InFileOrder(GridModel model, UcteWriteResult written)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(written);
        var map = new Dictionary<int, int>();
        for (int i = 0; i < model.Buses.Count; i++) map[model.Buses[i].Id] = i + 1;
        int Id(int bus) => map.TryGetValue(bus, out int k) ? k : bus;
        return model.CopyWith(
            buses: model.Buses.Select(b => b with { Id = Id(b.Id) }).ToList(),
            generators: model.AllGenerators.Where(u => u.InService).Select(u => u with { Bus = Id(u.Bus) }).ToList(),
            generatorsAreWholeFleet: true,
            branches: written.BranchOrder.Select(k => model.Branches[k] with { From = Id(model.Branches[k].From), To = Id(model.Branches[k].To) }).ToList(),
            oltcs: model.Oltcs.Select(o => o with { ControlsBus = Id(o.ControlsBus) }).ToList(),
            transformerFlowControls: model.TransformerFlowControls.Select(f => f with { FromBus = Id(f.FromBus) }).ToList());
    }

    /// <summary>
    /// The UCTE country codes a synthesised node code may start with: the two-letter name written as the ##Z block, and
    /// the code's first character. Source: PowSyBl's UCTE model (powsybl-core, MPL-2.0, UcteCountryCode), less XX, the
    /// fictitious border node (X). Every letter and digit is some country's code, so none is free for GridSim to use.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, char> CountryCodes = new Dictionary<string, char>(StringComparer.Ordinal)
    {
        ["AL"] = 'A', ["AT"] = 'O', ["BA"] = 'W', ["BE"] = 'B', ["BG"] = 'V', ["BY"] = '3', ["CH"] = 'S', ["CZ"] = 'C',
        ["DE"] = 'D', ["DK"] = 'K', ["ES"] = 'E', ["FR"] = 'F', ["GB"] = '5', ["GR"] = 'G', ["HR"] = 'H', ["HU"] = 'M',
        ["IE"] = '4', ["IT"] = 'I', ["KS"] = '_', ["LT"] = '6', ["LU"] = '1', ["MA"] = '2', ["MD"] = '7', ["ME"] = '0',
        ["MK"] = 'Y', ["NL"] = 'N', ["NO"] = '9', ["PL"] = 'Z', ["PT"] = 'P', ["RO"] = 'R', ["RS"] = 'J', ["SE"] = '8',
        ["SI"] = 'L', ["SK"] = 'Q', ["TR"] = 'T', ["UA"] = 'U',
    };

    /// <summary>Why <paramref name="country"/> is not a country a synthesised node code can be given.</summary>
    public static string CountryRefusal(string country) => country == "XX"
        ? "UCTE-DEF writer: XX is UCTE's block for X-nodes (boundary nodes), which other UCTE tools take as boundary points; a synthesised node is an ordinary bus, so give its real country, one of " + string.Join(", ", CountryCodes.Keys) + "."
        : $"UCTE-DEF writer: the country '{country}' is not a UCTE country code; give one of {string.Join(", ", CountryCodes.Keys)} (two capital letters).";

    /// <summary>The UCTE voltage level code of a base voltage (the reader's table, <see cref="UcteParser.NominalKv"/>).</summary>
    private static char LevelCode(double baseKv, string who)
    {
        foreach (KeyValuePair<char, double> kv in UcteParser.NominalKv.OrderBy(p => p.Key))
            if (kv.Value == baseKv) return kv.Key;
        throw new InvalidDataException(FormattableString.Invariant(
            $"UCTE-DEF writer: {who} has base voltage {baseKv} kV, which is not one of the UCTE voltage levels (750, 500, 380, 330, 220, 150, 120, 110, 70, 27 kV)."));
    }

    private static string OneLine(string s) => s.Replace('\n', ' ').Replace('\r', ' ');

    /// <summary>
    /// <paramref name="value"/> in at most <paramref name="width"/> characters, with as many decimals as fit (invariant
    /// culture, rounded half away from the exact binary value), so the text's error is at most half a unit in its last
    /// decimal. A zero is written without a sign. A value that does not fit even with no decimals is refused.
    /// </summary>
    internal static string Fixed(double value, int width, string what)
    {
        if (!double.IsFinite(value)) throw new InvalidDataException($"UCTE-DEF writer: {what} is {value.ToString("R", Inv)}; it must be finite.");
        if (value == 0) value = 0.0;   // -0 is written as 0
        for (int d = width - 1; d >= 0; d--)
        {
            string s = value.ToString("F" + d.ToString(Inv), Inv);
            if (s.Length > width) continue;
            if (double.Parse(s, NumberStyles.Float, Inv) == 0) s = 0.0.ToString("F" + d.ToString(Inv), Inv);
            return s;
        }
        throw new InvalidDataException($"UCTE-DEF writer: {what} is {value.ToString("R", Inv)}, which does not fit its {width} columns.");
    }

    /// <summary>One fixed-column record being built.</summary>
    private sealed class Row
    {
        private readonly StringBuilder _sb = new();

        private void Put(int from, string text)
        {
            if (_sb.Length < from - 1) _sb.Append(' ', from - 1 - _sb.Length);
            else if (_sb.Length > from - 1) throw new InvalidOperationException("UCTE-DEF writer: fields written out of column order.");
            _sb.Append(text);
        }

        public void Left(UcteColumns.Field f, string text)
        {
            if (text.Length > f.Width) throw new InvalidDataException($"UCTE-DEF writer: '{text}' does not fit columns {f.From}-{f.To}.");
            Put(f.From, text.PadRight(f.Width));
        }

        public void Num(UcteColumns.Field f, double value, string what) => Put(f.From, Fixed(value, f.Width, what).PadLeft(f.Width));

        /// <summary>Text as a source file had it (trimmed), right-aligned in its columns; blank stays blank.</summary>
        public void Text(UcteColumns.Field f, string text, string what)
        {
            if (text.Length > f.Width) throw new InvalidDataException($"UCTE-DEF writer: {what} '{text}' does not fit columns {f.From}-{f.To}.");
            if (text.Length > 0) Put(f.From, text.PadLeft(f.Width));
        }

        public void Rest(int from, string text)
        {
            if (text.Length == 0) return;
            Put(from, text);
        }

        public override string ToString() => _sb.ToString().TrimEnd();
    }
}
