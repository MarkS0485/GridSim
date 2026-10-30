using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Dse;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N264 (LF4; Ruling R1: Mark delegated EE calls 2026-10-04, and Mark's determinism rule). The ensemble Kalman filter
/// used one random stream for the whole run, and draws taken by a step that then failed could not be given back, so every
/// later window depended on whether an earlier window had failed. Each window now draws from its own stream, derived from
/// (seed, number of committed windows) by <see cref="EnsembleKalmanStateEstimator.WindowSeed"/>; a failed step commits
/// nothing, so it cannot shift later windows. Also: the diagonal process-noise path refuses an entry that is not a
/// variance (negative, NaN, infinite) instead of reading a negative one as 0.
/// </summary>
public sealed partial class LF4LeftoversTests
{
    // ---- WindowSeed: pinned against SplitMix64 computed by hand (independently, in Python, from the published
    // constants; seed 0's first output is the reference value 0xE220A8397B1DCDAF, whose top 31 bits are 1896895516) ----

    [Theory]
    [InlineData(20260713, 0L, 20260713)]       // window 0 is the seed itself: the first window never moved
    [InlineData(20260713, 1L, 952398231)]
    [InlineData(20260713, 2L, 1938286683)]
    [InlineData(20260713, 3L, 888184535)]
    [InlineData(0, 1L, 1896895516)]            // 0xE220A8397B1DCDAF >> 33
    [InlineData(-1, 1L, 970497489)]            // (uint)-1 = 0xFFFFFFFF
    [InlineData(int.MinValue, 5L, 1285995687)]
    [InlineData(7, 1L << 40, 2112060658)]
    public void N264_window_seed_is_the_splitmix64_output_of_the_seed(int seed, long window, int expected)
        => Assert.Equal(expected, EnsembleKalmanStateEstimator.WindowSeed(seed, window));

    [Fact]
    public void N264_window_seed_refuses_a_negative_window_index()
        => Assert.Equal("windowIndex",
            Assert.Throws<ArgumentOutOfRangeException>(() => EnsembleKalmanStateEstimator.WindowSeed(1, -1)).ParamName);

    /// <summary>The random walk, until told to fail: then it throws from the Predict of member <see cref="FailAtMember"/>,
    /// AFTER the earlier members of that step have drawn their process noise (so before N264 those draws were lost).</summary>
    private sealed class FailLateModel : IProcessModel
    {
        private readonly RandomWalkProcessModel _inner;
        private int _calls;
        public FailLateModel(int angleCount) => _inner = new RandomWalkProcessModel(angleCount);
        public bool Fail { get; set; }
        public int FailAtMember { get; init; } = 5;
        public string Name => "fail-late-test-model";
        public void Predict(double[] x, double dtSeconds, double[,] f, double[,] q)
        {
            if (Fail && _calls++ == FailAtMember) throw new InvalidOperationException("fail-late-test-model: told to fail");
            _inner.Predict(x, dtSeconds, f, q);
        }
    }

    private static (GridModel Model, List<MeasurementSet> Windows, int Angles) N264Windows()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        List<MeasurementSet> windows = Enumerable.Range(0, 4)
            .Select(k => SyntheticMeasurements.Sample(model, truth, new SyntheticMeasurementOptions { Seed = 2640 + k })).ToList();
        return (model, windows, model.Buses.Count(b => b.Type != BusType.Slack));
    }

    private static void N264SameEstimate(DseEstimate expected, DseEstimate actual, string what)
    {
        foreach ((double[] e, double[] a, string name) in new[]
                 {
                     (expected.Vm, actual.Vm, "Vm"), (expected.VaDeg, actual.VaDeg, "VaDeg"),
                     (expected.StateVmSigma, actual.StateVmSigma, "Vm sigma"), (expected.StateVaSigmaDeg, actual.StateVaSigmaDeg, "Va sigma"),
                 })
        {
            Assert.Equal(e.Length, a.Length);
            for (int i = 0; i < e.Length; i++)
                Assert.True(BitConverter.DoubleToInt64Bits(e[i]) == BitConverter.DoubleToInt64Bits(a[i]), $"{what} {name}[{i}]: {e[i]:R} vs {a[i]:R}");
        }
    }

    [Fact]
    public void N264_a_window_that_fails_after_drawing_does_not_shift_the_later_windows()
    {
        // Filter A sees w0, then a step that fails after members 0-4 drew their noise, then w1, w2, w3. Filter B never sees
        // the failed step. Before N264 A's later windows differed from B's (the 5 x s draws of the failed step were gone
        // from the shared stream); now they are equal to the bit.
        (GridModel model, List<MeasurementSet> w, int angles) = N264Windows();
        var failing = new FailLateModel(angles);
        var a = new EnsembleKalmanStateEstimator(model, failing, ensembleSize: 30);
        var b = new EnsembleKalmanStateEstimator(model, new FailLateModel(angles), ensembleSize: 30);
        N264SameEstimate(b.Step(w[0], 1.0), a.Step(w[0], 1.0), "window 0");
        failing.Fail = true;
        Assert.Contains("told to fail", Assert.Throws<InvalidOperationException>(() => a.Step(w[1], 1.0)).Message, StringComparison.Ordinal);
        failing.Fail = false;
        for (int k = 1; k < w.Count; k++)
            N264SameEstimate(b.Step(w[k], 1.0 + k), a.Step(w[k], 1.0 + k), $"window {k}");
    }

    [Fact]
    public void N264_a_first_window_that_fails_after_drawing_leaves_the_first_stream_for_the_next_window()
    {
        // The first window draws the initial ensemble and then the noise; failing at member 5 consumed both. The window
        // after it is now the first committed window and must equal a filter that never saw the failure.
        (GridModel model, List<MeasurementSet> w, int angles) = N264Windows();
        var failing = new FailLateModel(angles) { Fail = true };
        var a = new EnsembleKalmanStateEstimator(model, failing, ensembleSize: 30);
        var b = new EnsembleKalmanStateEstimator(model, new FailLateModel(angles), ensembleSize: 30);
        Assert.Throws<InvalidOperationException>(() => a.Step(w[0], 1.0));
        failing.Fail = false;
        for (int k = 0; k < w.Count; k++)
            N264SameEstimate(b.Step(w[k], 1.0), a.Step(w[k], 1.0), $"window {k}");
    }

    // ---- the diagonal path refuses an entry that is not a variance ----

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-1e-300)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void N264_the_diagonal_sampler_refuses_a_diagonal_entry_that_is_not_a_variance(double bad)
    {
        var q = new double[3, 3];
        q[0, 0] = 4e-4; q[1, 1] = bad; q[2, 2] = 9e-6;
        var x = new[] { 1.0, 2.0, 3.0 };
        int draws = 0;
        var ex = Assert.Throws<InvalidOperationException>(() => new ProcessNoiseSampler("my-model").AddSample(q, x, () => { draws++; return 0.5; }));
        Assert.Contains("process model 'my-model'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Q[1,1]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a variance", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, draws);                          // refused before any draw
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, x);         // and before touching the member
    }

    [Fact]
    public void N264_zero_and_negative_zero_variances_are_still_accepted_unchanged()
    {
        // 0 is a variance (a state with no process noise, dt = 0); -0.0 compares equal to 0 and keeps the old statement.
        var q = new double[2, 2];
        q[0, 0] = 0.0; q[1, 1] = -0.0;
        var x = new[] { 1.0, 2.0 };
        new ProcessNoiseSampler("t").AddSample(q, x, () => 0.7);
        Assert.Equal(BitConverter.DoubleToInt64Bits(1.0 + Math.Sqrt(Math.Max(0.0, 0)) * 0.7), BitConverter.DoubleToInt64Bits(x[0]));
        Assert.Equal(BitConverter.DoubleToInt64Bits(2.0 + Math.Sqrt(Math.Max(-0.0, 0)) * 0.7), BitConverter.DoubleToInt64Bits(x[1]));
    }

    /// <summary>The random walk with one diagonal process-noise entry made negative once told to.</summary>
    private sealed class NegativeVarianceModel : IProcessModel
    {
        private readonly RandomWalkProcessModel _inner;
        public NegativeVarianceModel(int angleCount) => _inner = new RandomWalkProcessModel(angleCount);
        public bool Negative { get; set; }
        public string Name => "negative-variance-test-model";
        public void Predict(double[] x, double dtSeconds, double[,] f, double[,] q)
        {
            _inner.Predict(x, dtSeconds, f, q);
            if (Negative) q[2, 2] = -1e-6;
        }
    }

    [Fact]
    public void N264_the_filter_refuses_a_negative_process_variance_and_keeps_its_state()
    {
        (GridModel model, List<MeasurementSet> w, int angles) = N264Windows();
        var model2 = new NegativeVarianceModel(angles);
        var a = new EnsembleKalmanStateEstimator(model, model2, ensembleSize: 30);
        var b = new EnsembleKalmanStateEstimator(model, new NegativeVarianceModel(angles), ensembleSize: 30);
        a.Step(w[0], 1.0);
        b.Step(w[0], 1.0);
        model2.Negative = true;
        var ex = Assert.Throws<InvalidOperationException>(() => a.Step(w[1], 1.0));
        Assert.Contains("negative-variance-test-model", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Q[2,2]", ex.Message, StringComparison.Ordinal);
        model2.Negative = false;
        N264SameEstimate(b.Step(w[1], 1.0), a.Step(w[1], 1.0), "after the refused window");
    }
}
