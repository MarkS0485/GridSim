using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using GridSim.Core.Assets;
using GridSim.Core.Assets.Protection;
using GridSim.Core.Model;

namespace GridSim.Core.IO.Json;

/// <summary>The outcome of <see cref="V2CaseWriter.Write"/>: the two files written and the notes on what was not carried.</summary>
public sealed record V2CaseWriteResult(string ManifestPath, string NetworkPath, IReadOnlyList<string> Notes);

/// <summary>
/// N299 (October 2026, LF5; Ruling R1: Mark delegated the EE / product calls 2026-10-04): writes a <see cref="GridModel"/>
/// as a JSON v2 case, a <c>gridsim-case/2</c> manifest plus one <c>gridsim-network/2</c> file beside it, that
/// <see cref="V2CaseLoader"/> reads back to the same network. It is the v2 twin of <see cref="JsonCaseWriter"/> (the v1
/// folder writer, F24) and exists so <c>gridsim convert</c> can round-trip a v2 case WITH its fault sequence data (N234:
/// generator X''d / X2, transformer windings, zero-sequence impedance and neutral reactances), which JSON v1 cannot hold.
///
/// <para><b>What "the same" means.</b> Every number a power flow reads is written exactly (System.Text.Json writes the
/// shortest form that reads back to the same double) and in the same ORDER as the model: buses, generators (the whole
/// fleet, out-of-service units included, F16), branches. The solvers sum per-bus admittances and injections in that order,
/// so the loaded model solves bit-identically. As in the v1 writer, the manifest's system block says
/// <c>"dispatch": false</c> (the written outputs are the operating point), generators carry pMinMw / pMaxMw / qOutputMvar
/// and the strings "Infinity" / "-Infinity" for unlimited limits, and a bus's demand is one row holding its exact
/// Pd / Qd.</para>
///
/// <para><b>Branch order and the transformer array.</b> The v2 loader builds every <c>lines</c> element first and every
/// <c>transformers</c> element after them (across all network files), and only a <c>transformers</c> element can carry
/// windings and neutral reactances. So a branch is written in place as a line (a transformer as a line with
/// <c>isTransformer</c>, tapRatio and phaseShiftDeg, exactly as the v1 writer keeps transformers in transmission.json),
/// except for the trailing run of branches from the first one carrying winding data: those are written as
/// <c>transformers</c> elements, which keeps the model order. That covers every case the v2 loader itself builds (its
/// transformers already come last). A branch whose winding data cannot be placed so without reordering the branches (it
/// lies before a branch a transformer element cannot represent: a line, or a transformer with charging, a length, an
/// equipment class, or zero-sequence data the v2 rules refuse) keeps its place as a line and its winding data is
/// reported in the notes as not written; the power flow is unaffected either way.</para>
///
/// <para><b>Byte-stable output.</b> The files are a pure function of the model: members in a fixed order, numbers in
/// their shortest round-trip form, two-space indentation, "\n" line ends on every OS and a final newline. Writing the
/// loaded-back model again gives the same bytes (pinned by tests).</para>
///
/// <para><b>The asset-dynamics and protection bundles</b> (N321, LF7, October 2026; Ruling R1: Mark delegated the EE /
/// product calls 2026-10-04). Every asset class of the model's bundles is written as a case-local template
/// <c>gridsim.local.&lt;class name&gt;@1</c> (the v1 projection's convention, which the loader reads back to the class
/// name itself), in library order; the binding rules as the manifest's <c>bindings</c>, in rule order; each element's
/// class (generator, line or transformer <c>DynamicsClass</c>) as its <c>template</c>; and the placed protective
/// devices as the network file's <c>protections</c>. The loader turns them back into the same library (same order, same
/// kinds, modes, parameters and provenance), the same rules and the same devices, so asset-dynamics and protection runs
/// on the written case are the runs on the model. The template kind is the loader's own table read backwards
/// (<see cref="V2KindOf"/>: gridFollowingInverter, gridFormingInverter, oltc for a transformer class, lineType for a line
/// class, protection). A class with no <c>_default</c> provenance gets the provenance v1 falls back to
/// (<see cref="V2CaseLoader.V1Fallback"/>), because a v2 template requires one; the loader drops exactly that entry
/// again. A bundle this writer cannot represent exactly is NOT written in part: when a class name cannot be a local
/// template id (it must be dot-separated segments of letters, digits, '_' and '-'; a name that came from an imported
/// template, <c>id@rev</c>, cannot), a class has a kind no template kind models, or a binding rule's match type is not
/// a v2 match type even in lower case, nothing of the bundles is written and the notes say why, as before N321.</para>
///
/// <para><b>What is not written</b> is reported in the returned notes, never dropped silently, as the v1 writer does:
/// a bundle that cannot be written (above) and the element template references that point into it, an element class
/// that names no class of the bundle, MATPOWER cost curves, out-of-service branches (the v2 loader refuses them for now;
/// D-DOC-162), names synthesised for unnamed elements (v2 requires names). A value v2 cannot hold (a non-finite
/// number where a finite one is needed, a blank case name, a bus with no positive base voltage) is refused with
/// <see cref="InvalidDataException"/>, naming it, and so is a model whose bundles contradict themselves (two different
/// classes under one name, a binding rule or a placed device naming no class).</para>
/// </summary>
public static partial class V2CaseWriter
{
    /// <summary>The network file name written beside <paramref name="manifestPath"/>: the manifest's file name with a
    /// trailing <c>.json</c> and then <c>.case</c> removed, plus <c>.network.json</c> (<c>uk.case.json</c> gives
    /// <c>uk.network.json</c>).</summary>
    public static string NetworkFileNameFor(string manifestPath)
    {
        string stem = Path.GetFileName(manifestPath);
        if (stem.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) stem = stem[..^5];
        if (stem.EndsWith(".case", StringComparison.OrdinalIgnoreCase)) stem = stem[..^5];
        return stem + ".network.json";
    }

    // Indented, "\n" line ends on every OS (byte-stable output; the default follows Environment.NewLine).
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true, NewLine = "\n" };

    /// <summary>
    /// Write <paramref name="model"/> as a v2 case: the manifest at <paramref name="manifestPath"/> and the network file
    /// <see cref="NetworkFileNameFor"/> beside it (the folder is created if absent). Never overwrites: an existing
    /// manifest or network file is refused with <see cref="IOException"/>. Returns both paths and the notes on what
    /// could not be carried (empty when nothing was lost).
    /// </summary>
    public static V2CaseWriteResult Write(GridModel model, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(manifestPath);
        CaseWriterChecks.RefuseReferencesToOmittedBranches(model, "JSON v2 case writer");   // GR-164
        CaseWriterChecks.RefuseLccLinks(model, "JSON v2 case writer");
        string manifestFull = Path.GetFullPath(manifestPath);
        string dir = Path.GetDirectoryName(manifestFull)!;
        string networkName = NetworkFileNameFor(manifestFull);
        string networkFull = Path.Combine(dir, networkName);
        if (File.Exists(manifestFull) || Directory.Exists(manifestFull))
            throw new IOException($"'{manifestPath}' already exists; the JSON v2 case writer never overwrites a case.");
        if (File.Exists(networkFull) || Directory.Exists(networkFull))
            throw new IOException($"'{networkFull}' already exists; the JSON v2 case writer never overwrites a case.");
        if (string.IsNullOrWhiteSpace(model.Name))
            throw new InvalidDataException("the case name is blank; a JSON v2 case manifest requires a name with at least one non-blank character.");

        var notes = new List<string>();
        // N321: decide first whether the bundles can be written exactly (null: there are none, or they cannot be, and
        // the notes say which); both files then follow the one plan, so a manifest never lists a template the network
        // file does not use as planned, or the other way round.
        Bundles? bundles = PlanBundles(model, notes);
        byte[] network = NetworkBytes(model, bundles, notes);
        byte[] manifest = ManifestBytes(model, networkName, bundles);
        Directory.CreateDirectory(dir);
        // The network file first: a manifest never exists without the file it lists.
        WriteNew(networkFull, network);
        WriteNew(manifestFull, manifest);
        return new V2CaseWriteResult(manifestFull, networkFull, notes);
    }

    /// <summary>
    /// The fault sequence data the v2 writer carries, compared bit for bit between a model and its written-and-loaded
    /// copy (the <c>convert</c> verb's extra round-trip check for a v2 output: the power-flow comparison cannot see
    /// these fields). Compares every generator (the whole fleet) and every IN-SERVICE branch of
    /// <paramref name="original"/> with <paramref name="back"/>'s, in order: X''d, X2, R0, X0, both windings and both
    /// neutral reactances. Returns the first difference, or null when they are identical.
    /// </summary>
    public static string? FirstSequenceDifference(GridModel original, GridModel back)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(back);
        static bool Same(double x, double y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y);
        static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        if (original.AllGenerators.Count != back.AllGenerators.Count)
            return $"{original.AllGenerators.Count} vs {back.AllGenerators.Count} generators";
        for (int i = 0; i < original.AllGenerators.Count; i++)
        {
            Generator a = original.AllGenerators[i], b = back.AllGenerators[i];
            if (!Same(a.SubtransientXdPu, b.SubtransientXdPu)) return $"generator {i} X''d {R(a.SubtransientXdPu)} vs {R(b.SubtransientXdPu)}";
            if (!Same(a.NegativeSeqXPu, b.NegativeSeqXPu)) return $"generator {i} X2 {R(a.NegativeSeqXPu)} vs {R(b.NegativeSeqXPu)}";
        }
        List<Branch> inService = original.Branches.Where(x => x.InService).ToList();
        if (inService.Count != back.Branches.Count) return $"{inService.Count} in-service branches vs {back.Branches.Count}";
        for (int i = 0; i < inService.Count; i++)
        {
            Branch a = inService[i], b = back.Branches[i];
            if (!Same(a.R0, b.R0)) return $"branch {i} ({a.From}-{a.To}) R0 {R(a.R0)} vs {R(b.R0)}";
            if (!Same(a.X0, b.X0)) return $"branch {i} ({a.From}-{a.To}) X0 {R(a.X0)} vs {R(b.X0)}";
            if (a.FromWinding != b.FromWinding || a.ToWinding != b.ToWinding)
                return $"branch {i} ({a.From}-{a.To}) windings {a.FromWinding?.ToString() ?? "none"}/{a.ToWinding?.ToString() ?? "none"} vs {b.FromWinding?.ToString() ?? "none"}/{b.ToWinding?.ToString() ?? "none"}";
            if (!Same(a.FromNeutralXPu, b.FromNeutralXPu) || !Same(a.ToNeutralXPu, b.ToNeutralXPu))
                return $"branch {i} ({a.From}-{a.To}) neutral reactances {R(a.FromNeutralXPu)}/{R(a.ToNeutralXPu)} vs {R(b.FromNeutralXPu)}/{R(b.ToNeutralXPu)}";
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The manifest
    // ---------------------------------------------------------------------------------------------------------------

    private static byte[] ManifestBytes(GridModel model, string networkName, Bundles? bundles) => Json(w =>
    {
        w.WriteStartObject();
        w.WriteString("schema", V2CaseLoader.CaseSchemaTag);
        w.WriteString("name", model.Name);
        w.WriteString("description", "Written by gridsim convert (the JSON v2 case writer, N299): the network and its fault sequence data, "
            + "with the generator outputs as the operating point (dispatch false).");
        w.WriteStartObject("system");
        Num(w, "baseMva", model.BaseMva, "system baseMva");
        Num(w, "frequencyHz", model.FrequencyHz, "system frequencyHz");
        w.WriteBoolean("dispatch", false);   // the written outputs are the operating point (as the v1 writer, F24)
        w.WriteEndObject();
        w.WriteStartArray("network");
        w.WriteStringValue(networkName);
        w.WriteEndArray();

        // N321: the bundles' classes as local templates (library order) and the binding rules (rule order). Both arrays
        // are left out when empty, which the loader reads as empty (a case with no bundle writes exactly what it wrote
        // before N321).
        if (bundles is not null && bundles.Classes.Count > 0)
        {
            w.WriteStartArray("localTemplates");
            foreach (AssetClass c in bundles.Classes) WriteTemplate(w, c);
            w.WriteEndArray();
        }
        if (bundles is not null && bundles.Rules.Count > 0)
        {
            w.WriteStartArray("bindings");
            foreach ((string matchType, string className) in bundles.Rules)
            {
                w.WriteStartObject();
                w.WriteString("matchType", matchType);
                w.WriteString("template", LocalRef(className));
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    });

    /// <summary>
    /// N321: one asset class as a case-local template. id <c>gridsim.local.&lt;name&gt;</c>, revision 1, active, the
    /// name as its title, the mode as its description (left out when empty: the loader reads an absent description as
    /// the empty mode), every parameter as a plain number in the class's order, and the provenance map in the class's
    /// order, led by the v1 fallback <c>_default</c> when the class has none (the loader drops exactly that entry again).
    /// A class whose own <c>_default</c> equals that fallback reads back without the entry, which
    /// <see cref="AssetClass.ProvenanceFor"/> answers identically.
    /// </summary>
    private static void WriteTemplate(Utf8JsonWriter w, AssetClass c)
    {
        w.WriteStartObject();
        w.WriteString("id", V2CaseLoader.LocalIdPrefix + c.Name);
        w.WriteNumber("rev", LocalRev);
        w.WriteString("kind", V2KindOf(c.Kind)!);   // PlanBundles admitted only kinds with a v2 template kind
        w.WriteString("status", "active");
        w.WriteString("title", c.Name);
        if (c.Mode.Length > 0) w.WriteString("description", c.Mode);
        w.WriteStartObject("params");
        foreach ((string key, double value) in c.Params) Num(w, key, value, $"asset class '{c.Name}' parameter '{key}'");
        w.WriteEndObject();
        w.WriteStartObject("provenance");
        if (!c.Provenance.ContainsKey(AssetClass.DefaultKey))
            WriteProvenance(w, AssetClass.DefaultKey, V2CaseLoader.V1Fallback);
        foreach ((string key, AssetClassProvenance p) in c.Provenance) WriteProvenance(w, key, p);
        w.WriteEndObject();
        w.WriteEndObject();
    }

    private static void WriteProvenance(Utf8JsonWriter w, string key, AssetClassProvenance p)
    {
        w.WriteStartObject(key);
        w.WriteString("tag", p.Tag.ToString());   // exactly the enum member name, which the v2 schema's tag list is
        w.WriteString("note", p.Note);
        w.WriteEndObject();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // The network file
    // ---------------------------------------------------------------------------------------------------------------

    private static byte[] NetworkBytes(GridModel model, Bundles? bundles, List<string> notes)
    {
        // Bus types by id, for each generator's role (the loader re-derives bus types from the buses and the roles, so
        // writing the role of the bus a unit sits on reproduces every type exactly; the v1 writer's rule).
        var typeById = new Dictionary<int, BusType>();
        foreach (Bus b in model.Buses) typeById.TryAdd(b.Id, b.Type);

        // Names: v2 requires one on every generator, demand, line and transformer. An unnamed generator or branch (a
        // MATPOWER unit or branch) gets a name from its position, refused if it would clash with a name already used.
        IReadOnlyList<Generator> fleet = model.AllGenerators;
        List<Branch> branches = model.Branches.Where(x => x.InService).ToList();
        int outOfService = model.Branches.Count - branches.Count;
        string[] genNames = Names(fleet.Select(g => g.Name).ToList(), i => $"unit-{i + 1}", "generator");
        string[] branchNames = Names(branches.Select(x => x.Name).ToList(), i => $"branch-{i + 1}", "branch");
        int namedGens = fleet.Count(g => g.Name is null), namedBranches = branches.Count(x => x.Name is null);

        // The split: branches [0, split) are lines, [split, n) transformer elements (see the class summary).
        int split = TransformerSplit(branches, out int lostWindings);

        int lostCosts = 0, lostGenTemplates = 0, lostBranchTemplates = 0;
        byte[] bytes = Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("schema", V2CaseLoader.NetworkSchemaTag);

            w.WriteStartArray("buses");
            foreach (Bus b in model.Buses)
            {
                string at = $"bus {b.Id}";
                if (!(double.IsFinite(b.BaseKv) && b.BaseKv > 0.0))
                    throw new InvalidDataException($"{at} baseKv is {b.BaseKv.ToString("R", CultureInfo.InvariantCulture)}; a JSON v2 bus needs a base voltage above 0 kV.");
                w.WriteStartObject();
                w.WriteNumber("id", b.Id);
                if (b.Name is not null) w.WriteString("name", b.Name);
                Num(w, "baseKv", b.BaseKv, at + " baseKv");
                w.WriteString("type", TypeWord(b.Type));
                Num(w, "vm", b.Vm, at + " vm");
                Num(w, "vaDeg", b.VaDeg, at + " vaDeg");
                Num(w, "gs", b.Gs, at + " gs");
                Num(w, "bs", b.Bs, at + " bs");
                Num(w, "x", b.X, at + " x");
                Num(w, "y", b.Y, at + " y");
                if (b.Zip is ZipLoad z)
                {
                    w.WriteStartObject("zip");
                    Num(w, "ap", z.Ap, at + " zip ap"); Num(w, "bp", z.Bp, at + " zip bp"); Num(w, "cp", z.Cp, at + " zip cp");
                    Num(w, "aq", z.Aq, at + " zip aq"); Num(w, "bq", z.Bq, at + " zip bq"); Num(w, "cq", z.Cq, at + " zip cq");
                    w.WriteEndObject();
                }
                if (b.VminPu is double lo) Num(w, "vminPu", lo, at + " vminPu");
                if (b.VmaxPu is double hi) Num(w, "vmaxPu", hi, at + " vmaxPu");
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("generators");
            for (int i = 0; i < fleet.Count; i++)
            {
                Generator g = fleet[i];
                string at = $"generator {i} (bus {g.Bus})";
                w.WriteStartObject();
                w.WriteString("name", genNames[i]);
                w.WriteNumber("bus", g.Bus);
                // The schema's technology word: lower case, digits and hyphens (or empty). A type the loader would
                // refuse is refused here, naming it, rather than written into a case that cannot be read back.
                if (!TypeWordPattern().IsMatch(g.Type))
                    throw new InvalidDataException($"{at} has type '{g.Type}'; a JSON v2 technology is lower case letters, digits and hyphens (for example wind-offshore).");
                w.WriteString("type", g.Type);   // always: absent would become "ccgt"
                NonNegative(w, "capacityMva", g.MBase, at + " MBase");
                Num(w, "outputMw", g.Pg, at + " Pg");
                Num(w, "qOutputMvar", g.Qg, at + " Qg");
                Num(w, "voltageSetpoint", g.Vg, at + " Vg");
                Limit(w, "qMinMvar", g.Qmin, at + " Qmin", upper: false);
                Limit(w, "qMaxMvar", g.Qmax, at + " Qmax", upper: true);
                Num(w, "pMinMw", g.Pmin, at + " Pmin");
                Limit(w, "pMaxMw", g.Pmax, at + " Pmax", upper: true);
                NonNegative(w, "inertiaH", g.H, at + " H");
                w.WriteString("role", TypeWord(typeById.GetValueOrDefault(g.Bus)));
                // N321: the unit's own class, as a reference to its local template (null: no class, or one this write
                // cannot carry, counted for the notes below).
                if (g.DynamicsClass is not null)
                {
                    if (bundles?.RefFor(g.DynamicsClass) is string gRef) w.WriteString("template", gRef);
                    else lostGenTemplates++;
                }
                if (g.PowerFlowOnly) w.WriteBoolean("powerFlowOnly", true);
                if (BitConverter.DoubleToInt64Bits(g.UnitTransformerXPu) != 0) NonNegative(w, "unitTransformerXPu", g.UnitTransformerXPu, at + " unitTransformerXPu");
                if (g.ConverterInterfaced is not null || g.ConverterFaultCurrentPu is not null || g.GridForming)
                {
                    w.WriteStartObject("converter");
                    if (g.ConverterInterfaced is bool ci) w.WriteBoolean("interfaced", ci);
                    if (g.ConverterFaultCurrentPu is double k) Num(w, "faultCurrentPu", k, at + " converterFaultCurrentPu");
                    if (g.GridForming) w.WriteBoolean("gridForming", true);
                    w.WriteEndObject();
                }
                // N234: the fault sequence data (0 is the model's "not supplied", which v2 writes by omission).
                if (BitConverter.DoubleToInt64Bits(g.SubtransientXdPu) != 0) Num(w, "subtransientXdPu", g.SubtransientXdPu, at + " SubtransientXdPu");
                if (BitConverter.DoubleToInt64Bits(g.NegativeSeqXPu) != 0) Num(w, "negativeSeqXPu", g.NegativeSeqXPu, at + " NegativeSeqXPu");
                if (!g.InService) w.WriteBoolean("inService", false);
                w.WriteEndObject();
                if (g.Cost is not null || g.ReactiveCost is not null) lostCosts++;
            }
            w.WriteEndArray();

            // One demand row per bus that carries any demand, in bus order (the v1 writer's rule; a -0.0 demand is the
            // one bit pattern a JSON case does not keep, since the loader sums it onto +0.0).
            w.WriteStartArray("demands");
            foreach (Bus b in model.Buses)
            {
                if (BitConverter.DoubleToInt64Bits(b.Pd) == 0 && BitConverter.DoubleToInt64Bits(b.Qd) == 0) continue;
                w.WriteStartObject();
                w.WriteString("name", $"demand-{b.Id.ToString(CultureInfo.InvariantCulture)}");
                w.WriteNumber("bus", b.Id);
                Num(w, "pMw", b.Pd, $"bus {b.Id} Pd");
                Num(w, "qMvar", b.Qd, $"bus {b.Id} Qd");
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("lines");
            for (int i = 0; i < split; i++)
            {
                Branch br = branches[i];
                string at = $"branch {i} ({br.From}-{br.To})";
                w.WriteStartObject();
                w.WriteString("name", branchNames[i]);
                w.WriteNumber("from", br.From);
                w.WriteNumber("to", br.To);
                Num(w, "rPu", br.R, at + " R");
                Num(w, "xPu", br.X, at + " X");
                Num(w, "bPu", br.B, at + " B");
                Num(w, "ratingMva", br.RateAMva, at + " RateA");
                if (BitConverter.DoubleToInt64Bits(br.RateBMva) != 0) Num(w, "ratingBMva", br.RateBMva, at + " RateB");
                if (BitConverter.DoubleToInt64Bits(br.RateCMva) != 0) Num(w, "ratingCMva", br.RateCMva, at + " RateC");
                if (BitConverter.DoubleToInt64Bits(br.TapRatio) != BitConverter.DoubleToInt64Bits(1.0)) Positive(w, "tapRatio", br.TapRatio, at + " TapRatio");
                if (BitConverter.DoubleToInt64Bits(br.PhaseShiftDeg) != 0) Num(w, "phaseShiftDeg", br.PhaseShiftDeg, at + " PhaseShiftDeg");
                if (br.IsTransformer) w.WriteBoolean("isTransformer", true);
                if (BitConverter.DoubleToInt64Bits(br.LengthKm) != 0) Num(w, "lengthKm", br.LengthKm, at + " LengthKm");
                if (br.Equipment is not null) w.WriteString("equipment", br.Equipment);
                if (BitConverter.DoubleToInt64Bits(br.R0) != 0) Num(w, "r0Pu", br.R0, at + " R0");
                if (BitConverter.DoubleToInt64Bits(br.X0) != 0) Num(w, "x0Pu", br.X0, at + " X0");
                // N321: the line's own passive-plant class (see the generator above).
                if (br.DynamicsClass is not null)
                {
                    if (bundles?.RefFor(br.DynamicsClass) is string lRef) w.WriteString("template", lRef);
                    else lostBranchTemplates++;
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (split < branches.Count)
            {
                w.WriteStartArray("transformers");
                for (int i = split; i < branches.Count; i++)
                {
                    Branch br = branches[i];
                    string at = $"branch {i} ({br.From}-{br.To})";
                    w.WriteStartObject();
                    w.WriteString("name", branchNames[i]);
                    w.WriteNumber("from", br.From);
                    w.WriteNumber("to", br.To);
                    Num(w, "rPu", br.R, at + " R");
                    Num(w, "xPu", br.X, at + " X");
                    if (BitConverter.DoubleToInt64Bits(br.TapRatio) != BitConverter.DoubleToInt64Bits(1.0)) Positive(w, "tapRatio", br.TapRatio, at + " TapRatio");
                    if (BitConverter.DoubleToInt64Bits(br.PhaseShiftDeg) != 0) Num(w, "phaseShiftDeg", br.PhaseShiftDeg, at + " PhaseShiftDeg");
                    Num(w, "ratingMva", br.RateAMva, at + " RateA");
                    if (BitConverter.DoubleToInt64Bits(br.RateBMva) != 0) Num(w, "ratingBMva", br.RateBMva, at + " RateB");
                    if (BitConverter.DoubleToInt64Bits(br.RateCMva) != 0) Num(w, "ratingCMva", br.RateCMva, at + " RateC");
                    // N234: the windings (TransformerSplit put a branch here only when it has them, or when it sits after
                    // one that has them and has no zero-sequence data at all), r0 / x0 and the neutral reactances.
                    if (br.FromWinding is WindingConnection wf && br.ToWinding is WindingConnection wt)
                    {
                        w.WriteStartObject("windings");
                        w.WriteString("from", wf.ToString());
                        w.WriteString("to", wt.ToString());
                        w.WriteEndObject();
                    }
                    if (BitConverter.DoubleToInt64Bits(br.R0) != 0) Num(w, "r0Pu", br.R0, at + " R0");
                    if (BitConverter.DoubleToInt64Bits(br.X0) != 0) Num(w, "x0Pu", br.X0, at + " X0");
                    if (BitConverter.DoubleToInt64Bits(br.FromNeutralXPu) != 0 || BitConverter.DoubleToInt64Bits(br.ToNeutralXPu) != 0)
                    {
                        w.WriteStartObject("neutralXPu");
                        if (BitConverter.DoubleToInt64Bits(br.FromNeutralXPu) != 0) Num(w, "from", br.FromNeutralXPu, at + " FromNeutralXPu");
                        if (BitConverter.DoubleToInt64Bits(br.ToNeutralXPu) != 0) Num(w, "to", br.ToNeutralXPu, at + " ToNeutralXPu");
                        w.WriteEndObject();
                    }
                    // N321: the transformer's own passive-plant class (see the generator above).
                    if (br.DynamicsClass is not null)
                    {
                        if (bundles?.RefFor(br.DynamicsClass) is string tRef) w.WriteString("template", tRef);
                        else lostBranchTemplates++;
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            if (model.Oltcs.Count > 0)
            {
                w.WriteStartArray("oltcs");
                foreach (Oltc o in model.Oltcs)
                {
                    w.WriteStartObject();
                    RequireNonBlankName(o.Transformer, "an OLTC's transformer");   // finding 41: same name definition
                    w.WriteString("transformer", o.Transformer);
                    w.WriteNumber("controlsBus", o.ControlsBus);
                    Num(w, "targetVpu", o.TargetVpu, "OLTC targetVpu");
                    Num(w, "minTap", o.MinTap, "OLTC minTap");
                    Num(w, "maxTap", o.MaxTap, "OLTC maxTap");
                    Num(w, "stepPu", o.StepPu, "OLTC stepPu");
                    Num(w, "deadbandPu", o.DeadbandPu, "OLTC deadbandPu");
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            if (model.Boundaries.Count > 0)
            {
                w.WriteStartArray("boundaries");
                foreach (Boundary b in model.Boundaries)
                {
                    w.WriteStartObject();
                    RequireNonBlankName(b.Name, "a boundary");   // finding 41
                    w.WriteString("name", b.Name);
                    Num(w, "limitMw", b.LimitMw, $"boundary {b.Name} limitMw");
                    w.WriteStartArray("branches");
                    foreach (BoundaryBranch bb in b.Branches)
                    {
                        w.WriteStartObject();
                        RequireNonBlankName(bb.Branch, $"boundary '{b.Name}' branch");   // finding 41
                        w.WriteString("branch", bb.Branch);
                        w.WriteNumber("direction", bb.Direction);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }

            // N321: the placed protective devices, in the bundle's order (v1 ProtectionInstanceDto, with the class as a
            // template reference). capacityMw is left out at +0 (the loader's absent value) and targetBranch when there
            // is none, so each reads back as the same device.
            if (bundles is not null && bundles.Devices.Count > 0)
            {
                w.WriteStartArray("protections");
                foreach (ProtectionInstance d in bundles.Devices)
                {
                    string at = $"protective device '{d.Name}'";
                    w.WriteStartObject();
                    w.WriteString("name", d.Name);
                    w.WriteString("template", bundles.RefFor(d.ClassName)!);   // PlanBundles checked every device's class
                    w.WriteNumber("monitorBus", d.MonitorBus);
                    if (BitConverter.DoubleToInt64Bits(d.CapacityMw) != 0) Num(w, "capacityMw", d.CapacityMw, at + " capacityMw");
                    if (d.TargetBranch is not null) w.WriteString("targetBranch", d.TargetBranch);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        });

        if (namedGens > 0)
            notes.Add($"{namedGens} unnamed generator(s) written as unit-<n> (their position in the fleet, from 1): JSON v2 requires a generator name.");
        if (namedBranches > 0)
            notes.Add($"{namedBranches} unnamed branch(es) written as branch-<n> (their position among the in-service branches, from 1): JSON v2 requires a line or transformer name.");
        if (outOfService > 0)
            notes.Add($"{outOfService} out-of-service branch(es) not written (the v2 loader does not read an out-of-service line or transformer yet); the written case solves the same, but its branch list is shorter.");
        if (lostWindings > 0)
            notes.Add($"{lostWindings} branch(es) carry transformer winding data that cannot be written without reordering the branches (a v2 transformer element always follows every line); they are written as lines in place and their winding connections and neutral reactances are not written, so earth-fault studies of the written case will differ.");
        if (lostCosts > 0)
            notes.Add($"{lostCosts} generator(s) carry a MATPOWER cost curve (gencost), which JSON v2 cannot hold yet (F19); no solver uses it today.");
        // N321: an element class is lost only when no bundle is written (none exists, or it cannot be written exactly; the
        // reason is in a note PlanBundles added) or when it names no class of the written bundle.
        if (lostGenTemplates + lostBranchTemplates > 0)
            notes.Add($"{lostGenTemplates + lostBranchTemplates} element template reference(s) ({lostGenTemplates} generator, {lostBranchTemplates} branch) not written: "
                + (bundles is null
                    ? "they name asset classes of a bundle that is not written as v2 templates (see the other notes)."
                    : "they name no class of the case's asset-dynamics or protection bundle, so no template of the written case could carry them."));
        if (bundles is null && model.AssetDynamics is not null)
            notes.Add("the authored asset-dynamics bundle (asset classes and binding rules) is not written as v2 templates and bindings; add them to the written case by hand if it needs them.");
        if (bundles is null && model.Protections is not null)
            notes.Add("the protection bundle (placed devices and their classes) is not written; add it to the written case by hand if it needs it.");
        if (bundles is not null && bundles.Respelled > 0)
            notes.Add($"{bundles.Respelled} class reference(s) spelled a class name in another letter case (v1 matches class names ignoring case, v2 template references do not); written with the class's own spelling, so the written case reads back with that spelling.");
        return bytes;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // N321: the asset-dynamics and protection bundles
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The revision every written local template has (the v1 projection's, which the loader's class-name rule
    /// reads back to the bare class name).</summary>
    internal const int LocalRev = 1;

    /// <summary>A local template id after its prefix: dot-separated segments of letters, digits, '_' and '-', each
    /// starting with a letter or digit (case.schema.json templateId, less the <c>gridsim.local.</c> prefix).</summary>
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_-]*(\.[A-Za-z0-9][A-Za-z0-9_-]*)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocalName();

    /// <summary>A v2 binding match type (case.schema.json binding.matchType).</summary>
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]*\*?\z", RegexOptions.CultureInvariant)]
    private static partial Regex MatchTypePattern();

    /// <summary>The pinned reference to the local template of class <paramref name="className"/>.</summary>
    private static string LocalRef(string className) =>
        V2CaseLoader.LocalIdPrefix + className + "@" + LocalRev.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The v2 template kind of an asset-class kind: <see cref="V2CaseLoader.V1KindOf"/> read backwards (the loader maps
    /// gridFollowingInverter, gridFormingInverter, oltc, lineType and protection to the v1 kinds the model's
    /// <see cref="AssetKind"/> parses from). Null for a kind no template kind becomes (SynchronousMachine: the class
    /// library refuses it anyway, R073 / N268). A test pins this table against the loader's.
    /// </summary>
    internal static string? V2KindOf(AssetKind kind) => kind switch
    {
        AssetKind.GridFollowingInverter => "gridFollowingInverter",
        AssetKind.GridFormingInverter => "gridFormingInverter",
        AssetKind.Transformer => "oltc",
        AssetKind.Line => "lineType",
        AssetKind.Protection => "protection",
        _ => null,
    };

    /// <summary>What one write carries of the bundles: the classes (library order), the binding rules (rule order, each
    /// with the class's own spelling) and the placed devices, plus the class lookup element references go through.</summary>
    private sealed class Bundles
    {
        public required IReadOnlyList<AssetClass> Classes { get; init; }
        public required IReadOnlyList<(string MatchType, string ClassName)> Rules { get; init; }
        public required IReadOnlyList<ProtectionInstance> Devices { get; init; }
        public required IReadOnlyDictionary<string, AssetClass> ByName { get; init; }   // ignoring case, as v1 matches

        /// <summary>References (rules, devices, elements) whose spelling differed from the class's own, in letter case.</summary>
        public int Respelled { get; set; }

        /// <summary>The template reference for a class name, or null when no class of the bundle has that name. The
        /// class's own spelling is written (v2 references are exact; v1 matched ignoring case), counted when it differs.</summary>
        public string? RefFor(string className)
        {
            if (!ByName.TryGetValue(className, out AssetClass? c)) return null;
            if (!string.Equals(c.Name, className, StringComparison.Ordinal)) Respelled++;
            return LocalRef(c.Name);
        }
    }

    /// <summary>
    /// N321: decide what of the model's bundles this write carries. Null when the model has none, or when they cannot be
    /// written exactly (a class name that cannot be a local template id, a class kind with no template kind, a binding
    /// match type v2 cannot hold); in that case a note names every reason and NOTHING of the bundles is written (a part
    /// of a bundle would load as a different bundle). A bundle that contradicts itself is refused
    /// (<see cref="InvalidDataException"/>): two different classes under one name, a binding rule or a placed device that
    /// names no class.
    /// </summary>
    private static Bundles? PlanBundles(GridModel model, List<string> notes)
    {
        if (model.AssetDynamics is null && model.Protections is null) return null;

        // The classes: the asset-dynamics library, then any class only the protection library holds (the v1 loader builds
        // both from the one asset-classes.json, so normally they are the same classes). Names compare ignoring case, as
        // the libraries do.
        var classes = new List<AssetClass>();
        var byName = new Dictionary<string, AssetClass>(StringComparer.OrdinalIgnoreCase);
        void Add(AssetClass c)
        {
            if (byName.TryGetValue(c.Name, out AssetClass? have))
            {
                if (!ReferenceEquals(have, c) && !SameClass(have, c))
                    throw new InvalidDataException($"the model's asset-dynamics and protection libraries hold two different classes named '{c.Name}'; a JSON v2 case has one template per name.");
                return;
            }
            byName[c.Name] = c;
            classes.Add(c);
        }
        foreach (AssetClass c in model.AssetDynamics?.Library.Classes ?? Enumerable.Empty<AssetClass>()) Add(c);
        foreach (AssetClass c in model.Protections?.Library.Classes ?? Enumerable.Empty<AssetClass>()) Add(c);

        // What a v2 case cannot hold exactly. Every reason is collected, so the note names them all.
        var cannot = new List<string>();
        foreach (AssetClass c in classes)
        {
            if (!LocalName().IsMatch(c.Name))
                cannot.Add($"class '{c.Name}' cannot be a local template id ({V2CaseLoader.LocalIdPrefix}<name>, the name being dot-separated segments of letters, digits, '_' and '-')");
            if (V2KindOf(c.Kind) is null)
                cannot.Add($"class '{c.Name}' is kind {c.Kind}, which no v2 template kind becomes");
        }
        // The binding rules, each with its class's own spelling (v2 references are exact; v1 matched ignoring case).
        var rules = new List<(string MatchType, string ClassName)>();
        int relowered = 0, respelledRules = 0;
        foreach (AssetBinding r in model.AssetDynamics?.Bindings.Rules ?? Array.Empty<AssetBinding>())
        {
            if (!byName.TryGetValue(r.ClassName, out AssetClass? c))
                throw new InvalidDataException($"the binding rule for '{r.MatchType}' names class '{r.ClassName}', which is no class of the model's bundles; a JSON v2 binding must name a template of the case.");
            // v1 matches a type ignoring case; v2 writes match types in lower case. Lower-casing an ASCII match type
            // keeps every match the rule makes (AssetBindingSet.Resolve compares ignoring case).
            string mt = r.MatchType.ToLowerInvariant();
            if (!MatchTypePattern().IsMatch(mt))
            {
                cannot.Add($"binding match type '{r.MatchType}' is not a v2 match type (lower-case letters, digits and '-', with an optional trailing '*')");
                continue;
            }
            if (!string.Equals(mt, r.MatchType, StringComparison.Ordinal)) relowered++;
            if (!string.Equals(c.Name, r.ClassName, StringComparison.Ordinal)) respelledRules++;
            rules.Add((mt, c.Name));
        }
        if (cannot.Count > 0)
        {
            notes.Add("the asset-dynamics / protection bundle cannot be written exactly as JSON v2 templates, so none of it is written: "
                + string.Join("; ", cannot) + ".");
            return null;
        }

        var devices = new List<ProtectionInstance>();
        foreach (ProtectionInstance d in model.Protections?.Instances ?? Array.Empty<ProtectionInstance>())
        {
            if (!byName.ContainsKey(d.ClassName))
                throw new InvalidDataException($"protective device '{d.Name}' uses class '{d.ClassName}', which is no class of the model's bundles; a JSON v2 device must name a template of the case.");
            devices.Add(d);
        }

        // The rules' respellings are counted here; the elements' and devices' are counted by RefFor as the network file
        // is written (before the notes are made).
        var plan = new Bundles { Classes = classes, Rules = rules, Devices = devices, ByName = byName, Respelled = respelledRules };

        if (relowered > 0)
            notes.Add($"{relowered} binding match type(s) written in lower case (JSON v2 match types are lower case; the rule matches exactly the same types, ignoring case as before), so the written case reads back with the lower-case spelling.");
        if (model.AssetDynamics is null && classes.Count > 0)
            notes.Add("the protection bundle's classes are written as local templates; the written case loads them as an asset-class library too (with no binding rules), as every JSON case with classes does.");
        return plan;
    }

    /// <summary>True when two classes are the same content: name (ignoring case), kind, mode, every parameter (bits, in
    /// order) and every provenance entry (in order).</summary>
    private static bool SameClass(AssetClass a, AssetClass b)
    {
        if (!string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase) || a.Kind != b.Kind || !string.Equals(a.Mode, b.Mode, StringComparison.Ordinal))
            return false;
        if (a.Params.Count != b.Params.Count || a.Provenance.Count != b.Provenance.Count) return false;
        foreach (((string ka, double va), (string kb, double vb)) in a.Params.Zip(b.Params))
            if (!string.Equals(ka, kb, StringComparison.Ordinal) || BitConverter.DoubleToInt64Bits(va) != BitConverter.DoubleToInt64Bits(vb)) return false;
        foreach (((string ka, AssetClassProvenance pa), (string kb, AssetClassProvenance pb)) in a.Provenance.Zip(b.Provenance))
            if (!string.Equals(ka, kb, StringComparison.Ordinal) || pa != pb) return false;
        return true;
    }

    /// <summary>
    /// Where the transformer elements start (see the class summary). <c>n</c> when no branch carries winding data. A
    /// branch "needs" a transformer element when it has a winding or a neutral reactance; a branch "can be" one when the
    /// v2 transformer element and the v2 loader's N234 rules represent it exactly: a transformer with no charging
    /// (B is +0), no length, no equipment class, both windings or neither, r0 / x0 only with windings, x0 present
    /// (finite, above 0) whenever r0 is, r0 finite and not negative, and neutral reactances only on a WyeGrounded side
    /// (finite, not negative). The split is the later of the first branch that needs one and the start of the longest
    /// trailing run of branches that can be one; <paramref name="lost"/> counts the branches that need one before it.
    /// </summary>
    internal static int TransformerSplit(IReadOnlyList<Branch> branches, out int lost)
    {
        int n = branches.Count;
        int firstNeed = n;
        for (int i = 0; i < n; i++) if (NeedsTransformerElement(branches[i])) { firstNeed = i; break; }
        int suffix = n;
        while (suffix > 0 && CanBeTransformerElement(branches[suffix - 1])) suffix--;
        int split = Math.Max(firstNeed, suffix);
        lost = 0;
        for (int i = 0; i < split; i++) if (NeedsTransformerElement(branches[i])) lost++;
        return split;
    }

    private static bool NeedsTransformerElement(Branch b) =>
        b.FromWinding is not null || b.ToWinding is not null
        || BitConverter.DoubleToInt64Bits(b.FromNeutralXPu) != 0 || BitConverter.DoubleToInt64Bits(b.ToNeutralXPu) != 0;

    private static bool CanBeTransformerElement(Branch b)
    {
        if (!b.IsTransformer || BitConverter.DoubleToInt64Bits(b.B) != 0 || BitConverter.DoubleToInt64Bits(b.LengthKm) != 0 || b.Equipment is not null)
            return false;
        if (!(b.TapRatio > 0.0) || !double.IsFinite(b.TapRatio)) return false;
        bool windings = b.FromWinding is not null && b.ToWinding is not null;
        if ((b.FromWinding is null) != (b.ToWinding is null)) return false;
        bool hasR0 = BitConverter.DoubleToInt64Bits(b.R0) != 0, hasX0 = BitConverter.DoubleToInt64Bits(b.X0) != 0;
        bool hasNeutral = BitConverter.DoubleToInt64Bits(b.FromNeutralXPu) != 0 || BitConverter.DoubleToInt64Bits(b.ToNeutralXPu) != 0;
        if (!windings && (hasR0 || hasX0 || hasNeutral)) return false;
        if (hasR0 && !hasX0) return false;
        if (hasR0 && !(double.IsFinite(b.R0) && b.R0 >= 0.0)) return false;
        if (hasX0 && !(double.IsFinite(b.X0) && b.X0 > 0.0)) return false;
        if (BitConverter.DoubleToInt64Bits(b.FromNeutralXPu) != 0
            && !(b.FromWinding == WindingConnection.WyeGrounded && double.IsFinite(b.FromNeutralXPu) && b.FromNeutralXPu >= 0.0)) return false;
        if (BitConverter.DoubleToInt64Bits(b.ToNeutralXPu) != 0
            && !(b.ToWinding == WindingConnection.WyeGrounded && double.IsFinite(b.ToNeutralXPu) && b.ToNeutralXPu >= 0.0)) return false;
        return true;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Each element's written name: its own, or <paramref name="synth"/>(index) when it has none. A synthesised
    /// name that another element already uses is refused (never two elements with one name).</summary>
    private static string[] Names(IReadOnlyList<string?> names, Func<int, string> synth, string what)
    {
        var used = new HashSet<string>(names.OfType<string>(), StringComparer.Ordinal);
        var result = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] is string own)
            {
                // Bug hunt 2026-10-29, finding 41: the v2 schema's name needs a non-blank character, so a name of "" or
                // only blanks (which the model, the bridge and the importers allow) is refused here, naming the
                // element, instead of being written into a case V2CaseLoader then refuses at its schema stage.
                RequireNonBlankName(own, $"{what} {i}");
                result[i] = own;
                continue;
            }
            string s = synth(i);
            if (!used.Add(s))
                throw new InvalidDataException($"{what} {i} has no name and the name it would be given, '{s}', is already used by another {what}; name it.");
            result[i] = s;
        }
        return result;
    }

    /// <summary>Finding 41: refuse a name the v2 schema's <c>$defs/name</c> (pattern "\S") would refuse.</summary>
    private static void RequireNonBlankName(string? name, string who)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException($"{who} has the name '{name}', which is empty or only blanks; a v2 case name needs at least one non-blank character. Name it.");
    }

    private static string TypeWord(BusType t) => t switch { BusType.Slack => "slack", BusType.Pv => "pv", _ => "pq" };

    private static byte[] Json(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, WriterOptions)) body(w);
        ms.WriteByte((byte)'\n');   // a final newline: a text file, the same bytes on every OS
        return ms.ToArray();
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        fs.Write(bytes);
    }

    // A number the loader needs finite: refused, naming it, when it is not.
    private static void Num(Utf8JsonWriter w, string name, double value, string what)
    {
        if (!double.IsFinite(value))
            throw new InvalidDataException($"{what} is {value.ToString(CultureInfo.InvariantCulture)}, which a JSON v2 case cannot hold.");
        w.WriteNumber(name, value);
    }

    // A generator limit: finite as a number, unlimited as the string "Infinity" (an upper limit) or "-Infinity" (a
    // lower one), NaN refused. The schema admits only the infinity on the open side, so the other one is refused.
    private static void Limit(Utf8JsonWriter w, string name, double value, string what, bool upper)
    {
        if (double.IsPositiveInfinity(value) && upper) w.WriteString(name, "Infinity");
        else if (double.IsNegativeInfinity(value) && !upper) w.WriteString(name, "-Infinity");
        else if (double.IsInfinity(value))
            throw new InvalidDataException($"{what} is {value.ToString(CultureInfo.InvariantCulture)}, which a JSON v2 case cannot hold for {(upper ? "an upper" : "a lower")} limit.");
        else Num(w, name, value, what);
    }

    // A number the schema requires to be 0 or above.
    private static void NonNegative(Utf8JsonWriter w, string name, double value, string what)
    {
        if (value < 0)
            throw new InvalidDataException($"{what} is {value.ToString(CultureInfo.InvariantCulture)}; a JSON v2 case needs it 0 or above.");
        Num(w, name, value, what);
    }

    // A number the schema requires to be above 0.
    private static void Positive(Utf8JsonWriter w, string name, double value, string what)
    {
        if (!(value > 0) && double.IsFinite(value))
            throw new InvalidDataException($"{what} is {value.ToString(CultureInfo.InvariantCulture)}; a JSON v2 case needs it above 0.");
        Num(w, name, value, what);
    }

    /// <summary>A generator technology word (network.schema.json generators[].type): empty, or lower case letters,
    /// digits and hyphens starting with a letter.</summary>
    [GeneratedRegex(@"\A([a-z][a-z0-9-]*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex TypeWordPattern();
}
