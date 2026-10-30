using System;

namespace GridSim.Web.Components.Assets;

/// <summary>The page an <see cref="AssetRoute"/> targets. Fleet pages ignore <c>Key</c>; detail pages key on
/// a generator index, branch index, or bus id (see each page).</summary>
public enum AssetPage
{
    Landing,
    GenFleet, GenDetail,
    IcFleet, IcDetail,
    TxFleet, TxDetail,
    CktFleet, CktDetail,
    SubFleet, SubDetail,
    TapFleet,
}

/// <summary>One node in the explorer's drill stack: which page, its key, and a breadcrumb title.</summary>
public sealed record AssetRoute(AssetPage Page, int Key = -1, string Title = "")
{
    /// <summary>
    /// The explorer page a SCADA register row opens (R099 click-through, the web twin of the WPF OpenPointCmd):
    /// a substation by bus id, a circuit or transformer by model branch index. Null for a point with no asset page
    /// (system frequency) or no valid key. The title is left empty for the explorer to resolve from its asset graph.
    /// </summary>
    public static AssetRoute? FromScada(GridSim.Web.Projection.ScadaTarget target, int key) => target switch
    {
        GridSim.Web.Projection.ScadaTarget.Substation => new AssetRoute(AssetPage.SubDetail, key),
        GridSim.Web.Projection.ScadaTarget.Circuit when key >= 0 => new AssetRoute(AssetPage.CktDetail, key),
        GridSim.Web.Projection.ScadaTarget.Transformer when key >= 0 => new AssetRoute(AssetPage.TxDetail, key),
        _ => null,
    };
}

/// <summary>
/// The navigation surface handed to every asset page. Pages call these to push a related entity's detail
/// onto the shell's back-stack - this is what turns the explorer into a navigable graph (a cable links to
/// its substations, a substation to its plant and circuits, a generator back to its bus). The shell
/// (<c>AssetsTab</c>) supplies the single <see cref="Push"/> delegate; the helpers just shape routes.
/// </summary>
public sealed class AssetNavContext
{
    private readonly Action<AssetRoute> _push;
    public AssetNavContext(Action<AssetRoute> push) => _push = push;

    public void Open(AssetRoute route) => _push(route);

    public void OpenGenerator(int genIndex, string name, bool interconnector) =>
        _push(new AssetRoute(interconnector ? AssetPage.IcDetail : AssetPage.GenDetail, genIndex, name));

    public void OpenSubstation(int busId, string name) =>
        _push(new AssetRoute(AssetPage.SubDetail, busId, name));

    public void OpenBranch(int branchIndex, bool isTransformer, string name) =>
        _push(new AssetRoute(isTransformer ? AssetPage.TxDetail : AssetPage.CktDetail, branchIndex, name));

    public void OpenTransformer(int branchIndex, string name) =>
        _push(new AssetRoute(AssetPage.TxDetail, branchIndex, name));
}
