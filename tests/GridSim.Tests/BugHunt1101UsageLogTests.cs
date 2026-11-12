using GridSim.Web.Sessions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace GridSim.Tests;

/// <summary>Static bug hunt of 2026-11-01: #188, the web usage log is rotated and start-up no longer reads it all.</summary>
public sealed partial class BugHunt1101WebTests
{
    private static IConfiguration UsageConfig(string logPath, string? maxBytes) => new ConfigurationBuilder()
        .AddInMemoryCollection(new[]
        {
            new KeyValuePair<string, string?>("Analytics:LogPath", logPath),
            new KeyValuePair<string, string?>(PresenceService.MaxLogBytesKey, maxBytes),
        })
        .Build();

    [Fact]
    public void B188_an_oversized_log_is_rotated_and_its_visits_are_kept_as_one_number()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-bh1101-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string log = Path.Combine(dir, "usage.jsonl");
            // 300 visits written by an install from before rotation: about 30 KB, above a 4096-byte rotation size.
            File.WriteAllLines(log, Enumerable.Range(0, 300).Select(i =>
                $"{{\"t\":\"2026-10-01T00:00:00Z\",\"ev\":\"visit\",\"id\":\"guest-{i:x4}\",\"case\":\"gb-full\",\"raw\":true,\"dur_s\":30}}"));

            using (var presence = new PresenceService(UsageConfig(log, "4096")))
            {
                Assert.Equal(300, presence.TotalVisits);
                Assert.Equal(4096, presence.MaxLogBytes);
            }
            // The live file starts afresh; the old lines are in one rotated file; the sidecar holds their count.
            Assert.Equal(0, new FileInfo(log).Length);
            string[] rotated = Directory.GetFiles(dir, "usage-*.jsonl");
            Assert.Single(rotated);
            Assert.Equal(300, File.ReadLines(rotated[0]).Count());
            Assert.Equal("300", File.ReadAllText(log + ".visits"));

            // The next start-up reads the number and the (small) live file only, and still counts every visit.
            using (var again = new PresenceService(UsageConfig(log, "4096")))
                Assert.Equal(300, again.TotalVisits);
            Assert.Single(Directory.GetFiles(dir, "usage-*.jsonl"));   // nothing rotated twice
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData("100")]
    [InlineData("-5")]
    [InlineData("lots")]
    public void B188_a_malformed_rotation_size_stops_start_up_naming_the_setting(string value)
    {
        string log = Path.Combine(Path.GetTempPath(), "gridsim-bh1101-usage-" + Guid.NewGuid().ToString("N"), "usage.jsonl");
        var ex = Assert.Throws<InvalidOperationException>(() => new PresenceService(UsageConfig(log, value)));
        Assert.Contains(PresenceService.MaxLogBytesKey, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void B188_the_default_rotation_size_is_16_mib()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-bh1101-usage-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var presence = new PresenceService(UsageConfig(Path.Combine(dir, "usage.jsonl"), null));
            Assert.Equal(16L * 1024 * 1024, presence.MaxLogBytes);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }
}
