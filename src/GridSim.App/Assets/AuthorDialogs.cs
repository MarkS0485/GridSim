using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GridSim.Ui.Assets.Editing;

namespace GridSim.App.Assets;

// UI programme Phase 4.1: the window's answers to Author mode's questions (IAuthorPrompts), as small modal dialogs over
// the main window, in the control-room style. One dialog type: a message, any number of one-line text boxes, and a row
// of buttons; closing it with the window's own close button is the last button (Cancel). Kept in code (no AXAML) as it
// has no layout of its own beyond a stack.

/// <summary>The real dialogs behind <see cref="IAuthorPrompts"/>, shown modally over <see cref="Owner"/>.</summary>
public sealed class AuthorDialogs : IAuthorPrompts
{
    /// <summary>Dialogs over <paramref name="owner"/>.</summary>
    public AuthorDialogs(Window owner) => Owner = owner ?? throw new ArgumentNullException(nameof(owner));

    /// <summary>The window the dialogs are modal over.</summary>
    public Window Owner { get; }

    /// <inheritdoc />
    public async Task<DirtyChoice> AskSaveDiscardCancelAsync(string message)
    {
        (int button, _) = await AuthorPromptWindow.ShowAsync(Owner, "Unsaved changes", message, Array.Empty<(string, string)>(),
            new[] { "Save", "Discard", "Cancel" }).ConfigureAwait(true);
        return button switch { 0 => DirtyChoice.Save, 1 => DirtyChoice.Discard, _ => DirtyChoice.Cancel };
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message)
    {
        (int button, _) = await AuthorPromptWindow.ShowAsync(Owner, title, message, Array.Empty<(string, string)>(),
            new[] { "Yes", "No" }).ConfigureAwait(true);
        return button == 0;
    }

    /// <inheritdoc />
    public async Task<string?> AskTextAsync(string title, string message, string initial)
    {
        (int button, IReadOnlyList<string> texts) = await AuthorPromptWindow.ShowAsync(Owner, title, message,
            new[] { ("", initial) }, new[] { "OK", "Cancel" }).ConfigureAwait(true);
        return button == 0 ? texts[0] : null;
    }

    /// <summary>
    /// Shell slice (v2 + pickers, 2026-10-20): Save As's folder through the OS folder picker (Avalonia's
    /// IStorageProvider), opening in the folder the suggestion sits in when that exists (the user's own case folder).
    /// The picker chooses an existing folder: make a new one in it (its New folder button) or choose an empty one; the
    /// Save As rules are checked after the pick, unchanged. A platform with no folder picker asks for the folder as one
    /// line of text, as before. A choice that is not a folder on this computer is refused with the reason (thrown, so
    /// the Author status line shows it), never treated as a cancel.
    /// </summary>
    public async Task<string?> PickSaveAsFolderAsync(string title, string message, string suggested)
    {
        if (!Owner.StorageProvider.CanPickFolder) return await AskTextAsync(title, message, suggested).ConfigureAwait(true);
        string? start = string.IsNullOrWhiteSpace(suggested) ? null : Path.GetDirectoryName(Path.GetFullPath(suggested));
        GridSim.App.Shell.PickResult pick = await new GridSim.App.Shell.StoragePickers(Owner)
            .PickFolderAsync(title + ": choose a new or empty folder for your copy (never a bundled folder)", start).ConfigureAwait(true);
        if (pick.Problem is { } problem) throw new InvalidOperationException(problem);
        return pick.Path;   // null: cancelled
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<KeyValuePair<string, string>>?> AskFieldsAsync(string title, string message, IReadOnlyList<NewRecordField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        (string, string)[] boxes = fields.Select(f => (f.Label + (f.Choices.Count > 0 ? "  (one of: " + string.Join(", ", f.Choices) + ")" : ""), "")).ToArray();
        (int button, IReadOnlyList<string> texts) = await AuthorPromptWindow.ShowAsync(Owner, title, message, boxes,
            new[] { "Add", "Cancel" }).ConfigureAwait(true);
        if (button != 0) return null;
        return fields.Select((f, i) => new KeyValuePair<string, string>(f.Field, texts[i])).ToList();
    }
}

/// <summary>A small modal dialog: a message, labelled one-line text boxes, and buttons.</summary>
internal sealed class AuthorPromptWindow : Window
{
    private readonly List<TextBox> _boxes = new();
    private int _button;

    private AuthorPromptWindow(string title, string message, IReadOnlyList<(string Label, string Initial)> boxes, IReadOnlyList<string> buttons)
    {
        Title = title;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _button = buttons.Count - 1;   // closing the window is the last button (Cancel / No)

        var stack = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        foreach ((string label, string initial) in boxes)
        {
            if (label.Length > 0) stack.Children.Add(new TextBlock { Text = label, FontSize = 11, Opacity = 0.8 });
            var box = new TextBox { Text = initial };
            _boxes.Add(box);
            stack.Children.Add(box);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        for (int i = 0; i < buttons.Count; i++)
        {
            int at = i;
            var b = new Button { Content = buttons[i], MinWidth = 80 };
            b.Click += (_, _) =>
            {
                _button = at;
                Close();
            };
            row.Children.Add(b);
        }
        stack.Children.Add(row);
        Content = stack;
    }

    /// <summary>Show the dialog modally over <paramref name="owner"/>; returns the button pressed (the last when closed)
    /// and the texts of the boxes.</summary>
    public static async Task<(int Button, IReadOnlyList<string> Texts)> ShowAsync(Window owner, string title, string message,
        IReadOnlyList<(string Label, string Initial)> boxes, IReadOnlyList<string> buttons)
    {
        var w = new AuthorPromptWindow(title, message, boxes, buttons);
        await w.ShowDialog(owner).ConfigureAwait(true);
        return (w._button, w._boxes.Select(b => b.Text ?? "").ToList());
    }
}
