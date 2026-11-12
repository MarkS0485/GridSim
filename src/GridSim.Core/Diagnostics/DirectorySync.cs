using System.Runtime.InteropServices;
using System.Text;

namespace GridSim.Core.Diagnostics;

/// <summary>
/// GR-837 (November 2026): flush a directory's own entries to disk on Linux and macOS, so a rename into it survives a
/// power cut. A file written to a temporary name, flushed and then moved over the old one is durable only once the
/// directory entry that names it is on disk too; POSIX gives that only through fsync on the directory, which .NET has no
/// API for (it refuses to open a directory as a file), hence the two libc calls. On Windows the rename is a journalled
/// metadata change on NTFS and there is no directory handle to flush, so this is a no-op there. Fail loud: a directory
/// that cannot be opened or flushed throws <see cref="IOException"/> naming it and the error number.
/// </summary>
internal static class DirectorySync
{
    private const int ReadOnly = 0;   // O_RDONLY, the same value on Linux and macOS

    /// <summary>fsync the directory <paramref name="directory"/> (Linux and macOS); nothing on Windows.</summary>
    public static void Flush(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (OperatingSystem.IsWindows()) return;

        byte[] path = Encoding.UTF8.GetBytes(Path.GetFullPath(directory) + "\0");
        int fd = Open(path, ReadOnly);
        if (fd < 0)
            throw new IOException($"Could not open directory '{directory}' to flush it to disk (errno {Marshal.GetLastPInvokeError()}).");
        try
        {
            if (Fsync(fd) != 0)
                throw new IOException($"Could not flush directory '{directory}' to disk (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally
        {
            _ = Close(fd);   // a failed close after a successful fsync loses nothing; the status is not needed
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(byte[] path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);
}
