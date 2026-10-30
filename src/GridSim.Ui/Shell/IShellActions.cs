namespace GridSim.Ui.Shell;

/// <summary>
/// What the shell's commands do, as seen from the platform-neutral side (UI programme Phase 1.1). The front end (the
/// Avalonia window) implements it with real dialogs; <see cref="ShellCommands.Register"/> wires each command to one
/// member, so the registry itself never touches a window. A test can pass a recording implementation and check that a
/// menu item or a shortcut reached the right action.
/// </summary>
public interface IShellActions
{
    /// <summary>Show the model picker (the cases <see cref="Models.ModelCatalog"/> finds) and record the choice.</summary>
    Task OpenCaseAsync();

    /// <summary>Ask for a pre-solved run folder (the WPF "LOAD RUN..." button) and record it as the chosen source.</summary>
    Task OpenRunFolderAsync();

    /// <summary>Close the app, saving the settings on the way out as the WPF window does.</summary>
    Task ExitAsync();

    /// <summary>Show the About box: the build line, build, runtime and licence.</summary>
    Task ShowAboutAsync();

    /// <summary>Show THIRD-PARTY-NOTICES.md as shipped beside the app.</summary>
    Task ShowThirdPartyNoticesAsync();

    /// <summary>Show LICENSE.md as shipped beside the app.</summary>
    Task ShowLicenceAsync();
}
