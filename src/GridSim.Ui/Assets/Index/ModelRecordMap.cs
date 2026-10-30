using System.Globalization;
using System.Text.Json;
using GridSim.Core.Assets;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.IO.Json.Lossless;
using GridSim.Core.Model;
using GridSim.Ui.Assets.Keys;

namespace GridSim.Ui.Assets.Index;

/// <summary>One demand record of the case file and the bus the loader folds it into.</summary>
/// <param name="Record">The demand record.</param>
/// <param name="Bus">The bus id it names (its "bus" member).</param>
/// <param name="Name">Its "name", or null when it has none.</param>
/// <param name="Ordinal">Its position among every demand record of the case (v2: across the network files, in order).</param>
public sealed record DemandRecord(CaseRef Record, int Bus, string? Name, int Ordinal);

/// <summary>
/// UI programme ASSETS B1 (plan section 3.3): which record of the case file each element of the solved model was built
/// from, and back. The rules are the loader's (JsonGridLoader.Build, shared by the v2 loader through its v1 DTOs):
/// <list type="bullet">
/// <item><description>buses: the bus records in order (v2: every network file's buses, in the manifest's file order);</description></item>
/// <item><description>generators: the generator records in order, out-of-service units included, against
/// <see cref="GridModel.AllGenerators"/> (the in-service list <see cref="GridModel.Generators"/> is that fleet with the
/// out-of-service units left out, in the same order);</description></item>
/// <item><description>branches: every line record, then every transformer record (v1: transmission.json then
/// transformer.json; v2: every file's lines, then every file's transformers), against <see cref="GridModel.Branches"/>; a
/// line record marked <c>"isTransformer": true</c> builds a transformer branch in the lines' place;</description></item>
/// <item><description>tap changers, boundaries, protections: their records in order, against the model's lists;</description></item>
/// <item><description>asset classes and bindings (v1 only: v2 carries templates in its manifest, not records): their
/// records in order, against the class library and the binding rules;</description></item>
/// <item><description>demand: each record names the bus the loader adds it to (the model keeps no demand element).</description></item>
/// </list>
/// The map is checked as it is built: every count must match, and each pair must agree on what identifies the element
/// (a bus's id; a unit's bus and name; a branch's ends, name and kind; an OLTC's transformer and controlled bus; a
/// boundary's, protection's or class's name; a binding's match type and class). Any disagreement (the file changed on disk
/// after the load, or a rule here drifted from the loader's) refuses the map with every problem named, so a record is
/// never shown for the wrong element. Tests pin the map against the loader on every bundled case and fixture.
/// </summary>
public sealed class ModelRecordMap
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The most problems a refusal lists (the count of the rest is given).
    private const int MaxListed = 20;

    private readonly CaseRef[] _buses;
    private readonly CaseRef[] _fleet;          // aligned with AllGenerators
    private readonly int[] _inServiceToFleet;   // in-service index -> AllGenerators index
    private readonly CaseRef[] _branches;
    private readonly CaseRef[] _oltcs;
    private readonly CaseRef[] _boundaries;
    private readonly CaseRef[] _protections;
    private readonly CaseRef[] _classes;
    private readonly CaseRef[] _bindings;
    private readonly List<DemandRecord> _demands;
    private readonly Dictionary<CaseRef, (AssetKeyKind Kind, int Index)> _reverse = new();

    private ModelRecordMap(CaseRef[] buses, CaseRef[] fleet, int[] inServiceToFleet, CaseRef[] branches, CaseRef[] oltcs,
        CaseRef[] boundaries, CaseRef[] protections, CaseRef[] classes, CaseRef[] bindings, List<DemandRecord> demands)
    {
        _buses = buses;
        _fleet = fleet;
        _inServiceToFleet = inServiceToFleet;
        _branches = branches;
        _oltcs = oltcs;
        _boundaries = boundaries;
        _protections = protections;
        _classes = classes;
        _bindings = bindings;
        _demands = demands;
        // The reverse map: every record names exactly one element (a record mapped twice is a build error).
        void Rev(CaseRef[] refs, AssetKeyKind kind)
        {
            for (int i = 0; i < refs.Length; i++)
                if (!_reverse.TryAdd(refs[i], (kind, i)))
                    throw new InvalidOperationException($"{refs[i].Display} is mapped to two model elements (internal error).");
        }
        Rev(buses, AssetKeyKind.Substation);
        Rev(fleet, AssetKeyKind.OutOfServiceUnit);   // the fleet position; GeneratorOf turns it into the right kind
        Rev(branches, AssetKeyKind.Circuit);         // the branch index; the kind is fixed by the branch
        Rev(oltcs, AssetKeyKind.TapChanger);
        Rev(boundaries, AssetKeyKind.Boundary);
        Rev(protections, AssetKeyKind.Protection);
        Rev(classes, AssetKeyKind.AssetClass);
        Rev(bindings, AssetKeyKind.AssetBinding);
        foreach (DemandRecord d in demands)
            if (!_reverse.TryAdd(d.Record, (AssetKeyKind.Demand, d.Ordinal)))
                throw new InvalidOperationException($"{d.Record.Display} is mapped twice (internal error).");
    }

    /// <summary>How many bus records are mapped (equal to the model's bus count).</summary>
    public int BusCount => _buses.Length;

    /// <summary>How many generator records are mapped (equal to the model's whole fleet, out-of-service units included).</summary>
    public int GeneratorCount => _fleet.Length;

    /// <summary>How many branch records are mapped (lines then transformers; equal to the model's branch count).</summary>
    public int BranchCount => _branches.Length;

    /// <summary>How many OLTC records are mapped.</summary>
    public int OltcCount => _oltcs.Length;

    /// <summary>How many boundary records are mapped.</summary>
    public int BoundaryCount => _boundaries.Length;

    /// <summary>How many protection records are mapped.</summary>
    public int ProtectionCount => _protections.Length;

    /// <summary>How many asset-class records are mapped (0 for a v2 case, whose classes are manifest templates).</summary>
    public int AssetClassCount => _classes.Length;

    /// <summary>How many binding records are mapped (0 for a v2 case).</summary>
    public int BindingCount => _bindings.Length;

    /// <summary>Every demand record, in case order, with the bus the loader folds it into.</summary>
    public IReadOnlyList<DemandRecord> Demands => _demands;

    /// <summary>The record of the bus at <paramref name="busIndex"/> of the model's bus list.</summary>
    public CaseRef BusRecord(int busIndex) => _buses[busIndex];

    /// <summary>The record of the unit at <paramref name="fleetIndex"/> of <see cref="GridModel.AllGenerators"/>.</summary>
    public CaseRef FleetRecord(int fleetIndex) => _fleet[fleetIndex];

    /// <summary>The <see cref="GridModel.AllGenerators"/> position of the in-service unit at <paramref name="index"/> of
    /// <see cref="GridModel.Generators"/>.</summary>
    public int FleetIndexOf(int index) => _inServiceToFleet[index];

    /// <summary>The record of the in-service unit at <paramref name="index"/> of <see cref="GridModel.Generators"/>.</summary>
    public CaseRef GeneratorRecord(int index) => _fleet[_inServiceToFleet[index]];

    /// <summary>The record of the branch at <paramref name="branchIndex"/> (a line record or a transformer record).</summary>
    public CaseRef BranchRecord(int branchIndex) => _branches[branchIndex];

    /// <summary>The record of OLTC <paramref name="index"/>.</summary>
    public CaseRef OltcRecord(int index) => _oltcs[index];

    /// <summary>The record of boundary <paramref name="index"/>.</summary>
    public CaseRef BoundaryRecord(int index) => _boundaries[index];

    /// <summary>The record of protection <paramref name="index"/>.</summary>
    public CaseRef ProtectionRecord(int index) => _protections[index];

    /// <summary>The record of asset class <paramref name="index"/>, or null when the case has no class records (v2).</summary>
    public CaseRef? AssetClassRecord(int index) => index >= 0 && index < _classes.Length ? _classes[index] : null;

    /// <summary>The record of binding rule <paramref name="index"/>, or null when the case has no binding records (v2).</summary>
    public CaseRef? BindingRecord(int index) => index >= 0 && index < _bindings.Length ? _bindings[index] : null;

    /// <summary>
    /// The model element built from <paramref name="record"/>: the kind and index as the reverse lookup holds them
    /// (Substation = bus index, OutOfServiceUnit = AllGenerators position for EVERY generator record, Circuit = branch
    /// index for every branch record, Demand = demand ordinal; the others their list index), or null when the record is
    /// not one the map knows (system.json, a nested record).
    /// </summary>
    public (AssetKeyKind Kind, int Index)? ElementOf(CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return _reverse.TryGetValue(record, out var e) ? e : null;
    }

    /// <summary>The asset classes of a model in library order: the dynamics library, else the protections' library (the
    /// loader builds both from the same class records), else none.</summary>
    public static IReadOnlyList<AssetClass> ClassesOf(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        AssetClassLibrary? lib = model.AssetDynamics?.Library ?? model.Protections?.Library;
        return lib is null ? Array.Empty<AssetClass>() : lib.Classes.ToList();
    }

    /// <summary>The binding rules of a model in order (none without a dynamics bundle).</summary>
    public static IReadOnlyList<AssetBinding> BindingsOf(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.AssetDynamics?.Bindings.Rules ?? (IReadOnlyList<AssetBinding>)Array.Empty<AssetBinding>();
    }

    /// <summary>The protections of a model in order (none without a protection bundle).</summary>
    public static IReadOnlyList<GridSim.Core.Assets.Protection.ProtectionInstance> ProtectionsOf(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.Protections?.Instances ?? (IReadOnlyList<GridSim.Core.Assets.Protection.ProtectionInstance>)Array.Empty<GridSim.Core.Assets.Protection.ProtectionInstance>();
    }

    // ===================================================================================================================
    // Building and checking
    // ===================================================================================================================

    // One record as the map reads it: where it is, its node, and the document it lives in (for its key comparer).
    private readonly record struct Rec(CaseRef Ref, JsonSourceNode Node, CaseDocument Doc, RecordShape Shape);

    // Every record of one role, in the loader's order: the collections of that role in document order (v1: the one file;
    // v2: each network file in the manifest's order), each array in element order. An absent file or array has none.
    private static List<Rec> RecordsOf(CaseSession s, string role)
    {
        var list = new List<Rec>();
        foreach (RecordCollection c in s.CollectionsOf(role))
        {
            JsonSourceNode? array = s.ArrayOf(c);
            if (array is null) continue;
            CaseDocument d = s.Documents[c.Document];
            for (int i = 0; i < array.Elements.Count; i++)
                list.Add(new Rec(CaseRef.Of(new RecordRef(d.Id, c.ArrayPointer, i), d.FileName), array.Elements[i], d, c.Shape));
        }
        return list;
    }

    /// <summary>
    /// Build the map of <paramref name="model"/> (loaded from the case <paramref name="session"/> holds) and check it.
    /// Throws <see cref="InvalidDataException"/> naming every disagreement when the case's records do not build this model.
    /// </summary>
    internal static ModelRecordMap Build(GridModel model, CaseSession session)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(session);
        var problems = new List<string>();

        List<Rec> buses = RecordsOf(session, CaseRoles.Buses);
        List<Rec> gens = RecordsOf(session, CaseRoles.Generators);
        List<Rec> branches = RecordsOf(session, CaseRoles.Lines);
        int lineCount = branches.Count;
        branches.AddRange(RecordsOf(session, CaseRoles.Transformers));
        List<Rec> oltcs = RecordsOf(session, CaseRoles.Oltcs);
        List<Rec> boundaries = RecordsOf(session, CaseRoles.Boundaries);
        List<Rec> protections = RecordsOf(session, CaseRoles.Protections);
        List<Rec> classes = RecordsOf(session, CaseRoles.AssetClasses);
        List<Rec> bindings = RecordsOf(session, CaseRoles.AssetBindings);
        List<Rec> demands = RecordsOf(session, CaseRoles.Demands);

        IReadOnlyList<Generator> fleet = model.AllGenerators;
        IReadOnlyList<AssetClass> modelClasses = ClassesOf(model);
        IReadOnlyList<AssetBinding> modelBindings = BindingsOf(model);
        var modelProtections = ProtectionsOf(model);

        bool Count(string what, int records, int elements)
        {
            if (records == elements) return true;
            problems.Add($"{what}: the case file holds {records.ToString(Inv)} record(s), the running model {elements.ToString(Inv)}");
            return false;
        }

        // ---- buses: the id ----
        if (Count("buses", buses.Count, model.Buses.Count))
            for (int i = 0; i < buses.Count; i++)
                if (Int(buses[i], "id") != model.Buses[i].Id)
                    problems.Add($"{buses[i].Ref.Display}: bus id {Show(Int(buses[i], "id"))}, the model's bus {i.ToString(Inv)} is {model.Buses[i].Id.ToString(Inv)}");

        // ---- generators: the whole fleet, by bus and name ----
        if (Count("generators", gens.Count, fleet.Count))
            for (int i = 0; i < gens.Count; i++)
            {
                Generator g = fleet[i];
                if (Int(gens[i], "bus") != g.Bus || !string.Equals(Str(gens[i], "name"), g.Name, StringComparison.Ordinal))
                    problems.Add($"{gens[i].Ref.Display}: unit '{Str(gens[i], "name")}' at bus {Show(Int(gens[i], "bus"))}, the model's unit {i.ToString(Inv)} is '{g.Name}' at bus {g.Bus.ToString(Inv)}");
            }

        // ---- branches: lines then transformers, by ends, name and kind ----
        if (Count("branches (lines then transformers)", branches.Count, model.Branches.Count))
            for (int i = 0; i < branches.Count; i++)
            {
                Branch b = model.Branches[i];
                // A transformer record is a transformer; a line record is one only when it says "isTransformer": true
                // (the loader's Branch(..., IsTransformer: l.IsTransformer)).
                bool isTx = i >= lineCount || Bool(branches[i], "isTransformer") == true;
                if (Int(branches[i], "from") != b.From || Int(branches[i], "to") != b.To
                    || !string.Equals(Str(branches[i], "name"), b.Name, StringComparison.Ordinal) || isTx != b.IsTransformer)
                    problems.Add($"{branches[i].Ref.Display}: {(isTx ? "transformer" : "line")} '{Str(branches[i], "name")}' {Show(Int(branches[i], "from"))}->{Show(Int(branches[i], "to"))}, the model's branch {i.ToString(Inv)} is {(b.IsTransformer ? "transformer" : "line")} '{b.Name}' {b.From.ToString(Inv)}->{b.To.ToString(Inv)}");
            }

        // ---- tap changers: transformer name and controlled bus ----
        if (Count("tap changers", oltcs.Count, model.Oltcs.Count))
            for (int i = 0; i < oltcs.Count; i++)
                if (!string.Equals(Str(oltcs[i], "transformer"), model.Oltcs[i].Transformer, StringComparison.Ordinal)
                    || Int(oltcs[i], "controlsBus") != model.Oltcs[i].ControlsBus)
                    problems.Add($"{oltcs[i].Ref.Display}: OLTC on '{Str(oltcs[i], "transformer")}', the model's OLTC {i.ToString(Inv)} is on '{model.Oltcs[i].Transformer}'");

        // ---- boundaries and protections: the name ----
        if (Count("boundaries", boundaries.Count, model.Boundaries.Count))
            for (int i = 0; i < boundaries.Count; i++)
                if (!string.Equals(Str(boundaries[i], "name"), model.Boundaries[i].Name, StringComparison.Ordinal))
                    problems.Add($"{boundaries[i].Ref.Display}: boundary '{Str(boundaries[i], "name")}', the model's boundary {i.ToString(Inv)} is '{model.Boundaries[i].Name}'");
        if (Count("protections", protections.Count, modelProtections.Count))
            for (int i = 0; i < protections.Count; i++)
                if (!string.Equals(Str(protections[i], "name"), modelProtections[i].Name, StringComparison.Ordinal))
                    problems.Add($"{protections[i].Ref.Display}: protection '{Str(protections[i], "name")}', the model's protection {i.ToString(Inv)} is '{modelProtections[i].Name}'");

        // ---- asset classes and bindings (v1 records; a v2 case has none and its classes come from templates) ----
        bool v1 = session.Format == CaseFormat.V1Folder;
        if (v1 && Count("asset classes", classes.Count, modelClasses.Count))
            for (int i = 0; i < classes.Count; i++)
                if (!string.Equals(Str(classes[i], "name"), modelClasses[i].Name, StringComparison.Ordinal))
                    problems.Add($"{classes[i].Ref.Display}: class '{Str(classes[i], "name")}', the model's class {i.ToString(Inv)} is '{modelClasses[i].Name}'");
        if (v1 && Count("asset bindings", bindings.Count, modelBindings.Count))
            for (int i = 0; i < bindings.Count; i++)
                if (!string.Equals(Str(bindings[i], "matchType"), modelBindings[i].MatchType, StringComparison.Ordinal)
                    || !string.Equals(Str(bindings[i], "class"), modelBindings[i].ClassName, StringComparison.Ordinal))
                    problems.Add($"{bindings[i].Ref.Display}: binding '{Str(bindings[i], "matchType")}' -> '{Str(bindings[i], "class")}', the model's rule {i.ToString(Inv)} is '{modelBindings[i].MatchType}' -> '{modelBindings[i].ClassName}'");
        if (!v1 && (classes.Count > 0 || bindings.Count > 0))
            problems.Add("a v2 case holds asset-class or binding records, which the v2 loader never reads (internal error)");

        // ---- demand: every record names a bus of the model ----
        var busIds = new HashSet<int>(model.Buses.Select(b => b.Id));
        var demandList = new List<DemandRecord>(demands.Count);
        for (int i = 0; i < demands.Count; i++)
        {
            int? bus = Int(demands[i], "bus");
            if (bus is not int id || !busIds.Contains(id))
            {
                problems.Add($"{demands[i].Ref.Display}: demand on bus {Show(bus)}, which is not a bus of the model");
                continue;
            }
            demandList.Add(new DemandRecord(demands[i].Ref, id, Str(demands[i], "name"), i));
        }

        if (problems.Count > 0)
            throw new InvalidDataException(
                $"The case file does not build the running model ({problems.Count.ToString(Inv)} problem(s)): "
                + string.Join("; ", problems.Take(MaxListed)) + (problems.Count > MaxListed ? "; ..." : "") + ".");

        // The in-service list is the whole fleet with the out-of-service units left out, in order.
        var inService = new List<int>(model.Generators.Count);
        for (int k = 0; k < fleet.Count; k++)
            if (fleet[k].InService) inService.Add(k);
        if (inService.Count != model.Generators.Count)
            throw new InvalidDataException($"The model's in-service list holds {model.Generators.Count.ToString(Inv)} unit(s), its fleet {inService.Count.ToString(Inv)} in service (internal error).");

        return new ModelRecordMap(
            buses.Select(r => r.Ref).ToArray(), gens.Select(r => r.Ref).ToArray(), inService.ToArray(),
            branches.Select(r => r.Ref).ToArray(), oltcs.Select(r => r.Ref).ToArray(), boundaries.Select(r => r.Ref).ToArray(),
            protections.Select(r => r.Ref).ToArray(), v1 ? classes.Select(r => r.Ref).ToArray() : Array.Empty<CaseRef>(),
            v1 ? bindings.Select(r => r.Ref).ToArray() : Array.Empty<CaseRef>(), demandList);
    }

    // ---- reading a member the way the loader reads it: the written value, else the DTO default (the absence rule) ----

    private static string Show(int? v) => v is int i ? i.ToString(Inv) : "(none)";

    private static int? Int(Rec r, string field)
    {
        if (RecordRead.Member(r.Node, field, r.Doc.KeyComparer) is { } n)
            return n is { Kind: JsonSourceKind.Number, IntegerValue: long l } && l >= int.MinValue && l <= int.MaxValue ? (int)l : null;
        return r.Shape.Field(field)?.DefaultLiteral is string lit && int.TryParse(lit, NumberStyles.Integer, Inv, out int d) ? d : null;
    }

    private static bool? Bool(Rec r, string field)
    {
        if (RecordRead.Member(r.Node, field, r.Doc.KeyComparer) is { } n)
            return n.Kind switch { JsonSourceKind.True => true, JsonSourceKind.False => false, _ => null };
        return r.Shape.Field(field)?.DefaultLiteral switch { "true" => true, "false" => false, _ => null };
    }

    private static string? Str(Rec r, string field)
    {
        if (RecordRead.Member(r.Node, field, r.Doc.KeyComparer) is { } n)
            return n.Kind == JsonSourceKind.String ? n.StringValue : null;
        if (r.Shape.Field(field)?.DefaultLiteral is string lit && lit.StartsWith('"'))
        {
            using JsonDocument doc = JsonDocument.Parse(lit);
            return doc.RootElement.GetString();
        }
        return null;
    }
}
