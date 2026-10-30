using System.Globalization;
using GridSim.Core.IO.Json.CaseEditing;
using GridSim.Core.Model;
using GridSim.Core.Presentation;

namespace GridSim.Ui.Assets.Keys;

// UI programme ASSETS B1 (plan section 3.3, Assets/Keys): ONE identity for an asset, whatever knows about it. The A2 pages
// keep their own constructor tuples (index, bus, name; or index, from, to, name; or a bus id) and the A3 tree names the
// page a node opens (AssetPageKey). Neither can name "this transformer" across the solved model, a record of the case file
// and, from Phase 5, an overlay record. An AssetKey can: it is a kind plus exactly one reference:
//   - ModelRef:   where the asset is in the solved model (a model index confirmed by its bus or ends and its model name,
//                 by the existing AssetLookup rules; or a bus id), as every page already identifies its unit;
//   - CaseRef:    a record of the case file (document, array pointer, index: the CaseEditing RecordRef, in public form);
//   - OverlayRef: an overlay record id (Phase 5; made since slice 5-view by the overlay index, Assets/Overlay).
// Tree nodes that are not assets (the landing page, a class, a group) carry a key too, of kind Home, Class or Group, whose
// only content is the node's stable section id, so every node of the tree has exactly one key and every key one node.

/// <summary>What an <see cref="AssetKey"/> names.</summary>
public enum AssetKeyKind
{
    /// <summary>The landing page of the ASSETS tab.</summary>
    Home,

    /// <summary>An asset class node of the tree (SUBSTATIONS, GENERATORS, DEMANDS, ...).</summary>
    Class,

    /// <summary>A grouping node inside a class (a voltage level, a fuel, a station, the bindings).</summary>
    Group,

    /// <summary>A bus (shown as a substation), by bus id.</summary>
    Substation,

    /// <summary>An in-service generating unit (not an interconnector), by in-service model index.</summary>
    Generator,

    /// <summary>An interconnector link (a generator of the interconnector fuel), by in-service model index.</summary>
    Interconnector,

    /// <summary>A line or cable, by branch index.</summary>
    Circuit,

    /// <summary>A two-winding transformer, by branch index.</summary>
    Transformer,

    /// <summary>An on-load tap changer record, by its position in the model's OLTC list.</summary>
    TapChanger,

    /// <summary>A demand record of the case file (the model folds demand into its bus, so only the record names it).</summary>
    Demand,

    /// <summary>A boundary, by its position in the model's boundary list.</summary>
    Boundary,

    /// <summary>A placed protective device, by its position in the model's protection list.</summary>
    Protection,

    /// <summary>An authored asset (dynamics) class, by its position in the model's class library.</summary>
    AssetClass,

    /// <summary>An asset binding rule, by its position in the model's binding set.</summary>
    AssetBinding,

    /// <summary>A generating unit marked out of service, by its position in the model's whole fleet (AllGenerators).</summary>
    OutOfServiceUnit,

    /// <summary>An overlay record (Phase 5).</summary>
    OverlayRecord,
}

/// <summary>
/// Where an asset is in the solved model: a model index confirmed by the unit's bus (generator) or ends (branch) and its
/// model name, as the A2 pages identify their unit (AssetLookup decides whether the index still names the same thing);
/// or, for a bus, the bus id alone. Fields a kind does not use keep their defaults (index -1, buses 0, name null).
/// </summary>
/// <param name="Index">The model index (generator, branch, OLTC, boundary, protection, class, binding, fleet position),
/// or -1 for a bus.</param>
/// <param name="BusA">The bus id (a bus), the unit's bus, the From end of a branch, an OLTC's controlled bus, a
/// protection's monitored bus.</param>
/// <param name="BusB">The To end of a branch; 0 for every other kind.</param>
/// <param name="Name">The model name the reference is confirmed by (null when the model has none).</param>
public sealed record ModelRef(int Index = -1, int BusA = 0, int BusB = 0, string? Name = null)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A bus, by id.</summary>
    public static ModelRef Bus(int busId) => new(-1, busId);

    /// <summary>A generating unit (model index, its bus, its model name).</summary>
    public static ModelRef Unit(int index, int bus, string? name) => new(index, bus, 0, name);

    /// <summary>A branch (model index, From, To, model name).</summary>
    public static ModelRef Branch(int index, int from, int to, string? name) => new(index, from, to, name);

    /// <summary>An element known by its position in a model list and its name (an OLTC, boundary, protection, class or
    /// binding), with the bus it is tied to when it has one.</summary>
    public static ModelRef Ordinal(int index, string? name, int bus = 0) => new(index, bus, 0, name);

    /// <summary>The generator this reference names in <paramref name="model"/> (its in-service list), by the AssetLookup
    /// rule: the unit at the index when it still has the same bus and name, else the one unit with that bus and name,
    /// else null (gone, or ambiguous: shown as "not in this model", never another unit).</summary>
    public (Generator Unit, int Index)? FindGenerator(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return AssetLookup.FindGenerator(model, Index, BusA, Name);
    }

    /// <summary>The branch this reference names in <paramref name="model"/>, by the AssetLookup rule (index, then the one
    /// branch with the same ends and name, else null).</summary>
    public (Branch Branch, int Index)? FindBranch(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return AssetLookup.FindBranch(model, Index, BusA, BusB, Name);
    }

    /// <summary>True when <paramref name="model"/> holds a bus with the id <see cref="BusA"/>.</summary>
    public bool HasBus(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        foreach (Bus b in model.Buses) if (b.Id == BusA) return true;
        return false;
    }

    /// <summary>A short invariant text ("bus 12", "#5 bus 12 'Drax 1'", "#7 12->13 'L1'").</summary>
    public string Display
    {
        get
        {
            string name = Name is null ? "" : " '" + Name + "'";
            if (Index < 0) return "bus " + BusA.ToString(Inv) + name;
            if (BusB != 0) return string.Create(Inv, $"#{Index} {BusA}->{BusB}") + name;
            return BusA != 0 ? string.Create(Inv, $"#{Index} bus {BusA}") + name : "#" + Index.ToString(Inv) + name;
        }
    }
}

/// <summary>
/// A record of the case file: the document (its index in the read-only case session and its file name), the JSON pointer
/// of the array holding it ("" for a v1 file whose root is the array, "/generators" in a v2 network file) and its index
/// in that array. This is the CaseEditing <c>RecordRef</c> with the file name beside it, in a public form (the editing
/// engine itself is internal to Core and reached from GridSim.Ui only).
/// </summary>
/// <param name="Document">The document's index in the case session.</param>
/// <param name="ArrayPointer">The JSON pointer of the array holding the record.</param>
/// <param name="Index">The record's index in that array.</param>
/// <param name="FileName">The document's file name (for display).</param>
public sealed record CaseRef(int Document, string ArrayPointer, int Index, string FileName)
{
    /// <summary>The JSON pointer of the record itself ("/17", "/generators/0"). Phase 4.1: a document whose root is one
    /// object (a v1 system.json, a v2 case manifest) is the record at index -1, whose pointer is <see cref="ArrayPointer"/>
    /// itself ("" for the root), exactly as the engine's RecordRef says it.</summary>
    public string Pointer => Index < 0 ? ArrayPointer : ArrayPointer + "/" + Index.ToString(CultureInfo.InvariantCulture);

    /// <summary>"generation.json #17" (v1) or "good.network.json generators #0" (v2), the CaseEditing describe form
    /// without the record's name. Phase 4.1: a root-object record (index -1) is shown by its file name alone.</summary>
    public string Display => Index < 0 ? FileName : FileName + (ArrayPointer.Length > 0 ? " " + ArrayPointer.TrimStart('/') : "")
        + " #" + Index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The engine's record reference.</summary>
    internal RecordRef ToRecordRef() => new(Document, ArrayPointer, Index);

    /// <summary>The public form of an engine record reference.</summary>
    internal static CaseRef Of(RecordRef r, string fileName) => new(r.Document, r.ArrayPointer, r.Index, fileName);
}

/// <summary>
/// An overlay record (gridsim-assets/2, Phase 5), by its record id. Slice 5-view (plan Q12) shows a record in more than
/// one place of the tree: once under OVERLAY (by kind; its canonical node, <see cref="At"/> null) and again under each
/// model asset it attaches to. Each of those places is a node of its own, so each needs a key of its own (the tree keeps
/// "every key one node"): <see cref="At"/> is the id of the tree place such an attachment node shows the record at. Every
/// key with the same <see cref="RecordId"/> shows the same record.
/// </summary>
/// <param name="RecordId">The overlay record's id.</param>
/// <param name="At">The tree place of an attachment node (null for the record's canonical node).</param>
public sealed record OverlayRef(string RecordId, string? At = null);

/// <summary>
/// The identity of one asset (or one non-asset node of the explorer tree): a kind and exactly one reference. Two keys are
/// equal when they have the same kind and an equal reference (names compared ordinally). Made only through the factories,
/// which refuse a reference of the wrong sort for the kind, so a key can never hold two references or none.
/// </summary>
public sealed class AssetKey : IEquatable<AssetKey>
{
    private AssetKey(AssetKeyKind kind, string? section, ModelRef? model, CaseRef? @case, OverlayRef? overlay)
    {
        Kind = kind;
        Section = section;
        Model = model;
        Case = @case;
        Overlay = overlay;
    }

    /// <summary>What the key names.</summary>
    public AssetKeyKind Kind { get; }

    /// <summary>The tree section id of a Home, Class or Group key ("home", "class:GeneratorFleet", "group:subs:400");
    /// null for an asset.</summary>
    public string? Section { get; }

    /// <summary>The solved-model reference, or null.</summary>
    public ModelRef? Model { get; }

    /// <summary>The case-file record reference, or null.</summary>
    public CaseRef? Case { get; }

    /// <summary>The overlay record reference, or null.</summary>
    public OverlayRef? Overlay { get; }

    /// <summary>The landing page.</summary>
    public static AssetKey Home { get; } = new(AssetKeyKind.Home, "home", null, null, null);

    /// <summary>An asset class node of the tree, by its section id.</summary>
    public static AssetKey ForClass(string section) => new(AssetKeyKind.Class, NonEmpty(section), null, null, null);

    /// <summary>A grouping node of the tree, by its section id.</summary>
    public static AssetKey ForGroup(string section) => new(AssetKeyKind.Group, NonEmpty(section), null, null, null);

    /// <summary>An asset of the solved model. Refuses the tree kinds, the demand kind (a demand is a case record) and the
    /// overlay kind.</summary>
    public static AssetKey ForModel(AssetKeyKind kind, ModelRef model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (kind is AssetKeyKind.Home or AssetKeyKind.Class or AssetKeyKind.Group or AssetKeyKind.Demand or AssetKeyKind.OverlayRecord)
            throw new ArgumentException($"A {kind} key does not take a model reference.", nameof(kind));
        return new AssetKey(kind, null, model, null, null);
    }

    /// <summary>A record of the case file. Only a demand is named by its record alone (the model folds it into its bus);
    /// every other asset is named by its model reference and finds its record through the index.</summary>
    public static AssetKey ForCase(AssetKeyKind kind, CaseRef record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (kind != AssetKeyKind.Demand)
            throw new ArgumentException($"A {kind} key takes a model reference; only a demand is named by its case record.", nameof(kind));
        return new AssetKey(kind, null, null, record, null);
    }

    /// <summary>An overlay record (Phase 5).</summary>
    public static AssetKey ForOverlay(OverlayRef overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        return new AssetKey(AssetKeyKind.OverlayRecord, null, null, null, overlay);
    }

    private static string NonEmpty(string section)
    {
        ArgumentException.ThrowIfNullOrEmpty(section);
        return section;
    }

    // ---- the keys the explorer tree gives each asset (one place, so the tree, the connections and the tests agree) ----

    /// <summary>The key of bus <paramref name="busId"/>.</summary>
    public static AssetKey Substation(int busId) => ForModel(AssetKeyKind.Substation, ModelRef.Bus(busId));

    /// <summary>The key of the in-service unit at <paramref name="index"/> of <paramref name="model"/>: an interconnector
    /// when <paramref name="isLink"/>, else a generator.</summary>
    public static AssetKey Unit(GridModel model, int index, bool isLink)
    {
        ArgumentNullException.ThrowIfNull(model);
        Generator g = model.Generators[index];
        return ForModel(isLink ? AssetKeyKind.Interconnector : AssetKeyKind.Generator, ModelRef.Unit(index, g.Bus, g.Name));
    }

    /// <summary>The key of the branch at <paramref name="index"/> of <paramref name="model"/> (a transformer or a circuit).</summary>
    public static AssetKey Branch(GridModel model, int index)
    {
        ArgumentNullException.ThrowIfNull(model);
        Branch b = model.Branches[index];
        return ForModel(b.IsTransformer ? AssetKeyKind.Transformer : AssetKeyKind.Circuit, ModelRef.Branch(index, b.From, b.To, b.Name));
    }

    /// <summary>The key of OLTC record <paramref name="index"/> of <paramref name="model"/>.</summary>
    public static AssetKey TapChanger(GridModel model, int index)
    {
        ArgumentNullException.ThrowIfNull(model);
        Oltc o = model.Oltcs[index];
        return ForModel(AssetKeyKind.TapChanger, ModelRef.Ordinal(index, o.Transformer, o.ControlsBus));
    }

    /// <summary>The key of the unit at position <paramref name="allIndex"/> of the whole fleet (AllGenerators), marked out
    /// of service.</summary>
    public static AssetKey OutOfService(GridModel model, int allIndex)
    {
        ArgumentNullException.ThrowIfNull(model);
        Generator g = model.AllGenerators[allIndex];
        return ForModel(AssetKeyKind.OutOfServiceUnit, ModelRef.Unit(allIndex, g.Bus, g.Name));
    }

    /// <summary>The display text: the kind and the reference ("Generator #5 bus 12 'Drax 1'", "Demand demand.json #3",
    /// "Class class:GeneratorFleet").</summary>
    public string Display => Kind + " " + (Model?.Display ?? Case?.Display ?? OverlayText ?? Section ?? "");

    // An overlay record's id; an attachment node's key adds the tree place it shows the record at (slice 5-view).
    private string? OverlayText => Overlay is null ? null : Overlay.At is null ? Overlay.RecordId : Overlay.RecordId + " @ " + Overlay.At;

    /// <inheritdoc />
    public override string ToString() => Display;

    /// <inheritdoc />
    public bool Equals(AssetKey? other) => other is not null && Kind == other.Kind
        && string.Equals(Section, other.Section, StringComparison.Ordinal)
        && Equals(Model, other.Model) && Equals(Case, other.Case) && Equals(Overlay, other.Overlay);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as AssetKey);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Kind, Section is null ? 0 : StringComparer.Ordinal.GetHashCode(Section), Model, Case, Overlay);

    /// <summary>Equality operator (value equality).</summary>
    public static bool operator ==(AssetKey? a, AssetKey? b) => a is null ? b is null : a.Equals(b);

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(AssetKey? a, AssetKey? b) => !(a == b);
}
