using System.ComponentModel;
using System.Runtime.CompilerServices;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Hosting;

namespace GridSim.Ui.Assets.Bridge;

/// <summary>
/// UI programme ASSETS fix S6 (plan section 3.3, Assets/Bridge; decision 4 of section 8: S6 first): the one place that asks
/// the GDA bridge for a substation's real per-asset data (<c>op="asset"</c> at the stream anchor, the WPF window's
/// BridgeApplyAsset, shared as <see cref="BridgeSession.ApplyAssetAsync"/>) and fills the substation page's GDA PER-ASSET
/// DATA card.
///
/// <para>The quirk it fixes. WPF, and the Avalonia app until this fix, filled the card only when the user arrived from the
/// map fly-out's OPEN IN EXPLORER in bridge mode: the same substation reached any other way showed no card. Now the card is
/// asked for on EVERY route to a substation page while the bridge streams: the explorer tree, the search (a match or
/// "go to bus id"), back / forward, a SCADA row, the map's "Open in Assets", OPEN IN EXPLORER, and the cross-links inside
/// the pages (a unit page's bus link, a CONNECTIONS row, an overview row). It does so by watching the one thing every route
/// changes: the navigator's page on screen (<see cref="AssetNavigatorModel.Current"/>). Whenever that becomes a substation
/// page that has not been asked for yet, and the bridge is streaming, the page is told the query is under way and the
/// answer is applied to it if it is still on screen (the shared method's own rule), with exactly the status line and rows
/// the old route gave. A page already asked for (back / forward to the same page object) keeps the card it has; it is not
/// asked again.</para>
///
/// <para>Outside bridge mode nothing is asked and the card stays hidden, as before. On a head without the bridge (the
/// mobile heads have none) the bridge accessor gives null, so the feature is off there by construction.</para>
///
/// <para>The WPF app keeps its own route (its separate bridge copy stays until Phase 2 retires WPF): this fix lives in the
/// shell side of the shared code (the navigator's watcher), not in the shared page code, so WPF behaviour is unchanged.</para>
/// </summary>
public sealed class AssetInsightSource : IDisposable
{
    private readonly AssetNavigatorModel _navigator;
    private readonly Func<BridgeSession?> _bridge;

    // The substation pages already asked for (weakly held: a page dropped from the history is freed). The value is the
    // bridge session that was asked, so a page shown again under a different session is asked again.
    private readonly ConditionalWeakTable<AssetPageModel, BridgeSession> _asked = new();
    private bool _disposed;

    /// <summary>
    /// Watch <paramref name="navigator"/>'s page on screen and ask <paramref name="bridge"/>'s session (null on a head
    /// without the bridge) for each substation page that appears while it streams.
    /// </summary>
    public AssetInsightSource(AssetNavigatorModel navigator, Func<BridgeSession?> bridge)
    {
        _navigator = navigator ?? throw new ArgumentNullException(nameof(navigator));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _navigator.PropertyChanged += OnNavigatorChanged;
    }

    /// <summary>The query the last substation page shown in bridge mode started (null before any), so a caller or a
    /// test can wait for it.</summary>
    public Task? Pending { get; private set; }

    /// <summary>How many queries this source has started (for tests: a route that asks nothing leaves it unchanged).</summary>
    public int QueryCount { get; private set; }

    private void OnNavigatorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssetNavigatorModel.Current)) Ask();
    }

    /// <summary>
    /// Ask for the page on screen now, if it is a substation page not yet asked for under the streaming bridge session.
    /// Returns true when a query was started. The navigator's page changes call this; a front end may call it too (it is
    /// idempotent per page and session).
    /// </summary>
    public bool Ask()
    {
        if (_disposed) return false;
        if (_bridge() is not { IsActive: true } bridge) return false;   // outside bridge mode: no card (as before)
        if (_navigator.Current is not SubstationDetailModel<PlantRowModel, ConnectionRowModel> sub) return false;
        if (_asked.TryGetValue(sub, out BridgeSession? askedOf) && ReferenceEquals(askedOf, bridge)) return false;
        _asked.AddOrUpdate(sub, bridge);
        int busId = sub.BusId;
        QueryCount++;
        // The shared method: "querying ..." on the page now, then the rows and status line on the substation page still
        // showing this bus when the answer comes (or the failure, or "no data"), exactly as the OPEN IN EXPLORER route did.
        Pending = bridge.ApplyAssetAsync(busId, sub.ApplyAsset,
            () => _navigator.Current is SubstationDetailModel<PlantRowModel, ConnectionRowModel> cur && cur.BusId == busId ? cur.ApplyAsset : null);
        return true;
    }

    /// <summary>Stop watching the navigator.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _navigator.PropertyChanged -= OnNavigatorChanged;
    }
}
