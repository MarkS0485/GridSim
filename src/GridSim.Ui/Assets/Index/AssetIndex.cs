using System.Globalization;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.IO.Json.Overlay;
using GridSim.Core.IO.Json.Schema;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Presentation;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Assets.Keys;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Overlay;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Assets.Index;

/// <summary>
/// UI programme ASSETS B1 (plan section 3.3, Assets/Index): everything the ASSETS tab knows about the running model's
/// assets, from two sources: the solved model (always) and, when the model was read from a case file (a v1 folder or a
/// v2 manifest), that case opened READ-ONLY in the case editor's engine (CaseSession: the documents as written, the record
/// shapes and field specs of the schema, and the ReferenceIndex of who refers to what). The two are joined by the
/// <see cref="ModelRecordMap"/>, checked as it is built.
/// <para>Read-only: the session is opened and read, never changed (no transaction is ever begun on it), and nothing here
/// writes a file. Nothing in the solver is touched: the model is only read.</para>
/// <para>B2 reads it for the tree's new class nodes (<see cref="AssetTree.Build(GridModel?, AssetIndex?)"/>), the
/// PROPERTIES sub-tab (<see cref="Properties"/>) and the CONNECTIONS sub-tab (<see cref="Connections"/>).</para>
/// </summary>
public sealed class AssetIndex
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly Dictionary<int, int> _firstBus = new();   // bus id -> first index in the model's bus list

    private AssetIndex(GridModel model, AssetCaseSource source)
    {
        Model = model;
        Source = source;
        for (int i = 0; i < model.Buses.Count; i++) _firstBus.TryAdd(model.Buses[i].Id, i);
        Classes = ModelRecordMap.ClassesOf(model);
        Bindings = ModelRecordMap.BindingsOf(model);
        Protections = ModelRecordMap.ProtectionsOf(model);
    }

    /// <summary>The model the index was built for (the first frame's model of this structure).</summary>
    public GridModel Model { get; }

    /// <summary>What the model was read from.</summary>
    public AssetCaseSource Source { get; }

    /// <summary>The case opened read-only, or null (not a case file, or it could not be opened).</summary>
    internal CaseSession? Session { get; private set; }

    /// <summary>Who refers to what in the case, or null without a case.</summary>
    internal ReferenceIndex? References { get; private set; }

    /// <summary>Model element to case record and back, or null without a case (or when the case does not build the
    /// model; <see cref="CaseNote"/> says why).</summary>
    public ModelRecordMap? Map { get; private set; }

    /// <summary>True when the case's records are mapped (PROPERTIES and the where-used list can be shown).</summary>
    public bool HasCase => Map is not null;

    /// <summary>Why there are no case records: the source is not a case file (plan Q9), or the case could not be opened
    /// or does not build the running model. Null when <see cref="HasCase"/>, and for an undeclared source.</summary>
    public string? CaseNote { get; private set; }

    /// <summary>The model's asset classes, in library order.</summary>
    public IReadOnlyList<AssetClass> Classes { get; }

    /// <summary>The model's binding rules, in order.</summary>
    public IReadOnlyList<AssetBinding> Bindings { get; }

    /// <summary>The model's placed protections, in order.</summary>
    public IReadOnlyList<GridSim.Core.Assets.Protection.ProtectionInstance> Protections { get; }

    /// <summary>
    /// The case's asset overlay as the ASSETS tab shows it (UI programme ASSETS slice 5-view), or null when the shell
    /// handed none (a v1 case, a v2 case without "assets", any other source). Carried, not used by the solver: nothing
    /// above (the map, PROPERTIES, CONNECTIONS) reads it.
    /// </summary>
    public OverlayIndex? Overlay { get; private set; }

    /// <summary>Every demand record (empty without a case).</summary>
    public IReadOnlyList<DemandRecord> Demands => Map?.Demands ?? (IReadOnlyList<DemandRecord>)Array.Empty<DemandRecord>();

    /// <summary>
    /// Build the index of <paramref name="model"/> read from <paramref name="source"/>. For a case file the case is opened
    /// read-only under <paramref name="licence"/> (the gate the load passed) and mapped; a case that cannot be opened, or
    /// whose records do not build this model, gives an index of the model alone with <see cref="CaseNote"/> saying why
    /// (shown beside the sub-tabs; never a record shown for the wrong element).
    /// <para>UI programme ASSETS slice 5-view: <paramref name="overlay"/> is the asset overlay the v2 loader carried for
    /// this model (null for none) and <paramref name="overlayProblems"/> the overlay checker's problems for it (none for an
    /// overlay the loader accepted). The overlay is indexed last, against everything above, and changes none of it.</para>
    /// </summary>
    public static AssetIndex Build(GridModel model, AssetCaseSource source, LicenceGate licence,
        CaseAssetOverlay? overlay = null, IReadOnlyList<SchemaError>? overlayProblems = null)
    {
        AssetIndex ix = BuildCase(model, source, licence);
        if (overlay is not null) ix.Overlay = OverlayIndex.Build(overlay, ix, overlayProblems);
        return ix;
    }

    // The B1 build: the model and, for a case file, the case opened read-only and mapped.
    private static AssetIndex BuildCase(GridModel model, AssetCaseSource source, LicenceGate licence)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(licence);
        var ix = new AssetIndex(model, source);
        if (!source.IsCaseFile)
        {
            ix.CaseNote = source.NotACaseReason;
            return ix;
        }
        try
        {
            CaseSession s = source.Kind == AssetSourceKind.V1Folder
                ? CaseSession.OpenV1(source.Path!, licence)
                : CaseSession.OpenV2(source.Path!, licence);
            ix.Session = s;
            ix.References = ReferenceIndex.Build(s);
            ix.Map = ModelRecordMap.Build(model, s);
        }
        catch (Exception ex) when (ex is CaseLicenceDeniedException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or JsonException)
        {
            // A refused licence, a refused map or an unreadable case (InvalidDataException), a missing or locked file
            // (IOException, UnauthorizedAccessException), malformed JSON. Anything else is a bug and is not swallowed.
            ix.Session = null;
            ix.References = null;
            ix.Map = null;
            ix.CaseNote = "PROPERTIES is not shown: " + ex.Message;
        }
        return ix;
    }

    /// <summary>The explorer tree of this index: the A3 tree of the model, then the B2 class nodes that have members.</summary>
    public AssetTree BuildTree() => AssetTree.Build(Model, this);

    // ===================================================================================================================
    // Keys
    // ===================================================================================================================

    /// <summary>True when the in-service unit at <paramref name="index"/> is an interconnector link: the one Core rule,
    /// <see cref="FuelTaxonomy.IsInterconnectorLink"/> (bug #28: a grid infeed is not a link).</summary>
    public static bool IsLink(Generator g) => FuelTaxonomy.IsInterconnectorLink(g.Type);

    /// <summary>True when the unit is plant, the members of the GENERATORS and GENERATOR PHASORS pages: neither an
    /// interconnector link nor a grid infeed (<see cref="FuelTaxonomy.IsGridInfeed"/>, a power-flow device at a model
    /// boundary). This is exactly the membership those pages have always had (every unit outside the imported-energy
    /// category), so no generator figure moved with bug #28; only the link lists lost the grid infeeds. A grid infeed is
    /// reached from its substation page's "unit on this bus" row.</summary>
    public static bool IsPlant(Generator g) => !IsLink(g) && !FuelTaxonomy.IsGridInfeed(g.Type);

    /// <summary>The key of boundary <paramref name="index"/>.</summary>
    public AssetKey BoundaryKey(int index) => AssetKey.ForModel(AssetKeyKind.Boundary, ModelRef.Ordinal(index, Model.Boundaries[index].Name));

    /// <summary>The key of protection <paramref name="index"/> (tied to its monitored bus).</summary>
    public AssetKey ProtectionKey(int index) =>
        AssetKey.ForModel(AssetKeyKind.Protection, ModelRef.Ordinal(index, Protections[index].Name, Protections[index].MonitorBus));

    /// <summary>The key of asset class <paramref name="index"/>.</summary>
    public AssetKey ClassKey(int index) => AssetKey.ForModel(AssetKeyKind.AssetClass, ModelRef.Ordinal(index, Classes[index].Name));

    /// <summary>The key of binding rule <paramref name="index"/> (named by its match type).</summary>
    public AssetKey BindingKey(int index) => AssetKey.ForModel(AssetKeyKind.AssetBinding, ModelRef.Ordinal(index, Bindings[index].MatchType));

    /// <summary>The key of a demand record.</summary>
    public static AssetKey DemandKey(DemandRecord d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return AssetKey.ForCase(AssetKeyKind.Demand, d.Record);
    }

    /// <summary>The key of the generator at <paramref name="fleetIndex"/> of the whole fleet: its in-service generator or
    /// interconnector key, or its out-of-service key.</summary>
    public AssetKey FleetKey(int fleetIndex)
    {
        Generator g = Model.AllGenerators[fleetIndex];
        if (!g.InService) return AssetKey.OutOfService(Model, fleetIndex);
        int inService = 0;
        for (int k = 0; k < fleetIndex; k++) if (Model.AllGenerators[k].InService) inService++;
        return AssetKey.Unit(Model, inService, IsLink(g));
    }

    /// <summary>The asset class index whose name is <paramref name="name"/> (ignoring case, as the class library looks
    /// classes up), or -1.</summary>
    public int ClassIndexOf(string? name)
    {
        if (name is null) return -1;
        for (int i = 0; i < Classes.Count; i++)
            if (string.Equals(Classes[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The first branch of <paramref name="name"/> (ordinal, the boundary and protection lookup), or -1.</summary>
    public int BranchIndexOf(string? name)
    {
        if (name is null) return -1;
        for (int i = 0; i < Model.Branches.Count; i++)
            if (string.Equals(Model.Branches[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>The first bus of id <paramref name="busId"/> in the model's bus list, or -1.</summary>
    public int BusIndexOf(int busId) => _firstBus.TryGetValue(busId, out int i) ? i : -1;

    /// <summary>
    /// The case record <paramref name="key"/> stands for, or null (no case; a node that is not a record; an element that
    /// is no longer the one the key names). The model reference is confirmed first, by the AssetLookup rules for units
    /// and branches and by position and name for the listed elements.
    /// </summary>
    public CaseRef? RecordOf(AssetKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Map is null) return null;
        if (key.Kind == AssetKeyKind.Demand) return key.Case;
        if (key.Model is not ModelRef r) return null;
        switch (key.Kind)
        {
            case AssetKeyKind.Substation:
                return BusIndexOf(r.BusA) is int bi and >= 0 ? Map.BusRecord(bi) : null;
            case AssetKeyKind.Generator:
            case AssetKeyKind.Interconnector:
                return r.FindGenerator(Model) is { } g ? Map.GeneratorRecord(g.Index) : null;
            case AssetKeyKind.OutOfServiceUnit:
                return Same(r, Model.AllGenerators, r.Index, x => (x.Bus, x.Name)) ? Map.FleetRecord(r.Index) : null;
            case AssetKeyKind.Circuit:
            case AssetKeyKind.Transformer:
                return r.FindBranch(Model) is { } b ? Map.BranchRecord(b.Index) : null;
            case AssetKeyKind.TapChanger:
                return Same(r, Model.Oltcs, r.Index, x => (x.ControlsBus, (string?)x.Transformer)) ? Map.OltcRecord(r.Index) : null;
            case AssetKeyKind.Boundary:
                return r.Index >= 0 && r.Index < Model.Boundaries.Count && Model.Boundaries[r.Index].Name == r.Name ? Map.BoundaryRecord(r.Index) : null;
            case AssetKeyKind.Protection:
                return r.Index >= 0 && r.Index < Protections.Count && Protections[r.Index].Name == r.Name ? Map.ProtectionRecord(r.Index) : null;
            case AssetKeyKind.AssetClass:
                return r.Index >= 0 && r.Index < Classes.Count && Classes[r.Index].Name == r.Name ? Map.AssetClassRecord(r.Index) : null;
            case AssetKeyKind.AssetBinding:
                return r.Index >= 0 && r.Index < Bindings.Count && Bindings[r.Index].MatchType == r.Name ? Map.BindingRecord(r.Index) : null;
            default:
                return null;
        }
    }

    // True when the list element at index still has the bus and name the reference was made with.
    private static bool Same<T>(ModelRef r, IReadOnlyList<T> list, int index, Func<T, (int Bus, string? Name)> id)
    {
        if (index < 0 || index >= list.Count) return false;
        (int bus, string? name) = id(list[index]);
        return bus == r.BusA && string.Equals(name, r.Name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The key of the element <paramref name="record"/> built, or null (no case; a record that builds no element). A record
    /// nested inside another (one branch of a boundary) gives the key of the record that holds it.
    /// </summary>
    public AssetKey? KeyOf(CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Map is null) return null;
        if (Map.ElementOf(record) is not { } e)
        {
            // A nested record: "/3/branches" (v1) or "/boundaries/3/branches" (v2) belongs to the record at "/3" of "".
            int cut = record.ArrayPointer.LastIndexOf('/');
            if (cut < 0) return null;
            string parent = record.ArrayPointer[..cut];
            int slash = parent.LastIndexOf('/');
            if (slash < 0 || !int.TryParse(parent[(slash + 1)..], NumberStyles.None, Inv, out int at)) return null;
            return KeyOf(new CaseRef(record.Document, parent[..slash], at, record.FileName));
        }
        return e.Kind switch
        {
            AssetKeyKind.Substation => AssetKey.Substation(Model.Buses[e.Index].Id),
            AssetKeyKind.OutOfServiceUnit => FleetKey(e.Index),
            AssetKeyKind.Circuit => AssetKey.Branch(Model, e.Index),
            AssetKeyKind.TapChanger => AssetKey.TapChanger(Model, e.Index),
            AssetKeyKind.Boundary => BoundaryKey(e.Index),
            AssetKeyKind.Protection => ProtectionKey(e.Index),
            AssetKeyKind.AssetClass => ClassKey(e.Index),
            AssetKeyKind.AssetBinding => BindingKey(e.Index),
            AssetKeyKind.Demand => AssetKey.ForCase(AssetKeyKind.Demand, record),
            _ => null,
        };
    }

    // ===================================================================================================================
    // PROPERTIES
    // ===================================================================================================================

    /// <summary>
    /// The PROPERTIES content of <paramref name="key"/>: its record's fields as written (raw literal, or absent and what
    /// absence means), with the unit and help text of the schema, read-only; and, where the solver has a value of its own
    /// for a field, that value from <paramref name="frame"/> beside it under a caption saying what it is. Null when the key
    /// has no record (no case, or not a record).
    /// </summary>
    public AssetProperties? Properties(AssetKey key, SimFrame? frame)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (RecordOf(key) is not CaseRef rec || Session is not CaseSession s) return null;
        CaseDocument doc = s.Documents[rec.Document];
        JsonSourceNode node = doc.Text.Resolve(rec.Pointer)
            ?? throw new InvalidOperationException($"{rec.Display}: the record is not in the read-only case (internal error).");
        RecordShape shape = s.CollectionAt(rec.Document, rec.ArrayPointer)?.Shape
            ?? throw new InvalidOperationException($"{rec.Display}: no record collection at {rec.ArrayPointer} (internal error).");
        Dictionary<string, (string Caption, string Value)> solved = frame is null
            ? new Dictionary<string, (string, string)>(StringComparer.Ordinal)
            : Solved(key, node, doc, frame);

        var rows = new List<PropertyRow>(shape.Fields.Count);
        var matched = new HashSet<JsonSourceMember>();
        foreach (FieldSpec f in shape.Fields)
        {
            JsonSourceMember? m = node.FindMember(f.Name, doc.KeyComparer);
            if (m is not null) matched.Add(m);
            (string label, string unit) = PropertyRow.SplitTitle(f.Title);
            (string caption, string value) = solved.TryGetValue(f.Name, out var sv) ? sv : ("", "");
            rows.Add(new PropertyRow(f.Name, label, unit, f.Description, f.Kind.ToString(), f.Required, m is not null,
                m is null ? "" : doc.Text.RawText(m.Value), f.AbsentText, caption, value));
        }
        // A member the schema does not list (a loaded case has none: the loader refuses unknown members) is still shown.
        foreach (JsonSourceMember m in node.Members)
            if (!matched.Contains(m))
                rows.Add(new PropertyRow(m.Name, m.Name, "", "Not a field of this record's schema.", "Unknown", false, true,
                    doc.Text.RawText(m.Value), "none", "", ""));

        string name = RecordRead.Str(node, "name", doc.KeyComparer) is { } n ? " '" + n + "'" : "";
        string note = frame is null
            ? "No solved frame yet: the solved column fills in when the model is solved."
            : "Solved values from tick " + frame.Tick.ToString(Inv) + ".";
        return new AssetProperties(rec, rec.Display + name, shape.Title, note, rows);
    }

    /// <summary>The shortest round-trip invariant text of a number (the case editor's number format).</summary>
    public static string Num(double v) => JsonLiteral.FormatNumber(v);

    // The solver's own value for the fields that have one, read from the frame's model (the model the solver used this
    // frame) or its result, with the caption that says what it is. Lines carry the loader's per-circuit equivalent (r, x
    // divided by the circuit count, b and the ratings multiplied by it), labelled so (plan section 3.5).
    private static Dictionary<string, (string Caption, string Value)> Solved(AssetKey key, JsonSourceNode node, CaseDocument doc, SimFrame frame)
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        GridModel m = frame.Model;
        const string AsSolved = "as solved";
        switch (key.Kind)
        {
            case AssetKeyKind.Substation:
            {
                int id = key.Model!.BusA;
                IReadOnlyList<int> ids = frame.Result.BusIds;
                for (int k = 0; k < ids.Count; k++)
                {
                    if (ids[k] != id) continue;
                    d["vm"] = (AsSolved, Num(frame.Result.Vm[k]));
                    d["vaDeg"] = (AsSolved, Num(frame.Result.VaDeg[k]));
                    break;
                }
                break;
            }
            case AssetKeyKind.Generator:
            case AssetKeyKind.Interconnector:
                if (key.Model!.FindGenerator(m) is { } u)
                {
                    Generator g = u.Unit;
                    d["outputMw"] = (AsSolved, Num(g.Pg));
                    d["qOutputMvar"] = (AsSolved, Num(g.Qg));
                    d["voltageSetpoint"] = (AsSolved, Num(g.Vg));
                    d["capacityMva"] = (AsSolved, Num(g.MBase));
                    d["qMinMvar"] = (AsSolved, Num(g.Qmin));
                    d["qMaxMvar"] = (AsSolved, Num(g.Qmax));
                    d["pMinMw"] = (AsSolved, Num(g.Pmin));
                    d["pMaxMw"] = (AsSolved, Num(g.Pmax));
                    d["inertiaH"] = (AsSolved, Num(g.H));
                }
                break;
            case AssetKeyKind.Circuit:
            case AssetKeyKind.Transformer:
                if (key.Model!.FindBranch(m) is { } br)
                {
                    Branch b = br.Branch;
                    string cap = AsSolved;
                    if (!b.IsTransformer)
                    {
                        int c = RecordRead.Int(node, "circuits", doc.KeyComparer) ?? 1;
                        cap = "as solved: per-circuit equivalent of " + c.ToString(Inv) + (c == 1 ? " circuit" : " circuits");
                        d["bPu"] = (cap, Num(b.B));
                    }
                    d["rPu"] = (cap, Num(b.R));
                    d["xPu"] = (cap, Num(b.X));
                    d["r0Pu"] = (cap, Num(b.R0));
                    d["x0Pu"] = (cap, Num(b.X0));
                    d["ratingMva"] = (cap, Num(b.RateAMva));
                    d["ratingBMva"] = (cap, Num(b.RateBMva));
                    d["ratingCMva"] = (cap, Num(b.RateCMva));
                    d["tapRatio"] = (AsSolved, Num(b.TapRatio));
                    d["phaseShiftDeg"] = (AsSolved, Num(b.PhaseShiftDeg));
                }
                break;
            case AssetKeyKind.Demand:
            {
                int bus = RecordRead.Int(node, "bus", doc.KeyComparer) ?? int.MinValue;
                foreach (Bus b in m.Buses)
                {
                    if (b.Id != bus) continue;
                    string cap = "bus " + bus.ToString(Inv) + " total as solved (every demand record on the bus)";
                    d["pMw"] = (cap, Num(b.Pd));
                    d["qMvar"] = (cap, Num(b.Qd));
                    break;
                }
                break;
            }
        }
        return d;
    }

    // ===================================================================================================================
    // CONNECTIONS
    // ===================================================================================================================

    /// <summary>
    /// The CONNECTIONS content of <paramref name="key"/> (Run mode): the model adjacency (a bus's branches, units, tap
    /// changers, demand records and protections; a unit's bus; a branch's ends and the tap changers, boundaries and
    /// protections tied to it; and so on), each row a link to the node it names; and, with a case, the case editor's
    /// where-used list (every record that refers to this bus, branch name or class name). Empty for a node that is not an
    /// asset, or an asset no longer in the model.
    /// </summary>
    public AssetConnections Connections(AssetKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var rows = new List<ConnectionRow>();
        var used = new List<ConnectionRow>();
        GridModel m = Model;
        const string S = AssetConnections.ModelSection;
        void Row(string relation, string label, string detail, AssetKey? target) => rows.Add(new ConnectionRow(S, relation, label, detail, target));
        void BusRow(string relation, int busId)
        {
            int bi = BusIndexOf(busId);
            Row(relation, bi >= 0 ? m.Buses[bi].Name ?? "bus " + busId.ToString(Inv) : "bus " + busId.ToString(Inv),
                "bus " + busId.ToString(Inv), bi >= 0 ? AssetKey.Substation(busId) : null);
        }
        void BranchRow(string relation, int bi, string detail = "")
        {
            Branch b = m.Branches[bi];
            Row(relation, b.Name ?? (b.IsTransformer ? $"TX {b.From}-{b.To}" : "branch " + bi.ToString(Inv)),
                detail.Length > 0 ? detail : string.Create(Inv, $"#{bi}  {b.From} -> {b.To}"), AssetKey.Branch(m, bi));
        }
        void ClassRow(string relation, string? name)
        {
            int ci = ClassIndexOf(name);
            if (name is not null) Row(relation, name, ci >= 0 ? Classes[ci].Kind.ToString() : "not in the class library", ci >= 0 ? ClassKey(ci) : null);
        }
        int[] driven = m.Oltcs.Count > 0 ? SteadyStateSolver.MapOltcRecordsToBranches(m) : Array.Empty<int>();

        switch (key.Kind)
        {
            case AssetKeyKind.Substation:
            {
                int id = key.Model!.BusA;
                if (BusIndexOf(id) < 0) break;
                for (int i = 0; i < m.Branches.Count; i++)
                {
                    Branch b = m.Branches[i];
                    string what = b.IsTransformer ? "transformer" : "circuit";
                    if (b.From == id) BranchRow(what + " (from end)", i);
                    if (b.To == id) BranchRow(what + " (to end)", i);
                }
                for (int i = 0; i < m.Generators.Count; i++)
                    if (m.Generators[i].Bus == id)
                    {
                        Generator g = m.Generators[i];
                        bool link = IsLink(g);
                        Row(link ? "interconnector on this bus" : "unit on this bus", g.Name ?? $"unit @ bus {g.Bus}",
                            string.Create(Inv, $"#{i}  {g.Type}"), AssetKey.Unit(m, i, link));
                    }
                for (int k = 0; k < m.AllGenerators.Count; k++)
                    if (!m.AllGenerators[k].InService && m.AllGenerators[k].Bus == id)
                        Row("out-of-service unit on this bus", m.AllGenerators[k].Name ?? $"unit @ bus {id}",
                            m.AllGenerators[k].Type, AssetKey.OutOfService(m, k));
                for (int k = 0; k < m.Oltcs.Count; k++)
                    if (m.Oltcs[k].ControlsBus == id)
                        Row("tap changer regulating this bus", m.Oltcs[k].Transformer, "OLTC #" + k.ToString(Inv), AssetKey.TapChanger(m, k));
                foreach (DemandRecord dr in Demands)
                    if (dr.Bus == id) Row("demand record folded into this bus", dr.Name ?? "(unnamed)", dr.Record.Display, DemandKey(dr));
                for (int k = 0; k < Protections.Count; k++)
                    if (Protections[k].MonitorBus == id)
                        Row("protection monitoring this bus", Protections[k].Name, Protections[k].ClassName, ProtectionKey(k));
                if (References is not null) Used(References.BusReferrers(id));
                break;
            }
            case AssetKeyKind.Generator:
            case AssetKeyKind.Interconnector:
                if (key.Model!.FindGenerator(m) is { } u)
                {
                    BusRow("connected bus", u.Unit.Bus);
                    ClassRow("dynamics class", u.Unit.DynamicsClass);
                }
                break;
            case AssetKeyKind.OutOfServiceUnit:
            {
                ModelRef r = key.Model!;
                if (r.Index >= 0 && r.Index < m.AllGenerators.Count && m.AllGenerators[r.Index].Bus == r.BusA)
                    BusRow("connected bus (the unit is out of service)", r.BusA);
                break;
            }
            case AssetKeyKind.Circuit:
            case AssetKeyKind.Transformer:
                if (key.Model!.FindBranch(m) is { } found)
                {
                    Branch b = found.Branch;
                    BusRow("from bus", b.From);
                    BusRow("to bus", b.To);
                    for (int k = 0; k < driven.Length; k++)
                        if (driven[k] == found.Index) Row("tap changer driving this transformer", m.Oltcs[k].Transformer,
                            "regulates bus " + m.Oltcs[k].ControlsBus.ToString(Inv), AssetKey.TapChanger(m, k));
                    for (int k = 0; k < m.Boundaries.Count; k++)
                        foreach (BoundaryBranch bb in m.Boundaries[k].Branches)
                            if (b.Name is not null && string.Equals(bb.Branch, b.Name, StringComparison.Ordinal))
                                Row("boundary crossing this branch", m.Boundaries[k].Name, "direction " + bb.Direction.ToString("+0;-0", Inv), BoundaryKey(k));
                    for (int k = 0; k < Protections.Count; k++)
                        if (b.Name is not null && string.Equals(Protections[k].TargetBranch, b.Name, StringComparison.Ordinal))
                            Row("protection tripping this branch", Protections[k].Name, Protections[k].ClassName, ProtectionKey(k));
                    ClassRow("dynamics class", b.DynamicsClass);
                    if (References is not null && b.Name is not null) Used(References.BranchReferrers(b.Name));
                }
                break;
            case AssetKeyKind.TapChanger:
            {
                int k = key.Model!.Index;
                if (k < 0 || k >= m.Oltcs.Count || m.Oltcs[k].Transformer != key.Model.Name) break;
                if (driven[k] >= 0) BranchRow("driven transformer", driven[k]);
                BusRow("regulated bus", m.Oltcs[k].ControlsBus);
                break;
            }
            case AssetKeyKind.Demand:
                foreach (DemandRecord dr in Demands)
                    if (dr.Record == key.Case) BusRow("folds into bus", dr.Bus);
                break;
            case AssetKeyKind.Boundary:
            {
                int k = key.Model!.Index;
                if (k < 0 || k >= m.Boundaries.Count || m.Boundaries[k].Name != key.Model.Name) break;
                foreach (BoundaryBranch bb in m.Boundaries[k].Branches)
                {
                    int bi = BranchIndexOf(bb.Branch);
                    if (bi >= 0) BranchRow("branch of the boundary", bi, "direction " + bb.Direction.ToString("+0;-0", Inv));
                    else Row("branch of the boundary", bb.Branch, "not a branch of the model", null);
                }
                break;
            }
            case AssetKeyKind.Protection:
            {
                int k = key.Model!.Index;
                if (k < 0 || k >= Protections.Count || Protections[k].Name != key.Model.Name) break;
                BusRow("monitored bus", Protections[k].MonitorBus);
                if (Protections[k].TargetBranch is string tb)
                {
                    int bi = BranchIndexOf(tb);
                    if (bi >= 0) BranchRow("target branch", bi);
                    else Row("target branch", tb, "not a branch of the model", null);
                }
                ClassRow("protection class", Protections[k].ClassName);
                break;
            }
            case AssetKeyKind.AssetClass:
            {
                int ci = key.Model!.Index;
                if (ci < 0 || ci >= Classes.Count || Classes[ci].Name != key.Model.Name) break;
                string name = Classes[ci].Name;
                for (int k = 0; k < Bindings.Count; k++)
                    if (string.Equals(Bindings[k].ClassName, name, StringComparison.OrdinalIgnoreCase))
                        Row("binding rule using this class", Bindings[k].MatchType, "binds type '" + Bindings[k].MatchType + "'", BindingKey(k));
                for (int k = 0; k < Protections.Count; k++)
                    if (string.Equals(Protections[k].ClassName, name, StringComparison.OrdinalIgnoreCase))
                        Row("protection of this class", Protections[k].Name, "bus " + Protections[k].MonitorBus.ToString(Inv), ProtectionKey(k));
                for (int k = 0; k < m.AllGenerators.Count; k++)
                    if (string.Equals(m.AllGenerators[k].DynamicsClass, name, StringComparison.OrdinalIgnoreCase))
                        Row("unit naming this class", m.AllGenerators[k].Name ?? $"unit @ bus {m.AllGenerators[k].Bus}",
                            "bus " + m.AllGenerators[k].Bus.ToString(Inv), FleetKey(k));
                for (int i = 0; i < m.Branches.Count; i++)
                    if (string.Equals(m.Branches[i].DynamicsClass, name, StringComparison.OrdinalIgnoreCase))
                        BranchRow("branch naming this class", i);
                if (References is not null) Used(References.ClassReferrers(name));
                break;
            }
            case AssetKeyKind.AssetBinding:
            {
                int k = key.Model!.Index;
                if (k < 0 || k >= Bindings.Count || Bindings[k].MatchType != key.Model.Name) break;
                ClassRow("bound class", Bindings[k].ClassName);
                break;
            }
        }
        return rows.Count == 0 && used.Count == 0 ? AssetConnections.None : new AssetConnections(rows, used);

        // The where-used list: every referring record, linked to the node of the element it builds (if any).
        void Used(IReadOnlyList<ReferenceIndex.Referrer> referrers)
        {
            foreach (ReferenceIndex.Referrer r in referrers)
            {
                CaseRef at = CaseRef.Of(r.Record, Session!.Documents[r.Record.Document].FileName);
                used.Add(new ConnectionRow(AssetConnections.WhereUsedSection, r.Field, r.What, at.Display, KeyOf(at)));
            }
        }
    }
}
