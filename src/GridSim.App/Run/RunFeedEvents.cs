using GridSim.Core.Analysis;
using GridSim.Core.Simulation;

namespace GridSim.App.Run;

// UI programme Phase 1.4: what the run pipeline tells the dashboard. The WPF window called its DashboardViewModel
// directly at fixed points of its load and frame code (SwitchTo, LoadReplay, RenderReplayFrame, ShowHostFrame,
// Pause_Click). The Avalonia run pipeline (RunViewModel) raises one event at each of those same points instead, so the
// dashboard (GridSim.App.Dashboard.DashboardHost) can make the same calls in the same order without the run pipeline
// knowing the dashboard exists. Every event is raised on the UI thread.

/// <summary>Which kind of frame the run pipeline is about to show.</summary>
public enum RunFrameKind
{
    /// <summary>A live solve that finished (the WPF ShowHostFrame "solved" path).</summary>
    LiveSolved,

    /// <summary>A live solve that threw (the WPF ShowHostFrame SOLVE FAULT path); <see cref="RunFrameEventArgs.Fault"/> says why.</summary>
    LiveFaulted,

    /// <summary>A frame of a pre-solved run (the WPF RenderReplayFrame path).</summary>
    Replay,

    /// <summary>
    /// UI programme slice 1.8: a frame streamed by the GDA bridge, or a bridge what-if result shown as a frame. WPF
    /// shows both through the same RenderReplayFrame path as a replay frame (the dashboard evaluates the boundary
    /// transfers itself and takes no solve time), so the dashboard treats it exactly as <see cref="Replay"/>.
    /// </summary>
    Bridge,
}

/// <summary>
/// One frame the run pipeline is about to show, with everything the WPF window handed its dashboard for it: the frame,
/// the boundary transfers the host evaluated, and the measured base-solve time (R101). A replay frame carries no
/// boundaries and no solve time: the WPF window evaluated a replay frame's boundaries in its own display code, and the
/// dashboard does the same (see DashboardHost).
/// </summary>
public sealed class RunFrameEventArgs : EventArgs
{
    /// <summary>The kind of frame.</summary>
    public required RunFrameKind Kind { get; init; }

    /// <summary>The frame itself; null only for <see cref="RunFrameKind.LiveFaulted"/>.</summary>
    public SimFrame? Frame { get; init; }

    /// <summary>The host's boundary transfers for a live frame (empty for a replay or a non-converged frame).</summary>
    public IReadOnlyList<BoundaryFlow> Boundaries { get; init; } = Array.Empty<BoundaryFlow>();

    /// <summary>The measured base-solve time of a live frame in milliseconds (0 for a replay frame).</summary>
    public double SolveMs { get; init; }

    /// <summary>The live tick number (0 for a replay frame).</summary>
    public int Tick { get; init; }

    /// <summary>Why a live solve failed; null unless <see cref="Kind"/> is <see cref="RunFrameKind.LiveFaulted"/>.</summary>
    public Exception? Fault { get; init; }

    /// <summary>
    /// UI programme slice 1.7: why a handler refused to show this frame, or null when none did. The dashboard sets it
    /// (<see cref="Reject"/>) when its own update of the frame throws: in WPF that update and the map's update sit in one
    /// guard (ShowHostFrame and RenderReplayFrame: <c>_vm.Update</c>, then <c>_renderer.SetState</c>), so a frame the
    /// dashboard throws on never reaches the map and never becomes the current frame. The run pipeline reads this after
    /// raising the event and then does the same: the frame takes the SOLVE FAULT path and goes no further.
    /// </summary>
    public Exception? Rejection { get; private set; }

    /// <summary>True when a handler refused this frame (<see cref="Rejection"/> says why).</summary>
    public bool Rejected => Rejection is not null;

    /// <summary>
    /// Refuse this frame, with the reason. The first refusal is kept (a later one is the same frame failing again, and
    /// the first is the one the dashboard reported).
    /// </summary>
    public void Reject(Exception reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Rejection ??= reason;
    }
}

/// <summary>A case or run was loaded (the WPF SwitchTo / LoadReplay success path).</summary>
public sealed class RunLoadedEventArgs : EventArgs
{
    /// <summary>True for a pre-solved run, false for a live case.</summary>
    public required bool IsReplay { get; init; }

    /// <summary>The WPF event-log line for the load ("Loaded case9  -  9 buses, ..."), the same text as <see cref="RunViewModel.LoadedText"/>.</summary>
    public required string LoadedText { get; init; }
}
