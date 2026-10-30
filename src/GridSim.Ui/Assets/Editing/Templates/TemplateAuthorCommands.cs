using GridSim.Ui.Commands;

namespace GridSim.Ui.Assets.Editing.Templates;

// UI programme Phase 4.4 (2026-10-21): the template commands of AUTHOR mode, declared once. Like every Author command
// they are Author mode only and desktop only (mobile has no Author mode). They are context-menu commands of the
// TEMPLATES subtree's own node kinds (no menu bar entry, no shortcut, so no gesture can clash):
//   Add template...       on the TEMPLATES class node (CommandTargets.TemplateClassNode);
//   Duplicate, Delete     on a template node (CommandTargets.Template; Delete is enabled for a local template only);
//   Bind to template...   on a template node (bind one generator, or every generator of a type by a rule);
//   Reset override        on an override node (CommandTargets.TemplateOverride).
// They are registered apart from AuthorCommands (by the Author mode controller, as the overlay command is), so the
// Phase 4.1 Author command set, and the context menu of every record kind, is unchanged.

/// <summary>What the template commands act on: the shell's Author mode controller implements it.</summary>
public interface ITemplateAuthorActions
{
    /// <summary>The Author mode's model while the tab is in AUTHOR, else null.</summary>
    AssetAuthorModel? Author { get; }

    /// <summary>Add a local inverter template (asks for its kind, name and title).</summary>
    Task AddTemplateAsync();

    /// <summary>Duplicate the selected template as a new local template.</summary>
    Task DuplicateTemplateAsync();

    /// <summary>Delete the selected local template (refused, listing them, while anything uses it).</summary>
    Task DeleteTemplateAsync();

    /// <summary>Bind a generator, or every generator of a type, to the selected template (asks which).</summary>
    Task BindToTemplateAsync();

    /// <summary>Reset the selected override to the template's own value.</summary>
    Task ResetOverrideAsync();
}

/// <summary>The template commands of AUTHOR mode (UI programme Phase 4.4).</summary>
public static class TemplateAuthorCommands
{
    /// <summary>Context menu on the TEMPLATES node: Add template...</summary>
    public const string AddTemplate = "template.add";

    /// <summary>Context menu on a template: Duplicate.</summary>
    public const string Duplicate = "template.duplicate";

    /// <summary>Context menu on a template: Delete.</summary>
    public const string Delete = "template.delete";

    /// <summary>Context menu on a template: Bind to template...</summary>
    public const string Bind = "template.bind";

    /// <summary>Context menu on an override: Reset override.</summary>
    public const string ResetOverride = "template.resetOverride";

    /// <summary>Register the template commands into <paramref name="registry"/>, each running a member of
    /// <paramref name="actions"/>, and return them in registration order. Throws (from the registry) on a clash.</summary>
    public static IReadOnlyList<AppCommand> Register(CommandRegistry registry, ITemplateAuthorActions actions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(actions);
        AppCommand Add(AppCommand c) => registry.Register(c);
        return new[]
        {
            Add(new AppCommand
            {
                Id = AddTemplate, Title = "Add template...", Modes = CommandModes.Author, Targets = CommandTargets.TemplateClassNode,
                CanExecute = () => actions.Author?.CanAddTemplate == true, Execute = actions.AddTemplateAsync,
            }),
            Add(new AppCommand
            {
                Id = Duplicate, Title = "Duplicate", Modes = CommandModes.Author, Targets = CommandTargets.Template,
                CanExecute = () => actions.Author?.HasSelectedTemplate == true, Execute = actions.DuplicateTemplateAsync,
            }),
            Add(new AppCommand
            {
                Id = Delete, Title = "Delete", Modes = CommandModes.Author, Targets = CommandTargets.Template,
                CanExecute = () => actions.Author?.HasSelectedLocalTemplate == true, Execute = actions.DeleteTemplateAsync,
            }),
            Add(new AppCommand
            {
                Id = Bind, Title = "Bind to template...", Modes = CommandModes.Author, Targets = CommandTargets.Template,
                CanExecute = () => actions.Author?.HasSelectedTemplate == true, Execute = actions.BindToTemplateAsync,
            }),
            Add(new AppCommand
            {
                Id = ResetOverride, Title = "Reset override", Modes = CommandModes.Author, Targets = CommandTargets.TemplateOverride,
                CanExecute = () => actions.Author?.HasSelectedOverride == true, Execute = actions.ResetOverrideAsync,
            }),
        };
    }
}
