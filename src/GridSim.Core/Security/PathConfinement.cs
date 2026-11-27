namespace GridSim.Core.Security;

/// <summary>
/// The path rule of a confined folder (the scripting session's <c>--confine</c> base, and the root of a model plug-in
/// configuration): a path must resolve inside the base folder, must not be a network (UNC) or device path, and must not
/// pass through a symbolic link or junction on the way. <c>GridSim.Scripting.PathConfinement</c> forwards here.
/// </summary>
public static class PathConfinement
{
    /// <summary>Null when <paramref name="full"/> (the resolved form of <paramref name="written"/>) lies inside
    /// <paramref name="baseDirectory"/>, else the reason it does not, in a few words.</summary>
    public static string? Refusal(string written, string full, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(full);
        ArgumentNullException.ThrowIfNull(baseDirectory);
        if (IsNetworkOrDevice(written) || IsNetworkOrDevice(full)) return "a network or device path";
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        string rel = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || rel.StartsWith("../", StringComparison.Ordinal))
            return "it is outside the base folder";
        // Every component from the base folder down must be a plain file or folder, not a link to somewhere else.
        string at = root;
        foreach (string part in rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            at = Path.Combine(at, part);
            FileSystemInfo info = Directory.Exists(at) ? new DirectoryInfo(at) : new FileInfo(at);
            if (!info.Exists) break;
            if (info.LinkTarget is not null) return "it passes through a symbolic link or junction";
        }
        return null;
    }

    /// <summary>True for <c>\\server\share</c>, <c>//server/share</c>, <c>\\?\</c> and <c>\\.\</c> forms, and (R1-01) the
    /// NT object-manager prefix <c>\??\</c> (either slash), which Windows passes straight to the object manager
    /// (<c>\??\UNC\host\share</c>, <c>\??\GLOBALROOT\...</c>).</summary>
    public static bool IsNetworkOrDevice(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length >= 2 && (path[0] is '\\' or '/') && (path[1] is '\\' or '/')) return true;
        return path.Length >= 4 && (path[0] is '\\' or '/') && path[1] == '?' && path[2] == '?' && (path[3] is '\\' or '/');
    }
}
