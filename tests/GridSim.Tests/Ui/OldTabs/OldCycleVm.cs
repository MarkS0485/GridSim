// Test oracle for UiTabsParityTests (UI programme Phase 1.6, first tab batch). NOT production code.
//
// Below is the WPF CycleVm exactly as it was before its logic moved to src/GridSim.Ui/Tabs (src/GridSim.Wpf/Views/CycleVm.cs
// at 2.0.22), copied mechanically from git. The only edits are:
//   - the line "using System.Windows.Media;" removed (the shims in OldTabsShims.cs stand in) (1x);
//   - the line "using GridSim.Wpf.Visuals;" removed (Palette and Fuels are shims) (1x);
//   - the namespace GridSim.Wpf.Views became GridSim.Tests.Ui.OldTabs (1x);
//   - "System.Windows.Application.Current" written "Application.Current" (the shim) (1x);
// Every formatting call, culture, branch and ordering is the old one, so it is the oracle for every displayed value.

using System.Collections.ObjectModel;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.Scheduling;
using GridSim.Core.Simulation;

namespace GridSim.Tests.Ui.OldTabs;

/// <summary>One job's outcome in the CYCLE tab's job table.</summary>
public sealed class CycleJobRow : Notify
{
    public string Name { get; init; } = "";
    public string Priority { get; init; } = "";
    private string _outcome = ""; public string Outcome { get => _outcome; set => Set(ref _outcome, value); }
    public Brush OutcomeBrush { get; init; } = Brushes.Gray;
    public Brush PriorityBrush { get; init; } = Brushes.Gray;
}

/// <summary>
/// The CYCLE tab: the desktop twin of the web control room's cycle telemetry. It drives the shared,
/// host-agnostic <see cref="ResidentCycleService"/> - each solved frame is handed to the resident analysis
/// cycle (single-core base solve already done; heavy downstream analyses fan out across a worker pool off the
/// UI thread) - and projects the latest <see cref="CycleResult"/> into bindable properties. Because the sim
/// ticks on a DispatcherTimer, <see cref="Update"/> runs on the UI thread and simply polls the latest cycle;
/// no cross-thread marshalling is needed.
/// </summary>
public sealed class CycleVm : Notify, IDisposable
{
    // Modest worker share so the background fan-out never starves the UI thread.
    private static readonly CycleOptions Options = new()
    {
        Workers = Math.Max(1, Math.Min(4, Environment.ProcessorCount - 1)),
    };

    private ResidentCycleService? _svc;
    // Topology fingerprint of the model the current service was built for. LiveSim mints a fresh GridModel
    // instance every tick (re-dispatch / solved-voltage warm start), so we must key the service on the
    // network SHAPE - not the per-tick instance - or it would be torn down and restarted every frame and no
    // cycle would ever finish. It rebuilds only on a real case switch (bus/branch count changes).
    private int _busCount = -1;
    private int _branchCount = -1;

    private static readonly Brush Green = Palette.Frozen(Palette.Loading(0.0));
    private static readonly Brush Amber = Palette.Frozen(Palette.Loading(0.85));
    private static readonly Brush Red = Palette.Frozen(Palette.Loading(1.15));
    private static readonly Brush Muted = Palette.Frozen(Colors.Gray);

    private bool _hasData; public bool HasData { get => _hasData; set => Set(ref _hasData, value); }

    private string _hero = "WAITING FOR FIRST CYCLE"; public string Hero { get => _hero; set => Set(ref _hero, value); }
    private Brush _heroBrush = Muted; public Brush HeroBrush { get => _heroBrush; set => Set(ref _heroBrush, value); }
    private string _subtitle = ""; public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }
    private string _util = " - "; public string Util { get => _util; set => Set(ref _util, value); }

    private string _cycleIndex = " - "; public string CycleIndex { get => _cycleIndex; set => Set(ref _cycleIndex, value); }
    private string _baseSolve = " - "; public string BaseSolve { get => _baseSolve; set => Set(ref _baseSolve, value); }
    private string _wall = " - "; public string Wall { get => _wall; set => Set(ref _wall, value); }
    private string _compute = " - "; public string Compute { get => _compute; set => Set(ref _compute, value); }
    private string _high = " - "; public string High { get => _high; set => Set(ref _high, value); }
    private Brush _highBrush = Muted; public Brush HighBrush { get => _highBrush; set => Set(ref _highBrush, value); }
    private string _low = " - "; public string Low { get => _low; set => Set(ref _low, value); }
    private string _cancelled = " - "; public string Cancelled { get => _cancelled; set => Set(ref _cancelled, value); }
    private string _backlog = " - "; public string Backlog { get => _backlog; set => Set(ref _backlog, value); }
    private string _coverage = " - "; public string Coverage { get => _coverage; set => Set(ref _coverage, value); }
    private Brush _coverageBrush = Muted; public Brush CoverageBrush { get => _coverageBrush; set => Set(ref _coverageBrush, value); }
    private string _worst = " - "; public string Worst { get => _worst; set => Set(ref _worst, value); }
    private Brush _worstBrush = Muted; public Brush WorstBrush { get => _worstBrush; set => Set(ref _worstBrush, value); }

    public ObservableCollection<CycleJobRow> Jobs { get; } = new();

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
    private void OnCycleCompleted() =>
        Application.Current?.Dispatcher.BeginInvoke((Action)ApplyLatest);

    /// <summary>Project the latest completed cycle into the bound properties (runs on the UI thread).</summary>
    private void ApplyLatest()
    {
        ResidentCycleService? svc = _svc;
        CycleResult? r = svc?.Latest;
        if (svc is null || r is null) { HasData = false; return; }
        HasData = true;

        double utilPct = r.Utilization * 100.0;
        Hero = r.HighTierMet ? "HIGH TIER MET" : "HIGH TIER MISSED";
        HeroBrush = r.HighTierMet ? Green : Red;
        Subtitle = $"single-core base solve -> parallel fan-out | budget {svc.Workers} workers x {r.BudgetTotalCoreMs / Math.Max(1, svc.Workers):F0} ms window";
        Util = $"{utilPct:F0}%";

        CycleIndex = r.CycleIndex.ToString();
        BaseSolve = r.BaseSolveMs > 0 ? $"{r.BaseSolveMs:F1} ms" : "-";
        Wall = $"{r.WallMs:F0} ms";
        Compute = $"{r.BudgetSpentCoreMs / 1000.0:F2} / {r.BudgetTotalCoreMs / 1000.0:F2} core-s  ({utilPct:F0}%)";
        High = $"{r.HighCompleted} / {r.HighTotal} complete";
        HighBrush = r.HighTierMet ? Green : Red;
        Low = $"{r.LowCompleted} / {r.LowAdmitted} / {r.LowDeferred}";
        Cancelled = r.LowCancelled.ToString();
        Backlog = r.BacklogDepth.ToString();

        if (svc.ShardsTotal > 0)
        {
            Coverage = $"{svc.ShardsCovered} / {svc.ShardsTotal} shards";
            CoverageBrush = svc.ShardsCovered >= svc.ShardsTotal ? Green : Amber;
        }
        else { Coverage = "n/a"; CoverageBrush = Muted; }

        (string worst, bool failed, int viol) = WorstContingency(r);
        Worst = worst;
        WorstBrush = failed || viol > 0 ? Red : Green;

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
            .Select(j => new CycleJobRow
            {
                Name = j.Name,
                Priority = j.Priority.ToString(),
                Outcome = OutcomeText(j),
                OutcomeBrush = OutcomeBrush(j.Outcome),
                PriorityBrush = j.Priority == JobPriority.High ? Green : Muted,
            })
            .ToList();

        // Rebuild the collection in place (small list; the tab isn't the hot path).
        Jobs.Clear();
        foreach (CycleJobRow row in rows) Jobs.Add(row);
    }

    private static string OutcomeText(JobResult j) => j.Outcome switch
    {
        JobOutcome.Completed => $"OK {j.CoreMs:F0} ms",
        JobOutcome.Cancelled => "! cancelled",
        JobOutcome.Deferred => "| deferred",
        _ => "X faulted",
    };

    private static Brush OutcomeBrush(JobOutcome o) => o switch
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
        HeroBrush = Muted;
        Worst = " - "; WorstBrush = Muted;
        Coverage = " - "; CoverageBrush = Muted;
        Jobs.Clear();
    }

    /// <summary>Tear down the resident cycle's background worker (app shutdown / model teardown).</summary>
    public void Dispose()
    {
        if (_svc is not null) { _svc.Updated -= OnCycleCompleted; _svc.Dispose(); }
        _svc = null;
        _busCount = -1;
        _branchCount = -1;
    }
}

