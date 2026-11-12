using System.Security.Cryptography;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Web.Realtime;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Static bug hunt of 2026-11-01: #175, the web realtime audit records exactly the windows that were estimated.
/// </summary>
public sealed partial class BugHunt1101WebTests
{
    private static void WriteWindow(string path, double vm)
    {
        new MeasurementSet
        {
            Case = "case9",
            TakenUtc = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Measurements = new[] { new Measurement(MeasurementKind.VmPu, vm, 0.01, Bus: 1) },
        }.Save(path);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void B175_the_feed_records_each_consumed_window_by_name_and_content_and_nothing_else()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gridsim-bh1101-windows-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "nested"));
        try
        {
            WriteWindow(Path.Combine(dir, "w1.json"), 1.01);
            WriteWindow(Path.Combine(dir, "nested", "ignored.json"), 0.5);       // not read by the feed
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "not a window");      // not read by the feed
            var feed = new ReplayDirectoryWindowFeed(IeeeCases.Case9(), dir);
            Assert.Null(feed.ConsumedInput);   // nothing consumed yet

            // A window added AFTER start-up (the old audit hashed the folder once, when the host started) is read when the
            // feed runs, and now it is recorded too.
            WriteWindow(Path.Combine(dir, "w2.json"), 1.02);
            List<MeasurementSet> read = feed.Windows().ToList();
            Assert.True(feed.SkippedFiles.Count == 0, string.Join("; ", feed.SkippedFiles));
            Assert.Equal(2, read.Count);

            Assert.Equal(new[] { ("w1.json", Sha(Path.Combine(dir, "w1.json"))), ("w2.json", Sha(Path.Combine(dir, "w2.json"))) },
                feed.Consumed.ToArray());
            GridSim.Core.Diagnostics.AuditInput input = feed.ConsumedInput!;
            Assert.Equal("windows-consumed", input.Name);
            string expected = $"w1.json\t{Sha(Path.Combine(dir, "w1.json"))}\nw2.json\t{Sha(Path.Combine(dir, "w2.json"))}\n";
            Assert.Equal(GridSim.Core.Diagnostics.AuditInput.OfText("windows-consumed", expected).Sha256Hex, input.Sha256Hex);

            // Changing a file the feed never reads changes nothing in the record.
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "changed");
            Assert.Equal(input.Sha256Hex, feed.ConsumedInput!.Sha256Hex);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    public void B175_the_start_up_folder_hash_is_gone_and_the_hosted_service_adds_the_consumed_record()
    {
        string program = ReadRepo("src/GridSim.Web/Program.cs");
        Assert.DoesNotContain("InputForArgument(\"windows-dir\"", program, StringComparison.Ordinal);
        string service = ReadRepo("src/GridSim.Web/Realtime/RealtimeEstimatorHostedService.cs");
        Assert.Contains("if (_feed.ConsumedInput is GridSim.Core.Diagnostics.AuditInput consumed) _audit.AddInput(consumed);", service, StringComparison.Ordinal);
    }
}
