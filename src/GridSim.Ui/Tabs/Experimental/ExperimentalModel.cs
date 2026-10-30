using System.Collections.ObjectModel;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.Physics.Experimental;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Tabs.Experimental;

/// <summary>
/// One physical-law row on the EXPERIMENTAL tab's conformance table.
/// <para>UI programme Phase 1.6: the WPF <c>LawRow</c>, with <c>StatusBrush</c> now the colour token
/// <see cref="StatusColour"/> (default <c>Brushes.Gray</c>). Every value is fixed when the row is made (the tab rebuilds
/// its rows on each frame), so a front end's own row type copies them through
/// <see cref="LawRowModel(LawRowModel)"/>.</para>
/// </summary>
public class LawRowModel : ObservableModel
{
    /// <summary>An empty row, for an object initialiser.</summary>
    public LawRowModel() { }

    /// <summary>A row with every value of <paramref name="source"/> (a front end's own row type is made this way).</summary>
    protected LawRowModel(LawRowModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Number = source.Number;
        Name = source.Name;
        StatusText = source.StatusText;
        Residual = source.Residual;
        Unit = source.Unit;
        Gated = source.Gated;
        StatusColour = source.StatusColour;
    }

    public string Number { get; init; } = "";
    public string Name { get; init; } = "";
    public string StatusText { get; init; } = "";
    public string Residual { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Gated { get; init; } = "";
    public UiColour StatusColour { get; init; } = DashboardColours.Gray;
}

/// <summary>One generator -> load allocation from the power-flow trace (the WPF <c>TraceRow</c>, unchanged: it had no
/// brush).</summary>
public class TraceRowModel : ObservableModel
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Mw { get; init; } = "";
}

/// <summary>
/// EXPERIMENTAL tab - the desktop mirror of the Web experimental panel. Two things over the current solved
/// frame: (1) the physical-law CONFORMANCE audit (the same independent per-branch recompute the CLI's
/// <c>physics-check</c> verb runs, with the hard-gate verdict), and (2) the opt-in experimental models
/// behind the <c>experimental</c> verb - passive-network reciprocity, the continuum electromechanical
/// wave (dominant inter-area mode + its area split), Bialek power-flow tracing (top generator -> load
/// allocations), and flow-based carbon intensity. Heavy traces are skipped on very large cases and the
/// wave degrades gracefully when a case carries no per-machine inertia. Reads any solved
/// <see cref="SimFrame"/>; every model here is provenance-tagged and off the validated solver path.
///
/// <para>UI programme Phase 1.6 (third tab batch): this is the logic of the WPF <c>Views/ExperimentalVm.cs</c>, moved
/// here line for line so the Avalonia app shows the same tab from the same code. Every displayed string and number is
/// produced by the same formatting call, in the same culture, in the same order, and the models still run OFF the UI
/// thread (R020) with one computation at a time and only the newest parked frame computed next. The changes are at the
/// framework boundary only: each WPF <c>Brush</c> became a neutral colour token (<see cref="UiColour"/>, the same four
/// bytes; <c>GateBrush</c> is now <see cref="GateColour"/>, a law row's <c>StatusBrush</c> its <c>StatusColour</c>),
/// and the way back to the UI thread (the WPF code read <c>SynchronizationContext.Current</c> and continued on
/// <c>TaskScheduler.FromCurrentSynchronizationContext()</c>) is the poster each front end passes to the constructor,
/// as the CYCLE port does. The WPF <c>ExperimentalVm</c> derives from this class with a poster over that same
/// synchronisation-context scheduler, so its threading and its XAML are unchanged.</para>
/// </summary>
public class ExperimentalModel : ObservableModel
{
    // The tab's fixed colours: the WPF (unfrozen) brushes of the same bytes.
    private static readonly UiColour Holds = new(0xFF, 0x2F, 0xD2, 0x7F);
    private static readonly UiColour Breaks = new(0xFF, 0xE0, 0x5A, 0x5A);
    private static readonly UiColour Muted = new(0xFF, 0x8A, 0x97, 0xA6);

    private readonly Func<Action<Action>?> _uiPoster;

    /// <summary>
    /// An EXPERIMENTAL tab whose models run on a thread-pool worker and whose results are shown on the UI thread.
    /// </summary>
    /// <param name="uiPoster">Called on the UI thread each time a frame is to be computed. It returns the function that
    /// runs an action on that UI thread later (the worker hands its "show this result" step to it), or null when the
    /// calling thread has no UI loop to return to (a test host, a console caller): the frame is then computed and shown
    /// inline, as the WPF code did when there was no synchronisation context. The WPF tab passes a poster over
    /// <c>TaskScheduler.FromCurrentSynchronizationContext()</c>; the Avalonia tab its UI-thread Post.</param>
    public ExperimentalModel(Func<Action<Action>?> uiPoster)
    {
        _uiPoster = uiPoster ?? throw new ArgumentNullException(nameof(uiPoster));
    }

    public ObservableCollection<LawRowModel> Laws { get; } = new();
    public ObservableCollection<TraceRowModel> TopFlows { get; } = new();

    private string _gate = " - "; public string Gate { get => _gate; set => Set(ref _gate, value); }
    /// <summary>The gate verdict's colour (was the WPF <c>GateBrush</c>, default <c>Brushes.Gray</c>).</summary>
    private UiColour _gateColour = DashboardColours.Gray; public UiColour GateColour { get => _gateColour; set => Set(ref _gateColour, value); }

    private string _reciprocity = " - "; public string Reciprocity { get => _reciprocity; set => Set(ref _reciprocity, value); }
    private string _wave = " - "; public string Wave { get => _wave; set => Set(ref _wave, value); }
    private string _waveSplit = " - "; public string WaveSplit { get => _waveSplit; set => Set(ref _waveSplit, value); }
    private string _carbon = " - "; public string Carbon { get => _carbon; set => Set(ref _carbon, value); }
    private string _traceStatus = ""; public string TraceStatus { get => _traceStatus; set => Set(ref _traceStatus, value); }

    private string _status = "Physical-law conformance and experimental models over the current frame.";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _provenance =
        "Experimental models are opt-in, provenance-tagged and OFF the validated solver path. Conformance is an independent per-branch recompute.";
    public string Provenance { get => _provenance; set => Set(ref _provenance, value); }

    // ---- named thresholds (R204/R213: were literals; the same values and names as the web
    // PresentationLimits, pinned equal by PresentationConstantsAntiDriftTests) ----

    /// <summary>Above this many buses the Bialek tracing and carbon tracing are skipped (the web gate is the same).</summary>
    public const int HeavyModelBusLimit = GridSim.Core.Presentation.PresentationConstants.HeavyModelBusLimit;   // N023: the one shared copy (was the literal 1500)

    /// <summary>Signed mode-shape amplitude a machine must exceed (either sign) to be listed on one side of the
    /// dominant inter-area mode's split.</summary>
    public const double ModeShapeSplitAmplitude = GridSim.Core.Presentation.PresentationConstants.ModeShapeSplitAmplitude;   // N023: the one shared copy (was the literal 0.05)

    private SimFrame? _pending;
    private bool _active;

    /// <summary>
    /// Set by the shell while the EXPERIMENTAL tab is the selected tab (R020, October 2026). The tab's work - the
    /// full physical-law audit, reciprocity, the continuum-wave model and (on small cases) the tracing - used to run
    /// on the UI thread for EVERY frame whatever tab was showing. While false, <see cref="Update"/> only remembers
    /// the latest frame; turning it on catches up on that frame at once, so the tab never shows a stale state.
    /// Defaults to false because the EXPERIMENTAL tab is not the start-up tab.
    /// </summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (_active && _pending is not null) Refresh(_pending);   // catch up on show
        }
    }

    /// <summary>Accept a new solved frame: computed now when the tab is showing, otherwise kept for when it is.</summary>
    public void Update(SimFrame f)
    {
        if (f?.Model is null || f.Result is null) return;
        _pending = f;
        if (_active) Refresh(f);
    }

    /// <summary>
    /// Whether <see cref="ContinuumWaveModel.FromSolvedCase"/> can possibly succeed for this frame, checked before
    /// calling it (R020). That call throws when the solve did not converge, when there are no generators, or when any
    /// generator has H not &gt; 0 - but the H check only runs after it has built a dense admittance and the dense
    /// (n + g)^2 augmented admittance (about 1.45 GB on gb-full, on the UI thread). This reads the same three
    /// conditions directly, so a frame bound to throw skips the call and shows exactly what the catch showed. It is
    /// the twin of the web <c>FrameProjector.WaveModelCanForm</c>, which carries the unit tests.
    /// </summary>
    public static bool WaveModelCanForm(GridModel model, PowerFlowResult r)
    {
        if (!r.Converged) return false;
        if (model.Generators.Count == 0) return false;
        foreach (Generator g in model.Generators)
            if (!(g.H > 0.0)) return false;
        return true;
    }

    // ---- R020 remainder (October 2026): the tab's models run OFF the UI thread ----
    // The physical-law audit, reciprocity, the wave model and (on small cases) the tracing used to run on the
    // dispatcher thread inside Refresh. They now run on a thread-pool worker; only the formatting and the
    // collection updates (bound objects with UI-thread affinity, and the culture-dependent number formats exactly as
    // before) run back on the UI thread. One computation at a time: a frame that arrives while one is running is
    // parked, and only the NEWEST parked frame is computed next, so a slow model never builds a backlog.

    /// <summary>What the worker computed for one frame: the raw model outputs, no bound objects, no formatted text.</summary>
    private sealed record Computed(
        PhysicsLawReport Report,
        LawResult Reciprocity,
        bool WaveAttempted,
        ContinuumWaveModel.WaveModes? Wave,
        bool TraceSkipped,
        PowerFlowTracing.TraceResult? Trace,
        double CarbonGramsPerKwh,
        bool TraceNotConverged = false,
        bool WaveSkipped = false);

    private SimFrame? _parked;   // newest frame waiting for the worker (touched on the UI thread only)
    private bool _computing;     // a worker computation is running (UI thread only)
    private Action<Action>? _post;   // the UI poster of the running chain (UI thread only)

    private void Refresh(SimFrame f)
    {
        // The models, exactly as they ran on the UI thread before (same calls, same order, same guards). Pure reads of
        // the frame: the same calls already run concurrently on thread-pool threads in the web projector.
        static Computed Compute(SimFrame frame)
        {
            GridModel model = frame.Model;
            PowerFlowResult r = frame.Result;

            // (1) Physical-law conformance - the same audit as physics-check.
            PhysicsLawReport report = PhysicsLaws.Audit(model, r);

            // (2) Experimental models. Reciprocity is cheap; the wave degrades without inertia.
            LawResult recip = ReciprocityCheck.Evaluate(model);

            ContinuumWaveModel.WaveModes? wave = null;
            bool attempted = false;
            // The wave model builds a dense (n + g)^2 augmented admittance (about 1.45 GB on gb-full) for every frame
            // while the tab shows, so it takes the same large-case guard as the tracing below; such a frame says it
            // was skipped. It used to be attempted on any case whose generators all carried H.
            bool waveSkipped = model.BusCount > HeavyModelBusLimit;
            if (!waveSkipped && WaveModelCanForm(model, r))
            {
                attempted = true;
                try { wave = ContinuumWaveModel.FromSolvedCase(model, r); }
                catch (InvalidOperationException) { wave = null; }   // shown as "needs per-machine inertia", as before
            }

            // Bialek tracing + carbon are skipped on very large cases (same 1,500-bus guard as the Web tab).
            bool skipped = model.BusCount > HeavyModelBusLimit;
            PowerFlowTracing.TraceResult? tr = null;
            double carbon = 0.0;
            // D-DOC-087: tracing refuses a non-converged state (the convergence contract); such a frame says so.
            bool notConverged = !skipped && !r.Converged;
            if (!skipped && !notConverged)
            {
                tr = PowerFlowTracing.Trace(model, r);
                carbon = CarbonTracing.Trace(model, r).SystemGramsPerKwh;
            }
            return new Computed(report, recip, attempted, wave, skipped, tr, carbon, notConverged, waveSkipped);
        }

        // No UI loop to return to (a test host, a console caller): compute and apply inline, as before R020.
        Action<Action>? ui = _uiPoster();
        if (ui is null) { Apply(Compute(f)); return; }

        _parked = f;
        if (!_computing) { _post = ui; StartNext(); }

        // Take the newest parked frame to a worker; apply its result back on the UI thread (through the poster), then
        // continue with whatever arrived meanwhile. Always called on the UI thread.
        void StartNext()
        {
            SimFrame next = _parked!;
            _parked = null;
            _computing = true;
            Action<Action> post = _post!;
            Task.Run(() => Compute(next)).ContinueWith(t => post(() =>
            {
                _computing = false;
                // Fail loud: a model that throws is shown on the tab as MODEL FAILED, with the previous verdict and rows
                // cleared. It used to be rethrown inside this continuation, which only faulted the continuation task
                // (nobody observes it, so it never reached the dispatcher): Apply was skipped and the tab kept showing
                // the last good frame's "ADMISSIBLE - all hard laws hold" while the model kept failing.
                if (t.Exception is { } ex)
                {
                    ShowModelFailure(System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).SourceException);
                    if (_parked is not null && _active) StartNext();
                    return;
                }
                Apply(t.Result);
                if (_parked is not null && _active) StartNext();
            }), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// A model threw on this frame: clear every verdict and row and say so in red, so no earlier frame's result can be
    /// read as this one's (UI thread). The next frame that computes cleanly repaints the tab as normal.
    /// </summary>
    private void ShowModelFailure(Exception ex)
    {
        Laws.Clear();
        TopFlows.Clear();
        Gate = $"MODEL FAILED: {ex.Message}";
        GateColour = Breaks;
        Reciprocity = " - ";
        Wave = " - ";
        WaveSplit = " - ";
        Carbon = " - ";
        TraceStatus = "";
        Status = $"A model failed on this frame ({ex.GetType().Name}); no conformance verdict is shown.";
    }

    /// <summary>Show a computed frame: the same text, colours and rows the UI-thread Refresh produced (UI thread).</summary>
    private void Apply(Computed c)
    {
        PhysicsLawReport report = c.Report;
        var hard = new HashSet<int>(PhysicsCheckRegistry.HardGateLaws);
        Laws.Clear();
        foreach (LawResult l in report.Laws)
            Laws.Add(CreateLawRow(new LawRowModel
            {
                Number = l.Number.ToString(),
                Name = l.Name,
                StatusText = l.Status.ToString(),
                Residual = double.IsNaN(l.Residual) ? " - " : $"{l.Residual:0.###E+0}",
                Unit = l.Unit,
                Gated = hard.Contains(l.Number) ? "HARD" : "",
                StatusColour = l.Status == LawStatus.Holds ? Holds : l.Status == LawStatus.Violated ? Breaks : Muted,
            }));

        bool admissible = report.Gate(PhysicsCheckRegistry.HardGateLaws);
        Gate = admissible ? "ADMISSIBLE - all hard laws hold" : "REJECTED - a hard law is violated";
        GateColour = admissible ? Holds : Breaks;

        LawResult recip = c.Reciprocity;
        Reciprocity = $"{recip.Status} (asymmetry {recip.Residual:0.###E+0})";

        if (c.WaveSkipped) { Wave = $"skipped (>{HeavyModelBusLimit} buses)"; WaveSplit = " - "; }
        else if (!c.WaveAttempted || c.Wave is null) { Wave = "needs per-machine inertia"; WaveSplit = " - "; }
        else
        {
            ContinuumWaveModel.WaveModes w = c.Wave;
            Wave = $"{w.DominantInterAreaHz:0.###} Hz dominant inter-area mode";
            if (w.DominantModeShape.Count > 0)
            {
                var plus = w.DominantModeShape.Where(s => s.Amplitude > ModeShapeSplitAmplitude).Select(s => $"bus {s.Machine}");
                var minus = w.DominantModeShape.Where(s => s.Amplitude < -ModeShapeSplitAmplitude).Select(s => $"bus {s.Machine}");
                WaveSplit = $"({string.Join(", ", plus)}) vs ({string.Join(", ", minus)})";
            }
            else WaveSplit = " - ";
        }

        TopFlows.Clear();
        if (c.TraceSkipped)
        {
            TraceStatus = $"trace skipped (>{HeavyModelBusLimit} buses)";
            Carbon = " - ";
        }
        else if (c.TraceNotConverged)
        {
            TraceStatus = "power flow did not converge on this frame - nothing to trace";
            Carbon = " - ";
        }
        else
        {
            PowerFlowTracing.TraceResult tr = c.Trace!;
            if (tr.FlowGraphAcyclic)
            {
                foreach (var x in tr.Allocations.OrderByDescending(a => a.Mw).Take(6))
                    TopFlows.Add(new TraceRowModel { From = $"gen bus {x.GenBus}", To = $"load bus {x.LoadBus}", Mw = $"{x.Mw:N0} MW" });
                TraceStatus = "";
            }
            else TraceStatus = "flow graph has a cycle - proportional trace not defined";

            Carbon = $"{c.CarbonGramsPerKwh:N0} gCO2/kWh";
        }

        Status = admissible
            ? "State is physically admissible; experimental attributions below."
            : "State breaches a hard physical law - see the flagged rows.";
    }

    /// <summary>The law row a front end shows for the neutral <paramref name="row"/> (a front end overrides this to
    /// make its own row type; the neutral row by default).</summary>
    protected virtual LawRowModel CreateLawRow(LawRowModel row) => row;
}
