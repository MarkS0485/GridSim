using System.IO;

namespace GridSim.Ui.Shell;

/// <summary>
/// A text document that ships beside the app (F07: LICENSE.md and THIRD-PARTY-NOTICES.md are copied next to every
/// build and publish of the CLI, the WPF app and the Avalonia desktop head). The Help menu's viewers read them from
/// there, so what the user reads is exactly the file that shipped, not a copy compiled into the app that could drift.
/// <para>
/// A missing or unreadable file is never shown as an empty page: <see cref="Read"/> returns the reason in
/// <see cref="ShippedDocumentText.Problem"/>, and the viewer shows that instead, so a broken package is visible.
/// </para>
/// </summary>
public sealed record ShippedDocument(string Title, string FileName)
{
    /// <summary>The software licence.</summary>
    public static ShippedDocument Licence { get; } = new("Licence", "LICENSE.md");

    /// <summary>The third-party notices: every package and data source the app ships, with its licence.</summary>
    public static ShippedDocument ThirdPartyNotices { get; } = new("Third-party notices", "THIRD-PARTY-NOTICES.md");

    /// <summary>
    /// The full path of the file in <paramref name="folder"/>, by default the app's own folder
    /// (<see cref="AppContext.BaseDirectory"/>, where the build copies it).
    /// </summary>
    public string PathIn(string? folder = null) => Path.Combine(folder ?? AppContext.BaseDirectory, FileName);

    /// <summary>
    /// Read the file from <paramref name="folder"/> (default: the app's folder). Never throws for a missing or
    /// unreadable file; the result carries the text, or the reason it could not be read.
    /// </summary>
    public ShippedDocumentText Read(string? folder = null)
    {
        string path = PathIn(folder);
        if (!File.Exists(path))
            return new ShippedDocumentText(this, path, "",
                $"{FileName} is missing from {Path.GetDirectoryName(path)}. Every build ships it beside the app, so this " +
                "installation is incomplete; the file is also in the GridSim source repository.");
        try
        {
            string text = File.ReadAllText(path);
            return text.Length == 0
                ? new ShippedDocumentText(this, path, "", $"{path} is empty; the shipped file has been damaged.")
                : new ShippedDocumentText(this, path, text, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ShippedDocumentText(this, path, "", $"{path} could not be read ({ex.Message}).");
        }
    }
}

/// <summary>
/// What <see cref="ShippedDocument.Read"/> found: the text (empty when it could not be read) and, when it could not,
/// the reason in plain words.
/// </summary>
public sealed record ShippedDocumentText(ShippedDocument Document, string Path, string Text, string? Problem)
{
    /// <summary>True when the file was read and holds text.</summary>
    public bool IsReadable => Problem is null;
}
