using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace GridSim.App.Workbench;

/// <summary>One labelled text field of an <see cref="EntryDialog"/>.</summary>
public sealed record EntryField(string Label, string Default);

/// <summary>
/// A small modal form (A5): labelled text fields, OK and Cancel, and a line that says why a value was refused. It closes with the
/// typed values only when the validator accepts them (null means accepted), so a bad entry never leaves the dialog. Shown with
/// <c>ShowDialog&lt;string[]?&gt;</c>; null when cancelled.
/// </summary>
public sealed class EntryDialog : Window
{
    private readonly List<TextBox> _boxes = [];
    private readonly Func<IReadOnlyList<string>, string?> _validate;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.OrangeRed, IsVisible = false };

    /// <summary>For the designer only.</summary>
    public EntryDialog() : this("Entry", [], _ => null) { }

    public EntryDialog(string title, IReadOnlyList<EntryField> fields, Func<IReadOnlyList<string>, string?> validate)
    {
        ArgumentNullException.ThrowIfNull(fields);
        _validate = validate ?? throw new ArgumentNullException(nameof(validate));
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        if (this.TryFindResource("Bg", ActualThemeVariant, out object? bg) && bg is IBrush brush) Background = brush;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), Margin = new Thickness(0, 0, 0, 8) };
        for (int i = 0; i < fields.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = fields[i].Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
            var box = new TextBox { Text = fields[i].Default, Margin = new Thickness(0, 3), Name = "Field" + i };
            Grid.SetRow(label, i);
            Grid.SetRow(box, i);
            Grid.SetColumn(box, 1);
            grid.Children.Add(label);
            grid.Children.Add(box);
            _boxes.Add(box);
        }
        var ok = new Button { Content = "OK", Name = "OkButton", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", Name = "CancelButton", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(12), Spacing = 6 };
        root.Children.Add(grid);
        root.Children.Add(_error);
        root.Children.Add(buttons);
        Content = root;
    }

    /// <summary>The text of each field, in order.</summary>
    public IReadOnlyList<string> Values => _boxes.Select(b => b.Text ?? "").ToList();

    /// <summary>The refusal currently shown, or null.</summary>
    public string? ErrorText => _error.IsVisible ? _error.Text : null;

    public void SetValue(int index, string text) => _boxes[index].Text = text;

    /// <summary>Validate and, when accepted, close with the values (what OK does). Returns the values, or null when refused (the reason is shown).</summary>
    public string[]? Accept()
    {
        string[] values = Values.ToArray();
        string? problem = _validate(values);
        _error.Text = problem;
        _error.IsVisible = problem is not null;
        if (problem is not null) return null;
        Close(values);
        return values;
    }
}
