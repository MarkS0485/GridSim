using GridSim.Core.Assets;
using GridSim.Core.Dynamics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R006 (October 2026 review): <c>MeasurementDelay</c> (the transport-delay buffer behind every grid-following
/// inverter and protection controller) used to walk its whole 512-sample queue twice per update. It is now a ring
/// buffer read by binary search. These tests drive the new buffer and the ORIGINAL queue implementation (kept
/// verbatim below as <see cref="ReferenceDelay"/>) with the same pushes and compare every <c>Read</c> and
/// <c>PhaseStep</c> answer bit for bit (<see cref="BitConverter.DoubleToInt64Bits(double)"/>, so signed zeros and
/// NaN payloads count): constant and jittered substeps, repeated time stamps, zero, small and longer-than-history
/// latencies, the 512-sample wrap, out-of-order and NaN time stamps, and Clear part-way through.
/// </summary>
public class MeasurementDelayBitIdentityTests
{
    /// <summary>The pre-R006 implementation, verbatim (only the class name changed).</summary>
    private sealed class ReferenceDelay
    {
        private readonly Queue<(double t, double hz, double rocof, double phase)> _q = new();

        public void Clear() => _q.Clear();

        public void Push(double t, double hz, double rocof, double phase)
        {
            _q.Enqueue((t, hz, rocof, phase));
            while (_q.Count > 512) _q.Dequeue();
        }

        public (double hz, double rocof, double phase) Read(double nowS, double latencyS)
        {
            double cutoff = nowS - latencyS;
            (double t, double hz, double rocof, double phase) best = _q.Count > 0 ? _q.Peek() : (nowS, FrequencyLimits.NominalHz, 0.0, 0.0);
            foreach (var s in _q)
            {
                if (s.t <= cutoff + 1e-12) best = s;
                else break;
            }
            return (best.hz, best.rocof, best.phase);
        }

        public double PhaseStep(double nowS, double windowS)
        {
            if (_q.Count == 0) return 0.0;
            double latest = 0.0; bool haveLatest = false;
            double refPhase = 0.0; bool haveRef = false;
            double cutoff = nowS - windowS;
            foreach (var s in _q)
            {
                if (!haveRef || s.t <= cutoff + 1e-12) { refPhase = s.phase; haveRef = haveRef || s.t <= cutoff + 1e-12; }
                latest = s.phase; haveLatest = true;
            }
            return haveLatest ? latest - refPhase : 0.0;
        }
    }

    private static readonly double[] Latencies = { 0.0, 1e-13, 0.02, 0.05, 0.2, 1.0, 10.24, 20.0 };
    private static readonly double[] Windows = { 0.0, 0.02, 0.04, 0.5, 30.0 };

    private static void AssertSame(double expected, double actual, string what) =>
        Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
            $"{what}: expected {expected:R} (0x{BitConverter.DoubleToInt64Bits(expected):X16}), got {actual:R} (0x{BitConverter.DoubleToInt64Bits(actual):X16})");

    /// <summary>Compare every read at one clock instant (several latencies and windows, and a clock a little behind
    /// and ahead of the last push).</summary>
    private static void CompareAt(ReferenceDelay reference, MeasurementDelay actual, double now, int step)
    {
        foreach (double clock in new[] { now, now - 0.013, now + 0.007 })
        {
            foreach (double lat in Latencies)
            {
                (double hz, double rocof, double phase) e = reference.Read(clock, lat);
                (double hz, double rocof, double phase) a = actual.Read(clock, lat);
                AssertSame(e.hz, a.hz, $"step {step} Read({clock:R}, {lat}).hz");
                AssertSame(e.rocof, a.rocof, $"step {step} Read({clock:R}, {lat}).rocof");
                AssertSame(e.phase, a.phase, $"step {step} Read({clock:R}, {lat}).phase");
            }
            foreach (double win in Windows)
                AssertSame(reference.PhaseStep(clock, win), actual.PhaseStep(clock, win), $"step {step} PhaseStep({clock:R}, {win})");
        }
    }

    /// <summary>Push the same sample into both and compare.</summary>
    private static void Drive(IEnumerable<double> times, int seed)
    {
        var rng = new Random(seed);
        var reference = new ReferenceDelay();
        var actual = new MeasurementDelay();
        CompareAt(reference, actual, 0.0, -1);   // empty history
        int step = 0;
        foreach (double t in times)
        {
            // Values with signed zeros and a NaN now and then, so a wrong pick or a sign slip would show.
            double hz = rng.Next(20) == 0 ? -0.0 : 49.5 + rng.NextDouble();
            double rocof = rng.Next(25) == 0 ? double.NaN : rng.NextDouble() - 0.5;
            double phase = rng.Next(15) == 0 ? 0.0 : (rng.NextDouble() - 0.5) * 360.0;
            reference.Push(t, hz, rocof, phase);
            actual.Push(t, hz, rocof, phase);
            CompareAt(reference, actual, double.IsNaN(t) ? step * 0.02 : t, step);
            step++;
        }
    }

    [Fact]
    public void Constant_substep_past_the_512_sample_wrap_matches_the_queue_bit_for_bit()
        => Drive(Enumerable.Range(0, 1400).Select(i => i * 0.02), seed: 1);

    [Fact]
    public void Jittered_substep_and_repeated_time_stamps_match_the_queue_bit_for_bit()
    {
        var rng = new Random(2);
        var times = new List<double>();
        double t = 0.0;
        for (int i = 0; i < 1300; i++)
        {
            int k = rng.Next(10);
            t += k == 0 ? 0.0 : k == 1 ? 1e-13 : 0.005 + rng.NextDouble() * 0.03;   // equal stamps and sub-tolerance steps
            times.Add(t);
        }
        Drive(times, seed: 3);
    }

    [Fact]
    public void Out_of_order_and_NaN_time_stamps_take_the_original_scan_and_match_the_queue()
    {
        var times = new List<double>();
        for (int i = 0; i < 300; i++) times.Add(i * 0.02);
        times.Add(2.0);              // a step back inside the window
        for (int i = 0; i < 100; i++) times.Add(6.0 + i * 0.02);
        times.Add(double.NaN);       // NaN compares false everywhere
        for (int i = 0; i < 700; i++) times.Add(8.0 + i * 0.02);   // long enough for both breaks to leave the window
        times.Add(-5.0);             // and one more after the window became ordered again
        for (int i = 0; i < 600; i++) times.Add(30.0 + i * 0.02);
        Drive(times, seed: 4);
    }

    [Fact]
    public void Clear_part_way_resets_both_to_the_same_state()
    {
        var reference = new ReferenceDelay();
        var actual = new MeasurementDelay();
        for (int i = 0; i < 700; i++) { reference.Push(i * 0.02, 50.0 + i, i, -i); actual.Push(i * 0.02, 50.0 + i, i, -i); }
        reference.Push(1.0, 1, 1, 1); actual.Push(1.0, 1, 1, 1);   // leave an out-of-order step stored
        reference.Clear(); actual.Clear();
        CompareAt(reference, actual, 3.0, -1);   // empty again: nominal frequency, phase step 0
        for (int i = 0; i < 50; i++)
        {
            reference.Push(100.0 + i * 0.02, 49.9, 0.1, i);
            actual.Push(100.0 + i * 0.02, 49.9, 0.1, i);
            CompareAt(reference, actual, 100.0 + i * 0.02, i);
        }
    }

    [Fact]
    public void Capacity_is_the_old_512_samples()
    {
        Assert.Equal(512, MeasurementDelay.Capacity);
        // The oldest kept sample after 600 pushes at 20 ms is push 88 (600 - 512): a latency far beyond the history
        // returns it, as the queue did.
        var actual = new MeasurementDelay();
        for (int i = 0; i < 600; i++) actual.Push(i * 0.02, i, 0, 0);
        Assert.Equal(88.0, actual.Read(599 * 0.02, 1000.0).hz);
    }
}
