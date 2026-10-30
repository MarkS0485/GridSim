using GridSim.Core.Model;
using GridSim.Core.Scheduling;
using GridSim.Core.Simulation;
using GridSim.Web.Projection;

namespace GridSim.Web.Sessions;

/// <summary>
/// The Blazor-side wrapper over the shared, host-agnostic <see cref="ResidentCycleService"/>: it forwards
/// each solved frame to the resident analysis cycle and projects the resulting <see cref="CycleResult"/> into
/// the browser's <see cref="CycleDto"/>. All the scheduling/threading lives in Core so the WPF twin shares
/// exactly the same engine (no copy-drift).
/// </summary>
public sealed class SessionCycleService : IAsyncDisposable
{
    private readonly ResidentCycleService _resident;

    /// <summary>Raised when a new cycle completes (so the circuit re-renders between frames).</summary>
    public event Action? Updated;

    public SessionCycleService(GridModel model, CycleOptions options)
    {
        _resident = new ResidentCycleService(model, options);
        _resident.Updated += OnUpdated;
    }

    /// <summary>Telemetry of the most recently completed cycle as a browser DTO (null until the first lands).</summary>
    public CycleDto? Latest =>
        _resident.Latest is { } r
            ? CycleProjection.Project(r, _resident.ShardsCovered, _resident.ShardsTotal, _resident.Workers)
            : null;

    /// <summary>Why the resident cycle stopped (a cycle threw), or null while it is healthy.</summary>
    public string? LastError => _resident.LastError;

    /// <summary>Hand the latest solved frame to the resident cycle (non-blocking; runs off the caller's thread).</summary>
    public void Offer(SimFrame frame, double baseSolveMs) => _resident.Offer(frame, baseSolveMs);

    private void OnUpdated() => Updated?.Invoke();

    public ValueTask DisposeAsync()
    {
        _resident.Updated -= OnUpdated;
        _resident.Dispose();
        return ValueTask.CompletedTask;
    }
}
