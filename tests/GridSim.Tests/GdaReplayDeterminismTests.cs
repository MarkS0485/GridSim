using System;
using System.Collections.Generic;
using GridSim.Core.Gda;
using GridSim.Core.IO;
using GridSim.Core.PowerFlow;
using GridSim.Core.Simulation;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Replaying the same historical window twice reconstructs the same states bit for bit, on each power-flow engine.
/// Replay is the recovery path after a crash or a suspected tamper: the window is re-run and compared with the record,
/// so the replay itself must be exactly repeatable, warm starts included.
/// </summary>
public class GdaReplayDeterminismTests
{
    private static readonly DateTime Now = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Every step's summary record plus the full voltage and angle vectors, captured as solved.</summary>
    private static (GdaReplayResult Result, List<(double[] Vm, double[] VaDeg)> States) Run(SolverKind kind)
    {
        GdaTimeSeries series = GdaTimeSeries.Synthetic("day", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 24, 60);
        var states = new List<(double[] Vm, double[] VaDeg)>();
        GdaReplayResult result = GdaReplayLoop.Replay(IeeeCases.Case9(), series, kind, warmStart: true, utcNow: Now,
            onSolved: (_, _, _, pf, _) => states.Add(((double[])pf.Vm.Clone(), (double[])pf.VaDeg.Clone())));
        return (result, states);
    }

    [Theory]
    [InlineData(SolverKind.DenseNewton)]
    [InlineData(SolverKind.SparseNewton)]
    public void Replaying_the_same_window_twice_is_bit_identical(SolverKind kind)
    {
        (GdaReplayResult a, List<(double[] Vm, double[] VaDeg)> sa) = Run(kind);
        (GdaReplayResult b, List<(double[] Vm, double[] VaDeg)> sb) = Run(kind);

        Assert.Equal(24, a.Steps);
        Assert.Equal(0, a.NonConverged);
        Assert.Equal(a.Steps, b.Steps);
        Assert.Equal(sa.Count, sb.Count);

        for (int s = 0; s < a.StepResults.Count; s++)
        {
            GdaReplayStep x = a.StepResults[s], y = b.StepResults[s];
            Assert.Equal(x.TimeUtc, y.TimeUtc);
            Assert.Equal(x.Iterations, y.Iterations);
            Assert.Equal(BitConverter.DoubleToInt64Bits(x.TotalLossMw), BitConverter.DoubleToInt64Bits(y.TotalLossMw));
            Assert.Equal(BitConverter.DoubleToInt64Bits(x.MaxMismatch), BitConverter.DoubleToInt64Bits(y.MaxMismatch));
        }
        for (int s = 0; s < sa.Count; s++)
            for (int i = 0; i < sa[s].Vm.Length; i++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(sa[s].Vm[i]), BitConverter.DoubleToInt64Bits(sb[s].Vm[i]));
                Assert.Equal(BitConverter.DoubleToInt64Bits(sa[s].VaDeg[i]), BitConverter.DoubleToInt64Bits(sb[s].VaDeg[i]));
            }
    }
}
