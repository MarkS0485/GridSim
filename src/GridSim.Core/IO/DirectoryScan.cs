namespace GridSim.Core.IO;

/// <summary>
/// Guarded directory listing for the model catalogues (WPF <c>ModelCatalog</c>, web <c>CaseCatalog</c>), which
/// walk <c>gda-out/</c> and <c>results/</c> folders found up to eight levels above the app. One unreadable folder
/// (access denied, a network share that drops, a folder deleted mid-scan) must not abort the whole scan, and must
/// not vanish silently either: every folder that could not be read is recorded with the reason, for the caller to
/// show (R307).
/// </summary>
public static class DirectoryScan
{
    /// <summary>
    /// The immediate subdirectories of <paramref name="root"/>, sorted case-insensitively (ordinal; GR-644: names that
    /// differ only in case, which a case-sensitive file system allows, then by exact ordinal, so the order is total), fully
    /// enumerated INSIDE the guard so an error part-way through is caught too. On an IO or permission error the
    /// result is empty and one line "root: reason" is added to <paramref name="skipped"/>.
    /// </summary>
    public static IReadOnlyList<string> Subdirectories(string root, ICollection<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(skipped);
        try
        {
            return Directory.EnumerateDirectories(root)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ThenBy(d => d, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (IsScanError(ex))
        {
            skipped.Add($"{root}: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Run a yes/no probe on <paramref name="path"/> (for example "is this a run directory?"), treating an IO or
    /// permission error as "no" and recording "path: reason" in <paramref name="skipped"/>.
    /// </summary>
    public static bool Probe(string path, Func<string, bool> test, ICollection<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(skipped);
        try
        {
            return test(path);
        }
        catch (Exception ex) when (IsScanError(ex))
        {
            skipped.Add($"{path}: {ex.Message}");
            return false;
        }
    }

    // Only the failures a filesystem scan can legitimately hit are absorbed (and recorded); anything else is a
    // bug and propagates.
    private static bool IsScanError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}
