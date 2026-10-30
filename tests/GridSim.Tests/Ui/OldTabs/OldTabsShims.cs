using System.ComponentModel;
using System.Runtime.CompilerServices;
using GridSim.Core.Presentation;
using GridSim.Ui.Presentation;

// Test oracle support for UiTabsParityTests (UI programme Phase 1.6, first tab batch). NOT production code.
//
// The five old WPF tab view-models (OperatorVm, MarketVm, ScadaVm, DomainsVm, CycleVm at 2.0.22) are kept verbatim in
// this folder as the oracle. They named a handful of WPF types; these shims stand in for exactly what the old code read
// from them, nothing more:
//   - a Color is four bytes (Color.FromRgb makes it opaque, as the WPF one does); Colors.Gray is the WPF #FF808080;
//   - a Brush is only ever a solid colour here (SolidColorBrush holds one Color; Freeze only records that it was called);
//   - Brushes.Gray / Brushes.Transparent carry the WPF named colours' bytes (#FF808080, #00FFFFFF);
//   - Palette is the WPF Palette at 2.0.22: its colour arithmetic already was the neutral ColourRamps (UiColourRampsTests
//     pins that byte for byte against the older WPF arithmetic), converted to a Color; Frozen caches one brush per colour;
//   - Fuels is the WPF Visuals/Fuels.cs at 2.0.22, verbatim apart from its namespace;
//   - Visibility is the two values the tabs used;
//   - Application.Current is null (no WPF application runs in the test host), exactly what the old CycleVm saw when it
//     ran outside an application: a completed cycle is then not projected by itself, and the tests project it.
//   - Notify is the WPF DashboardViewModel.cs base class at 2.0.22, verbatim.

namespace GridSim.Tests.Ui.OldTabs;

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

/// <summary>Shim of the WPF named colours the tabs used.</summary>
public static class Colors
{
    public static readonly Color Gray = new(0xFF, 0x80, 0x80, 0x80);
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
}

/// <summary>Shim of the WPF Visibility values the tabs used.</summary>
public enum Visibility { Visible, Collapsed }

/// <summary>Shim of the WPF Palette at 2.0.22 (its ramps were the neutral ColourRamps, converted byte for byte).</summary>
public static class Palette
{
    public static Color FromHex(string hex) => Color.From(ColourRamps.FromHex(hex));
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

/// <summary>Shim of the WPF application: none runs in the test host.</summary>
public sealed class Application
{
    public static Application? Current => null;
    public Dispatcher Dispatcher { get; } = new();
}

/// <summary>Shim of the WPF dispatcher (never reached: <see cref="Application.Current"/> is null).</summary>
public sealed class Dispatcher
{
    public int Calls { get; private set; }

    public object? BeginInvoke(Delegate method)
    {
        Calls++;
        throw new InvalidOperationException("no WPF dispatcher in the test host");
    }
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
/// The WPF view of the shared fuel taxonomy. The keyword table lives in ONE place, Core's
/// <see cref="FuelTaxonomy"/> (N016 / R305 remainder, October 2026): this class used to keep its own copy, which had
/// drifted from the web and Core one - it read "hydrogen" as synchronous Hydro (the substring "hydro") and left
/// reciprocating engines ("recip", "diesel", "engine") as non-synchronous "Other". It now only translates Core's
/// <see cref="FuelCategory"/> and hex colour into the WPF <see cref="FuelClass"/> and <see cref="Color"/>, exactly as
/// the web <c>Fuels</c> does, so existing WPF callers are unchanged.
/// <para>Behaviour change, stated: a generator typed "hydrogen" / "fuel cell" / "electrolyser" now reads as
/// Hydrogen (storage, converter-connected, no inertia) instead of Hydro (renewable, synchronous); "recip" / "diesel"
/// / "engine" now reads as Reciprocating engine (fossil, synchronous) instead of Other. Every other classification
/// and colour is identical to the old WPF table.</para>
/// Spinning (synchronous) plant contributes rotational inertia; wind, solar, storage and interconnectors are
/// converter-connected and contribute none.
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
