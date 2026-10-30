using GridSim.Core.PowerFlow;
using GridSim.Ui.Commands;
using GridSim.Ui.Hosting;

namespace GridSim.Ui.Shell;

/// <summary>
/// The Run menu (UI programme Phase 1.2, the run pipeline): the WPF transport bar as registry commands. Play or pause,
/// step once, the SOLVER, STEP and SPEED choices (every value from <see cref="TransportChoices"/>, in its order), and the
/// two step-size keys the WPF window already has ('.' finer, ',' coarser).
/// <para>
/// The front end registers these only once a case has been loaded, because before that there is nothing for them to
/// act on and the registry shows no command without its page (the shell's rule). They are Run mode commands on the
/// desktop; the mobile heads (Phase 3) register their own Run-only set.
/// </para>
/// <para>
/// Shortcuts. The WPF window has none for play, pause or step (only buttons), so two free function keys are used: F5
/// plays or pauses (the usual "run" key) and F6 steps once. F10 was the other candidate but is the Windows menu-bar
/// key, so it is left alone. The period and comma keys keep the meaning they have in WPF (finer and coarser STEP).
/// None of these clashes with the WPF map keys still to be ported (F, M, C, B, G, D, R, A, T and Escape), and the
/// registry refuses any clash with the shell's own shortcuts when these are registered.
/// </para>
/// </summary>
public static class RunCommands
{
    /// <summary>Run / Play or pause (F5).</summary>
    public const string PlayPause = "run.playPause";

    /// <summary>Run / Step once (F6).</summary>
    public const string StepOnce = "run.stepOnce";

    /// <summary>Run / Finer step ('.', as in WPF).</summary>
    public const string FinerStep = "run.finerStep";

    /// <summary>Run / Coarser step (',', as in WPF).</summary>
    public const string CoarserStep = "run.coarserStep";

    /// <summary>The id of the Run / Solver item for <paramref name="kind"/>, for example <c>run.solver.denseNewton</c>.</summary>
    public static string SolverId(SolverKind kind) => "run.solver." + LowerFirst(kind.ToString());

    /// <summary>
    /// The id of the Run / Step size item for the STEP choice at <paramref name="index"/> in
    /// <see cref="TransportChoices.Steps"/> (0-based), for example <c>run.stepSize.choice1</c> for the first. The labels
    /// ("1 ms", "20 min") start with a digit, which an id cannot, so the id names the position instead.
    /// </summary>
    public static string StepId(int index) => "run.stepSize.choice" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The text the SPEED button shows for <paramref name="rate"/>, as WPF writes it: <c>x1</c>, <c>x5</c> ...</summary>
    public static string SpeedLabel(double rate) => "x" + rate.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The id of the Run / Speed item for <paramref name="rate"/>, for example <c>run.speed.x15</c>.</summary>
    public static string SpeedId(double rate) => "run.speed." + SpeedLabel(rate);

    /// <summary>
    /// Register the Run commands into <paramref name="registry"/>, each running one member of
    /// <paramref name="actions"/>, and return them in registration order (the front end asks them to refresh their
    /// enabled state when the feed changes between a live case and a replay). Throws (from
    /// <see cref="CommandRegistry.Register"/>) if any clashes with a command already there.
    /// </summary>
    public static IReadOnlyList<AppCommand> Register(CommandRegistry registry, IRunActions actions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actions);
        var added = new List<AppCommand>();
        AppCommand Add(AppCommand c) { added.Add(registry.Register(c)); return c; }

        // UI programme slice 1.7: every SOLVER, STEP and SPEED item carries a tick (AppCommand.IsChecked) on the choice
        // in force now, read from the actions themselves, so the tick can never disagree with what the run uses. A choice
        // made from the menu (or from the '.' and ',' keys) then asks every choice item to read its tick again, as the
        // map's toggles do: choosing one unticks the others. A choice made elsewhere (a picker, the SPEED button) is the
        // front end's to announce (it calls RaiseCheckedChanged on these when its own choice changes).
        var choices = new List<AppCommand>();
        Func<Task> ThenRefreshTicks(Func<Task> action) => async () =>
        {
            await action().ConfigureAwait(true);   // stay on the calling (UI) thread: the menu items are refreshed next
            foreach (AppCommand t in choices) t.RaiseCheckedChanged();
        };
        void AddChoice(AppCommand c)
        {
            Add(c);
            choices.Add(c);
        }

        Add(new AppCommand
        {
            Id = PlayPause, Title = "Play or pause", MenuPath = "Run/Play or pause", Gesture = "F5",
            Modes = CommandModes.Run, Execute = actions.TogglePlayAsync,
        });
        Add(new AppCommand
        {
            Id = StepOnce, Title = "Step once", MenuPath = "Run/Step once", Gesture = "F6",
            Modes = CommandModes.Run, Execute = actions.StepOnceAsync,
        });

        // SOLVER: every engine of the WPF picker, in its order. Disabled for a replay, as the WPF picker is.
        foreach (SolverChoice choice in TransportChoices.Solvers)
        {
            SolverChoice c = choice;   // each item runs its own choice
            AddChoice(new AppCommand
            {
                Id = SolverId(c.Kind), Title = c.Label, MenuPath = "Run/Solver/" + c.Label,
                Modes = CommandModes.Run, CanExecute = () => actions.CanChooseSolver,
                IsChecked = () => actions.CurrentSolver == c,
                Execute = ThenRefreshTicks(() => actions.ChooseSolverAsync(c)),
            });
        }

        // STEP: every simulated time step of the WPF picker, in its order, then the two WPF keys that move along it.
        for (int i = 0; i < TransportChoices.Steps.Count; i++)
        {
            StepChoice c = TransportChoices.Steps[i];
            AddChoice(new AppCommand
            {
                Id = StepId(i), Title = c.Label, MenuPath = "Run/Step size/" + c.Label,
                Modes = CommandModes.Run, CanExecute = () => actions.CanChooseStep,
                IsChecked = () => actions.CurrentStep == c,
                Execute = ThenRefreshTicks(() => actions.ChooseStepAsync(c)),
            });
        }
        // The two keys move the STEP choice too, so they refresh the ticks; they are actions, not choices (no tick).
        Add(new AppCommand
        {
            Id = FinerStep, Title = "Finer step", MenuPath = "Run/Finer step", Gesture = "Period",
            Modes = CommandModes.Run, CanExecute = () => actions.CanChooseStep, Execute = ThenRefreshTicks(actions.FinerStepAsync),
        });
        Add(new AppCommand
        {
            Id = CoarserStep, Title = "Coarser step", MenuPath = "Run/Coarser step", Gesture = "Comma",
            Modes = CommandModes.Run, CanExecute = () => actions.CanChooseStep, Execute = ThenRefreshTicks(actions.CoarserStepAsync),
        });

        // SPEED: the playback rates the WPF button cycles through, in its order (live and replay alike).
        foreach (double rate in TransportChoices.Playbacks)
        {
            double r = rate;
            AddChoice(new AppCommand
            {
                Id = SpeedId(r), Title = SpeedLabel(r), MenuPath = "Run/Speed/" + SpeedLabel(r),
                Modes = CommandModes.Run,
                // Exact equality is right here: the rate in force is always one of these same list values.
                IsChecked = () => actions.CurrentSpeed.Equals(r),
                Execute = ThenRefreshTicks(() => actions.ChooseSpeedAsync(r)),
            });
        }
        return added;
    }

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
}
