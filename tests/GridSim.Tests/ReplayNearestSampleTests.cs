using GridSim.Core.Events;
using GridSim.Core.Frequency;
using GridSim.Core.Replay;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R286 (October 2026 review, S5): the replay per-sample RMSE looked up the nearest recorded sample by a linear scan of
/// the whole upsampled trace for every simulated sample. On a time-ordered trace it is now a binary search. These tests
/// prove the index is always the scan's (random ordered traces with repeated times, queries before, inside, exactly on
/// and half-way between samples, and past both ends), that an out-of-order trace keeps the scan, and that the RMSE of
/// the two bundled NESO traces is bit-for-bit the value RECORDED FROM THE LINEAR-SCAN CODE (1.95.126).
/// </summary>
public class ReplayNearestSampleTests
{
    private static readonly DateTime T0 = new(2023, 12, 22, 13, 9, 0, DateTimeKind.Utc);

    [Fact]
    public void Binary_search_returns_the_linear_scans_index_on_ordered_traces()
    {
        var rng = new Random(286);
        for (int trial = 0; trial < 300; trial++)
        {
            int n = 1 + rng.Next(60);
            var pts = new List<FrequencyPoint>(n);
            long ticks = 0;
            for (int i = 0; i < n; i++)
            {
                int step = rng.Next(5);
                ticks += step == 0 ? 0 : step == 1 ? 1 : rng.Next(1, 400_000);   // repeats, one-tick steps, 0-40 ms steps
                pts.Add(new FrequencyPoint(T0.AddTicks(ticks), 50.0 - i * 1e-3, 0.0));
            }
            Assert.True(ReplayValidator.IsNonDecreasing(pts));
            var queries = new List<DateTime> { T0.AddTicks(-1_000_000), T0.AddTicks(ticks + 1_000_000), T0, T0.AddTicks(ticks) };
            for (int i = 0; i < n; i++)
            {
                queries.Add(pts[i].Time);                                          // exactly on a sample
                if (i + 1 < n)
                {
                    long a = pts[i].Time.Ticks, b = pts[i + 1].Time.Ticks;
                    queries.Add(new DateTime((a + b) / 2, DateTimeKind.Utc));       // half-way: a tie when the gap is even
                    queries.Add(new DateTime((a + b + 1) / 2, DateTimeKind.Utc));
                }
            }
            for (int q = 0; q < 20; q++) queries.Add(T0.AddTicks(rng.NextInt64(-500_000, ticks + 500_000)));
            foreach (DateTime t in queries)
                Assert.Equal(ReplayValidator.NearestIndex(pts, t), ReplayValidator.NearestIndexSorted(pts, t));
        }
    }

    [Fact]
    public void An_out_of_order_trace_is_detected_so_the_scan_is_kept()
    {
        var pts = new List<FrequencyPoint>
        {
            new(T0, 50.0, 0.0), new(T0.AddSeconds(1), 49.9, 0.0), new(T0.AddSeconds(0.5), 49.8, 0.0), new(T0.AddSeconds(2), 49.7, 0.0),
        };
        Assert.False(ReplayValidator.IsNonDecreasing(pts));
        Assert.True(ReplayValidator.IsNonDecreasing(new List<FrequencyPoint> { new(T0, 50, 0), new(T0, 50, 0) }));
        Assert.True(ReplayValidator.IsNonDecreasing(new List<FrequencyPoint>()));
        Assert.Equal(0, ReplayValidator.NearestIndexSorted(new List<FrequencyPoint>(), T0));
    }

    [Theory]
    [InlineData("gb-2023-12-22", 0x3FD1F9DA98A934C7L, 7926)]
    [InlineData("gb-2019-08-09", 0x3FC39F50E4EFD314L, 5996)]
    public void Bundled_trace_rmse_is_bit_identical_to_the_linear_scan_code(string id, long rmseBitsBefore, int scoredBefore)
    {
        GridEvent ev = EventCatalog.Get(id);
        // Moved 2026-10 (LF6 N317, Ruling R1 (Mark delegated EE calls 2026-10-04); source ESO S192 Tables 1 and 3): the
        // 2019 event now carries the recorded response (six services, the 700 MW/Hz demand effect, the RoCoF-protection
        // trip at 1.0 s instead of 3.0 s), which moves its simulated trajectory and so its RMSE against the trace
        // (0x3FC39F50E4EFD314 = about 0.1533 Hz before; about 0.099 Hz after, by an offline replica). This test is about
        // the nearest-sample search, not the event model, so it replays the event AS IT WAS when the bits were recorded
        // (1.95.126), and the pin stays exactly as recorded. The new RMSE is pinned in Lf6CalibrationBoundsTests.N317.
        if (id == "gb-2019-08-09") ev = Lf6CalibrationBoundsTests.Pre2019N317(ev);
        string csv = Path.Combine(AppContext.BaseDirectory, "data", "neso", $"{id}-frequency.csv");
        Assert.True(File.Exists(csv), csv);
        ev = ev with { Outcome = ev.Outcome with { NesoTraceCsvPath = csv } };
        ReplayResult r = ReplayValidator.Validate(ev);
        Assert.NotNull(r.PerSampleRmseHz);
        Assert.Equal(rmseBitsBefore, BitConverter.DoubleToInt64Bits(r.PerSampleRmseHz!.Value));
        Assert.Equal(scoredBefore, r.TraceSamplesScored);
        Assert.Equal(0, r.TraceSamplesPastEnd);
    }
}
