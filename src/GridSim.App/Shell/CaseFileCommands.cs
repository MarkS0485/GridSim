using GridSim.Ui.Commands;

namespace GridSim.App.Shell;

// UI programme shell slice (v2 + pickers, 2026-10-20): the File menu's two new open commands, registered by the main
// window through ShellCommands.Register's front-end File commands, so they sit after Open run folder... and before the
// audit-log viewer and Exit. They are this app's own (the WPF window loads no JSON v2 case and has neither):
//     Id "file.openCaseFolder"  Title "Open case folder..."  MenuPath "File/Open case folder..."  no shortcut
//     Id "file.openV2Case"      Title "Open v2 case..."      MenuPath "File/Open v2 case..."      no shortcut
// Both are desktop commands offered in Run and Author mode, as File > Open case... is (a load while authoring asks
// Save / Discard / Cancel first, through the view model's one load path). Neither has a shortcut, so the window's key
// bindings are exactly as before.

/// <summary>The File menu's Open case folder... and Open v2 case... (see the definitions at the top of this file).</summary>
public static class CaseFileCommands
{
    /// <summary>File / Open case folder...: the OS folder picker for a JSON v1 case folder.</summary>
    public const string OpenCaseFolder = "file.openCaseFolder";

    /// <summary>File / Open v2 case...: the OS file picker for a JSON v2 case manifest.</summary>
    public const string OpenV2Case = "file.openV2Case";

    /// <summary>The two command definitions, in menu order, each running one member of <paramref name="actions"/>
    /// (the window's real <see cref="ShellActions"/>).</summary>
    public static IReadOnlyList<AppCommand> Commands(ShellActions actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        return new[]
        {
            new AppCommand
            {
                Id = OpenCaseFolder, Title = "Open case folder...", MenuPath = "File/Open case folder...",
                Modes = CommandModes.Both, Execute = actions.OpenCaseFolderAsync,
            },
            new AppCommand
            {
                Id = OpenV2Case, Title = "Open v2 case...", MenuPath = "File/Open v2 case...",
                Modes = CommandModes.Both, Execute = actions.OpenV2CaseAsync,
            },
        };
    }
}
