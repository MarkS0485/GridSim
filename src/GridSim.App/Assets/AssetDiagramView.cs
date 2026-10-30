using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using GridSim.App.Controls.Diagram;
using GridSim.Ui.Assets.Navigation;
using GridSim.Ui.Assets.Pages;
using GridSim.Ui.Diagrams;

namespace GridSim.App.Assets;

/// <summary>
/// The richer single-line view of a substation or transformer page (UI programme ASSETS A3): the A1b toolkit diagram
/// (<see cref="AssetDiagrams.For"/>: <see cref="SubstationDiagramBuilder"/> or <see cref="TransformerDiagramBuilder"/>)
/// drawn by the A1b <see cref="DiagramControl"/>. It follows its <see cref="Page"/>: the diagram is rebuilt from the
/// page when the page sets the property it sets last in each refresh (<see cref="AssetDiagrams.RebuildOn"/>), and only
/// while this view is visible (it is behind the page's IEC VIEW toggle; turning it on rebuilds at once). A left click
/// on a symbol is passed on (<see cref="SymbolClicked"/>); the ASSETS view hands it to the navigator, which opens what
/// the symbol stands for.
/// </summary>
public sealed class AssetDiagramView : Decorator
{
    /// <summary>The page whose diagram this view shows.</summary>
    public static readonly StyledProperty<AssetPageModel?> PageProperty =
        AvaloniaProperty.Register<AssetDiagramView, AssetPageModel?>(nameof(Page));

    private AssetPageModel? _watched;
    private bool _attached;

    /// <summary>A view with an empty diagram control that fits the whole diagram.</summary>
    public AssetDiagramView()
    {
        Diagram = new DiagramControl { AutoFit = true };
        Diagram.SymbolClicked += (_, e) => SymbolClicked?.Invoke(this, e);
        Child = Diagram;
    }

    /// <inheritdoc cref="PageProperty"/>
    public AssetPageModel? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }

    /// <summary>The diagram control inside (its model is the page's diagram).</summary>
    public DiagramControl Diagram { get; }

    /// <summary>A left click on a symbol or connection of the diagram.</summary>
    public event EventHandler<DiagramHitEventArgs>? SymbolClicked;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty) Watch();
        else if (change.Property == IsVisibleProperty && IsVisible) Rebuild();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Watch();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Watch();   // a view off screen watches no page, so a closed page is not held by its handler
    }

    // Follow the current page while on screen: unsubscribe from the old one, subscribe to the new, rebuild.
    private void Watch()
    {
        AssetPageModel? want = _attached ? Page : null;
        if (ReferenceEquals(want, _watched)) return;
        if (_watched is not null) _watched.PropertyChanged -= OnPageChanged;
        _watched = want;
        if (_watched is not null) _watched.PropertyChanged += OnPageChanged;
        Rebuild();
    }

    private void OnPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not null && e.PropertyName == AssetDiagrams.RebuildOn(_watched)) Rebuild();
    }

    // The page's diagram now (nothing while hidden: the toggle's turning on rebuilds).
    private void Rebuild()
    {
        if (!IsVisible) return;
        Diagram.Model = AssetDiagrams.For(_watched);
    }
}
