using System.IO;
using System.Text.Json;
using GridSim.Ui.Models;

namespace GridSim.Ui.Settings;

/// <summary>
/// Small persisted UI state for the control suite  -  the chosen model source and the map
/// overlay toggles  -  so the dashboard reopens the way you left it. Stored as JSON under
/// <c>%LOCALAPPDATA%\GridSim\settings.json</c>. The app must never fail to start because of a settings
/// file, but a file it could not read is never silently replaced: when Load fails, the reason is kept in
/// <see cref="LoadProblem"/> (the window writes it to the event log) and Save leaves the user's file untouched for the
/// rest of the session, so a hand edit with a typo (for example a trailing comma after <c>BridgeEndpoint</c>) is not
/// overwritten by the defaults on exit.
/// </summary>
public sealed class AppSettings
{
    public string? LastSource { get; set; }
    /// <summary>The last-used source's identity (<see cref="ModelSource.Key"/>: its full path, or the bridge URL).
    /// Preferred over <see cref="LastSource"/>, which is a label and can repeat; the label stays as a fallback for a
    /// settings file written before this key existed.</summary>
    public string? LastSourcePath { get; set; }
    public bool ColorVoltage { get; set; }
    public bool Fleet { get; set; }
    public bool Minimap { get; set; } = true;
    public bool Coast { get; set; }
    public bool Boundaries { get; set; }
    public bool GspAreas { get; set; }
    public bool DnoAreas { get; set; }
    public bool GspRegions { get; set; }
    public bool Assets { get; set; }

    /// <summary>
    /// UI programme Phase 5d (2026-10-21): the NETWORK map's "Line routes" layer (View / Line routes), the asset
    /// overlay's line routes drawn as polylines through their towers. Off by default, so the map draws exactly as before
    /// until it is turned on; a settings file written before this member existed reads it as off.
    /// </summary>
    public bool LineRoutes { get; set; }

    /// <summary>
    /// WebSocket endpoint of the GDA<->GridSim bridge (<c>bridge_server.py</c>). When non-empty the model
    /// picker lists a "Live bridge" source that streams solved state from GDA. Blank hides it.
    /// F05: blank by default, so a fresh install points at no particular server. Set it in
    /// <c>%LOCALAPPDATA%\GridSim\settings.json</c> (for example <c>"BridgeEndpoint": "wss://bridge.example.com/ws/bridge"</c>);
    /// an existing settings file keeps the value it already has, because Load reads it back.
    /// </summary>
    public string BridgeEndpoint { get; set; } = "";

    /// <summary>
    /// Allow an unencrypted <c>ws://</c> <see cref="BridgeEndpoint"/> (default false: the bridge client refuses
    /// <c>ws://</c> and asks for <c>wss://</c>). Set it true only for a bridge on loopback or a private network whose
    /// transport is protected by other means, for example <c>"BridgeEndpoint": "ws://127.0.0.1:8770/ws/bridge"</c> with
    /// <c>"BridgeAllowUnencrypted": true</c>.
    /// </summary>
    public bool BridgeAllowUnencrypted { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridSim", "settings.json");

    /// <summary>
    /// The settings file every desktop front end shares (the WPF window and the Avalonia shell read and write the same
    /// one). Internal: the shell's <see cref="GridSim.Ui.Shell.ShellSettingsStore"/> exposes it.
    /// </summary>
    internal static string DefaultFilePath => FilePath;

    /// <summary>
    /// The file this instance was loaded from, and so the file <see cref="Save"/> writes. Null for an instance that was
    /// not loaded (a fresh <c>new AppSettings()</c>), which saves to <see cref="FilePath"/> as before. Only
    /// <see cref="LoadFrom"/> sets it, so the WPF window (which calls <see cref="Load"/>) reads and writes exactly the
    /// file it always did, while a test can load from a file of its own and never touch the user's settings. A private
    /// field, so it is never serialised.
    /// </summary>
    private string? _loadedFrom;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>
    /// Why the settings file could not be read this session (null when it was read or did not exist). While this is
    /// set, <see cref="Save"/> does not write: the file on disk may hold hand-edited values (such as
    /// <see cref="BridgeEndpoint"/>) that the defaults would erase. Not persisted.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? LoadProblem { get; private set; }

    /// <summary>Why the last <see cref="Save"/> did not write, or null when it wrote. Not persisted.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? SaveProblem { get; private set; }

    public static AppSettings Load() => LoadFrom(FilePath);

    /// <summary>
    /// <see cref="Load"/> from a named file; the instance then saves back to that same file. The same rules as
    /// <see cref="Load"/>: a missing file gives the defaults, an unreadable one gives the defaults with
    /// <see cref="LoadProblem"/> set and the file protected from <see cref="Save"/>.
    /// </summary>
    internal static AppSettings LoadFrom(string p)
    {
        // A missing file is the normal first-run case: defaults, and Save may create the file.
        if (!File.Exists(p)) return new AppSettings { _loadedFrom = p };
        try
        {
            AppSettings? read = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(p));
            if (read is not null) read._loadedFrom = p;
            return read ?? new AppSettings { LoadProblem = $"{p} holds JSON null, not a settings object", _loadedFrom = p };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Defaults apply for this session, but the reason is kept (and reported) and the file is protected from Save.
            return new AppSettings { LoadProblem = $"{p} could not be read ({ex.Message})", _loadedFrom = p };
        }
    }

    public void Save()
    {
        string p = _loadedFrom ?? FilePath;
        if (LoadProblem is not null)
        {
            // Never replace a file that could not be read: the user's hand edits would be lost without a word.
            SaveProblem = $"{p} was not saved: it was not readable when the app started, so it was left as it is";
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(this, SerializerOptions));
            SaveProblem = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persisting settings must not stop the window closing; the reason is kept for the caller to report.
            SaveProblem = $"{p} was not saved ({ex.Message})";
        }
    }
}
