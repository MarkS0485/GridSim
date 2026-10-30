using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using GridSim.Core.Presentation;
using GridSim.Ui.Presentation;

// Test oracle support for UiTabsThirdBatchParityTests (UI programme Phase 1.6, third tab batch). NOT production code.
//
// The old WPF view-models of the HUMAN SCALE, NESO RP1 and EXPERIMENTAL tabs (HumanScaleVm, NesoRp1Vm with the
// InspectorVm it fills, ExperimentalVm, all at 2.0.28) are kept verbatim in this folder as the oracle, with the two
// methods of the old AuditLogWindow that decide what the audit-log window shows. They named a handful of WPF types;
// these shims stand in for exactly what the old code read from them, nothing more (the same rules as the first
// batch's OldTabs/OldTabsShims.cs, in a folder of its own so that oracle stays untouched):
//   - a Color is four bytes (Color.FromRgb makes it opaque, as the WPF one does);
//   - a Brush is only ever a solid colour here (SolidColorBrush holds one Color; Freeze only records that it was called);
//   - Brushes.Gray / Brushes.Transparent / Brushes.White carry the WPF named colours' bytes (#FF808080, #00FFFFFF,
//     #FFFFFFFF);
//   - Palette is the WPF Palette at 2.0.28: its colour arithmetic already was the neutral ColourRamps (UiColourRampsTests
//     pins that byte for byte against the older WPF arithmetic), converted to a Color; Frozen caches one brush per colour;
//   - Fuels is the WPF Visuals/Fuels.cs at 2.0.28, verbatim apart from its namespace;
//   - Visibility is the two values the tabs used;
//   - RelayCommand / RelayCommand<T> are the WPF Views/Assets/AssetPages.cs ones at 2.0.28, verbatim (ICommand is the
//     base class library interface, so it needs no shim);
//   - Notify is the WPF DashboardViewModel.cs base class at 2.0.28, verbatim.

namespace GridSim.Tests.Ui.OldTabsC;

/// <summary>Shim of the WPF colour: alpha, red, green, blue.</summary>
public readonly record struct Color(byte A, byte R, byte G, byte B)
{
    /// <summary>The WPF Color.FromRgb: an opaque colour.</summary>
    public static Color FromRgb(byte r, byte g, byte b) => new(0xFF, r, g, b);

    /// <summary>The neutral token with the same four bytes (for the comparison).</summary>
    public UiColour Token => new(A, R, G, B);

    /// <summary>The colour with the same four bytes as a neutral token.</summary>
    public static Color From(UiColour c) => new(c.A, c.R, c.G, c.B);
}

/// <summary>Shim of the WPF brush: the old tab code only ever made solid ones.</summary>
public abstract class Brush { }

/// <summary>Shim of the WPF solid brush: one colour.</summary>
public sealed class SolidColorBrush : Brush
{
    public SolidColorBrush(Color c) { Color = c; }
    public Color Color { get; }
    public bool IsFrozen { get; private set; }
    public void Freeze() => IsFrozen = true;
}

/// <summary>Shim of the WPF named brushes the tabs used, with the WPF colours' exact bytes.</summary>
public static class Brushes
{
    public static readonly SolidColorBrush Gray = new(new Color(0xFF, 0x80, 0x80, 0x80));          // Colors.Gray
    public static readonly SolidColorBrush Transparent = new(new Color(0x00, 0xFF, 0xFF, 0xFF));   // Colors.Transparent
    public static readonly SolidColorBrush White = new(new Color(0xFF, 0xFF, 0xFF, 0xFF));         // Colors.White
}

/// <summary>Shim of the WPF Visibility values the tabs used.</summary>
public enum Visibility { Visible, Collapsed }

/// <summary>Shim of the WPF Palette at 2.0.28 (its ramps were the neutral ColourRamps, converted byte for byte).</summary>
public static class Palette
{
    public static Color FromHex(string hex) => Color.From(ColourRamps.FromHex(hex));
    public static Color Voltage(double vm) => Color.From(ColourRamps.Voltage(vm));
    public static Color Loading(double frac) => Color.From(ColourRamps.Loading(frac));

    private static readonly Dictionary<Color, SolidColorBrush> FrozenCache = new();

    public static SolidColorBrush Frozen(Color c)
    {
        lock (FrozenCache)
        {
            if (FrozenCache.TryGetValue(c, out SolidColorBrush? cached)) return cached;
            var b = new SolidColorBrush(c);
            b.Freeze();
            FrozenCache[c] = b;
            return b;
        }
    }
}

// ------------------------------------------------ the old Views/Assets/AssetPages.cs commands ------------------------------------------------

/// <summary>Minimal ICommand for asset-page navigation.</summary>
public sealed class RelayCommand(Action exec) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => exec();
}

/// <summary>ICommand that passes its <c>CommandParameter</c> through to the handler  -  used for
/// cross-links whose target id changes per frame (the parameter binds to a live VM property).</summary>
public sealed class RelayCommand<T>(Action<T> exec) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) { if (p is T t) exec(t); }
}

// ------------------------------------------------ the old DashboardViewModel.cs Notify ------------------------------------------------

/// <summary>Minimal INotifyPropertyChanged base.</summary>
public abstract class Notify : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Notify a derived/computed property alongside a <see cref="Set{T}"/>.</summary>
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ------------------------------------------------ the old Visuals/Fuels.cs ------------------------------------------------

/// <summary>How a generator's fuel sits in the inertia / decarbonisation picture.</summary>
public enum FuelClass { Fossil, Nuclear, Renewable, Storage, Interconnector, Other }

/// <summary>A classified fuel: display name, colour, and whether it's a spinning (inertial) machine.</summary>
public readonly record struct FuelInfo(string Name, Color Color, FuelClass Class, bool Synchronous)
{
    public bool IsFossil => Class == FuelClass.Fossil;
    public bool IsRenewable => Class == FuelClass.Renewable;
}

/// <summary>
/// The WPF view of the shared fuel taxonomy (Visuals/Fuels.cs at 2.0.28): it only translates Core's
/// <see cref="FuelCategory"/> and hex colour into the WPF <see cref="FuelClass"/> and <see cref="Color"/>.
/// </summary>
public static class Fuels
{
    public static FuelInfo Classify(string? type)
    {
        FuelDescriptor d = FuelTaxonomy.Classify(type);
        return new FuelInfo(d.Name, Palette.FromHex(d.Color), ToFuelClass(d.Category), d.Synchronous);
    }

    /// <summary>One-to-one translation of Core's category; an unknown value is a programming error, so it throws.</summary>
    public static FuelClass ToFuelClass(FuelCategory c) => c switch
    {
        FuelCategory.Fossil => FuelClass.Fossil,
        FuelCategory.Nuclear => FuelClass.Nuclear,
        FuelCategory.Renewable => FuelClass.Renewable,
        FuelCategory.Storage => FuelClass.Storage,
        FuelCategory.Interconnector => FuelClass.Interconnector,
        FuelCategory.Other => FuelClass.Other,
        _ => throw new ArgumentOutOfRangeException(nameof(c), c, "Unknown fuel category from the Core taxonomy."),
    };
}
