using System.Text.Json;
using GridSim.Core.Security;

namespace GridSim.Core.Journal;

/// <summary>Where this run's journal is written, and how.</summary>
/// <param name="Path">The journal file (absolute).</param>
/// <param name="Snapshot">True to keep input files in the snapshot store.</param>
/// <param name="MaxSegmentBytes">The size at which the active file is rotated.</param>
/// <param name="Source">What switched it on: "--journal", "GRIDSIM_JOURNAL" or the config file's path.</param>
public sealed record JournalSettings(string Path, bool Snapshot, long MaxSegmentBytes, string Source);

/// <summary>
/// Resolves whether a run is journaled. The journal is off by default. First match wins: the switch
/// <c>--journal &lt;file.jsonl&gt;</c>, then <c>GRIDSIM_JOURNAL=&lt;file&gt;</c>, then the user config file
/// (<c>gridsim-journal-config/1</c>). <c>GRIDSIM_JOURNAL=off</c> always wins, the switch included.
/// </summary>
public static class JournalConfig
{
    /// <summary>The config file's schema tag.</summary>
    public const string ConfigSchema = "gridsim-journal-config/1";

    /// <summary>The per-user config file: <c>&lt;LocalApplicationData&gt;/GridSim/journal.json</c>.</summary>
    public static string DefaultConfigPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridSim", "journal.json");

    /// <summary>The journal used when the config file switches it on with no path.</summary>
    public static string DefaultJournalPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GridSim", "journal", "journal.jsonl");

    /// <summary>
    /// Resolve the settings. <paramref name="error"/> is set for a bad <c>--journal</c> value (the caller exits 2);
    /// <paramref name="warning"/> for a bad environment variable or config file (the run goes on, not journaled).
    /// Returns null when the run is not journaled.
    /// </summary>
    /// <param name="flagPath">The value of <c>--journal</c>, or null when not given.</param>
    /// <param name="flagSnapshot">True when <c>--journal-snapshot</c> was given.</param>
    /// <param name="env">The value of GRIDSIM_JOURNAL, or null.</param>
    /// <param name="configPath">The config file to read (null for none).</param>
    public static JournalSettings? Resolve(string? flagPath, bool flagSnapshot, string? env, string? configPath,
        out string? error, out string? warning)
    {
        error = null;
        warning = null;
        if (env is not null && env.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)) return null;

        if (flagPath is not null)
        {
            if (string.IsNullOrWhiteSpace(flagPath) || flagPath.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"--journal expected a journal file path but got '{flagPath}'";
                return null;
            }
            if (PathConfinement.IsNetworkOrDevice(flagPath))
            {
                error = $"--journal '{flagPath}': a network or device path is refused; the journal is a local file.";
                return null;
            }
            return new JournalSettings(System.IO.Path.GetFullPath(flagPath), flagSnapshot, JournalFile.DefaultMaxSegmentBytes, "--journal");
        }

        if (!string.IsNullOrWhiteSpace(env))
        {
            // SEC-11: trimmed first, so the network test sees the path that is used (" \\server\share\j.jsonl" passed it).
            env = env.Trim();
            if (PathConfinement.IsNetworkOrDevice(env))
            {
                warning = $"GRIDSIM_JOURNAL '{env}': a network or device path is refused; this run is not journaled.";
                return null;
            }
            return new JournalSettings(System.IO.Path.GetFullPath(env), flagSnapshot, JournalFile.DefaultMaxSegmentBytes, "GRIDSIM_JOURNAL");
        }

        if (configPath is null || !File.Exists(configPath)) return null;
        try
        {
            using JsonDocument d = JsonDocument.Parse(GridSim.Core.IO.Utf8Json.WithoutBom(File.ReadAllBytes(configPath)));
            JsonElement r = d.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("schema", out JsonElement s) || s.GetString() != ConfigSchema)
                throw new InvalidDataException($"its schema is not {ConfigSchema}");
            bool enabled = r.TryGetProperty("enabled", out JsonElement en) && en.ValueKind == JsonValueKind.True;
            if (!enabled) return null;
            string path = DefaultJournalPath();
            if (r.TryGetProperty("path", out JsonElement p) && p.ValueKind == JsonValueKind.String)
            {
                string given = p.GetString()!;
                if (PathConfinement.IsNetworkOrDevice(given)) throw new InvalidDataException("its path is a network or device path");
                path = System.IO.Path.GetFullPath(given);
            }
            else if (r.TryGetProperty("path", out JsonElement pn) && pn.ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("its path is not a string or null");
            bool snapshot = flagSnapshot || (r.TryGetProperty("snapshot", out JsonElement sn) && sn.ValueKind == JsonValueKind.True);
            long max = JournalFile.DefaultMaxSegmentBytes;
            if (r.TryGetProperty("maxSegmentBytes", out JsonElement mx))
            {
                if (!mx.TryGetInt64(out max) || max < 1) throw new InvalidDataException("its maxSegmentBytes is not a positive whole number");
            }
            return new JournalSettings(path, snapshot, max, configPath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            warning = $"journal config '{configPath}' is not usable ({ex.Message}); this run is not journaled.";
            return null;
        }
    }
}
