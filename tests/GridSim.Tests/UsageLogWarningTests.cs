using GridSim.Web.Sessions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Known limitation "The usage log" (chapter 36, Web host): where the usage log could not be written it was switched
/// off without a message, either because its folder could not be created or because the folder existed and the file
/// in it could not be written (systemd ProtectSystem=strict, a container user), in which case every append failed in
/// silence. The log is still best-effort and never stops the app, but it is now probed at start-up and the reason it
/// is off is kept in <see cref="PresenceService.LogDisabledReason"/> (and printed on stderr). These tests drive the
/// service directly with an in-memory configuration; stderr is not captured, so they run in parallel safely.
/// </summary>
public sealed class UsageLogWarningTests
{
    private static IConfiguration Config(string logPath) => new ConfigurationBuilder()
        .AddInMemoryCollection(new[] { new KeyValuePair<string, string?>("Analytics:LogPath", logPath) })
        .Build();

    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-usage-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void A_log_folder_that_cannot_be_created_switches_the_log_off_and_says_why()
    {
        // A plain file stands where the log's folder should be, so the folder cannot be created on any system.
        string dir = NewTempDir();
        string blocker = Path.Combine(dir, "not-a-folder");
        File.WriteAllText(blocker, "a file where the log folder should be");
        string logPath = Path.Combine(blocker, "usage.jsonl");

        using var presence = new PresenceService(Config(logPath));

        Assert.Null(presence.LogPath);
        Assert.NotNull(presence.LogDisabledReason);
        Assert.Contains(logPath, presence.LogDisabledReason, StringComparison.Ordinal);
        Assert.Contains("Analytics:LogPath", presence.LogDisabledReason, StringComparison.Ordinal);
        Assert.Equal(0, presence.TotalVisits);
    }

    [Fact]
    public void A_log_file_that_cannot_be_written_in_an_existing_folder_switches_the_log_off_and_says_why()
    {
        // The folder exists (so creating it succeeds, as it did before the fix) but the log path itself is a folder,
        // so the file cannot be opened for append on any system. Before the fix the log was reported on (LogPath set)
        // and every visit's append then failed without a word.
        string dir = NewTempDir();
        string logPath = Path.Combine(dir, "usage.jsonl");
        Directory.CreateDirectory(logPath);

        using var presence = new PresenceService(Config(logPath));

        Assert.Null(presence.LogPath);
        Assert.NotNull(presence.LogDisabledReason);
        Assert.Contains(logPath, presence.LogDisabledReason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_writable_log_is_on_with_no_reason_and_is_created_empty()
    {
        string dir = NewTempDir();
        string logPath = Path.Combine(dir, "sub", "usage.jsonl");

        using var presence = new PresenceService(Config(logPath));

        Assert.Equal(logPath, presence.LogPath);
        Assert.Null(presence.LogDisabledReason);
        Assert.True(File.Exists(logPath));
        Assert.Equal(0L, new FileInfo(logPath).Length);
        Assert.Equal(0, presence.TotalVisits);
    }

    [Fact]
    public void The_start_up_probe_leaves_an_existing_log_byte_for_byte_unchanged()
    {
        string dir = NewTempDir();
        string logPath = Path.Combine(dir, "usage.jsonl");
        string before = "{\"t\":\"2026-10-01T00:00:00.0000000Z\",\"ev\":\"visit\",\"id\":\"guest-aaaa\"}\n"
                        + "{\"t\":\"2026-10-02T00:00:00.0000000Z\",\"ev\":\"visit\",\"id\":\"guest-bbbb\"}\n";
        File.WriteAllText(logPath, before);

        using var presence = new PresenceService(Config(logPath));

        Assert.Equal(logPath, presence.LogPath);
        Assert.Null(presence.LogDisabledReason);
        Assert.Equal(before, File.ReadAllText(logPath));
        Assert.Equal(2, presence.TotalVisits);
    }
}
