namespace GridSim.Core.Diagnostics;

/// <summary>
/// SEC-09 / SEC-10: open options for the audit log, its lock, anchor, signature and forwarded copies: a new file is created
/// readable and writable by its owner only on Linux and macOS (0600), as the journal's files are; on Windows the per-user
/// ACL of the folder is inherited. A temporary sibling has a random name and is created new, so a file or symbolic link
/// planted at a fixed name is never followed.
/// </summary>
internal static class UserOnlyFile
{
    /// <summary>File-stream options with the given mode, access and sharing; 0600 when a file is created off Windows.</summary>
    public static FileStreamOptions Options(FileMode mode, FileAccess access, FileShare share)
    {
        var o = new FileStreamOptions { Mode = mode, Access = access, Share = share };
        if (!OperatingSystem.IsWindows() && mode is FileMode.Create or FileMode.CreateNew or FileMode.OpenOrCreate or FileMode.Append)
            o.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return o;
    }

    /// <summary>A temporary sibling of <paramref name="full"/> with a random part, for a write that is then moved over it.</summary>
    public static string TempSibling(string full) => full + "." + Guid.NewGuid().ToString("N")[..12] + ".tmp";
}
