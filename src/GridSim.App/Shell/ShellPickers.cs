using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace GridSim.App.Shell;

// UI programme shell slice (v2 + pickers, 2026-10-20): the OS file and folder pickers behind File > Open case folder...,
// Open v2 case..., Open run folder... and Author mode's Save As.... They are asked through IShellPickers so a headless
// test answers with a path of its own and no real dialog is ever opened; the real answers come from Avalonia's
// IStorageProvider (the platform's own dialog on Windows, macOS and Linux).

/// <summary>
/// What a picker gave back. <see cref="Path"/> is the chosen local file or folder; when it is null, either the user
/// cancelled (<see cref="Problem"/> null too, nothing to say) or the pick could not be used, and
/// <see cref="Problem"/> says why in plain words (the platform has no such picker, the choice is not on this computer).
/// </summary>
public sealed record PickResult(string? Path, string? Problem)
{
    /// <summary>The user cancelled the picker: nothing chosen, nothing wrong.</summary>
    public static PickResult Cancelled { get; } = new(null, null);

    /// <summary>The user chose <paramref name="path"/>, a local file or folder.</summary>
    public static PickResult Chosen(string path) => new(path ?? throw new ArgumentNullException(nameof(path)), null);

    /// <summary>The pick could not be used, for <paramref name="problem"/>.</summary>
    public static PickResult Refused(string problem) => new(null, problem ?? throw new ArgumentNullException(nameof(problem)));
}

/// <summary>The OS pickers the shell asks (the window's real dialogs, or a test's scripted answers).</summary>
public interface IShellPickers
{
    /// <summary>Ask for one existing folder, the dialog titled <paramref name="title"/>, opening at
    /// <paramref name="startFolder"/> when that exists (null: the platform's choice).</summary>
    Task<PickResult> PickFolderAsync(string title, string? startFolder);

    /// <summary>Ask for one existing file matching <paramref name="patterns"/> (for example <c>*.case.json</c>), shown in
    /// the dialog as <paramref name="typeName"/>, opening at <paramref name="startFolder"/> when that exists.</summary>
    Task<PickResult> PickFileAsync(string title, string? startFolder, string typeName, IReadOnlyList<string> patterns);
}

/// <summary>
/// The real pickers: the OS dialogs of <see cref="Owner"/>'s IStorageProvider. A platform without a folder (or file)
/// picker, or a choice that is not a local path (a phone's content URI, a cloud provider's virtual item), is refused
/// with the reason, never silently ignored.
/// </summary>
public sealed class StoragePickers : IShellPickers
{
    /// <summary>The pickers of <paramref name="owner"/>'s storage provider.</summary>
    public StoragePickers(TopLevel owner) => Owner = owner ?? throw new ArgumentNullException(nameof(owner));

    /// <summary>The window the dialogs belong to.</summary>
    public TopLevel Owner { get; }

    /// <summary>The refusal when the platform has no folder picker.</summary>
    public const string NoFolderPickerText = "This platform cannot show a folder picker, so a folder cannot be chosen here.";

    /// <summary>The refusal when the platform has no file picker.</summary>
    public const string NoFilePickerText = "This platform cannot show a file picker, so a file cannot be chosen here.";

    /// <inheritdoc />
    public async Task<PickResult> PickFolderAsync(string title, string? startFolder)
    {
        IStorageProvider storage = Owner.StorageProvider;
        if (!storage.CanPickFolder) return PickResult.Refused(NoFolderPickerText);
        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartAsync(storage, startFolder).ConfigureAwait(true),
        }).ConfigureAwait(true);
        if (folders.Count == 0) return PickResult.Cancelled;
        string? local = folders[0].TryGetLocalPath();
        return local is null
            ? PickResult.Refused($"{folders[0].Name} is not a folder on this computer, so it cannot be used here.")
            : PickResult.Chosen(local);
    }

    /// <inheritdoc />
    public async Task<PickResult> PickFileAsync(string title, string? startFolder, string typeName, IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        IStorageProvider storage = Owner.StorageProvider;
        if (!storage.CanOpen) return PickResult.Refused(NoFilePickerText);
        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartAsync(storage, startFolder).ConfigureAwait(true),
            FileTypeFilter = new[]
            {
                new FilePickerFileType(typeName) { Patterns = patterns.ToArray() },
                FilePickerFileTypes.All,
            },
        }).ConfigureAwait(true);
        if (files.Count == 0) return PickResult.Cancelled;
        string? local = files[0].TryGetLocalPath();
        return local is null
            ? PickResult.Refused($"{files[0].Name} is not a file on this computer, so it cannot be read here.")
            : PickResult.Chosen(local);
    }

    // The folder the dialog opens at: the given one when it exists, else none (the platform's own choice). A start
    // folder the provider cannot resolve is not an error: the dialog simply opens where the platform likes.
    private static async Task<IStorageFolder?> StartAsync(IStorageProvider storage, string? startFolder)
    {
        if (string.IsNullOrWhiteSpace(startFolder) || !Directory.Exists(startFolder)) return null;
        return await storage.TryGetFolderFromPathAsync(startFolder).ConfigureAwait(true);
    }
}
