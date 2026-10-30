using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using GridSim.Ui.Diagrams;
using GridSim.Ui.Schematic;
using GridSim.Ui.Visuals;

namespace GridSim.App.Controls.Diagram;

/// <summary>What a hover or a click on a diagram found.</summary>
public sealed class DiagramHitEventArgs : EventArgs
{
    /// <summary>A hover or click result.</summary>
    public DiagramHitEventArgs(DiagramHit? hit, ScreenPoint diagramPoint)
    {
        Hit = hit;
        DiagramPoint = diagramPoint;
    }

    /// <summary>The symbol or connection under the pointer, or null (a hover that left everything).</summary>
    public DiagramHit? Hit { get; }

    /// <summary>The pointer position in diagram units.</summary>
    public ScreenPoint DiagramPoint { get; }
}

/// <summary>
/// An Avalonia control that draws an electrical diagram (UI programme ASSETS A1b): it renders a GridSim.Ui
/// <see cref="DiagramModel"/> through <see cref="DiagramRenderer"/> and the shared display-list painter, and reports
/// the symbol or connection under the pointer (<see cref="SymbolHovered"/> when it changes, <see cref="SymbolClicked"/>
/// on a left press).
///
/// <para>The view fits the whole diagram (with a margin) while <see cref="AutoFit"/> is on; the mouse wheel zooms about
/// the pointer and turns <see cref="AutoFit"/> off, and <see cref="Viewport"/> can also be set directly. The display
/// list is cached per model version, control size and viewport, so a frame that changes nothing rebuilds nothing; a
/// live-state change in the model (which raises its Changed event) redraws.</para>
/// </summary>
public sealed class DiagramControl : Control, ICustomHitTest
{
    /// <summary>The diagram to draw (null draws nothing).</summary>
    public static readonly StyledProperty<DiagramModel?> ModelProperty =
        AvaloniaProperty.Register<DiagramControl, DiagramModel?>(nameof(Model));

    /// <summary>True to keep the whole diagram fitted to the control (the default).</summary>
    public static readonly StyledProperty<bool> AutoFitProperty =
        AvaloniaProperty.Register<DiagramControl, bool>(nameof(AutoFit), true);

    /// <summary>The scale and offset used while <see cref="AutoFit"/> is off.</summary>
    public static readonly StyledProperty<DiagramViewport> ViewportProperty =
        AvaloniaProperty.Register<DiagramControl, DiagramViewport>(nameof(Viewport), DiagramViewport.Identity);

    /// <summary>The zoom step of one mouse-wheel notch.</summary>
    public const double WheelZoomFactor = 1.2;

    private VisualDisplayList? _cached;
    private (DiagramModel? Model, int Version, Size Size, DiagramViewport View) _cacheKey;
    private DiagramModel? _subscribed;
    private DiagramHit? _hover;

    static DiagramControl()
    {
        AffectsRender<DiagramControl>(ModelProperty, AutoFitProperty, ViewportProperty);
    }

    /// <inheritdoc cref="ModelProperty"/>
    public DiagramModel? Model { get => GetValue(ModelProperty); set => SetValue(ModelProperty, value); }

    /// <inheritdoc cref="AutoFitProperty"/>
    public bool AutoFit { get => GetValue(AutoFitProperty); set => SetValue(AutoFitProperty, value); }

    /// <inheritdoc cref="ViewportProperty"/>
    public DiagramViewport Viewport { get => GetValue(ViewportProperty); set => SetValue(ViewportProperty, value); }

    /// <summary>Raised when the symbol or connection under the pointer changes (to null when it leaves them).</summary>
    public event EventHandler<DiagramHitEventArgs>? SymbolHovered;

    /// <summary>Raised on a left press over a symbol or a connection.</summary>
    public event EventHandler<DiagramHitEventArgs>? SymbolClicked;

    /// <summary>The viewport in use now: fitted to the control while <see cref="AutoFit"/> is on, else <see cref="Viewport"/>.</summary>
    public DiagramViewport EffectiveViewport =>
        AutoFit ? DiagramViewport.Fit(Model?.Extent(), Bounds.Width, Bounds.Height) : Viewport;

    /// <summary>The symbol or connection at a point of this control (control coordinates), or null.</summary>
    public DiagramHit? HitTestAt(Point p) =>
        Model is DiagramModel m ? DiagramHitTester.HitTestScreen(m, EffectiveViewport, new ScreenPoint(p.X, p.Y)) : null;

    /// <summary>The whole control takes pointer input (empty areas included), so hovering off a symbol is reported.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == ModelProperty) Resubscribe();
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Resubscribe();
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Drop the model's handler so a model that outlives the control does not keep it alive.
        if (_subscribed is not null) _subscribed.Changed -= OnModelChanged;
        _subscribed = null;
    }

    private void Resubscribe()
    {
        if (_subscribed is not null) _subscribed.Changed -= OnModelChanged;
        _subscribed = Model;
        if (_subscribed is not null) _subscribed.Changed += OnModelChanged;
    }

    private void OnModelChanged(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>The display list for the current model, size and viewport (rebuilt only when one of them changed).</summary>
    public VisualDisplayList? CurrentDrawing()
    {
        DiagramModel? m = Model;
        if (m is null) return null;
        DiagramViewport view = EffectiveViewport;
        var key = (m, m.Version, Bounds.Size, view);
        if (_cached is null || key != _cacheKey)
        {
            _cached = DiagramRenderer.Render(m, view);
            _cacheKey = key;
        }
        return _cached;
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        VisualDisplayList? list = CurrentDrawing();
        if (list is not null) DisplayListPainter.Paint(context, list, this);
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
        Point p = e.GetPosition(this);
        DiagramHit? hit = HitTestAt(p);
        if (Equals(hit, _hover)) return;
        _hover = hit;
        SymbolHovered?.Invoke(this, new DiagramHitEventArgs(hit, EffectiveViewport.ToDiagram(new ScreenPoint(p.X, p.Y))));
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is null) return;
        _hover = null;
        SymbolHovered?.Invoke(this, new DiagramHitEventArgs(null, new ScreenPoint(double.NaN, double.NaN)));
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerPressed(e);
        PointerPoint pt = e.GetCurrentPoint(this);
        if (!pt.Properties.IsLeftButtonPressed) return;
        DiagramHit? hit = HitTestAt(pt.Position);
        if (hit is null) return;
        SymbolClicked?.Invoke(this, new DiagramHitEventArgs(hit, EffectiveViewport.ToDiagram(new ScreenPoint(pt.Position.X, pt.Position.Y))));
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerWheelChanged(e);
        if (Model is null || e.Delta.Y == 0) return;
        Point p = e.GetPosition(this);
        DiagramViewport from = EffectiveViewport;
        AutoFit = false;
        Viewport = from.ZoomAbout(new ScreenPoint(p.X, p.Y), e.Delta.Y > 0 ? WheelZoomFactor : 1 / WheelZoomFactor);
        e.Handled = true;
    }
}
