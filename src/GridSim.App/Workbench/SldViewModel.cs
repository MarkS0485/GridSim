using CommunityToolkit.Mvvm.ComponentModel;
using GridSim.Core.Licensing;
using GridSim.Core.Model;
using GridSim.Core.Presentation.Workbench;
using GridSim.Core.Presentation.Workbench.Sld;

namespace GridSim.App.Workbench;

/// <summary>
/// The view model of the single-line diagram pane of the workbench window (R4-18). It computes nothing of its own: the diagram is
/// <see cref="SldDiagrams.Build"/> (Core), the colours are <see cref="SldColouring"/> (Core), the hit test is <see cref="SldHitTester"/>
/// and every edit is an <see cref="SldCommand"/> given to an <see cref="ISldDocument"/> (Core), so the web client, which uses the same
/// calls, draws the same geometry and makes the same edits. The pane's controller code is therefore only selection state, pointer
/// arithmetic and wording.
/// </summary>
public sealed class SldViewModel : ObservableObject
{
    private ISldDocument? _doc;
    private SldModel? _readOnly;
    private SldColourInput? _input;
    private SldLayoutMode _requested = SldLayoutMode.Auto;
    private SldColourMode _colourMode = SldColourMode.VoltageLevel;
    private string? _selectedId;
    private string _status = "No diagram: load a case.";
    private long _seenRevision = -1;
    private bool _announceChanges = true;

    /// <summary>The sizes and spacings in force (the Core defaults).</summary>
    public SldOptions Options { get; } = new();

    /// <summary>The editable source, or null when the diagram is read-only (a MATPOWER case, or a JSON case that could not be opened for editing).</summary>
    public ISldDocument? Document => _doc;

    public bool HasDiagram => Diagram is not null;
    public SldDiagram? Diagram { get; private set; }
    public SldColourSet? Colours { get; private set; }
    public string? SelectedId { get => _selectedId; private set => SetProperty(ref _selectedId, value); }
    public string StatusText { get => _status; set => SetProperty(ref _status, value); }

    /// <summary>The layout the user asked for; the diagram's own <see cref="SldDiagram.Mode"/> is what was possible.</summary>
    public SldLayoutMode RequestedLayout
    {
        get => _requested;
        set { if (SetProperty(ref _requested, value)) Rebuild(); }
    }

    public SldColourMode ColourMode
    {
        get => _colourMode;
        set { if (SetProperty(ref _colourMode, value)) Rebuild(); }
    }

    public bool CanEdit => _doc is { CanEdit: true };
    public bool CanMove => CanEdit && Diagram is { CanMove: true } && _doc is SldCaseDocument;
    public bool CanUndo => _doc?.CanUndo == true;
    public bool CanRedo => _doc?.CanRedo == true;
    public bool IsDirty => _doc?.IsDirty == true;

    /// <summary>Raised at the end of <see cref="Rebuild"/> when the document's revision changed since the previous rebuild (an edit, undo, redo or save), so the workbench can bring its project model in line with the document (A3).</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>The model the diagram shows.</summary>
    public SldModel? Model => _doc?.Model ?? _readOnly;

    // ------------------------------------------------------------------ loading

    /// <summary>Show a project's model read-only (a MATPOWER case, or when no editable source is known).</summary>
    public void LoadReadOnly(GridModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _doc = null;
        _readOnly = SldModel.From(model);
        _input = null;
        SelectedId = null;
        StatusText = "Read-only diagram: this source has no case files to write back to.";
        Rebuild();
    }

    /// <summary>Open a JSON case folder for editing. Returns false (read-only fallback by the caller) and says why in <see cref="StatusText"/> on failure.</summary>
    public bool TryOpenCase(string folder, LicenceGate? gate = null)
    {
        try
        {
            _doc = SldCaseDocument.Open(folder, gate ?? LicenceGate.Resolve());
            _readOnly = null;
            _input = null;
            SelectedId = null;
            StatusText = $"{_doc.Name}: editable. The automatic layout is read-only; adopt it into the case to move busbars.";
            Rebuild();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText = "The case cannot be opened for editing: " + ex.Message;
            return false;
        }
    }

    /// <summary>Open a node-breaker file (<c>gridsim-substations/1</c>) for editing its switch states.</summary>
    public bool TryOpenSubstations(string path)
    {
        try
        {
            _doc = SldSubstationDocument.Open(path);
            _readOnly = null;
            _input = null;
            SelectedId = null;
            StatusText = $"{_doc.Name}: node-breaker diagram. Select a switch and use Toggle switch.";
            Rebuild();
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            StatusText = "The node-breaker file cannot be opened: " + ex.Message;
            return false;
        }
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Rebuild the geometry and the colours from the model (after any change of source, layout request, colour mode or document revision).</summary>
    public void Rebuild()
    {
        SldModel? m = Model;
        if (m is null) { Diagram = null; Colours = null; OnPropertyChanged(nameof(HasDiagram)); return; }
        Diagram = SldDiagrams.Build(m, _requested, Options);
        Colours = SldColouring.Colour(m, Diagram.Geometry, _colourMode, _input);
        long previous = _seenRevision;
        _seenRevision = _doc?.Revision ?? 0;
        if (SelectedId is not null && Diagram.Geometry.Symbol(SelectedId) is null) SelectedId = null;
        OnPropertyChanged(nameof(Diagram));
        OnPropertyChanged(nameof(Colours));
        OnPropertyChanged(nameof(HasDiagram));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanMove));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(IsDirty));
        if (_announceChanges && _doc is not null && _seenRevision != previous) DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool _busy;

    /// <summary>True while a result colouring is being computed off the UI thread.</summary>
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }

    /// <summary>Colour by the result of a power flow of the diagram's model (needs a bus-branch diagram). A failure is said in <see cref="StatusText"/>.</summary>
    public void ColourByPowerFlow(SldColourMode mode = SldColourMode.Loading)
    {
        if (Model is not { } m) return;
        try { _input = SldResults.PowerFlow(m, _input); }
#pragma warning disable CA1031 // Not swallowed: shown in the status line.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusText = "Colour by result failed: " + ex.Message;
            return;
        }
        ColourMode = mode;
        Rebuild();
    }

    /// <summary>
    /// As <see cref="ColourByPowerFlow"/>, but the power flow runs on a worker thread and the colours are applied back on the calling
    /// (UI) context. Refused while a colouring is already running; a result for a model the diagram no longer shows (an edit landed
    /// meanwhile) is dropped and said. Any failure is said in <see cref="StatusText"/>.
    /// </summary>
    public async Task ColourByPowerFlowAsync(SldColourMode mode = SldColourMode.Loading)
    {
        if (Model is not { } m) return;
        if (IsBusy) { StatusText = "A result colouring is already running."; return; }
        IsBusy = true;
        StatusText = "Colouring by result...";
        SldColourInput? seed = _input;
        try
        {
            SldColourInput input = await Task.Run(() => SldResults.PowerFlow(m, seed)).ConfigureAwait(true);
            if (!ReferenceEquals(Model, m)) { StatusText = "Colour by result dropped: the diagram changed while it ran."; return; }
            _input = input;
            ColourMode = mode;
            Rebuild();
            StatusText = "Coloured by result.";
        }
#pragma warning disable CA1031 // Not swallowed: shown in the status line.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusText = "Colour by result failed: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    /// <summary>The diagram as SVG (the same text the web client shows).</summary>
    public string? ToSvg() => Diagram is null ? null : SldExport.ToSvg(Diagram.Geometry, Colours, Model is { } m && m.Grid is { } g ? g.Name : "Single-line diagram");

    // ------------------------------------------------------------------ pointer

    /// <summary>Select what is at a point of the diagram (units of the geometry); returns the hit.</summary>
    public SldHit SelectAt(double x, double y)
    {
        if (Diagram is null) return new SldHit(SldHitKind.None, "", "", 0);
        SldHit hit = SldHitTester.HitTest(Diagram.Geometry, x, y, 4);
        SelectedId = hit.Kind == SldHitKind.None ? null : hit.ElementId;
        return hit;
    }

    public void Select(string? id) => SelectedId = id;

    /// <summary>The bus id of the selected busbar, or null when something else (or nothing) is selected.</summary>
    public int? SelectedBus => Model is { } m && SelectedId is { } id && id.StartsWith("bus/", StringComparison.Ordinal)
        && int.TryParse(id.AsSpan(4), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int b) && m.NodeOfBus(b) >= 0 ? b : null;

    // ------------------------------------------------------------------ edits

    private SldCommandResult Run(SldCommand c)
    {
        if (_doc is null) return Report(SldCommandResult.Refused("This diagram is read-only."));
        SldCommandResult r = _doc.Execute(c);
        Afterwards(r);
        return r;
    }

    private SldCommandResult Report(SldCommandResult r) { StatusText = r.Message + (r.Problems.Count > 0 ? " " + string.Join(" ", r.Problems.Take(3)) : ""); return r; }

    private void Afterwards(SldCommandResult r)
    {
        Report(r);
        _input = null;
        Rebuild();
    }

    /// <summary>Move the selected busbar so that its centre is at the given point of the diagram (written to the case as x and y).</summary>
    public SldCommandResult MoveSelectedTo(double x, double y)
    {
        if (SelectedBus is not int bus) return Report(SldCommandResult.Refused("Select a busbar to move."));
        if (!CanMove) return Report(SldCommandResult.Refused("The automatic layout is read-only. Adopt the layout into the case, then move busbars."));
        return Run(new SldMoveBus(bus, x / Options.LayoutScale, y / Options.LayoutScale));
    }

    /// <summary>Write the automatic layout into the case and show the diagram placed from it.</summary>
    public SldCommandResult AdoptLayout()
    {
        SldCommandResult r = Run(new SldAdoptLayout());
        if (r.Ok) { _requested = SldLayoutMode.Case; Rebuild(); OnPropertyChanged(nameof(RequestedLayout)); }
        return r;
    }

    /// <summary>Add a bus at a point (connected to the selected bus when one is selected, which a new bus needs to be valid).</summary>
    public SldCommandResult AddBusAt(double x, double y, double baseKv)
    {
        if (!CanMove) return Report(SldCommandResult.Refused("Adopt the layout into the case first: a new bus is placed by its x and y."));
        int? to = SelectedBus;
        if (to is null) return Report(SldCommandResult.Refused("Select the bus the new bus is to be connected to."));
        return Run(new SldAddBus(null, baseKv, x / Options.LayoutScale, y / Options.LayoutScale, null, to));
    }

    /// <summary>Add a bus one row below the selected bus and connect it to that bus (the same base voltage).</summary>
    public SldCommandResult AddBusBelowSelected(double drop = 160)
    {
        if (SelectedBus is not int b || Diagram?.Geometry.Symbol(SelectedId!) is not { } sym || Model is not { } m) return Report(SldCommandResult.Refused("Select the bus the new bus is to be connected to."));
        double kv = m.Nodes[m.NodeOfBus(b)].BaseKv;
        return AddBusAt(sym.Box.CentreX, sym.Box.CentreY + drop, kv);
    }

    public SldCommandResult AddGeneratorToSelected(double capacityMva, double outputMw, double? voltageSetpointPu = null)
        => SelectedBus is int b ? Run(new SldAddGenerator(b, capacityMva, outputMw, null, voltageSetpointPu)) : Report(SldCommandResult.Refused("Select a busbar for the generator."));

    public SldCommandResult AddLoadToSelected(double pMw, double qMvar = 0)
        => SelectedBus is int b ? Run(new SldAddLoad(b, pMw, qMvar)) : Report(SldCommandResult.Refused("Select a busbar for the load."));

    /// <summary>Connect the selected bus to another with a new line.</summary>
    public SldCommandResult ConnectSelectedTo(int otherBus, double rPu = 0.01, double xPu = 0.1)
        => SelectedBus is int b ? Run(new SldAddBranch(SldBranchKind.Line, b, otherBus, rPu, xPu)) : Report(SldCommandResult.Refused("Select a busbar to connect from."));

    /// <summary>Add a line or transformer between two existing buses (A5), through the same document and undo path as every other edit.</summary>
    public SldCommandResult AddBranchBetween(BranchEntry b)
    {
        ArgumentNullException.ThrowIfNull(b);
        return Run(new SldAddBranch(b.Kind, b.From, b.To, b.RPu, b.XPu, b.RatingMva, null, b.BPu, b.TapRatio));
    }

    public SldCommandResult DeleteSelected()
        => SelectedId is { } id ? Run(new SldDelete(id)) : Report(SldCommandResult.Refused("Select something to delete."));

    /// <summary>Toggle the state of the selected switch of a node-breaker diagram.</summary>
    public SldCommandResult ToggleSelectedSwitch()
    {
        if (SelectedId is not { } id || !id.StartsWith("switch/", StringComparison.Ordinal) || Model is not { } m)
            return Report(SldCommandResult.Refused("Select a switch."));
        SldElement? sw = m.Elements.FirstOrDefault(e => e.Id == id);
        if (sw is null) return Report(SldCommandResult.Refused("Select a switch."));
        // InService is "closed": toggling a closed switch opens it.
        return Run(new SldSetSwitch(id["switch/".Length..], sw.InService));
    }

    public SldCommandResult Undo()
    {
        if (_doc is null) return Report(SldCommandResult.Refused("Nothing to undo."));
        SldCommandResult r = _doc.Undo();
        Afterwards(r);
        return r;
    }

    public SldCommandResult Redo()
    {
        if (_doc is null) return Report(SldCommandResult.Refused("Nothing to redo."));
        SldCommandResult r = _doc.Redo();
        Afterwards(r);
        return r;
    }

    public SldSaveResult? Save()
    {
        if (_doc is null) { StatusText = "This diagram is read-only."; return null; }
        SldSaveResult r = _doc.Save();
        StatusText = r.Message;
        // A save changes the revision but not the content: every edit already told the workbench (DocumentChanged), so a save is not a change of the network.
        _announceChanges = false;
        try { Rebuild(); }
        finally { _announceChanges = true; }
        return r;
    }

    /// <summary>True when the document changed since the last rebuild (a client that edits behind the view model's back calls <see cref="Rebuild"/>).</summary>
    public bool IsStale => _doc is not null && _doc.Revision != _seenRevision;
}
