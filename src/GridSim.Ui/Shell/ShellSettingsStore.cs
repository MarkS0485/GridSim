using GridSim.Ui.Settings;

namespace GridSim.Ui.Shell;

/// <summary>
/// Where the shell reads and writes its <see cref="AppSettings"/> (UI programme Phase 1.1). The real app uses
/// <see cref="Default"/>: the same <c>settings.json</c> under the local application data folder that the WPF window
/// uses, in the same JSON format (it is the same class), so either app picks up where the other left off. A test uses
/// <see cref="ForFile"/> with a file of its own, so a test run never reads or rewrites the user's settings.
/// <para>
/// All of <see cref="AppSettings"/>' safety rules carry over unchanged: a file that could not be read is reported
/// through <see cref="AppSettings.LoadProblem"/> and is never overwritten by <see cref="AppSettings.Save"/>.
/// </para>
/// </summary>
public sealed class ShellSettingsStore
{
    private ShellSettingsStore(string filePath) => FilePath = filePath;

    /// <summary>The settings file the WPF window and this shell share.</summary>
    public static ShellSettingsStore Default { get; } = new(AppSettings.DefaultFilePath);

    /// <summary>A store over <paramref name="filePath"/> (tests). The path must be absolute, so it cannot depend on
    /// the working folder.</summary>
    public static ShellSettingsStore ForFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!System.IO.Path.IsPathFullyQualified(filePath))
            throw new ArgumentException($"The settings path must be absolute, not \"{filePath}\"", nameof(filePath));
        return new ShellSettingsStore(filePath);
    }

    /// <summary>The file this store reads and writes.</summary>
    public string FilePath { get; }

    /// <summary>
    /// Read the settings. Never throws for a missing or unreadable file (see <see cref="AppSettings.Load"/>); the
    /// instance saves back to this store's file.
    /// </summary>
    public AppSettings Load() => AppSettings.LoadFrom(FilePath);
}
