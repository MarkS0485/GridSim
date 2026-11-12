using GridSim.Core.Estimation;
using GridSim.Core.Interpose;
using GridSim.Core.IO.Telemetry;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-305: the interposer judged the forward-inference horizon on a window's instant (its newest time tag) only, so a
/// point stamped long before the others was merged into the same snapshot. The owner's decision: an opt-in limit on the
/// spread of time tags in one window, off by default. Pinned: with no limit a widely spread window maps exactly as
/// before; with a limit it is refused whole (cleared, counted in SpreadRefused, not a map error, marked so the CLI can
/// tell it from a crosswalk mismatch), on both flush overloads; a window inside the limit maps; untagged points take no
/// part; a negative limit is refused. The CLI wiring (interpose --max-stamp-spread-ms) is pinned at source level.
/// </summary>
public sealed class Gr305InterposeStampSpreadTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Historical = Now.AddHours(-200);

    /// <summary>A protocol that hands back, for each frame, the next prepared list of points (the frame bytes are not
    /// read). Lets the test choose every time tag exactly.</summary>
    private sealed class ScriptedProtocol : IRtuProtocol
    {
        private readonly Queue<IReadOnlyList<RtuPoint>> _frames = new();
        public ScriptedProtocol(params IReadOnlyList<RtuPoint>[] frames) { foreach (var f in frames) _frames.Enqueue(f); }
        public string Name => "scripted";
        public string DisplayName => "scripted test protocol";
        public RtuTransportKind Transport => RtuTransportKind.Tcp;
        public int DefaultPort => 1;
        public IReadOnlyList<RtuPoint> Decode(ReadOnlySpan<byte> frame, QualityFold quality) => _frames.Dequeue();
    }

    private static RtuPointMap Crosswalk() => new()
    {
        Case = "t",
        Bindings = new List<RtuPointBinding>
        {
            new() { Address = "A1", Kind = MeasurementKind.VmPu, Bus = 1, Sigma = 0.01 },
            new() { Address = "A2", Kind = MeasurementKind.VmPu, Bus = 2, Sigma = 0.01 },
        },
    };

    private static RtuPoint Point(string address, double value, DateTime? stamp) =>
        new(address, RtuPointType.Analog, value, RtuQuality.Good, stamp);

    // Two points in two frames: A1 stamped at the historical instant, A2 stamped `apart` earlier.
    private static InterposePipeline Pipe(TimeSpan apart, TimeSpan? limit, bool secondTagged = true)
    {
        var protocol = new ScriptedProtocol(
            new[] { Point("A1", 1.01, Historical) },
            new[] { Point("A2", 0.99, secondTagged ? Historical - apart : null) });
        var pipe = new InterposePipeline(protocol, Crosswalk(), QualityFold.Default, "t") { MaxStampSpread = limit };
        pipe.Ingest(new byte[] { 1 });
        pipe.Ingest(new byte[] { 2 });
        return pipe;
    }

    [Fact]
    public void Gr305_without_a_limit_a_widely_spread_window_maps_as_before()
    {
        InterposePipeline pipe = Pipe(TimeSpan.FromHours(6), limit: null);
        Assert.Null(pipe.MaxStampSpread);
        MeasurementSet set = pipe.FlushWindow(Historical, utcNow: Now)!;
        Assert.Equal(2, set.Measurements.Count);
        Assert.Equal(0, pipe.SpreadRefused);
    }

    [Fact]
    public void Gr305_with_a_limit_a_window_spread_wider_is_refused_whole_and_counted()
    {
        InterposePipeline pipe = Pipe(TimeSpan.FromMinutes(5), limit: TimeSpan.FromSeconds(10));
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => pipe.FlushWindow(Historical, utcNow: Now));
        Assert.True(InterposePipeline.IsSpreadRefusal(ex));
        Assert.Contains("span 300000 ms", ex.Message, StringComparison.Ordinal);
        Assert.Contains("more than the 10000 ms limit", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, pipe.SpreadRefused);
        Assert.Equal(0, pipe.MapErrors);          // not a crosswalk mismatch
        Assert.Equal(0, pipe.PendingPoints);      // the window is cleared exactly once
        Assert.Null(pipe.FlushWindow(Historical, utcNow: Now));   // and nothing is left to flush
    }

    [Fact]
    public void Gr305_the_live_flush_refuses_the_same_window()
    {
        InterposePipeline pipe = Pipe(TimeSpan.FromMinutes(5), limit: TimeSpan.FromSeconds(10));
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => pipe.FlushWindow(Historical, InterposeFreshPolicy.Refuse, utcNow: Now));
        Assert.True(InterposePipeline.IsSpreadRefusal(ex));
        Assert.Equal(1, pipe.SpreadRefused);
        Assert.Equal(0, pipe.HeldFresh);
    }

    [Fact]
    public void Gr305_a_window_inside_the_limit_maps_and_the_limit_is_inclusive()
    {
        InterposePipeline pipe = Pipe(TimeSpan.FromSeconds(10), limit: TimeSpan.FromSeconds(10));
        Assert.Equal(2, pipe.FlushWindow(Historical, utcNow: Now)!.Measurements.Count);
        Assert.Equal(0, pipe.SpreadRefused);
    }

    [Fact]
    public void Gr305_an_untagged_point_takes_no_part_in_the_spread()
    {
        InterposePipeline pipe = Pipe(TimeSpan.FromHours(6), limit: TimeSpan.Zero, secondTagged: false);
        Assert.Equal(2, pipe.FlushWindow(Historical, utcNow: Now)!.Measurements.Count);
    }

    [Fact]
    public void Gr305_a_crosswalk_mismatch_is_not_a_spread_refusal()
    {
        Assert.False(InterposePipeline.IsSpreadRefusal(new InvalidDataException("crosswalk")));
    }

    [Fact]
    public void Gr305_a_negative_limit_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Pipe(TimeSpan.Zero, limit: TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void Gr305_interpose_wires_the_opt_in_flag_and_reports_refusals()
    {
        string src = File.ReadAllText(Path.Combine(CliProcess.Root(), "src", "GridSim.Cli", "Program.cs")).Replace("\r\n", "\n");
        Assert.Contains("{ MaxStampSpread = maxStampSpread }", src, StringComparison.Ordinal);
        Assert.Contains("catch (InvalidDataException ex) when (InterposePipeline.IsSpreadRefusal(ex))", src, StringComparison.Ordinal);
        Assert.Contains("\"--max-stamp-spread-ms\", // GR-305", src, StringComparison.Ordinal);
        Assert.Contains("if (pipeline.SpreadRefused > 0)", src, StringComparison.Ordinal);
    }
}
