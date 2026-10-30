using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N042 (the classical twin of R106 / ruling Q15): the CLASSICAL critical clearing time is searched on the
/// integration-step grid and reported as the longest whole-step clearing time that keeps synchronism. These tests
/// pin that contract:
///   - every reported CCT is an exact whole number of 2 ms steps (StableSteps * step, bit for bit);
///   - the bracket is at step resolution: clearing at the CCT holds synchronism in a direct study built exactly
///     as the CLI's `stability --clear` builds it, and clearing one step later loses it;
///   - the exact WSCC-9 values with the CLI's Anderson-Fouad X'd at every bus, against the old continuous
///     bisection's sub-step values (each old value lies within one step above the new one);
///   - the search fails loud on a meaningless step, window or iteration cap.
/// </summary>
public class ClassicalCctStepQuantisedTests
{
    private const double Step = ClassicalInit.CctDefaultStepSeconds;   // 2 ms
    private const double Onset = ClassicalInit.CctOnsetSeconds;         // 0.1 s

    // WSCC-9 solved, with the CLI's Anderson-Fouad transient reactances per machine.
    private static (GridModel Model, PowerFlowResult Pf, double[] Xdp) Case9()
    {
        // Moved 2026-10 (D2 N017 / R238, Ruling R1: Mark delegated EE calls 2026-10-04; source: Anderson and Fouad 2003,
        // Example 2.6, see IeeeCases.Wscc9NominalFrequencyHz): case9 is now the WSCC system's 60 Hz and the CCT / modal paths
        // take the model's frequency. These tests pin the SEARCH / ASSEMBLY ALGORITHM on the network their numbers were captured
        // on, so they keep that network at its old 50 Hz nominal. Before: IeeeCases.Case9() (then 50 Hz). After: the same model
        // CopyWith(frequencyHz: 50.0); every pinned number below is unchanged. The 60 Hz case9 values are new pins.
        GridModel model = IeeeCases.Case9().CopyWith(frequencyHz: 50.0);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++) xdp[i] = IeeeCases.Wscc9TransientReactancePu[model.Generators[i].Bus];
        return (model, pf, xdp);
    }

    // One study exactly as the CLI's classical `stability --clear` builds it: fault window [onset, onset + clear),
    // the same step, run to onset + clear + the post-clear window.
    private static bool StudySurvives(GridModel model, PowerFlowResult pf, double[] xdp, int bus, double clear)
    {
        ClassicalInit.Prepared p = ClassicalInit.FromSolvedCase(model, pf, xdp, bus, holdNonMachines: true);
        var fault = new ClassicalMultiMachine.Fault(Onset, Onset + clear, p.YReducedFaulted!);
        return ClassicalMultiMachine.Simulate(p.H, new double[p.H.Length], p.Pm, p.Eq, p.Delta0, p.YReduced,
            50.0, Step, Onset + clear + ClassicalInit.CctPostClearWindowSeconds, fault).StayedInSynchronism;
    }

    // Exact new values (whole steps) and the old continuous-bisection values (1.95.089), per WSCC-9 bus.
    // The old bisection narrowed onto a boundary inside a step (the classical RK4 samples the fault window at the
    // start, middle and end of each step) and reported the midpoint of a 3.8 microsecond bracket; the new search
    // reports the last whole step that holds. So every old value lies at or above the new one and no more than one
    // step plus the old bracket width above it. Bus 6 is the edge of that: the old value 0.3340015411376953 is
    // 1.5 microseconds past the new unstable end 0.334, because the classical kernel compares its sample times with
    // the fault edge without a tolerance, and at exactly 167 steps the sample AT the clearing instant rounds to
    // "still faulted" (a clear at 0.3339996 s does not). That is the kernel's own edge rule (the RMS integrator
    // gained a millionth-of-a-step tolerance in R249; the classical one has not), and the CLI's `--clear 334`
    // study sees the same rounding, so the reported bracket is still exactly what a user re-run reproduces.
    // Moved 2026-10 (N047, authorised by Mark's best-judgement ruling): bus 6 was 166 steps (0.332 s), a value that
    // encoded the rounding defect described above. The classical kernel now reads its fault edges with the R249
    // tolerance, so at 167 steps the sample at the clearing instant sees the cleared network, 167 steps (0.334 s)
    // holds synchronism and 168 loses it. The old continuous value 0.3340015411376953 now lies 1.5 microseconds
    // above the new CCT, inside the range this test has always allowed.
    [Theory]
    [InlineData(1, 275, 0.5500011444091797)]
    [InlineData(2, 171, 0.3430004119873047)]
    [InlineData(3, 165, 0.33099937438964844)]
    [InlineData(4, 259, 0.5189990997314453)]
    [InlineData(6, 167, 0.3340015411376953)]
    [InlineData(7, 305, 0.6109981536865234)]
    [InlineData(8, 178, 0.35700035095214844)]
    [InlineData(9, 380, 0.7609996795654297)]
    public void Case9_cct_is_an_exact_whole_number_of_steps(int bus, int steps, double oldValue)
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        CctBracket b = ClassicalInit.CriticalClearingBracket(model, pf, xdp, bus, holdNonMachines: true);
        Assert.False(b.UnstableAtZero);
        Assert.Equal(steps, b.StableSteps);
        Assert.Equal(steps + 1, b.UnstableSteps);
        Assert.True(b.AtStepResolution);
        Assert.Equal(steps * Step, b.StableSeconds);
        Assert.Equal(b.StableSeconds, ClassicalInit.CriticalClearingTimeSeconds(model, pf, xdp, bus, holdNonMachines: true));
        // The old sub-step value lies at or above the new CCT, by less than one step plus the old 3.8 microsecond
        // bracket (18 halvings of 1 s = 3.81e-6 s); 4e-6 s covers that width.
        Assert.InRange(oldValue - b.StableSeconds, 0.0, Step + 4e-6);
    }

    [Fact]
    public void Case9_bus7_cct_is_exactly_610_ms()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        // 305 * 0.002 rounds to the double nearest 0.61; the old continuous bisection gave 0.6109981536865234.
        Assert.Equal(0.61, ClassicalInit.CriticalClearingTimeSeconds(model, pf, xdp, 7, holdNonMachines: true));
        // The test-suite form (machines not held) gives the same bracket on WSCC-9 (every unit is a machine).
        Assert.Equal(0.61, ClassicalInit.CriticalClearingTimeSeconds(model, pf, xdp, 7));
    }

    [Fact]
    public void Case9_bus5_survives_the_whole_window()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        CctBracket b = ClassicalInit.CriticalClearingBracket(model, pf, xdp, 5, holdNonMachines: true);
        Assert.False(b.UnstableAtZero);
        Assert.Null(b.UnstableSteps);
        Assert.Equal(500, b.StableSteps);
        Assert.Equal(1.0, b.StableSeconds);   // 500 * 0.002 is exactly 1.0 in double: the old maxSeconds return
    }

    [Fact]
    public void The_cct_survives_in_a_cli_style_study_and_one_step_later_does_not()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        foreach (int bus in new[] { 1, 2, 3, 4, 6, 7, 8, 9 })
        {
            CctBracket b = ClassicalInit.CriticalClearingBracket(model, pf, xdp, bus, holdNonMachines: true);
            Assert.True(b.AtStepResolution, $"bus {bus}: bracket [{b.StableSteps}, {b.UnstableSteps}] is not one step");
            Assert.True(StudySurvives(model, pf, xdp, bus, b.StableSeconds),
                $"bus {bus}: clearing at the CCT {b.StableSeconds:R} s lost synchronism");
            Assert.False(StudySurvives(model, pf, xdp, bus, b.UnstableSeconds!.Value),
                $"bus {bus}: clearing one step later {b.UnstableSeconds:R} s held synchronism");
        }
    }

    [Fact]
    public void The_search_is_bit_identical_across_repeated_runs()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        CctBracket first = ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, holdNonMachines: true);
        for (int k = 0; k < 3; k++)
            Assert.Equal(first, ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, holdNonMachines: true));
    }

    [Fact]
    public void A_meaningless_step_window_or_cap_fails_loud()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9();
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, stepSeconds: 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, stepSeconds: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, maxSeconds: -1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, maxSeconds: double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, maxSeconds: 0.001));   // under one step
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, iterations: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClassicalInit.CriticalClearingTimeSeconds(model, pf, xdp, 7, stepSeconds: -0.002));
    }
}
