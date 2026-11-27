using System.Globalization;
using GridSim.Core.Diagnostics;
using GridSim.Core.Model;
using GridSim.Core.Model.Substations;

namespace GridSim.Core.IO.Dgs;

// =====================================================================================================================
// RC4 R4-11 part C: DgsFile -> SubstationModel (Part A's node-breaker model), every object keeping its DGS identity.
// The class map (DgsClassMap) says what each attribute does; nothing here claims a PowerFactory behaviour. A GridModel is
// then produced by SubstationProjection at the DGS switch states, so every solver sees an ordinary bus-branch case.
//
// Identities: a terminal's ID is its connectivity node id; a coupler's or StaSwitch's ID is its switch id; an element's ID
// is its equipment id. A cubicle with n switches adds nodes "<cubicle ID>.n1" .. ".n<n>" (the chain from the terminal to
// the element end, switches in ordinal ID order). DGS IDs are unique across the file (reader rule), so no id repeats.
// =====================================================================================================================

/// <summary>What became of one DGS object.</summary>
public enum DgsObjectOutcome { Mapped, Approximated, Carried, OutOfService, Refused }

/// <summary>One DGS object and its fate, with the GridSim ids it produced.</summary>
public sealed record DgsObjectRecord(string Class, string Id, string? LocName, int Line, DgsObjectOutcome Outcome, IReadOnlyList<string> GridSimIds, string Note);

/// <summary>A table whose class the importer does not interpret: listed by name, never dropped silently.</summary>
public sealed record DgsUnsupportedClass(string Class, int Rows, int FirstLine, bool KnownAndRefused);

/// <param name="AcceptUnconfirmed">Use attribute mappings whose name or meaning no public source confirms (each use is listed). Default false: such an object is refused.</param>
public sealed record DgsImportOptions(bool AcceptUnconfirmed = false, double BaseMva = GridModel.DefaultBaseMva);

public sealed class DgsImportResult
{
    /// <summary>Always <see cref="ImportProvenanceTag.FormatFromPublicDescription"/>.</summary>
    public ImportProvenanceTag Provenance { get; } = ImportProvenanceTag.FormatFromPublicDescription;
    public required string DgsVersion { get; init; }
    public required bool AcceptedUnconfirmed { get; init; }
    public required SubstationModel Model { get; init; }
    public required IReadOnlyList<DgsObjectRecord> Objects { get; init; }
    public required IReadOnlyList<DgsUnsupportedClass> Unsupported { get; init; }
    /// <summary>Columns present in the file that the class map does not name, per class (read and not used).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> UnreadColumns { get; init; }
    /// <summary>"Class.attribute" of every UNCONFIRMED mapping that was actually used.</summary>
    public required IReadOnlyList<string> UnconfirmedUsed { get; init; }
    public required IReadOnlyList<(string Descr, string Val, int Line)> General { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    /// <summary>GridSim id (node, switch, equipment) to the DGS object it came from.</summary>
    public required IReadOnlyDictionary<string, DgsObjectRecord> Identity { get; init; }

    /// <summary>True when nothing was refused or unsupported (out-of-service objects do not count: they are a stated model input).</summary>
    public bool Complete => Unsupported.Count == 0 && Objects.All(o => o.Outcome != DgsObjectOutcome.Refused);

    /// <summary>The projected bus-branch model at the DGS switch states (every switch stated by the file).</summary>
    public SubstationProjectionResult Project(BusNumbering numbering = BusNumbering.NodeRank) =>
        SubstationProjection.Project(Model, Model.NormalStates(), new SubstationProjectionOptions(UnspecifiedStatePolicy.Refuse, numbering));
}

/// <summary>Imports a <see cref="DgsFile"/>. Throws <see cref="DgsFormatException"/> for a topology that cannot be built.</summary>
public static class DgsImporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const string UnassignedSubstation = "dgs-unassigned";

    private sealed class Term { public required DgsRow Row; public double Kv; public string Sub = UnassignedSubstation; public bool Used; }
    private sealed class Cubicle { public List<string> Nodes = new(); public required DgsRow Row; public required string TermId; public required string ObjId; public int Bus; public bool Used; public string? FinalNode; }

    public static DgsImportResult Import(DgsFile file, DgsImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        options ??= new DgsImportOptions();
        var objects = new List<DgsObjectRecord>();
        var warnings = new List<string>();
        var unconfirmed = new SortedSet<string>(StringComparer.Ordinal);
        var identity = new Dictionary<string, DgsObjectRecord>(StringComparer.Ordinal);
        var outcomeOf = new Dictionary<string, int>(StringComparer.Ordinal);   // dgs id -> index in objects

        // ---- tables the importer does not interpret ----
        var unsupported = new List<DgsUnsupportedClass>();
        foreach (DgsTable t in file.Tables)
            if (DgsClassMap.Find(t.Name) is null)
                unsupported.Add(new DgsUnsupportedClass(t.Name, t.Rows.Count, t.Rows.Count > 0 ? t.Rows[0].Line : t.HeaderLine, DgsClassMap.RefusedClasses.Contains(t.Name)));
        var unread = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (DgsTable t in file.Tables)
        {
            DgsClassMapEntry? e = DgsClassMap.Find(t.Name);
            if (e is null) continue;
            string[] extra = t.Columns.Select(c => c.Name).Where(n => n != "ID" && e.Attribute(n) is null).ToArray();
            if (extra.Length > 0) unread[t.Name] = extra;
        }
        IEnumerable<DgsRow> Rows(string cls) => file.Table(cls)?.Rows ?? Enumerable.Empty<DgsRow>();
        string? Loc(DgsRow r) => r.Text("loc_name");

        DgsObjectRecord Add(string cls, DgsRow r, DgsObjectOutcome o, string note, params string[] ids)
        {
            var rec = new DgsObjectRecord(cls, r.Id, Loc(r), r.Line, o, ids, note);
            outcomeOf[cls + "\n" + r.Id] = objects.Count;
            objects.Add(rec);
            foreach (string id in ids) identity[id] = rec;
            return rec;
        }
        void Replace(string cls, DgsRow r, DgsObjectOutcome o, string note, params string[] ids)
        {
            int i = outcomeOf[cls + "\n" + r.Id];
            var rec = objects[i] with { Outcome = o, Note = note, GridSimIds = ids };
            objects[i] = rec;
            foreach (string id in ids) identity[id] = rec;
        }

        // The gate: an UNCONFIRMED attribute that is present and is used (not merely carried) refuses the object unless accepted.
        string? Gate(string cls, DgsRow r)
        {
            DgsClassMapEntry entry = DgsClassMap.Find(cls)!;
            var blocked = new List<string>();
            foreach (DgsAttributeMap a in entry.Attributes)
            {
                if (a.Confidence != DgsConfidence.Unconfirmed || a.Disposition == DgsDisposition.Carried) continue;
                if (r.Text(a.Name) is null) continue;
                if (options.AcceptUnconfirmed) unconfirmed.Add(cls + "." + a.Name); else blocked.Add(a.Name);
            }
            return blocked.Count == 0 ? null
                : $"attribute(s) {string.Join(", ", blocked)} of {cls} have an UNCONFIRMED name or meaning (no public source) and are refused; repeat with unconfirmed mappings accepted to use them.";
        }

        // ---- terminals ----
        var substations = new Dictionary<string, DgsRow>(StringComparer.Ordinal);
        foreach (DgsRow r in Rows("ElmSubstat")) substations[r.Id] = r;
        var terms = new Dictionary<string, Term>(StringComparer.Ordinal);
        foreach (DgsRow r in Rows("ElmTerm"))
        {
            string? why = r.Int("outserv") == 1 ? "the terminal is out of service (outserv = 1); what it isolates is not guessed."
                : !(r.Real("uknom") > 0) ? "uknom (nominal kV) is missing or not above 0."
                : Gate("ElmTerm", r);
            if (why is not null) { Add("ElmTerm", r, DgsObjectOutcome.Refused, why); continue; }
            var t = new Term { Row = r, Kv = r.Real("uknom")!.Value };
            string? fold = r.Text("fold_id");
            if (fold is not null && substations.ContainsKey(fold)) t.Sub = fold;
            terms[r.Id] = t;
            Add("ElmTerm", r, DgsObjectOutcome.Carried, "not used by any imported object; left out of the model.");
        }
        foreach (DgsRow r in Rows("ElmSubstat"))
        {
            string? gs = Gate("ElmSubstat", r);
            Add("ElmSubstat", r, gs is null ? DgsObjectOutcome.Carried : DgsObjectOutcome.Refused, gs ?? "no terminal of this substation is used by an imported object.");
        }

        // ---- cubicles and their switches ----
        var allIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DgsTable t in file.Tables) foreach (DgsRow r in t.Rows) allIds[r.Id] = t.Name;
        var cubicles = new Dictionary<string, Cubicle>(StringComparer.Ordinal);
        var cubicleByEnd = new Dictionary<(string, int), Cubicle>();
        foreach (DgsRow r in Rows("StaCubic"))
        {
            string? why = Gate("StaCubic", r);
            string? term = r.Text("fold_id"), obj = r.Text("obj_id");
            long bus = r.Int("obj_bus") ?? 0;
            if (why is null && (term is null || obj is null)) why = "fold_id (terminal) and obj_id (element) are both required.";
            if (why is null && !terms.ContainsKey(term!)) why = allIds.ContainsKey(term!) ? $"terminal '{term}' was refused or is not a terminal." : $"fold_id '{term}' names no object in the file.";
            if (why is null && !allIds.ContainsKey(obj!)) why = $"obj_id '{obj}' names no object in the file.";
            if (why is null && (bus < 0 || bus > 1)) why = $"obj_bus {bus} is not 0 or 1 (more than two ends per element is not read).";
            if (why is null && cubicleByEnd.ContainsKey((obj!, (int)bus))) why = $"end {bus} of '{obj}' already has a cubicle.";
            if (why is not null) { Add("StaCubic", r, DgsObjectOutcome.Refused, why); continue; }
            var c = new Cubicle { Row = r, TermId = term!, ObjId = obj!, Bus = (int)bus };
            cubicles[r.Id] = c; cubicleByEnd[(obj!, (int)bus)] = c;
            Add("StaCubic", r, DgsObjectOutcome.Carried, "no imported element uses this cubicle.");
        }
        var switchRows = new Dictionary<string, List<(DgsRow Row, bool Open, SwitchKind Kind)>>(StringComparer.Ordinal);
        foreach (DgsRow r in Rows("StaSwitch"))
        {
            string? why = Gate("StaSwitch", r);
            string? cub = r.Text("fold_id");
            if (why is null && (cub is null || !cubicles.ContainsKey(cub))) why = $"fold_id '{cub}' names no usable cubicle.";
            if (why is null && SwitchStateKind(r, out _, out _) is { } bad) why = bad;
            if (why is not null) { Add("StaSwitch", r, DgsObjectOutcome.Refused, why); continue; }
            SwitchStateKind(r, out bool open, out SwitchKind kind);
            if (!switchRows.TryGetValue(cub!, out var l)) switchRows[cub!] = l = new();
            l.Add((r, open, kind));
            Add("StaSwitch", r, DgsObjectOutcome.Carried, "its cubicle's element was not imported.");
        }
        foreach (var l in switchRows.Values) l.Sort((a, b) => string.CompareOrdinal(a.Row.Id, b.Row.Id));

        // ---- model accumulators ----
        var nodes = new Dictionary<string, (string Sub, double Kv)>(StringComparer.Ordinal);
        var nodeOrder = new List<string>();
        var swList = new List<(string Sub, NbSwitch Sw)>();
        var lines = new List<NbLine>(); var trafos = new List<NbTransformer>(); var loads = new List<NbLoad>();
        var gens = new List<NbGenerator>(); var shunts = new List<NbShunt>();

        void AddNode(string id, string sub, double kv) { if (nodes.TryAdd(id, (sub, kv))) nodeOrder.Add(id); }

        // End of an element: the cubicle exists and its terminal is usable. Pure check, nothing created.
        bool TryEnd(string objId, int bus, string label, out Cubicle? cub, out string? why)
        {
            if (!cubicleByEnd.TryGetValue((objId, bus), out cub)) { why = $"{label} has no usable StaCubic (end {bus}); the element is not connected there."; return false; }
            why = null; return true;
        }
        // Create the chain terminal - switches - element node once; returns the node the element sits on.
        string Materialise(Cubicle c)
        {
            if (c.FinalNode is not null) return c.FinalNode;
            Term t = terms[c.TermId];
            t.Used = true;
            AddNode(c.TermId, t.Sub, t.Kv);
            string prev = c.TermId;
            if (switchRows.TryGetValue(c.Row.Id, out var chain))
            {
                int k = 0;
                foreach (var (sr, open, kind) in chain)
                {
                    string next = c.Row.Id + ".n" + (++k).ToString(Inv);
                    AddNode(next, t.Sub, t.Kv);
                    swList.Add((t.Sub, new NbSwitch(sr.Id, kind, prev, next, open)));
                    Replace("StaSwitch", sr, DgsObjectOutcome.Mapped, $"switch between {prev} and {next} in cubicle {c.Row.Id}.", sr.Id);
                    c.Nodes.Add(next);
                    prev = next;
                }
            }
            c.Used = true;
            c.FinalNode = prev;
            return prev;
        }

        double Zbase(double kv) => kv * kv / options.BaseMva;

        // ---- line types and transformer types ----
        var lineTypes = new Dictionary<string, DgsRow>(StringComparer.Ordinal);
        foreach (DgsRow r in Rows("TypLne")) { lineTypes[r.Id]= r; Add("TypLne", r, DgsObjectOutcome.Carried, "no imported line uses this type."); }
        var trTypes = new Dictionary<string, DgsRow>(StringComparer.Ordinal);
        foreach (DgsRow r in Rows("TypTr2")) { trTypes[r.Id] = r; Add("TypTr2", r, DgsObjectOutcome.Carried, "no imported transformer uses this type."); }

        // ---- lines ----
        foreach (DgsRow r in Rows("ElmLne"))
        {
            if (r.Int("outserv") == 1) { Add("ElmLne", r, DgsObjectOutcome.OutOfService, "outserv = 1: left out of the model."); continue; }
            string? why = Gate("ElmLne", r);
            string? typ = r.Text("typ_id");
            DgsRow? ty = null;
            if (why is null && r.Int("nlnum") is long n && n != 1) why = $"nlnum = {n}: parallel-line counts other than 1 are refused.";
            double len = r.Real("dline") ?? 0;
            if (why is null && r.Real("dline") is null) why = "dline (length, km) is missing.";
            if (why is null && !(len > 0)) why = "dline must be above 0 km.";
            if (why is null && (typ is null || !lineTypes.TryGetValue(typ, out ty))) why = $"typ_id '{typ}' names no TypLne row.";
            if (why is null) why = Gate("TypLne", ty!);
            if (why is null && (ty!.Real("rline") is null || ty.Real("xline") is null)) why = $"type '{typ}' needs rline and xline.";
            Cubicle? ca = null, cb = null;
            if (why is null) { TryEnd(r.Id, 0, "line", out ca, out why); }
            if (why is null) { TryEnd(r.Id, 1, "line", out cb, out why); }
            if (why is not null) { Add("ElmLne", r, DgsObjectOutcome.Refused, why); continue; }
            double kv = terms[ca!.TermId].Kv;
            string note = "";
            if (terms[cb!.TermId].Kv != kv) { Add("ElmLne", r, DgsObjectOutcome.Refused, $"the line joins terminals of different nominal kV ({kv.ToString("R", Inv)} and {terms[cb.TermId].Kv.ToString("R", Inv)}); use a transformer."); continue; }
            string na = Materialise(ca), nb = Materialise(cb);
            double zb = Zbase(kv);
            double rr = ty!.Real("rline")!.Value * len / zb, xx = ty.Real("xline")!.Value * len / zb;
            double bb = (ty.Real("bline") ?? 0) * 1e-6 * len * zb;
            double rate = ty.Real("sline") is double sl ? Math.Sqrt(3.0) * kv * sl : 0;
            if (ty.Real("uline") is double ul && ul != kv) { note = $"type uline {ul.ToString("R", Inv)} kV differs from the terminal kV {kv.ToString("R", Inv)}; the terminal kV is used."; warnings.Add($"line {r.Id}: {note}"); }
            lines.Add(new NbLine(r.Id, na, nb, rr, xx, bb, rate));
            Add("ElmLne", r, note.Length > 0 ? DgsObjectOutcome.Approximated : DgsObjectOutcome.Mapped, note.Length > 0 ? note : $"type {typ}, {len.ToString("R", Inv)} km.", r.Id);
            Replace("TypLne", ty, DgsObjectOutcome.Mapped, "used by a line.", ty.Id);
        }
        // ---- transformers ----
        foreach (DgsRow r in Rows("ElmTr2"))
        {
            if (r.Int("outserv") == 1) { Add("ElmTr2", r, DgsObjectOutcome.OutOfService, "outserv = 1: left out of the model."); continue; }
            string? why = Gate("ElmTr2", r);
            string? typ = r.Text("typ_id");
            DgsRow? ty = null;
            if (why is null && r.Int("nntap") is long tap && tap != 0) why = $"nntap = {tap}: a tap position other than 0 is refused (the tap table is not read).";
            if (why is null && (typ is null || !trTypes.TryGetValue(typ, out ty))) why = $"typ_id '{typ}' names no TypTr2 row.";
            if (why is null) why = Gate("TypTr2", ty!);
            double strn = 0, uh = 0, ul = 0, uk = 0, pcu = 0;
            if (why is null)
            {
                strn = ty!.Real("strn") ?? 0; uh = ty.Real("utrn_h") ?? 0; ul = ty.Real("utrn_l") ?? 0; uk = ty.Real("uktr") ?? 0; pcu = ty.Real("pcutr") ?? 0;
                if (!(strn > 0 && uh > 0 && ul > 0 && uk > 0)) why = $"type '{typ}' needs strn, utrn_h, utrn_l and uktr above 0.";
            }
            Cubicle? ca = null, cb = null;
            if (why is null) { TryEnd(r.Id, 0, "transformer", out ca, out why); }
            if (why is null) { TryEnd(r.Id, 1, "transformer", out cb, out why); }
            double zk = uk / 100.0, rk = pcu / (1000.0 * strn);
            if (why is null && rk >= zk) why = "copper losses pcutr are not below the short-circuit voltage uktr (no real reactance).";
            if (why is not null) { Add("ElmTr2", r, DgsObjectOutcome.Refused, why); continue; }
            string nh = Materialise(ca!), nl = Materialise(cb!);
            double kvH = terms[ca!.TermId].Kv, kvL = terms[cb!.TermId].Kv;
            double xk = Math.Sqrt(zk * zk - rk * rk);
            double scale = options.BaseMva / strn;
            double ratio = (uh / kvH) / (ul / kvL);
            // The short-circuit impedance is z ul^2 / strn ohms on the LV side. On the LV terminal's base that is
            // z (Sb / strn) (ul / kvL)^2, with the ratio above at the HV side. When ul equals the LV terminal kV the
            // factor is exactly 1.0.
            double refer = (ul / kvL) * (ul / kvL);
            trafos.Add(new NbTransformer(r.Id, nh, nl, rk * scale * refer, xk * scale * refer, 0.0, ratio, 0.0, strn));
            string note = ty!.Real("i0tr") is not null ? "magnetising current i0tr is not modelled (B = 0)." : "";
            Add("ElmTr2", r, note.Length > 0 ? DgsObjectOutcome.Approximated : DgsObjectOutcome.Mapped, note.Length > 0 ? note : $"type {typ}.", r.Id);
            Replace("TypTr2", ty, DgsObjectOutcome.Mapped, "used by a transformer.", ty.Id);
        }
        // ---- loads ----
        foreach (DgsRow r in Rows("ElmLod"))
        {
            if (r.Int("outserv") == 1) { Add("ElmLod", r, DgsObjectOutcome.OutOfService, "outserv = 1: left out of the model."); continue; }
            string? why = Gate("ElmLod", r);
            if (why is null && r.Real("plini") is null) why = "plini (MW) is missing.";
            Cubicle? c = null;
            if (why is null) TryEnd(r.Id, 0, "load", out c, out why);
            if (why is not null) { Add("ElmLod", r, DgsObjectOutcome.Refused, why); continue; }
            double s = r.Real("scale0") ?? 1.0;
            loads.Add(new NbLoad(r.Id, Materialise(c!), r.Real("plini")!.Value * s, (r.Real("qlini") ?? 0) * s));
            bool typed = r.Text("typ_id") is not null;
            Add("ElmLod", r, typed ? DgsObjectOutcome.Approximated : DgsObjectOutcome.Mapped,
                typed ? "load type (voltage dependence) not applied: constant power." : "constant power.", r.Id);
        }
        // ---- generators and external grids ----
        foreach (string cls in new[] { "ElmSym", "ElmXnet" })
            foreach (DgsRow r in Rows(cls))
            {
                if (r.Int("outserv") == 1) { Add(cls, r, DgsObjectOutcome.OutOfService, "outserv = 1: left out of the model."); continue; }
                string? why = Gate(cls, r);
                bool slack = r.Int("ip_ctrl") == 1;
                string? bustp = r.Text("bustp");
                if (why is null && cls == "ElmXnet")
                {
                    if (bustp == "SL") slack = true;
                    else if (bustp == "PV") { }
                    else why = $"bustp '{bustp}' is not SL or PV (a PQ external grid is not mapped).";
                }
                Cubicle? c = null;
                if (why is null) TryEnd(r.Id, 0, cls == "ElmSym" ? "machine" : "external grid", out c, out why);
                if (why is not null) { Add(cls, r, DgsObjectOutcome.Refused, why); continue; }
                double pg = (cls == "ElmXnet" && bustp == "SL") ? 0.0 : r.Real("pgini") ?? 0.0;
                gens.Add(new NbGenerator(r.Id, Materialise(c!), pg, r.Real("usetp") ?? 1.0, 0.0, Slack: slack));
                bool approx = cls == "ElmSym" || r.Real("snss") is not null;
                Add(cls, r, approx ? DgsObjectOutcome.Approximated : DgsObjectOutcome.Mapped,
                    (slack ? "slack; " : "") + (cls == "ElmSym" ? "reactive limits not read (unlimited); qgini carried." : "short-circuit data not used."), r.Id);
            }
        // ---- shunts ----
        foreach (DgsRow r in Rows("ElmShnt"))
        {
            if (r.Int("outserv") == 1) { Add("ElmShnt", r, DgsObjectOutcome.OutOfService, "outserv = 1: left out of the model."); continue; }
            string? why = Gate("ElmShnt", r);
            if (why is null && r.Text("shtype") is string st && st != "2") why = $"shtype '{st}' is not 2 (capacitor); other shunt kinds are refused.";
            if (why is null && r.Real("qcapn") is null) why = "qcapn (MVAr) is missing.";
            Cubicle? c = null;
            if (why is null) TryEnd(r.Id, 0, "shunt", out c, out why);
            if (why is not null) { Add("ElmShnt", r, DgsObjectOutcome.Refused, why); continue; }
            shunts.Add(new NbShunt(r.Id, Materialise(c!), 0.0, r.Real("qcapn")!.Value));   // Bs = +qcapn: a capacitor injects qcapn MVAr at 1 pu
            Add("ElmShnt", r, DgsObjectOutcome.Mapped, "capacitor at 1 pu voltage (MVAr taken at nominal).", r.Id);
        }
        // ---- couplers ----
        foreach (DgsRow r in Rows("ElmCoup"))
        {
            string? why = r.Int("outserv") == 1 ? "the coupler is out of service (outserv = 1); refused rather than guessed." : Gate("ElmCoup", r);
            if (why is null) why = SwitchStateKind(r, out _, out _);
            Cubicle? ca = null, cb = null;
            if (why is null) TryEnd(r.Id, 0, "coupler", out ca, out why);
            if (why is null) TryEnd(r.Id, 1, "coupler", out cb, out why);
            if (why is null && terms[ca!.TermId].Sub != terms[cb!.TermId].Sub) why = $"the coupler joins terminals of different substations ('{terms[ca.TermId].Sub}', '{terms[cb.TermId].Sub}'); a switch lies inside one substation.";
            if (why is null && terms[ca!.TermId].Kv != terms[cb!.TermId].Kv) why = "the coupler joins terminals of different nominal kV.";
            if (why is not null) { Add("ElmCoup", r, DgsObjectOutcome.Refused, why); continue; }
            SwitchStateKind(r, out bool open, out SwitchKind kind);
            string n0 = Materialise(ca!), n1 = Materialise(cb!);
            swList.Add((terms[ca!.TermId].Sub, new NbSwitch(r.Id, kind, n0, n1, open)));
            Add("ElmCoup", r, r.Text("aUsage") is null ? DgsObjectOutcome.Approximated : DgsObjectOutcome.Mapped,
                (r.Text("aUsage") is null ? "no aUsage: taken as a breaker. " : "") + $"{(open ? "open" : "closed")} between {n0} and {n1}.", r.Id);
        }

        // ---- finalise carried states of terminals and cubicles ----
        foreach (var (id, t) in terms)
            if (t.Used) Replace("ElmTerm", t.Row, DgsObjectOutcome.Mapped, $"connectivity node, {t.Kv.ToString("R", Inv)} kV, substation {t.Sub}.", id);
        foreach (Cubicle c in cubicles.Values)
            if (c.Used) Replace("StaCubic", c.Row, DgsObjectOutcome.Mapped, $"attaches {c.ObjId} end {c.Bus} to terminal {c.TermId}.", c.Nodes.ToArray());

        // ---- assemble the substations ----
        var subs = new List<Substation>();
        foreach (string sid in nodes.Values.Select(v => v.Sub).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal))
        {
            var levelKv = nodes.Where(n => n.Value.Sub == sid).Select(n => n.Value.Kv).Distinct().OrderBy(k => k).ToList();
            var levels = levelKv.Select(k => new VoltageLevel(LevelId(k), k)).ToList();
            var sn = nodeOrder.Where(n => nodes[n].Sub == sid).OrderBy(n => n, StringComparer.Ordinal)
                .Select(n => new ConnectivityNode(n, LevelId(nodes[n].Kv))).ToList();
            var sw = swList.Where(s => s.Sub == sid).Select(s => s.Sw).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
            string? name = null;
            if (substations.TryGetValue(sid, out DgsRow? sr))
            {
                name = sr.Text("loc_name") ?? sid;
                if (objects[outcomeOf["ElmSubstat\n" + sid]].Outcome != DgsObjectOutcome.Refused)
                    Replace("ElmSubstat", sr, DgsObjectOutcome.Mapped, "substation with used terminals.");
            }
            subs.Add(new Substation(sid, name, levels, Array.Empty<SubstationBay>(), sn, sw));
        }
        var model = new SubstationModel
        {
            Name = "dgs-import",
            BaseMva = options.BaseMva,
            Substations = subs,
            Lines = lines.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
            Transformers = trafos.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
            Loads = loads.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
            Generators = gens.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
            Shunts = shunts.OrderBy(x => x.Id, StringComparer.Ordinal).ToList(),
        };
        IReadOnlyList<string> problems = model.Problems();
        if (problems.Count > 0) throw new DgsFormatException(problems.Select(p => "topology: " + p).ToList());

        return new DgsImportResult
        {
            DgsVersion = file.Version,
            AcceptedUnconfirmed = options.AcceptUnconfirmed,
            Model = model,
            Objects = objects.OrderBy(o => o.Line).ThenBy(o => o.Id, StringComparer.Ordinal).ToList(),
            Unsupported = unsupported.OrderBy(u => u.FirstLine).ToList(),
            UnreadColumns = unread,
            UnconfirmedUsed = unconfirmed.ToList(),
            General = file.General,
            Warnings = warnings,
            Identity = identity,
        };
    }

    private static string LevelId(double kv) => "kV" + kv.ToString("R", Inv);

    /// <summary>Reads on_off and aUsage. Returns null when fine, else the refusal text.</summary>
    private static string? SwitchStateKind(DgsRow r, out bool open, out SwitchKind kind)
    {
        open = false; kind = SwitchKind.Breaker;
        long? on = r.Int("on_off");
        if (on is null) return "on_off is missing: a switch state is never inferred.";
        if (on != 0 && on != 1) return $"on_off = {on} is not 0 or 1.";
        open = on == 0;
        switch (r.Text("aUsage"))
        {
            case null: case "cbk": kind = SwitchKind.Breaker; return null;
            case "dct": kind = SwitchKind.Disconnector; return null;
            case var u: return $"aUsage '{u}' is not cbk or dct; other switch usages are not mapped.";
        }
    }
}
