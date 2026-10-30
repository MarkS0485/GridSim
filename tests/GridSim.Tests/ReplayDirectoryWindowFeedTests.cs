using System;
using System.IO;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Web.Realtime;
using Xunit;

namespace GridSim.Tests;

/// <summary>The replay-directory feed: yields the directory's measurement sets in filename order, ready for
/// the resident estimator.</summary>
public class ReplayDirectoryWindowFeedTests
{
    [Fact]
    public void Yields_measurement_windows_from_a_directory_in_order()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet template = WlsEstimatorTests.ExactFullSet(model, truth);

        string dir = Path.Combine(Path.GetTempPath(), "gridsim-feed-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            DateTime baseT = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            for (int k = 0; k < 3; k++)
                (template with { TakenUtc = baseT.AddSeconds(k) }).Save(Path.Combine(dir, $"w{k:D3}.json"));

            var feed = new ReplayDirectoryWindowFeed(model, dir);
            var windows = feed.Windows().ToList();

            Assert.Equal(3, windows.Count);
            Assert.Equal(baseT, windows[0].TakenUtc);
            Assert.Equal(baseT.AddSeconds(2), windows[2].TakenUtc);
            Assert.Same(model, feed.Model);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact] // Wave 7: one corrupt/foreign file must be skipped, not crash the hosted estimator (web host)
    public void Skips_a_corrupt_or_foreign_json_file_instead_of_throwing()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet template = WlsEstimatorTests.ExactFullSet(model, truth);

        string dir = Path.Combine(Path.GetTempPath(), "gridsim-feed-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            DateTime baseT = new(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            (template with { TakenUtc = baseT }).Save(Path.Combine(dir, "a-good.json"));
            File.WriteAllText(Path.Combine(dir, "b-corrupt.json"), "{ this is not valid json");
            File.WriteAllText(Path.Combine(dir, "c-foreign.json"), "{\"schema\":\"something-else/1\"}");
            (template with { TakenUtc = baseT.AddSeconds(1) }).Save(Path.Combine(dir, "d-good.json"));

            var feed = new ReplayDirectoryWindowFeed(model, dir);
            var windows = feed.Windows().ToList();   // must not throw

            Assert.Equal(2, windows.Count);          // the two good files
            Assert.Equal(2, feed.SkippedFiles.Count); // the corrupt + foreign files, surfaced not swallowed
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
