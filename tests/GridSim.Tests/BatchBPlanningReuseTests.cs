using Xunit;

namespace GridSim.Tests;

/// <summary>Post-RC3 CLI-03: an unreadable completion record is a reported job failure, not a crash.</summary>
public sealed class BatchBPlanningReuseTests
{
    [Fact]
    public void A_locked_completion_record_is_reported_as_a_write_failure_not_an_unhandled_exception()
    {
        string root = TestTemp.Under("gridsim-batchb-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            string fixture = PlanningStudyExpansionTests.FixtureDir();
            Directory.CreateDirectory(Path.Combine(root, "master"));
            foreach (string f in Directory.GetFiles(Path.Combine(fixture, "master")))
                File.Copy(f, Path.Combine(root, "master", Path.GetFileName(f)));
            string manifest = Path.Combine(root, "seasonal.json");
            File.Copy(Path.Combine(fixture, "seasonal.json"), manifest);
            string dir = Path.Combine(root, "run");
            Assert.Equal(3, CliProcess.RunBuilt("planning-study", manifest, "--out", dir).ExitCode);

            string complete = Directory.GetFiles(Path.Combine(dir, "jobs"), "complete.json", SearchOption.AllDirectories).First();
            using (new FileStream(complete, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var rerun = CliProcess.RunBuilt("planning-study", manifest, "--out", dir);
                Assert.True(rerun.ExitCode is 1 or 3, rerun.ToString());
                Assert.DoesNotContain("AggregateException", rerun.StdErr);
                Assert.DoesNotContain("Unhandled", rerun.StdErr);
            }
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
