// HOW TO FEED (UI programme Phase 1.6; integrated 2026-10-20). The inspector is an overlay with no frame feed of its own:
// the tab that owns it fills it on a click (WPF: the MECHANICAL and NESO RP1 tabs). Give it that tab's inspector, in one line:
//
//     InspectorView.Model = machineEngineerView.Model.Inspector;
//
// (MachineEngineerView does this for its own overlay. NesoRp1View keeps the inspector's own model and shows each page
// raised on NesoRp1Model.InspectRequested in it, as WPF NesoRp1Vm.ShowInspector; integrated 2026-10-20.)

using Avalonia.Controls;
using Avalonia.Interactivity;
using GridSim.Ui.Tabs.Inspector;

namespace GridSim.App.Tabs;

/// <summary>
/// The drill-down inspector overlay of the WPF window (UI programme Phase 1.6): a title, a row of stat tiles and the
/// constituent rows of whatever was clicked, with a back-stack. Its data context is <see cref="Model"/>; it shows only
/// while the model is open. See the comment at the top of this file for the one line that wires it.
/// </summary>
public partial class InspectorView : UserControl
{
    /// <summary>An inspector with its own (closed, empty) model until <see cref="Model"/> is set.</summary>
    public InspectorView()
    {
        InitializeComponent();
        DataContext = new InspectorModel();
    }

    /// <summary>The inspector shown (the same object as the data context). Setting it rebinds the overlay.</summary>
    public InspectorModel Model
    {
        get => (InspectorModel)DataContext!;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            DataContext = value;
        }
    }

    // WPF BackCommand: the previous page, or close when there is none.
    private void Back_Click(object? sender, RoutedEventArgs e) => Model.Back();

    // WPF CloseCommand.
    private void Close_Click(object? sender, RoutedEventArgs e) => Model.Close();

    // WPF: each row is a button bound to the row's Drill command (none: the click does nothing).
    private void Row_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: InspectRowModel row }) row.DrillIn();
    }
}
