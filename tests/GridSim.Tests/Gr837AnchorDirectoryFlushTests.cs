using GridSim.Core.Diagnostics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-837: the audit anchor (and its signature) was written to a temporary file, flushed and moved over the old one, but
/// the folder was not flushed after the rename, so on Linux and macOS a power cut could lose the rename itself. Both
/// writers now flush the folder (DirectorySync.Flush, an fsync of the directory; a no-op on Windows). These pin that the
/// flush runs and succeeds on a real folder, fails loud on a missing one off Windows, and that both writers call it.
/// </summary>
public class Gr837AnchorDirectoryFlushTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gr837-" + Guid.NewGuid().ToString("N"));

    public Gr837AnchorDirectoryFlushTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Gr837_the_anchor_writes_and_reads_back_with_the_folder_flush_in_place()
    {
        string path = Path.Combine(_dir, "audit.anchor");
        var anchor = new AuditAnchor(0, AuditLog.Genesis);
        AuditAnchorFile.Write(path, anchor);
        AuditAnchorFile.Write(path, anchor);   // the replace path: the move over an existing anchor, then the flush
        Assert.Equal(anchor, AuditAnchorFile.Parse(File.ReadAllText(path), path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Gr837_a_real_folder_flushes_and_a_missing_one_fails_loud_off_windows()
    {
        DirectorySync.Flush(_dir);
        string missing = Path.Combine(_dir, "no-such-folder");
        if (OperatingSystem.IsWindows())
            DirectorySync.Flush(missing);   // no directory flush on Windows: nothing to fail
        else
            Assert.Contains("no-such-folder", Assert.Throws<IOException>(() => DirectorySync.Flush(missing)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr837_both_anchor_writers_flush_the_folder_after_the_move()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "GridSim.slnx not found");
        foreach (string file in new[] { "AuditAnchorFile.cs", "SignedAuditAnchor.cs" })
        {
            string src = File.ReadAllText(Path.Combine(dir!.FullName, "src", "GridSim.Core", "Diagnostics", file));
            int move = src.IndexOf("File.Move(temp, full, overwrite: true);", StringComparison.Ordinal);
            int flush = src.IndexOf("DirectorySync.Flush(Path.GetDirectoryName(full)!);", StringComparison.Ordinal);
            Assert.True(move >= 0 && flush > move, $"{file}: the folder flush must follow the move");
        }
    }
}
