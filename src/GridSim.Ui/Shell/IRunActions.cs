using GridSim.Ui.Hosting;

namespace GridSim.Ui.Shell;

/// <summary>
/// What the Run menu's commands do, as seen from the platform-neutral side (UI programme Phase 1.2, the run pipeline).
/// The front end (the Avalonia window's run view model) implements it over a <see cref="SimHost"/>;
/// <see cref="RunCommands.Register"/> wires each command to one member, so the registry itself never touches a window
/// or the host. Every choice offered comes from <see cref="TransportChoices"/>, the same lists the WPF transport bar
/// shows, in the same order.
/// </summary>
public interface IRunActions
{
    /// <summary>The WPF PAUSE / PLAY / RESUME button: stop the loop making frames, or start it again.</summary>
    Task TogglePlayAsync();

    /// <summary>
    /// Advance exactly one frame. A replay moves its cursor on by one (the WPF next-frame button); a live case pauses
    /// and solves exactly one more tick.
    /// </summary>
    Task StepOnceAsync();

    /// <summary>The WPF SOLVER picker: run the live case on <paramref name="choice"/> (the case is rebuilt on it).</summary>
    Task ChooseSolverAsync(SolverChoice choice);

    /// <summary>The WPF STEP picker: simulated time each live solve advances.</summary>
    Task ChooseStepAsync(StepChoice choice);

    /// <summary>The WPF '.' key: the next finer STEP choice (stays on the finest).</summary>
    Task FinerStepAsync();

    /// <summary>The WPF ',' key: the next coarser STEP choice (stays on the coarsest).</summary>
    Task CoarserStepAsync();

    /// <summary>The WPF SPEED button's values: play at <paramref name="rate"/> times the x1 cadence.</summary>
    Task ChooseSpeedAsync(double rate);

    /// <summary>
    /// False while the SOLVER choice has nothing to act on: a replay is pre-solved, so WPF disables its picker then.
    /// </summary>
    bool CanChooseSolver { get; }

    /// <summary>False while the STEP choice has nothing to act on: WPF hides its picker for a replay.</summary>
    bool CanChooseStep { get; }

    // ---- UI programme slice 1.7: the choice in force now, so the Run menu ticks it (AppCommand.IsChecked) ----

    /// <summary>The SOLVER choice in force now (one of <see cref="TransportChoices.Solvers"/>).</summary>
    SolverChoice CurrentSolver { get; }

    /// <summary>The STEP choice in force now (one of <see cref="TransportChoices.Steps"/>).</summary>
    StepChoice CurrentStep { get; }

    /// <summary>The SPEED (playback rate) in force now (one of <see cref="TransportChoices.Playbacks"/>).</summary>
    double CurrentSpeed { get; }
}
