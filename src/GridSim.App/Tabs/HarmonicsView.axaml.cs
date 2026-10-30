using Avalonia.Controls;

namespace GridSim.App.Tabs;

/// <summary>
/// The HARMONICS tab (UI programme slice 1.7). Everything it shows is bound to the shared
/// <see cref="GridSim.Ui.Tabs.Harmonics.HarmonicsModel"/>: four fixed texts, the WPF ones, fed nothing per frame (WPF
/// feeds its HARMONICS tab nothing either).
/// </summary>
public sealed partial class HarmonicsView : UserControl
{
    public HarmonicsView()
    {
        InitializeComponent();
    }
}
