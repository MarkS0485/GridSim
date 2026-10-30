// FEED (UI programme Phase 1.6, third batch; integrated 2026-10-20). The tab is fed through the dashboard's tab hooks
// exactly as the WPF DashboardViewModel feeds it, at its WPF positions in the four hook bodies (TabModels):
//     Update:        NesoRp1.Update(f, boundaries, clock);   (after Market.Update, before Mechanical/Domains)
//     ResetTimeline: NesoRp1.Reset();                        (after Machine.Reset, before Operator.Reset)
//     ClearStream:   NesoRp1.Reset();                        (after Market.Reset)
// (no ForgetModel hook). The bridge's per-tick market data (UI programme slice 1.8, the WPF
// DashboardViewModel.UpdateMarketData) calls NesoRp1.SetRp1Data(op, mk, tickUtc) after Market.SetMarketData (TabModels).
// The main window hosts it after MARKET, as WPF:
//     <TabItem Header="NESO RP1"><tabs:NesoRp1View x:Name="NesoRp1Tab" DataContext="{Binding Tabs.NesoRp1}" /></TabItem>
// The drill-down inspector: the InspectorView in InspectorSlot (x:Name Inspector) shows each page raised on
// NesoRp1Model.InspectRequested, as the WPF NesoRp1Vm.ShowInspector shows each page in its InspectorVm (the stats and
// rows as given, a row's Drill on click, the back-stack of the shared InspectorModel).

using Avalonia.Controls;
using Avalonia.Interactivity;
using GridSim.Ui.Tabs.Inspector;
using GridSim.Ui.Tabs.NesoRp1;

namespace GridSim.App.Tabs;

/// <summary>
/// The NESO RP1 tab (UI programme Phase 1.6). Everything it shows is bound to the shared <see cref="NesoRp1Model"/>.
/// The three WPF commands are click handlers over the same model methods: a dispatch-stack or inertia-by-source row
/// hands its fuel to <see cref="NesoRp1Model.InspectFuel"/> (the WPF CommandParameter was the row's Fuel), a BM row to
/// <see cref="NesoRp1Model.InspectBm"/> and an interconnector row to <see cref="NesoRp1Model.InspectIc"/>. Each builds
/// a drill-down page and raises it on <see cref="NesoRp1Model.InspectRequested"/>, which this view shows in its
/// inspector overlay (<see cref="InspectorModel"/>), as WPF.
/// </summary>
public sealed partial class NesoRp1View : UserControl
{
    private NesoRp1Model? _listening;   // the model whose drill-down pages this view shows (its data context)

    public NesoRp1View()
    {
        InitializeComponent();
    }

    /// <summary>The inspector overlay's model: each drill-down page of this tab is shown in it.</summary>
    public InspectorModel InspectorModel => Inspector.Model;

    // The tab's model: a click with no model behind the view is a wiring mistake and fails loud.
    private NesoRp1Model Model => DataContext as NesoRp1Model
        ?? throw new InvalidOperationException("The NESO RP1 view has no NesoRp1Model as its DataContext.");

    /// <summary>Listen for the drill-down pages of the model now behind the view (and of no other).</summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_listening is not null) _listening.InspectRequested -= ShowPage;
        _listening = DataContext as NesoRp1Model;
        if (_listening is not null) _listening.InspectRequested += ShowPage;
    }

    /// <summary>
    /// The WPF <c>NesoRp1Vm.ShowInspector</c>: show <paramref name="page"/> in the inspector, each stat as a tile with
    /// its value colour, each row with its swatch, value colour and drill, in the page's order (the shared inspector
    /// pushes the page it was showing onto its back-stack).
    /// </summary>
    public void ShowPage(Rp1InspectPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        InspectorModel inspector = Inspector.Model;
        inspector.Show(page.Title, page.Subtitle,
            page.Stats.Select(s => inspector.CreateStat(s.Label, s.Value, s.ValueColour)).ToList(),
            page.Rows.Select(r => inspector.CreateRow(r.Primary, r.Value, r.Swatch, r.ValueColour, r.Secondary, r.Sub, r.Drill)).ToList());
    }

    private void Fuel_Click(object? sender, RoutedEventArgs e)
    {
        switch ((sender as Control)?.DataContext)
        {
            case Rp1GenRowModel g: Model.InspectFuel(g.Fuel); break;
            case Rp1InertiaRowModel i: Model.InspectFuel(i.Fuel); break;
        }
    }

    private void Bm_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is Rp1BmRowModel b) Model.InspectBm(b);
    }

    private void Ic_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is Rp1IcRowModel ic) Model.InspectIc(ic);
    }
}
