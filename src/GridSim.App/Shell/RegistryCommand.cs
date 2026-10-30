using System.Windows.Input;
using GridSim.Ui.Commands;

namespace GridSim.App.Shell;

/// <summary>
/// Adapts one registry <see cref="AppCommand"/> to the <see cref="ICommand"/> that Avalonia menu items and key
/// bindings call. Enabled state follows <see cref="AppCommand.IsEnabled"/>, and a failure inside the command is never
/// swallowed: it goes to <c>onFailure</c>, which the window shows in its status bar with the command's title.
/// </summary>
public sealed class RegistryCommand : ICommand
{
    private readonly Action<AppCommand, Exception> _onFailure;
    private readonly Func<bool>? _blocked;

    /// <summary>Wrap <paramref name="command"/>; <paramref name="onFailure"/> receives any exception it throws.</summary>
    public RegistryCommand(AppCommand command, Action<AppCommand, Exception> onFailure) : this(command, onFailure, null) { }

    /// <summary>
    /// Wrap <paramref name="command"/> for a key binding that must stand aside while <paramref name="blocked"/> says so
    /// (the typing guard, <see cref="TypingGuard"/>): while it returns true the binding reports itself unable to run, so
    /// the key goes on to the control that has the focus instead of firing the command.
    /// </summary>
    public RegistryCommand(AppCommand command, Action<AppCommand, Exception> onFailure, Func<bool>? blocked)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        _onFailure = onFailure ?? throw new ArgumentNullException(nameof(onFailure));
        _blocked = blocked;
        // Forward the registry command's own notification, so menus refresh when its state changes.
        Command.CanExecuteChanged += (_, e) => CanExecuteChanged?.Invoke(this, e);
    }

    /// <summary>The registry command this runs.</summary>
    public AppCommand Command { get; }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => Command.IsEnabled && !(_blocked?.Invoke() ?? false);

    /// <inheritdoc />
    /// <remarks>
    /// ICommand.Execute cannot return a task, so the command runs to completion on the UI thread's message loop;
    /// anything it throws is caught here and handed to the failure callback, so it is reported, not lost.
    /// </remarks>
    public async void Execute(object? parameter)
    {
        // A guarded key binding asks CanExecute first, but a caller that does not (or a focus change in between) must
        // still not fire the command while the guard holds: the key belongs to the focused control.
        if (_blocked?.Invoke() ?? false) return;
        try
        {
            await Command.RunAsync().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Every failure is reported to the user through _onFailure; none is swallowed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _onFailure(Command, ex);
            // A toggle that failed changed nothing it reports: let its menu items read the tick again, so a click that
            // flipped the tick on its own does not leave a false one showing (UI programme integration, 2026-10-20).
            if (Command.IsToggle) Command.RaiseCheckedChanged();
        }
    }
}
