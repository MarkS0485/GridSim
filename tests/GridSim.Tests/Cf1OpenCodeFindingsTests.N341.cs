using System;
using System.Linq;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N341 / N343 (CF1, October 2026; Ruling R1, Mark delegated EE calls 2026-10-04). The CCT criterion is the first loss:
/// the largest clearing time below which EVERY trial holds synchronism (what PSS/E and PowerFactory report); a stable
/// pocket above it is a note, never the CCT. The 5 s post-clear window stays the documented default (first-swing
/// stability) and every CCT explanation now says so. These tests prove the criterion exhaustively on the two case9 buses
/// with pockets that matter most (2 and 7, 60 Hz, the CLI setup): every step count from 0 to the reported stable end is
/// simulated and holds, the next one loses, and the pocket above holds again. No number moves (FB1's pins stand).
/// </summary>
public sealed partial class Cf1OpenCodeFindingsTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public void N341_the_reported_cct_is_the_first_loss_every_trial_below_it_holds(int bus)
    {
        GridModel model = IeeeCases.Case9();
        Assert.Equal(60.0, model.FrequencyHz);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        double[] xdp = model.Generators.Select(g => IeeeCases.Wscc9TransientReactancePu[g.Bus]).ToArray();
        ClassicalInit.FaultScreen screen = ClassicalInit.PrepareFaultScreen(model, pf, xdp, holdNonMachines: true);
        CctBracket br = ClassicalInit.CriticalClearingBracket(screen, bus);   // the CLI's call (default 5 s window)
        Assert.False(br.UnstableAtZero);
        Assert.NotNull(br.UnstableSteps);
        ClassicalInit.Prepared p = screen.ForFault(bus);

        // One trial exactly as the search builds it (ClassicalInit.CriticalClearingBracketCore's Survives).
        double step = ClassicalInit.CctDefaultStepSeconds;
        bool Holds(int steps)
        {
            double clear = steps * step;
            var fault = new ClassicalMultiMachine.Fault(ClassicalInit.CctOnsetSeconds, ClassicalInit.CctOnsetSeconds + clear, p.YReducedFaulted!);
            return ClassicalMultiMachine.Simulate(p.H, new double[p.H.Length], p.Pm, p.Eq, p.Delta0, p.YReduced, model.FrequencyHz, step,
                ClassicalInit.CctOnsetSeconds + clear + ClassicalInit.CctPostClearWindowSeconds, fault).StayedInSynchronism;
        }

        // The definition, checked exhaustively: every clearing time from 0 to the CCT holds, one step more loses.
        for (int m = 0; m <= br.StableSteps; m++)
            Assert.True(Holds(m), $"bus {bus}: clearing after {m} steps loses synchronism, below the reported CCT of {br.StableSteps} steps");
        Assert.False(Holds(br.StableSteps + 1));
        Assert.Equal(br.StableSteps + 1, br.UnstableSteps);
        // A pocket above the first loss is reported as a note (the bracket's PocketStableSteps) and really does hold, but
        // it is never the CCT.
        if (br.PocketStableSteps is int pocket)
        {
            Assert.True(pocket > br.StableSteps + 1);
            Assert.True(Holds(pocket));
            Assert.True(br.NonMonotone);
        }
    }

    [Fact]
    public void N341_every_classical_cct_explanation_states_the_criterion_and_the_5_s_window()
    {
        Assert.Equal(5.0, ClassicalInit.CctPostClearWindowSeconds);
        Assert.Equal(5.0, RmsTransientStability.PostClearWindowSeconds);
        const string window = "Each trial is judged over the 5 s after the fault clears (the documented default window: first-swing "
            + "stability, the range in which the classical constant-E' model holds); an undamped system can lose synchronism on a "
            + "later swing, so a longer window can give a shorter CCT.";

        CliProcess.Result one = CliProcess.RunBuilt("stability", "case9", "--fault-bus", "7");
        Assert.True(one.ExitCode == 0, one.ToString());
        // FB1's pins stand: bus 7 at 60 Hz is 275 steps (550 ms), first loss 276, a pocket at 279 (558 ms).
        Assert.Contains("Critical clearing time for a 3-phase fault at bus 7:  550 ms", one.StdOut, StringComparison.Ordinal);
        Assert.Contains("NON-MONOTONE: clearing at 558 ms holds synchronism again", one.StdOut, StringComparison.Ordinal);
        Assert.Contains("It is the first loss from below", one.StdOut, StringComparison.Ordinal);
        Assert.Contains(window, one.StdOut, StringComparison.Ordinal);

        CliProcess.Result screen = CliProcess.RunBuilt("stability", "case9", "--screen");
        Assert.True(screen.ExitCode == 0, screen.ToString());
        Assert.Contains(window, screen.StdOut, StringComparison.Ordinal);
    }
}
