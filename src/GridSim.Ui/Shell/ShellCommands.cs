using GridSim.Ui.Commands;

namespace GridSim.Ui.Shell;

/// <summary>
/// The shell's commands (UI programme Phase 1.1): the first entries of the menu bar, taken from what the WPF window
/// offers today. Only commands whose page or dialog exists in the new app are registered, so the menu bar never shows
/// an item that does nothing; each later slice registers its own commands as its page is ported (the map overlay
/// toggles under View with the map, the audit-log viewer with its window, and so on).
/// <para>
/// The registration order below is the menu order: File, then Help, and the items within each in the order written.
/// UI programme integration (2026-10-20): Help carries <see cref="MenuOrders.Last"/>, so it stays the last menu on the
/// bar when the Run and View menus join with the first loaded case (the registry would otherwise put them after it,
/// in registration order). A front end's own File commands (the audit-log viewer) go in through the optional
/// <c>fileCommands</c>, which places them after Open run folder... and before Exit, as WPF has its AUDIT LOG... button
/// beside LOAD RUN....
/// There is no View menu yet: every toggle the WPF window has (the map overlays, the colouring, the fleet) acts on the
/// schematic, which arrives in Phase 1.5, and the registry shows no empty menus.
/// </para>
/// <para>
/// All shell commands are desktop commands offered in both modes. The mobile heads (Phase 3) register their own
/// Run-only set: a command offered on mobile must be Run only, which the registry enforces.
/// </para>
/// </summary>
public static class ShellCommands
{
    /// <summary>File / Open case...: pick a model from the catalogue.</summary>
    public const string OpenCase = "file.openCase";

    /// <summary>File / Open run folder...: pick a pre-solved run directory (WPF: LOAD RUN...).</summary>
    public const string OpenRunFolder = "file.openRunFolder";

    /// <summary>File / Exit.</summary>
    public const string Exit = "file.exit";

    /// <summary>Help / Licence: LICENSE.md as shipped.</summary>
    public const string Licence = "help.licence";

    /// <summary>Help / Third-party notices: THIRD-PARTY-NOTICES.md as shipped.</summary>
    public const string ThirdPartyNotices = "help.thirdPartyNotices";

    /// <summary>Help / About GridSim: the build line and the licence in use.</summary>
    public const string About = "help.about";

    /// <summary>
    /// Register the shell's commands into <paramref name="registry"/>, each running one member of
    /// <paramref name="actions"/>. Throws (from <see cref="CommandRegistry.Register"/>) if any clashes with a command
    /// already there. <paramref name="fileCommands"/> (optional) are the front end's own File menu commands, registered
    /// in the given order after Open run folder... and before Exit; each must have a File menu path (anything else is a
    /// wiring mistake and is refused, so a command cannot land on another menu by accident).
    /// </summary>
    public static void Register(CommandRegistry registry, IShellActions actions, IReadOnlyList<AppCommand>? fileCommands = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actions);

        // File. "Ctrl" is the platform command key: Control on Windows and Linux, Command on macOS.
        registry.Register(new AppCommand
        {
            Id = OpenCase, Title = "Open case...", MenuPath = "File/Open case...", Gesture = "Ctrl+O",
            Execute = actions.OpenCaseAsync,
        });
        registry.Register(new AppCommand
        {
            Id = OpenRunFolder, Title = "Open run folder...", MenuPath = "File/Open run folder...", Gesture = "Ctrl+Shift+O",
            Execute = actions.OpenRunFolderAsync,
        });
        // The front end's own File commands, in their order, between Open run folder... and Exit.
        foreach (AppCommand extra in fileCommands ?? Array.Empty<AppCommand>())
        {
            ArgumentNullException.ThrowIfNull(extra, nameof(fileCommands));
            if (extra.MenuPath is null || !extra.MenuPath.StartsWith("File/", StringComparison.Ordinal))
                throw new ArgumentException($"{extra} is passed as a File command but its menu path is \"{extra.MenuPath}\"", nameof(fileCommands));
            registry.Register(extra);
        }
        registry.Register(new AppCommand
        {
            Id = Exit, Title = "Exit", MenuPath = "File/Exit", Gesture = "Ctrl+Q",
            Execute = actions.ExitAsync,
        });

        // Help. No shortcuts: F1 is kept for the help pages when they exist.
        registry.Register(new AppCommand
        {
            Id = Licence, Title = "Licence", MenuPath = "Help/Licence", MenuOrder = MenuOrders.Last,
            Execute = actions.ShowLicenceAsync,
        });
        registry.Register(new AppCommand
        {
            Id = ThirdPartyNotices, Title = "Third-party notices", MenuPath = "Help/Third-party notices", MenuOrder = MenuOrders.Last,
            Execute = actions.ShowThirdPartyNoticesAsync,
        });
        registry.Register(new AppCommand
        {
            Id = About, Title = "About GridSim", MenuPath = "Help/About GridSim", MenuOrder = MenuOrders.Last,
            Execute = actions.ShowAboutAsync,
        });
    }
}
