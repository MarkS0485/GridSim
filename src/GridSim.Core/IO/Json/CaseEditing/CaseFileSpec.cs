using System.Diagnostics.CodeAnalysis;

namespace GridSim.Core.IO.Json.CaseEditing;

/// <summary>The three kinds of case the editor opens (Mark's decision D4: both v1 folders and v2 documents).</summary>
internal enum CaseFormat
{
    /// <summary>A v1 case folder: the eleven JSON files JsonGridLoader reads (see <see cref="CaseFileSpec.V1Files"/>).</summary>
    V1Folder,

    /// <summary>A v2 case manifest (gridsim-case/2, a *.case.json) and the network files it lists.</summary>
    V2Case,

    /// <summary>A single v2 network file (gridsim-network/2) opened on its own.</summary>
    V2Network,
}

/// <summary>
/// What a collection of records is, whatever file holds it: the v1 file (buses.json, ...) or the v2 network member
/// ("buses", ...). Reference integrity, keys and list columns are defined by role, so v1 and v2 share them.
/// </summary>
internal static class CaseRoles
{
    public const string Buses = "buses";
    public const string Generators = "generators";
    public const string Demands = "demands";
    public const string Lines = "lines";
    public const string Transformers = "transformers";
    public const string Oltcs = "oltcs";
    public const string Boundaries = "boundaries";
    public const string Protections = "protections";
    public const string AssetClasses = "assetClasses";
    public const string AssetBindings = "assetBindings";

    /// <summary>
    /// Phase 5b (2026-10-20): the records of one asset overlay file (gridsim-assets/2, its "records" array). Overlay
    /// records are carried beside the model and never used by the solver; the editor edits them like any other
    /// collection, keyed by their "id", with their parameters and references read against the overlay kind specs
    /// (<see cref="OverlayOps"/>). Only a v2 case manifest that lists "assets" has collections of this role.
    /// </summary>
    public const string Overlay = "overlay";

    /// <summary>The element arrays of a v2 network file (gridsim-network/2), in the order the editor shows them.</summary>
    public static IReadOnlyList<string> V2Network { get; } = new[] { Buses, Generators, Demands, Lines, Transformers, Oltcs, Boundaries, Protections };

    /// <summary>The section title of a role.</summary>
    public static string Title(string role) => role switch
    {
        Buses => "Buses",
        Generators => "Generation",
        Demands => "Demand",
        Lines => "Lines",
        Transformers => "Transformers",
        Oltcs => "Tap changers",
        Boundaries => "Boundaries",
        Protections => "Protections",
        AssetClasses => "Asset classes",
        AssetBindings => "Asset bindings",
        Overlay => "Asset overlay",
        _ => role,
    };

    /// <summary>The v1 file of a role (list columns are keyed by it).</summary>
    public static string V1File(string role) => CaseFileSpec.V1Files.First(f => f.Role == role).FileName;
}

/// <summary>
/// One collection of records in an open case: the document, the JSON pointer of its array ("" for a v1 file whose
/// root is the array, "/buses" for a v2 network file), its role and the shape of one record.
/// </summary>
internal sealed record RecordCollection(int Document, string ArrayPointer, string Role, RecordShape Shape);

/// <summary>
/// One file of a v1 case folder as the loader reads it: its name, whether its root is an array of records or a single
/// object, the schema that describes it (schemas/v1, embedded in Core), the <c>$defs</c> entry of one record, the
/// loader DTO it deserialises into, the section name the editor shows, and the role of its records. A test pins
/// <see cref="V1Files"/> equal to the test projector's LoaderFiles list and every DTO to its <c>$def</c>, so the three
/// can never drift apart.
/// </summary>
/// <remarks>Phase 0.1: <see cref="DtoType"/> is annotated (see <see cref="DtoTypeRef"/>) so the trimmer keeps the
/// DTO members the case editor reads by reflection, and the trim analyser can prove it.</remarks>
internal sealed record CaseFileSpec(string FileName, bool IsArray, string SchemaFile, string RecordDef,
    [property: DynamicallyAccessedMembers(DtoTypeRef.Needs)][param: DynamicallyAccessedMembers(DtoTypeRef.Needs)] Type DtoType,
    string Section, string? Role)
{
    /// <summary>The JSON pointer of one record's schema inside <see cref="SchemaFile"/>, for per-record validation.</summary>
    public string RecordPointer => "#/$defs/" + RecordDef;

    /// <summary>
    /// The eleven files JsonGridLoader.LoadDirectory reads, in the order the test projector lists them. Anything else
    /// in a case folder (an intl case's diagnostics.json, MODEL_CARD.md, ATTRIBUTION.txt) is not part of the case and
    /// is never opened for writing.
    /// </summary>
    public static IReadOnlyList<CaseFileSpec> V1Files { get; } = new CaseFileSpec[]
    {
        new("system.json", false, "system.schema.json", "system", typeof(SystemDto), "System", null),
        new("buses.json", true, "buses.schema.json", "bus", typeof(BusDto), "Buses", CaseRoles.Buses),
        new("generation.json", true, "generation.schema.json", "generator", typeof(GenDto), "Generation", CaseRoles.Generators),
        new("demand.json", true, "demand.schema.json", "demand", typeof(DemandDto), "Demand", CaseRoles.Demands),
        new("transmission.json", true, "transmission.schema.json", "line", typeof(LineDto), "Lines", CaseRoles.Lines),
        new("transformer.json", true, "transformer.schema.json", "transformer", typeof(TransformerDto), "Transformers", CaseRoles.Transformers),
        new("onloadtapchanger.json", true, "onloadtapchanger.schema.json", "oltc", typeof(OltcDto), "Tap changers", CaseRoles.Oltcs),
        new("asset-classes.json", true, "asset-classes.schema.json", "assetClass", typeof(AssetClassDto), "Asset classes", CaseRoles.AssetClasses),
        new("asset-bindings.json", true, "asset-bindings.schema.json", "assetBinding", typeof(AssetBindingDto), "Asset bindings", CaseRoles.AssetBindings),
        new("protections.json", true, "protections.schema.json", "protection", typeof(ProtectionInstanceDto), "Protections", CaseRoles.Protections),
        new("boundaries.json", true, "boundaries.schema.json", "boundary", typeof(BoundaryDto), "Boundaries", CaseRoles.Boundaries),
    };

    /// <summary>
    /// Phase 0.1: the same nested record shapes, in the same order, as annotated records (a tuple element cannot carry
    /// the trimming annotation), for the catalogue that reads the DTOs by reflection. <see cref="NestedDefs"/> is made
    /// from this list, so the two cannot differ.
    /// </summary>
    internal static IReadOnlyList<NestedDef> NestedDefList { get; } = new NestedDef[]
    {
        new(typeof(BusZipDto), "buses.schema.json", "zip"),
        new(typeof(BoundaryBranchDto), "boundaries.schema.json", "boundaryBranch"),
        new(typeof(ProvenanceDto), "asset-classes.schema.json", "provenance"),
    };

    /// <summary>
    /// The nested record shapes (not files of their own): DTO type, schema file and <c>$def</c>. With
    /// <see cref="V1Files"/> these are all fourteen v1 DTO types.
    /// </summary>
    // Built from NestedDefList (declared above, so it is initialised first).
    public static IReadOnlyList<(Type DtoType, string SchemaFile, string Def)> NestedDefs { get; } =
        NestedDefList.Select(n => (n.DtoType, n.SchemaFile, n.Def)).ToArray();

    /// <summary>One nested record shape: its DTO type (annotated for trimming), schema file and <c>$def</c>.</summary>
    internal sealed record NestedDef(
        [property: DynamicallyAccessedMembers(DtoTypeRef.Needs)][param: DynamicallyAccessedMembers(DtoTypeRef.Needs)] Type DtoType,
        string SchemaFile, string Def);

    /// <summary>The spec of a v1 file by name (exact, case-sensitive: the loader opens these exact names), or null.</summary>
    public static CaseFileSpec? ForFile(string fileName)
        => V1Files.FirstOrDefault(f => string.Equals(f.FileName, fileName, StringComparison.Ordinal));
}
