using GridSim.Core.PowerFlow;

namespace GridSim.Ui.Hosting;

/// <summary>One entry of the SOLVER picker: the label shown and the power-flow engine it selects.</summary>
/// <param name="Label">The text the picker shows.</param>
/// <param name="Kind">The engine (Auto picks per network).</param>
public sealed record SolverChoice(string Label, SolverKind Kind);

/// <summary>One entry of the STEP picker: the label shown and the simulated minutes each solve advances.</summary>
/// <param name="Label">The text the picker shows.</param>
/// <param name="Minutes">Simulated time per step, in minutes.</param>
public sealed record StepChoice(string Label, double Minutes);

/// <summary>
/// The transport bar's fixed choices, shared by every front end (UI programme Phase 0.2: moved out of the WPF window
/// unchanged, values and order exactly as before).
/// </summary>
public static class TransportChoices
{
    /// <summary>Auto (the default, first) and the three explicit engines.</summary>
    public static IReadOnlyList<SolverChoice> Solvers { get; } = new SolverChoice[]
    {
        new("Auto", SolverKind.Auto),
        new("Newton (dense)", SolverKind.DenseNewton),
        new("Newton (sparse)", SolverKind.SparseNewton),
        new("Sweep (radial)", SolverKind.DistributionSweep),
    };

    /// <summary>
    /// Simulation time-step (dt) = how much sim-time each solve advances. Fine dt shows live frequency dynamics (the
    /// swing equation is integrated at this dt); coarse dt sweeps the day/scenario. The last (20 min) is the default,
    /// to preserve the day-sweep behaviour.
    /// </summary>
    public static IReadOnlyList<StepChoice> Steps { get; } = new StepChoice[]
    {
        new("1 ms", 0.001 / 60), new("10 ms", 0.01 / 60), new("100 ms", 0.1 / 60),
        new("1 s", 1.0 / 60), new("10 s", 10.0 / 60), new("1 min", 1), new("5 min", 5), new("20 min", 20),
    };

    /// <summary>Playback rates (real-time cadence multipliers) the SPEED button cycles through, first is the default.</summary>
    public static IReadOnlyList<double> Playbacks { get; } = new double[] { 1, 5, 15, 60 };
}
