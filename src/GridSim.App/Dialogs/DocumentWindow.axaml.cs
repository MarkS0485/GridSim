using Avalonia.Controls;
using Avalonia.Interactivity;
using GridSim.Ui.Shell;

namespace GridSim.App.Dialogs;

/// <summary>
/// Shows one document shipped beside the app (Help > Licence, Help > Third-party notices). The window title is the
/// document's title; the file's full path is shown above the text, so the user can see exactly which file it is.
/// </summary>
public sealed partial class DocumentWindow : Window
{
    /// <summary>For the AXAML loader and the designer only; the app uses the other constructor.</summary>
    public DocumentWindow() : this(ShippedDocument.Licence.Read()) { }

    /// <summary>A viewer for <paramref name="document"/> as read from disk.</summary>
    public DocumentWindow(ShippedDocumentText document)
    {
        ArgumentNullException.ThrowIfNull(document);
        InitializeComponent();
        Document = document;
        Title = document.Document.Title;
        PathText.Text = document.Path;
        DocumentText.Text = document.Text;
        if (document.Problem is { } problem)
        {
            ProblemText.Text = problem;
            ProblemText.IsVisible = true;
        }
    }

    /// <summary>What was read (for tests): the path, the text and any problem.</summary>
    public ShippedDocumentText Document { get; }

    /// <summary>The text shown (for tests).</summary>
    public string ShownText => DocumentText.Text ?? "";

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
