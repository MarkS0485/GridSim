using System.ComponentModel;
using System.Runtime.CompilerServices;
using GridSim.Core.Presentation;
using GridSim.Ui.Presentation;

// Test oracle support for UiAssetPagesParityTests (UI programme ASSETS A2). NOT production code.
//
// The five old WPF asset page files (AssetPages, GeneratorPages, TransformerPages, CircuitPages, SubstationPages at
// commit 6e729a66, 2.0.46) are kept verbatim in this folder as the oracle. They named a handful of WPF types; these shims
// stand in for exactly what the old code read from them, nothing more. They live in their own namespace
// (GridSim.Tests.Ui.OldAssets), so they cannot clash with the other oracles' shims (OldTabs, OldTabsC):
//   - a Color is four bytes; Colors.Gray is the WPF #FF808080;
//   - a Brush is only ever a solid colour here (SolidColorBrush holds one Color; Freeze only records that it was called);
//   - Brushes.Gray carries the WPF named colour's bytes (#FF808080);
//   - Palette is the WPF Palette at 2.0.46: its colour arithmetic already was the neutral ColourRamps (UiColourRampsTests
//     pins that byte for byte against the older WPF arithmetic), converted to a Color; Frozen caches one brush per colour;
//   - Fuels / FuelInfo / FuelClass are the WPF Visuals/Fuels.cs at 2.0.46, verbatim apart from the namespace;
//   - Visibility is the two values the pages used;
//   - Notify is the WPF DashboardViewModel.cs base class at 2.0.46, verbatim;
//   - Visuals.MapConnection / MapGenerator / SubstationMapData are the data records of the WPF Visuals/SubstationMap.cs at
//     2.0.46, verbatim (the control itself is not needed: the page only builds the data).

namespace GridSim.Tests.Ui.OldAssets
{
    /// <summary>Shim of the WPF colour: alpha, red, green, blue.</summary>
    public readonly record struct Color(byte A, byte R, byte G, byte B)
    {
        /// <summary>The neutral token with the same four bytes (for the comparison).</summary>
        public UiColour Token => new(A, R, G, B);

        /// <summary>The colour with the same four bytes as a neutral token.</summary>
        public static Color From(UiColour c) => new(c.A, c.R, c.G, c.B);
    }

    /// <summary>Shim of the WPF named colours the pages used.</summary>
    public static class Colors
    {
        public static readonly Color Gray = new(0xFF, 0x80, 0x80, 0x80);
    }

    /// <summary>Shim of the WPF brush: the old page code only ever made solid ones.</summary>
    public abstract class Brush { }

    /// <summary>Shim of the WPF solid brush: one colour.</summary>
    public sealed class SolidColorBrush : Brush
    {
        public SolidColorBrush(Color c) { Color = c; }
        public Color Color { get; }
        public bool IsFrozen { get; private set; }
        public void Freeze() => IsFrozen = true;
    }

    /// <summary>Shim of the WPF named brushes the pages used, with the WPF colours' exact bytes.</summary>
    public static class Brushes
    {
        public static readonly SolidColorBrush Gray = new(new Color(0xFF, 0x80, 0x80, 0x80));   // Colors.Gray
    }

    /// <summary>Shim of the WPF Visibility values the pages used.</summary>
    public enum Visibility { Visible, Collapsed }

    /// <summary>Shim of the WPF Palette at 2.0.46 (its ramps were the neutral ColourRamps, converted byte for byte).</summary>
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

    // ------------------------------------------------ the old DashboardViewModel.cs Notify ------------------------------------------------

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

    /// <summary>The WPF view of the shared fuel taxonomy (verbatim apart from the namespace).</summary>
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
}

// ------------------------------------------------ the old Visuals/SubstationMap.cs data records ------------------------------------------------
// The old SubstationPages.cs names these as "Visuals.X" from its namespace, so they live in a nested Visuals namespace.

namespace GridSim.Tests.Ui.OldAssets.Visuals
{
    /// <summary>Snapshot of one connection for the substation single-line diagram.</summary>
    public sealed record MapConnection(
        string Name, string OtherEnd, string Kind, double FlowMw, bool Exporting, double LoadingFrac);

    /// <summary>Snapshot of one generator for the substation single-line diagram.</summary>
    public sealed record MapGenerator(string Name, string Fuel, Color FuelColor, double OutputMw);

    /// <summary>All the data the SubstationMap needs for one frame, built by the VM.</summary>
    public sealed class SubstationMapData
    {
        public double VoltagePu { get; init; }
        public double AngleDeg { get; init; }
        public double BaseKv { get; init; }
        public double DemandMw { get; init; }
        public double ReactiveMvar { get; init; }
        public double GenerationMw { get; init; }
        public string BusType { get; init; } = "";
        public double NetInjectionMw { get; init; }
        public IReadOnlyList<MapConnection> Connections { get; init; } = [];
        public IReadOnlyList<MapGenerator> Generators { get; init; } = [];
    }
}
