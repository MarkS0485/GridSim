using System.Collections.Generic;
using System.Windows.Media;
using GridSim.Ui.Tabs.MachineEngineer;
using GridSim.Wpf.Visuals;

namespace GridSim.Wpf.Views;

// UI programme Phase 1.6 (second tab batch): the MECHANICAL / PLANT ENGINEER tab's logic moved to the platform-neutral
// GridSim.Ui.Tabs.MachineEngineer.MachineEngineerModel (src/GridSim.Ui/Tabs/MachineEngineer/MachineEngineerModel.cs),
// line for line, with each WPF Brush replaced by a neutral colour token (UiColour). This file is now the thin WPF side of
// that boundary: the view-model and its rows derive from the neutral ones and add, under the OLD property names the XAML
// binds (RocofLegacyBrush, RocofModernBrush, ProjBrush, a row's Swatch, RiskBrush and RampBrush), the frozen brush of
// each token (Palette.Frozen: the same colour bytes, the same cached brush instances), and the InspectMachineCommand
// the rows' buttons bind. Its inspector is the WPF InspectorVm, so the overlay's visibilities and commands are the old
// ones. The dashboard still feeds and resets it through its hooks (Mechanical.Update(f, Clock), Mechanical.Reset(),
// Mechanical.SetIntrinsic(...)), at exactly the points it did. What the user sees is unchanged.

/// <summary>
/// THE MECHANICAL / PLANT ENGINEER TAB: the neutral <see cref="MachineEngineerModel"/> plus its WPF brushes, command,
/// inspector and rows.
/// </summary>
public sealed class MachineEngineerVm : MachineEngineerModel
{
    /// <summary>The rows' click: open the inspector on that machine (the neutral <see cref="MachineEngineerModel.InspectMachine"/>).</summary>
    public System.Windows.Input.ICommand InspectMachineCommand { get; }

    public MachineEngineerVm() : base(new InspectorVm())
    {
        InspectMachineCommand = new GridSim.Wpf.Views.Assets.RelayCommand<MachineRow>(InspectMachine);
    }

    // ---- the WPF brushes: each the cached frozen brush of its neutral colour token ----

    public Brush RocofLegacyBrush => Palette.Frozen(RocofLegacyColour);
    public Brush RocofModernBrush => Palette.Frozen(RocofModernColour);
    public Brush ProjBrush => Palette.Frozen(ProjColour);

    /// <summary>Each neutral colour property and the WPF brush property the XAML binds in its place.</summary>
    private static readonly Dictionary<string, string> AdapterOf = new(System.StringComparer.Ordinal)
    {
        [nameof(RocofLegacyColour)] = nameof(RocofLegacyBrush),
        [nameof(RocofModernColour)] = nameof(RocofModernBrush),
        [nameof(ProjColour)] = nameof(ProjBrush),
    };

    /// <summary>Announce the neutral property, then (when it has one) the WPF adapter property bound in its place.</summary>
    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name is not null && AdapterOf.TryGetValue(name, out string? adapter)) base.OnPropertyChanged(adapter);
    }

    // ---- the WPF rows ----

    protected override MachineRowModel CreateMachineRow() => new MachineRow();
    protected override StartRowModel CreateStartRow(string note) => new StartRow { Note = note };
}

/// <summary>One synchronous machine's row (the WPF row: adds the <see cref="Swatch"/>, <see cref="RiskBrush"/> and
/// <see cref="RampBrush"/> brushes).</summary>
public sealed class MachineRow : MachineRowModel
{
    public Brush Swatch => Palette.Frozen(SwatchColour);
    public Brush RiskBrush => Palette.Frozen(RiskColour);
    public Brush RampBrush => Palette.Frozen(RampColour);

    protected override void OnPropertyChanged(string? name)
    {
        base.OnPropertyChanged(name);
        if (name == nameof(SwatchColour)) base.OnPropertyChanged(nameof(Swatch));
        else if (name == nameof(RiskColour)) base.OnPropertyChanged(nameof(RiskBrush));
        else if (name == nameof(RampColour)) base.OnPropertyChanged(nameof(RampBrush));
    }
}

/// <summary>One committed-start line (the WPF row; nothing to add).</summary>
public sealed class StartRow : StartRowModel
{
}
