using System.Windows;
using System.Windows.Controls;

namespace GridSim.Wpf.Views;

public partial class GenerationView : UserControl
{
    public GenerationView()
    {
        InitializeComponent();
        // Refresh the trace whenever a trip is analysed  -  from this tab's button OR a
        // network-map right-click trip (both route through DashboardViewModel.AnalyseTrip).
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is DashboardViewModel ov) ov.TripCompared -= OnTripCompared;
            if (e.NewValue is DashboardViewModel nv) nv.TripCompared += OnTripCompared;
        };
    }

    private void OnTripCompared()
    {
        if (DataContext is DashboardViewModel vm) FreqTrace.SetResponses(vm.BaselineResponse, vm.TsgbResponse, vm.TripBands);   // N297: lines about the case's nominal
    }

    private void Trip_Click(object sender, RoutedEventArgs e) =>
        (DataContext as DashboardViewModel)?.TripCompare();
}
