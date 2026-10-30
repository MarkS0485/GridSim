using System;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N047 (October 2026, best-judgement ruling): the classical transient-stability kernel reads its fault edges with
/// the RMS integrator's R249 tolerance (a millionth of a step, <see cref="MultiMachineSubtransient.StageTimeToleranceSteps"/>).
/// An edge formed in floating point as onset + clearing can land one ulp past the RK4 sample time it means
/// (0.1 + 167 * 0.002 = 0.43400000000000005 against the sample 0.434), and before N047 that rounding decided whether
/// the sample at the clearing instant saw the fault.
/// </summary>
public class ClassicalFaultEdgeToleranceTests
{
    private const double Step = 0.002;

    private static ClassicalInit.Prepared Case9Prepared(int bus)
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
        return ClassicalInit.FromSolvedCase(model, pf, xdp, bus, holdNonMachines: true);
    }

    private static ClassicalMultiMachine.Result Run(ClassicalInit.Prepared p, double on, double clear, double duration = 1.0)
        => ClassicalMultiMachine.Simulate(p.H, new double[p.H.Length], p.Pm, p.Eq, p.Delta0, p.YReduced,
            50.0, Step, duration, new ClassicalMultiMachine.Fault(on, clear, p.YReducedFaulted!));

    private static void AssertSameTrajectory(ClassicalMultiMachine.Result a, ClassicalMultiMachine.Result b)
    {
        for (int m = 0; m < a.Machines.Count; m++)
            for (int k = 0; k < a.Machines[m].DeltaRad.Count; k++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Machines[m].DeltaRad[k]), BitConverter.DoubleToInt64Bits(b.Machines[m].DeltaRad[k]));
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Machines[m].SpeedPu[k]), BitConverter.DoubleToInt64Bits(b.Machines[m].SpeedPu[k]));
            }
    }

    private static bool SameTrajectory(ClassicalMultiMachine.Result a, ClassicalMultiMachine.Result b)
    {
        for (int m = 0; m < a.Machines.Count; m++)
            for (int k = 0; k < a.Machines[m].DeltaRad.Count; k++)
                if (BitConverter.DoubleToInt64Bits(a.Machines[m].DeltaRad[k]) != BitConverter.DoubleToInt64Bits(b.Machines[m].DeltaRad[k])) return false;
        return true;
    }

    [Fact]
    public void A_clearing_edge_one_ulp_past_a_sample_counts_as_reached()
    {
        double formed = 0.1 + 167 * Step;
        Assert.Equal(0.43400000000000005, formed);   // the rounding the tolerance absorbs
        Assert.NotEqual(0.434, formed);
        ClassicalInit.Prepared p = Case9Prepared(6);
        AssertSameTrajectory(Run(p, 0.1, formed), Run(p, 0.1, 0.434));
    }

    [Fact]
    public void An_onset_edge_one_ulp_past_a_sample_counts_as_reached()
    {
        double formed = 0.1 + 0.2;
        Assert.Equal(0.30000000000000004, formed);
        ClassicalInit.Prepared p = Case9Prepared(7);
        AssertSameTrajectory(Run(p, formed, 0.5), Run(p, 0.3, 0.5));
    }

    [Fact]
    public void An_edge_genuinely_between_samples_still_waits_for_the_next_sample()
    {
        // RK4 samples fall every half step (1 ms). A clear at 0.4345 s lies between the samples at 0.434 and 0.435, so
        // it acts at 0.435 exactly as a clear at 0.435 does, and differently from a clear at 0.434.
        ClassicalInit.Prepared p = Case9Prepared(6);
        ClassicalMultiMachine.Result between = Run(p, 0.1, 0.4345);
        AssertSameTrajectory(between, Run(p, 0.1, 0.435));
        Assert.False(SameTrajectory(between, Run(p, 0.1, 0.434)));
    }

    [Fact]
    public void Case9_bus6_classical_cct_is_334_ms_and_the_cli_style_study_agrees()
    {
        // Before N047: 166 steps (0.332 s), because at 167 steps the sample at the clearing instant rounded to "still
        // faulted". Now 167 steps (0.334 s) holds and 168 (0.336 s) loses synchronism.
        // Moved 2026-10 (D2 N017 / R238, Ruling R1: Mark delegated EE calls 2026-10-04; source: Anderson and Fouad 2003,
        // Example 2.6, see IeeeCases.Wscc9NominalFrequencyHz): case9 is now the WSCC system's 60 Hz and the CCT / modal paths
        // take the model's frequency. These tests pin the SEARCH / ASSEMBLY ALGORITHM on the network their numbers were captured
        // on, so they keep that network at its old 50 Hz nominal. Before: IeeeCases.Case9() (then 50 Hz). After: the same model
        // CopyWith(frequencyHz: 50.0); every pinned number below is unchanged. The 60 Hz case9 values are new pins.
        GridModel model = IeeeCases.Case9().CopyWith(frequencyHz: 50.0);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++) xdp[i] = IeeeCases.Wscc9TransientReactancePu[model.Generators[i].Bus];
        CctBracket b = ClassicalInit.CriticalClearingBracket(model, pf, xdp, 6, holdNonMachines: true);
        Assert.Equal(167, b.StableSteps);
        Assert.Equal(168, b.UnstableSteps);
        Assert.Equal(0.334, b.StableSeconds);
        Assert.False(b.NonMonotone);

        ClassicalInit.Prepared p = Case9Prepared(6);
        Assert.True(Run(p, 0.1, 0.1 + 0.334, 0.1 + 0.334 + 5.0).StayedInSynchronism);
        Assert.False(Run(p, 0.1, 0.1 + 0.336, 0.1 + 0.336 + 5.0).StayedInSynchronism);
    }

    [Theory]
    // bus, CCT steps, stable pocket above the first loss (0 = none), after N047 with the N048 search.
    [InlineData(1, 275, 0)]
    [InlineData(2, 171, 175)]
    [InlineData(3, 165, 0)]
    [InlineData(4, 259, 0)]
    [InlineData(6, 167, 0)]
    [InlineData(7, 305, 0)]
    [InlineData(8, 178, 190)]
    [InlineData(9, 380, 395)]
    public void Case9_classical_brackets_after_N047_are_pinned(int bus, int steps, int pocket)
    {
        // Moved 2026-10 (D2 N017 / R238, Ruling R1: Mark delegated EE calls 2026-10-04; source: Anderson and Fouad 2003,
        // Example 2.6, see IeeeCases.Wscc9NominalFrequencyHz): case9 is now the WSCC system's 60 Hz and the CCT / modal paths
        // take the model's frequency. These tests pin the SEARCH / ASSEMBLY ALGORITHM on the network their numbers were captured
        // on, so they keep that network at its old 50 Hz nominal. Before: IeeeCases.Case9() (then 50 Hz). After: the same model
        // CopyWith(frequencyHz: 50.0); every pinned number below is unchanged. The 60 Hz case9 values are new pins.
        GridModel model = IeeeCases.Case9().CopyWith(frequencyHz: 50.0);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++) xdp[i] = IeeeCases.Wscc9TransientReactancePu[model.Generators[i].Bus];
        CctBracket b = ClassicalInit.CriticalClearingBracket(model, pf, xdp, bus, holdNonMachines: true);
        Assert.Equal(steps, b.StableSteps);
        Assert.Equal(steps + 1, b.UnstableSteps);
        Assert.Equal(pocket == 0 ? null : pocket, b.PocketStableSteps);
    }

    [Fact]
    public void Without_the_N048_checks_bus_2_would_land_on_its_pocket()
    {
        // With N047 the bus-2 pocket moves from 174 to 175 steps, onto the bisection's path: the bisection alone
        // (scanSteps 0) now reports 350 ms, a clearing time above the first loss at 344 ms. The N048 search reports 342.
        // Moved 2026-10 (D2 N017 / R238, Ruling R1: Mark delegated EE calls 2026-10-04; source: Anderson and Fouad 2003,
        // Example 2.6, see IeeeCases.Wscc9NominalFrequencyHz): case9 is now the WSCC system's 60 Hz and the CCT / modal paths
        // take the model's frequency. These tests pin the SEARCH / ASSEMBLY ALGORITHM on the network their numbers were captured
        // on, so they keep that network at its old 50 Hz nominal. Before: IeeeCases.Case9() (then 50 Hz). After: the same model
        // CopyWith(frequencyHz: 50.0); every pinned number below is unchanged. The 60 Hz case9 values are new pins.
        GridModel model = IeeeCases.Case9().CopyWith(frequencyHz: 50.0);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        var xdp = new double[model.Generators.Count];
        for (int i = 0; i < xdp.Length; i++) xdp[i] = IeeeCases.Wscc9TransientReactancePu[model.Generators[i].Bus];
        CctBracket bisectionOnly = ClassicalInit.CriticalClearingBracket(model, pf, xdp, 2, holdNonMachines: true, scanSteps: 0);
        Assert.Equal(175, bisectionOnly.StableSteps);
        Assert.Equal(0.35000000000000003, bisectionOnly.StableSeconds);
        Assert.Equal(0.342, ClassicalInit.CriticalClearingTimeSeconds(model, pf, xdp, 2, holdNonMachines: true));
    }
}
