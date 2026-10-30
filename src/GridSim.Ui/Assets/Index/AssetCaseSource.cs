using GridSim.Ui.Models;

namespace GridSim.Ui.Assets.Index;

// UI programme ASSETS B1: what the running model was read from, as far as the ASSETS tab cares. Only a case file (a v1
// folder or a v2 document) has records to show on PROPERTIES; every other source has none, and the tab says why instead
// of showing an empty sub-tab (plan section 3.5 and question Q9: MATPOWER .m, a replay and the GDA bridge are OVERVIEW
// only). "Undeclared" is the navigator before the shell has said anything: the tab is then exactly the A3 tab.

/// <summary>The kind of source the running model came from.</summary>
public enum AssetSourceKind
{
    /// <summary>The shell has not declared a source: the ASSETS tab behaves exactly as in slice A3.</summary>
    Undeclared,

    /// <summary>A v1 case folder (the files JsonGridLoader reads).</summary>
    V1Folder,

    /// <summary>A v2 case manifest (gridsim-case/2) and the network files it lists.</summary>
    V2Case,

    /// <summary>A MATPOWER .m file.</summary>
    Matpower,

    /// <summary>A replay of an exported run folder.</summary>
    Replay,

    /// <summary>The GDA bridge stream.</summary>
    Bridge,
}

/// <summary>
/// The source of the running model (UI programme ASSETS B1): its kind and its path (the v1 folder, the v2 manifest, the
/// .m file, the run folder or the bridge endpoint).
/// </summary>
/// <param name="Kind">What kind of source it is.</param>
/// <param name="Path">The folder, file or endpoint it was read from (null when undeclared).</param>
public sealed record AssetCaseSource(AssetSourceKind Kind, string? Path)
{
    /// <summary>No source declared (the A3 behaviour).</summary>
    public static AssetCaseSource Undeclared { get; } = new(AssetSourceKind.Undeclared, null);

    /// <summary>A v1 case folder.</summary>
    public static AssetCaseSource V1(string folder) => new(AssetSourceKind.V1Folder, folder ?? throw new ArgumentNullException(nameof(folder)));

    /// <summary>A v2 case manifest.</summary>
    public static AssetCaseSource V2(string manifest) => new(AssetSourceKind.V2Case, manifest ?? throw new ArgumentNullException(nameof(manifest)));

    /// <summary>
    /// The source of a model the run pipeline loaded, by the rule SimHost and RunViewModel apply to a
    /// <see cref="ModelSource"/>: a bridge endpoint first, then a replay folder, then a MATPOWER file, then (slice 5-view) a
    /// JSON v2 case manifest, else the JSON case folder. Null gives <see cref="Undeclared"/>.
    /// </summary>
    public static AssetCaseSource From(ModelSource? source)
    {
        if (source is null) return Undeclared;
        if (source.Bridge is not null) return new AssetCaseSource(AssetSourceKind.Bridge, source.Bridge);
        if (source.ReplayDir is not null) return new AssetCaseSource(AssetSourceKind.Replay, source.ReplayDir);
        if (source.MFile is not null) return new AssetCaseSource(AssetSourceKind.Matpower, source.MFile);
        if (source.V2Case is not null) return V2(source.V2Case);
        return V1(source.Dir);
    }

    /// <summary>True for a v1 folder or a v2 manifest: a source with records to show.</summary>
    public bool IsCaseFile => Kind is AssetSourceKind.V1Folder or AssetSourceKind.V2Case;

    /// <summary>
    /// Why this source has no case records (plan Q9), in words the tab shows beside its sub-tabs; null for a case file
    /// and for an undeclared source.
    /// </summary>
    public string? NotACaseReason => Kind switch
    {
        AssetSourceKind.Matpower => "PROPERTIES is not shown: this model was read from a MATPOWER .m file, not a GridSim case file, so it has no case records.",
        AssetSourceKind.Replay => "PROPERTIES is not shown: this is a replay of an exported run folder, not a GridSim case file, so it has no case records.",
        AssetSourceKind.Bridge => "PROPERTIES is not shown: this model is streamed by the GDA bridge, not read from a GridSim case file, so it has no case records.",
        _ => null,
    };
}
