using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.Simulation;

namespace GridSim.Ui.Hosting;

/// <summary>What a <see cref="HostFrame"/> carries.</summary>
public enum HostFrameKind
{
    /// <summary>A live tick that solved: <see cref="HostFrame.Frame"/>, its boundary transfers and the solve time.</summary>
    LiveSolved,

    /// <summary>A live tick whose solve threw: <see cref="HostFrame.Fault"/> says why and <see cref="HostFrame.Tick"/> which.</summary>
    LiveFaulted,

    /// <summary>A pre-solved replay frame (the replay cursor advanced by one, or stayed on the last frame).</summary>
    Replay,
}

/// <summary>
/// One frame the <see cref="SimHost"/> loop produced, handed to the front end to show. The front end compares
/// <see cref="Epoch"/> with <see cref="SimHost.Epoch"/> when the frame arrives on its own thread and drops a frame whose
/// epoch has moved on (a different model was loaded, or the replay cursor was moved by hand, after the frame was made),
/// exactly as the WPF window used to drop a solve that finished after the model was switched.
/// </summary>
public sealed record HostFrame
{
    /// <summary>The host's <see cref="SimHost.Epoch"/> when the frame was made.</summary>
    public required int Epoch { get; init; }

    /// <summary>What the frame carries.</summary>
    public required HostFrameKind Kind { get; init; }

    /// <summary>The solved or replayed instant; null only for <see cref="HostFrameKind.LiveFaulted"/>.</summary>
    public SimFrame? Frame { get; init; }

    /// <summary>
    /// Live only: the boundary transfers of <see cref="Frame"/>, evaluated beside the solve (D-DOC-087: empty when the
    /// solve did not converge, because the analysis refuses a non-converged state). Empty for a replay frame: the
    /// front end evaluates a replay frame's boundaries itself, on the same path as a bridge frame.
    /// </summary>
    public IReadOnlyList<BoundaryFlow> Boundaries { get; init; } = Array.Empty<BoundaryFlow>();

    /// <summary>Live only: the wall-clock time of the base solve in milliseconds (R101: the CYCLE tab reports it).</summary>
    public double SolveMs { get; init; }

    /// <summary>Live only: the tick number the step was asked for (1 for the first step after a load).</summary>
    public int Tick { get; init; }

    /// <summary>
    /// <see cref="HostFrameKind.LiveFaulted"/> only: the exception the solve threw, reduced exactly as the WPF window
    /// reduced a faulted solve task before the move (<c>AggregateException.GetBaseException</c> of the task's
    /// exception), so the message shown is the same.
    /// </summary>
    public Exception? Fault { get; init; }

    /// <summary>Replay only: the cursor reached the last frame and the host stopped playing.</summary>
    public bool StoppedAtEnd { get; init; }
}

/// <summary>Where the replay cursor is, read under the host's lock so the four values agree.</summary>
/// <param name="Index">The 0-based cursor position.</param>
/// <param name="Count">How many frames the run holds.</param>
/// <param name="CurrentLabel">The label of the frame at the cursor (its time or slice name).</param>
/// <param name="AtEnd">True when the cursor is on the last frame.</param>
public sealed record ReplayCursor(int Index, int Count, string CurrentLabel, bool AtEnd);

/// <summary>A replay frame and the cursor after a hand-driven move (restart, step, seek).</summary>
/// <param name="Frame">The frame now at the cursor.</param>
/// <param name="Cursor">The cursor after the move.</param>
/// <param name="Before">The cursor index before the move (a seek compares the two to see whether it went backwards).</param>
public sealed record ReplayPosition(SimFrame Frame, ReplayCursor Cursor, int Before);

/// <summary>The outcome of <see cref="SimHost.LoadLive"/>: the loaded model, or the line that says why it was not loaded.</summary>
/// <param name="Model">The model the live simulation now runs (laid out for the map); null when refused.</param>
/// <param name="ActiveSolver">The power-flow engine the simulation resolved to; null when refused.</param>
/// <param name="Refusal">Why the model was not loaded (a load failure or the licence gate's message); null when loaded.</param>
public sealed record LiveLoadResult(GridModel? Model, string? ActiveSolver, string? Refusal)
{
    /// <summary>True when the model was loaded and the live simulation now runs it.</summary>
    public bool Loaded => Refusal is null;

    /// <summary>
    /// UI programme ASSETS slice 5-view: the asset overlay a JSON v2 case carried beside its model (its manifest's
    /// <c>assets</c> files, checked by the loader), or null (no overlay, a v1 case, a MATPOWER file, a refusal). Carried,
    /// not used by the solver: the live simulation was built from <see cref="Model"/> alone.
    /// </summary>
    public GridSim.Core.IO.Json.Overlay.CaseAssetOverlay? Overlay { get; init; }

    /// <summary>The notes the v2 loader made while reading the case (for example "overlay: N records carried, not used
    /// by the solver"); empty for every other source.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>The outcome of <see cref="SimHost.LoadReplay"/>: what the run holds, or the line that says why it was not loaded.</summary>
/// <param name="Title">The run's title; null when refused.</param>
/// <param name="LayoutModel">The model the map draws for the run; null when refused.</param>
/// <param name="First">The first frame (the replay opens parked on it); null when refused.</param>
/// <param name="Cursor">The cursor on the first frame; null when refused.</param>
/// <param name="LastLabel">The label of the last frame; null when refused.</param>
/// <param name="Refusal">Why the run was not loaded; null when loaded.</param>
public sealed record ReplayLoadResult(
    string? Title, GridModel? LayoutModel, SimFrame? First, ReplayCursor? Cursor, string? LastLabel, string? Refusal)
{
    /// <summary>True when the run was loaded and the host is now parked on its first frame.</summary>
    public bool Loaded => Refusal is null;
}
