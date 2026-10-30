using Avalonia.Controls;
using Avalonia.Interactivity;
using GridSim.Ui.Tabs.Scada;

namespace GridSim.App.Tabs;

/// <summary>
/// The SCADA tab (UI programme Phase 1.6). Everything it shows is bound to the shared <see cref="ScadaModel"/>. The
/// three WPF commands are click handlers over the same model methods: a register row hands its point to
/// <see cref="ScadaModel.OpenPoint"/> (which does nothing until a host wires <see cref="ScadaModel.Navigate"/>: the
/// Avalonia app has no ASSETS explorer yet), a row's ACK calls <see cref="ScadaModel.Ack"/>, and ACK ALL calls
/// <see cref="ScadaModel.AckAll"/>.
/// </summary>
public sealed partial class ScadaView : UserControl
{
    public ScadaView()
    {
        InitializeComponent();
    }

    // The tab's model: a click with no model behind the view is a wiring mistake and fails loud.
    private ScadaModel Model => DataContext as ScadaModel
        ?? throw new InvalidOperationException("The SCADA view has no ScadaModel as its DataContext.");

    private void Point_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ScadaPointModel p) Model.OpenPoint(p);
    }

    private void Ack_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ScadaEventModel ev) Model.Ack(ev);
    }

    private void AckAll_Click(object? sender, RoutedEventArgs e) => Model.AckAll();
}
