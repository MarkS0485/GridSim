using GridSim.Ui.Models;
using GridSim.Ui.Settings;

namespace GridSim.Ui.Shell;

/// <summary>
/// The shell's case-choosing rules (UI programme Phase 1.1), the same ones the WPF window applies to its NETWORK
/// picker, kept here so every front end chooses the same way:
/// <list type="bullet">
/// <item><description>the picker opens on the last-used source, matched by its saved path first (labels can repeat)
/// and by its saved label only for a settings file written before the path was saved;</description></item>
/// <item><description>a run folder already in the list (same folder by full path) is selected, not added twice; a new
/// one whose name repeats another source's label gets its parent folder in the label;</description></item>
/// <item><description>the choice is remembered in the settings under both its label and its path.</description></item>
/// </list>
/// Running the chosen case is the run pipeline's job (Phase 1.2); the shell only records the choice.
/// </summary>
public static class CaseChoice
{
    /// <summary>The index of the last-used source in <paramref name="sources"/>, or -1 when none matches.</summary>
    public static int LastUsedIndex(IReadOnlyList<ModelSource> sources, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.LastSourcePath is { Length: > 0 } key)
            for (int i = 0; i < sources.Count; i++)
                if (string.Equals(sources[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
        if (settings.LastSource is null) return -1;
        for (int i = 0; i < sources.Count; i++)
            if (string.Equals(sources[i].Label, settings.LastSource, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>
    /// The source the window opens on at start-up when no model is named on the command line (2026-10-24): the
    /// last-used source (<see cref="LastUsedIndex"/>), unless it is a pre-solved run. A run opens paused on its first
    /// frame and solves nothing, so starting on one left the app idle until a case was picked; a case is meant to just
    /// load and solve in continuous operation, so then the first source that solves live (a local case, not a run and
    /// not the bridge) is opened instead; the run is still in the picker, one choice away. A last-used local case or
    /// live bridge is opened as before. With no last-used match, or no live case to fall back on, the result is
    /// <see cref="LastUsedIndex"/>'s (the caller opens the first entry for -1).
    /// </summary>
    public static int StartupIndex(IReadOnlyList<ModelSource> sources, AppSettings settings)
    {
        int last = LastUsedIndex(sources, settings);
        if (last < 0 || sources[last].ReplayDir is null) return last;
        for (int i = 0; i < sources.Count; i++)
            if (sources[i].ReplayDir is null && sources[i].Bridge is null) return i;
        return last;
    }

    /// <summary>
    /// Add the run folder <paramref name="runDir"/> to <paramref name="sources"/> (unless the same folder is already
    /// there) and return the new list with the source to select. The input list is not changed.
    /// </summary>
    public static (List<ModelSource> Sources, ModelSource Selected) WithRunFolder(IReadOnlyList<ModelSource> sources, string runDir)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(runDir);
        ModelSource run = ModelCatalog.ReplaySource(runDir);
        ModelSource? existing = sources.FirstOrDefault(s => s.SameSourceAs(run));
        if (existing is not null) return (sources.ToList(), existing);
        List<ModelSource> list = ModelCatalog.Disambiguate(new List<ModelSource>(sources) { run });
        return (list, list[^1]);
    }

    /// <summary>Record <paramref name="chosen"/> as the last-used source, by label and by path, as WPF does.</summary>
    public static void Remember(AppSettings settings, ModelSource chosen)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(chosen);
        settings.LastSource = chosen.Label;
        settings.LastSourcePath = chosen.Key;
    }
}
