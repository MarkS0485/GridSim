using System.Collections.ObjectModel;
using GridSim.Core.Analysis;
using GridSim.Core.Scheduling;
using GridSim.Core.Simulation;
using GridSim.Ui.Dashboard;
using GridSim.Ui.Presentation;

namespace GridSim.Ui.Tabs.Cycle;

/// <summary>
/// One job's outcome in the CYCLE tab's job table.
/// <para>UI programme Phase 1.6: the WPF <c>CycleJobRow</c>, with <c>OutcomeBrush</c> and <c>PriorityBrush</c> now the
/// colour tokens <see cref="OutcomeColour"/> and <see cref="PriorityColour"/>. A front end's own row type copies a row
/// through <see cref="CycleJobRowModel(CycleJobRowModel)"/>.</para>
/// </summary>
public class CycleJobRowModel : ObservableModel
{
    /// <summary>An empty row, for an object initialiser.</summary>
    public CycleJobRowModel() { }

    /// <summary>A row with every value of <paramref name="source"/> (a front end's own row type is made this way).</summary>
    protected CycleJobRowModel(CycleJobRowModel source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Name = source.Name;
        Priority = source.Priority;
        _outcome = source.Outcome;
        OutcomeColour = source.OutcomeColour;
        PriorityColour = source.PriorityColour;
    }

    public string Name { get; init; } = "";
    public string Priority { get; init; } = "";
    private string _outcome = ""; public string Outcome { get => _outcome; set => Set(ref _outcome, value); }
    public UiColour OutcomeColour { get; init; } = DashboardColours.Gray;
    public UiColour PriorityColour { get; init; } = DashboardColours.Gray;
}

/// <summary>
/// The CYCLE tab: the desktop twin of the web control room's cycle telemetry. It drives the shared,
/// host-agnostic <see cref="ResidentCycleService"/> - each solved frame is handed to the resident analysis
/// cycle (single-core base solve already done; heavy downstream analyses fan out across a worker pool off the
/// UI thread) - and projects the latest <see cref="CycleResult"/> into bindable properties. The front end calls
/// <see cref="Update"/> on its UI thread with each frame; a completed cycle is projected back on the UI thread
/// through the poster the front end supplies.
///
/// <para>UI programme Phase 1.6: this is the logic of the WPF <c>Views/CycleVm.cs</c>, moved here line for line so the
/// Avalonia app shows the same tab from the same code. Every displayed string and number is produced by the same
/// formatting call, in the same culture, in the same order. The changes are at the framework boundary only: each WPF
/// <c>Brush</c> became a neutral colour token (<see cref="UiColour"/>, the same four bytes; <c>HeroBrush</c> is now
/// <see cref="HeroColour"/>, and so on), and the WPF call that marshalled a completed cycle onto the UI thread
/// (<c>Application.Current?.Dispatcher.BeginInvoke</c>) became the poster passed to the constructor, so each front end
/// supplies its own dispatcher. The WPF <c>CycleVm</c> derives from this class with the WPF dispatcher and maps the
/// tokens back to brushes under the old names, so its XAML and its threading are unchanged.</para>
/// </summary>
public class CycleModel : ObservableModel, IDisposable
{
    // Modest worker share so the background fan-out never starves the UI thread.
    private static readonly CycleOptions Options = new()
    {
        Workers = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1)),
    };

    private readonly Action<Action> _postToUi;

    /// <summary>
    /// A CYCLE tab whose completed cycles are projected through <paramref name="postToUi"/>: it is called on a worker
    /// thread with the projection, and must run it on the UI thread (the WPF tab passes the WPF dispatcher's
    /// BeginInvoke; the Avalonia tab its UI-thread Post).
    /// </summary>
    public CycleModel(Action<Action> postToUi)
    {
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
    }

    private ResidentCycleService? _svc;
    // Topology fingerprint of the model the current service was built for. LiveSim mints a fresh GridModel
    // instance every tick (re-dispatch / solved-voltage warm start), so we must key the service on the
    // network SHAPE - not the per-tick instance - or it would be torn down and restarted every frame and no
    // cycle would ever finish. It rebuilds only on a real case switch (bus/branch count changes).
    private int _busCount = -1;
    private int _branchCount = -1;

    // The tab's fixed colours: the WPF frozen brushes of Palette.Loading(0.0 / 0.85 / 1.15) and Colors.Gray.
    private static readonly UiColour Green = ColourRamps.Loading(0.0);
    private static readonly UiColour Amber = ColourRamps.Loading(0.85);
    private static readonly UiColour Red = ColourRamps.Loading(1.15);
    private static readonly UiColour Muted = DashboardColours.Gray;

    private bool _hasData; public bool HasData { get => _hasData; set => Set(ref _hasData, value); }

    private string _hero = "WAITING FOR FIRST CYCLE"; public string Hero { get => _hero; set => Set(ref _hero, value); }
    private UiColour _heroColour = Muted; public UiColour HeroColour { get => _heroColour; set => Set(ref _heroColour, value); }
    private string _subtitle = ""; public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }
    private string _util = " - "; public string Util { get => _util; set => Set(ref _util, value); }

    private string _cycleIndex = " - "; public string CycleIndex { get => _cycleIndex; set => Set(ref _cycleIndex, value); }
    private string _baseSolve = " - "; public string BaseSolve { get => _baseSolve; set => Set(ref _baseSolve, value); }
    private string _wall = " - "; public string Wall { get => _wall; set => Set(ref _wall, value); }
    private string _compute = " - "; public string Compute { get => _compute; set => Set(ref _compute, value); }
    private string _high = " - "; public string High { get => _high; set => Set(ref _high, value); }
    private UiColour _highColour = Muted; public UiColour HighColour { get => _highColour; set => Set(ref _highColour, value); }
    private string _low = " - "; public string Low { get => _low; set => Set(ref _low, value); }
    private string _cancelled = " - "; public string Cancelled { get => _cancelled; set => Set(ref _cancelled, value); }
    private string _backlog = " - "; public string Backlog { get => _backlog; set => Set(ref _backlog, value); }
    private string _coverage = " - "; public string Coverage { get => _coverage; set => Set(ref _coverage, value); }
    private UiColour _coverageColour = Muted; public UiColour CoverageColour { get => _coverageColour; set => Set(ref _coverageColour, value); }
    private string _worst = " - "; public string Worst { get => _worst; set => Set(ref _worst, value); }
    private UiColour _worstColour = Muted; public UiColour WorstColour { get => _worstColour; set => Set(ref _worstColour, value); }

    public ObservableCollection<CycleJobRowModel> Jobs { get; } = new();

    /// <param name="f">The solved frame to hand to the resident cycle.</param>
    /// <param name="baseSolveMs">The wall time of the base solve that produced <paramref name="f"/> (0 when the frame
    /// came from replay or the bridge, which did not solve it here). R101: WPF always passed 0, so the CYCLE tab's
    /// base-solve figure always read " - ".</param>
    public void Update(SimFrame f, double baseSolveMs = 0)
    {
        // (Re)build the resident cycle only on a real case switch (topology shape change), NOT every tick -
        // LiveSim mints a fresh GridModel instance per frame, so keying on the instance would tear down and
        // restart (cancel) the cycle every frame and no cycle would ever finish.
        if (_svc is null || f.Model.BusCount != _busCount || f.Model.Branches.Count != _branchCount)
        {
            if (_svc is not null) { _svc.Updated -= OnCycleCompleted; _svc.Dispose(); }
            _svc = new ResidentCycleService(f.Model, Options);
            _svc.Updated += OnCycleCompleted;
            _busCount = f.Model.BusCount;
            _branchCount = f.Model.Branches.Count;
        }

        // Hand off this solved frame (non-blocking; the fan-out runs off the UI thread) with its measured base-solve
        // time. The telemetry refreshes when a cycle COMPLETES (OnCycleCompleted), not per frame - so it updates
        // live the instant each cycle lands, independent of the frame cadence.
        _svc.Offer(f, baseSolveMs);
    }

    // Raised on a worker thread when a cycle completes; marshal the refresh onto the UI thread.
    private void OnCycleCompleted() => _postToUi(ApplyLatest);

    /// <summary>Project the latest completed cycle into the bound properties (runs on the UI thread).</summary>
    private void ApplyLatest() => Project(_svc);

    /// <summary>
    /// Project the latest completed cycle of <paramref name="svc"/> into the bound properties: the body of the WPF
    /// <c>ApplyLatest</c>, which read the tab's own service. Split out so the parity tests can project one finished
    /// cycle into this model and into the old WPF code alike (its timings differ from run to run).
    /// </summary>
    internal void Project(ResidentCycleService? svc)
    {
        CycleResult? r = svc?.Latest;
        if (svc is null || r is null) { HasData = false; return; }
        HasData = true;

        double utilPct = r.Utilization * 100.0;
        Hero = r.HighTierMet ? "HIGH TIER MET" : "HIGH TIER MISSED";
        HeroColour = r.HighTierMet ? Green : Red;
        Subtitle = $"single-core base solve -> parallel fan-out | budget {svc.Workers} workers x {r.BudgetTotalCoreMs / Math.Max(1, svc.Workers):F0} ms window";
        Util = $"{utilPct:F0}%";

        CycleIndex = r.CycleIndex.ToString();
        BaseSolve = r.BaseSolveMs > 0 ? $"{r.BaseSolveMs:F1} ms" : "-";
        Wall = $"{r.WallMs:F0} ms";
        Compute = $"{r.BudgetSpentCoreMs / 1000.0:F2} / {r.BudgetTotalCoreMs / 1000.0:F2} core-s  ({utilPct:F0}%)";
        High = $"{r.HighCompleted} / {r.HighTotal} complete";
        HighColour = r.HighTierMet ? Green : Red;
        Low = $"{r.LowCompleted} / {r.LowAdmitted} / {r.LowDeferred}";
        Cancelled = r.LowCancelled.ToString();
        Backlog = r.BacklogDepth.ToString();

        if (svc.ShardsTotal > 0)
        {
            Coverage = $"{svc.ShardsCovered} / {svc.ShardsTotal} shards";
            CoverageColour = svc.ShardsCovered >= svc.ShardsTotal ? Green : Amber;
        }
        else { Coverage = "n/a"; CoverageColour = Muted; }

        (string worst, bool failed, int viol) = WorstContingency(r);
        Worst = worst;
        WorstColour = failed || viol > 0 ? Red : Green;

        ProjectJobs(r);
    }

    /// <summary>
    /// The worst contingency of the cycle: the first outage whose re-solve did not converge (a failed state outranks
    /// any violation count), otherwise the one with the most violations. A non-converged outage is labelled by its
    /// actual status: "islands" only when the analysis found the outage splits the network
    /// (<see cref="ContingencyResult.Islanded"/>), otherwise "did not converge" (voltage collapse or an iteration cap
    /// on a connected network). Every non-converged outage used to read "islands".
    /// </summary>
    internal static (string text, bool failed, int violations) WorstContingency(CycleResult r)
    {
        string? worst = null;
        int worstViol = 0;
        bool failed = false, worstIslanded = false;
        foreach (JobResult jr in r.Results)
            if (jr.Payload is IReadOnlyList<ContingencyResult> list)
                foreach (ContingencyResult c in list)
                {
                    if (!c.Converged)
                    {
                        if (!failed) { worst = c.Outage; failed = true; worstIslanded = c.Islanded; worstViol = c.Violations.Count; }
                    }
                    else if (!failed && c.Violations.Count > worstViol) { worst = c.Outage; worstViol = c.Violations.Count; }
                }

        if (worst is null) return ("none - all secure", false, 0);
        return failed
            ? ($"{worst} - {(worstIslanded ? "islands" : "did not converge")}", true, worstViol)
            : ($"{worst} - {worstViol} violation{(worstViol == 1 ? "" : "s")}", false, worstViol);
    }

    private void ProjectJobs(CycleResult r)
    {
        var rows = r.Results
            .OrderBy(j => j.Priority)                    // High (0) before Low (1)
            .ThenByDescending(j => j.CoreMs)             // heaviest first within a tier
            .Select(j => CreateJobRow(new CycleJobRowModel
            {
                Name = j.Name,
                Priority = j.Priority.ToString(),
                Outcome = OutcomeText(j),
                OutcomeColour = OutcomeColourOf(j.Outcome),
                PriorityColour = j.Priority == JobPriority.High ? Green : Muted,
            }))
            .ToList();

        // Rebuild the collection in place (small list; the tab isn't the hot path).
        Jobs.Clear();
        foreach (CycleJobRowModel row in rows) Jobs.Add(row);
    }

    private static string OutcomeText(JobResult j) => j.Outcome switch
    {
        JobOutcome.Completed => $"OK {j.CoreMs:F0} ms",
        JobOutcome.Cancelled => "! cancelled",
        JobOutcome.Deferred => "| deferred",
        _ => "X faulted",
    };

    /// <summary>The outcome's colour (the WPF <c>OutcomeBrush</c> helper, same mapping).</summary>
    private static UiColour OutcomeColourOf(JobOutcome o) => o switch
    {
        JobOutcome.Completed => Green,
        JobOutcome.Cancelled => Amber,
        JobOutcome.Deferred => Muted,
        _ => Red,
    };

    /// <summary>
    /// Drop the resident cycle on a model switch, so the next frame builds a fresh service on the NEW model and the
    /// previous model's last results leave the screen. The shape test in <see cref="Update"/> (bus and branch counts)
    /// only spares a rebuild between frames of one model: two different networks with equal counts (MATPOWER case30
    /// and pglib_opf_case30_as are both 30 buses / 41 branches) used to share the first one's service, contingency list
    /// and shard coverage. Called by the dashboard on every model switch, replay load and bridge (re)start.
    /// </summary>
    public void ForgetModel()
    {
        Dispose();
        HasData = false;
        Hero = "WAITING FOR FIRST CYCLE";
        HeroColour = Muted;
        Worst = " - "; WorstColour = Muted;
        Coverage = " - "; CoverageColour = Muted;
        Jobs.Clear();
    }

    /// <summary>Tear down the resident cycle's background worker (app shutdown / model teardown).</summary>
    public void Dispose()
    {
        if (_svc is not null) { _svc.Updated -= OnCycleCompleted; _svc.Dispose(); }
        _svc = null;
        _busCount = -1;
        _branchCount = -1;
        GC.SuppressFinalize(this);
    }

    /// <summary>The row to show for <paramref name="row"/> (the WPF tab makes its <c>CycleJobRow</c> from it).</summary>
    protected virtual CycleJobRowModel CreateJobRow(CycleJobRowModel row) => row;
}
