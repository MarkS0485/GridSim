using System.Globalization;
using Avalonia.Data.Converters;
using GridSim.Core.Simulation;
using GridSim.Ui.Models;

namespace GridSim.App.Dialogs;

/// <summary>
/// The text File > Open case... shows for a source. A live case shows its catalogue label unchanged. A pre-solved run
/// (a folder of exported states that the app replays, not solves) also says that it is one and how many frames it
/// holds, for example "Replay  gda-replay-gbfull  (pre-solved run, 2 frames)": a replay plays its recorded frames and
/// stops at the last one, so a run of two or three frames looks like a stall unless the list says what it is.
///
/// <para>The frames are counted the way <see cref="PresolvedReplay"/> finds them: the slice-*.json and state-*.json
/// snapshots in the run's exports folder. A folder that cannot be read shows the label alone (the load itself then
/// reports why, fail loud); the count is never guessed.</para>
/// </summary>
public static class CaseListLabel
{
    /// <summary>The binding converter for the Open case list (ModelSource to text).</summary>
    public static readonly IValueConverter Converter = new FuncValueConverter<ModelSource?, string>(s => s is null ? "" : Text(s));

    /// <summary>The list text for <paramref name="source"/>.</summary>
    public static string Text(ModelSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.ReplayDir is null) return source.Label;
        int? frames = CountFrames(source.ReplayDir);
        if (frames is null) return source.Label;
        string noun = frames == 1 ? "frame" : "frames";
        return string.Create(CultureInfo.InvariantCulture, $"{source.Label}  (pre-solved run, {frames} {noun})");
    }

    /// <summary>The number of exported snapshots in <paramref name="runDir"/>, or null when the folder cannot be read.</summary>
    public static int? CountFrames(string runDir)
    {
        string exports = Path.Combine(runDir, "exports");
        try
        {
            if (!Directory.Exists(exports)) return null;
            return Directory.EnumerateFiles(exports, "*.json").Count(IsSnapshot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;   // unreadable: show the label alone; loading it reports the reason
        }
    }

    // The snapshot naming PresolvedReplay reads (its IndexOf): slice-NNN.json (transient runs) or state-NNN.json
    // (gda-replay), the part after the dash digits only, invariant culture. Anything else is not a frame.
    private static bool IsSnapshot(string path)
    {
        string n = Path.GetFileNameWithoutExtension(path);
        if (!(n.StartsWith("slice-", StringComparison.OrdinalIgnoreCase) ||
              n.StartsWith("state-", StringComparison.OrdinalIgnoreCase)))
            return false;
        int dash = n.IndexOf('-', StringComparison.Ordinal);
        return int.TryParse(n[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }
}
