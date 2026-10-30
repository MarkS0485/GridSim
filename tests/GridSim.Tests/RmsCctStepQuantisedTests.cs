using System;
using System.Collections.Generic;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R106 (ruling Q15, 2026-10-02): the RMS critical clearing time is searched on the integration-step grid and
/// reported as the longest whole-step clearing time that keeps synchronism. These tests pin that contract:
///   - the reported CCT is an exact whole number of steps (StableSteps * step, bit-for-bit);
///   - the bracket is at step resolution: clearing at the CCT holds synchronism in a direct Study, and clearing
///     one step later loses it (so the reported number means exactly what the CLI says it means);
///   - the dense and sparse network paths give the same bracket, bit-for-bit;
///   - the WSCC-9 bus-7 value with the CLI's synthesised GENROU data is exactly 0.604 s (302 steps of 2 ms),
///     where the old continuous bisection gave 0.6039981842041016 s.
/// </summary>
public class RmsCctStepQuantisedTests
{
    private const double Step = RmsTransientStability.CctDefaultStepSeconds;   // 2 ms
    private const double Onset = RmsTransientStability.CctDefaultOnsetSeconds;  // 0.1 s

    // The CLI's synthesised GENROU data (GridSim.Cli.CliDefaults ratios off X'd), with the WSCC-9 Anderson-Fouad X'd.
    private static (GridModel Model, PowerFlowResult Pf, IReadOnlyList<SubtransientInit.MachineData> Data) Case9()
    {
        // Moved 2026-10 (D2 N017 / R238, Ruling R1: Mark delegated EE calls 2026-10-04; source: Anderson and Fouad 2003,
        // Example 2.6, see IeeeCases.Wscc9NominalFrequencyHz): case9 is now the WSCC system's 60 Hz and the CCT / modal paths
        // take the model's frequency. These tests pin the SEARCH / ASSEMBLY ALGORITHM on the network their numbers were captured
        // on, so they keep that network at its old 50 Hz nominal. Before: IeeeCases.Case9() (then 50 Hz). After: the same model
        // CopyWith(frequencyHz: 50.0); every pinned number below is unchanged. The 60 Hz case9 values are new pins.
        GridModel model = IeeeCases.Case9().CopyWith(frequencyHz: 50.0);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        var data = new List<SubtransientInit.MachineData>();
        foreach (Generator gen in model.Generators)
        {
            double xdp = IeeeCases.Wscc9TransientReactancePu[gen.Bus];
            data.Add(new SubtransientInit.MachineData(H: gen.H, Damping: 0.0,
                Xd: 3.0 * xdp, Xdp: xdp, Xdpp: 0.8 * xdp, Xq: 2.5 * xdp, Xqp: 1.5 * xdp, Xqpp: 0.8 * xdp,
                Tdop: 8.0, Tdopp: 0.03, Tqop: 0.4, Tqopp: 0.05));
        }
        return (model, pf, data);
    }

    [Fact]
    public void Case9_bus7_cct_is_exactly_302_steps()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case9();
        CctBracket b = RmsTransientStability.CriticalClearingBracket(model, pf, data, 7);
        Assert.False(b.UnstableAtZero);
        Assert.Equal(302, b.StableSteps);
        Assert.Equal(303, b.UnstableSteps);
        Assert.True(b.AtStepResolution);
        Assert.Equal(302 * Step, b.StableSeconds);
        Assert.Equal(0.604, b.StableSeconds);   // 302 * 0.002 rounds to the double nearest 0.604
        Assert.Equal(b.StableSeconds, RmsTransientStability.CriticalClearingTimeSeconds(model, pf, data, 7));
    }

    [Fact]
    public void The_cct_survives_in_a_direct_study_and_one_step_later_does_not()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case9();
        foreach (int bus in new[] { 1, 2, 3, 7 })
        {
            CctBracket b = RmsTransientStability.CriticalClearingBracket(model, pf, data, bus);
            Assert.True(b.AtStepResolution, $"bus {bus}: bracket [{b.StableSteps}, {b.UnstableSteps}] is not one step");
            double duration = Onset + b.StableSeconds + RmsTransientStability.PostClearWindowSeconds;
            MultiMachineSubtransient.Result atCct = RmsTransientStability.Study(model, pf, data, bus, Onset,
                b.StableSeconds, stepSeconds: Step, durationSeconds: duration);
            MultiMachineSubtransient.Result late = RmsTransientStability.Study(model, pf, data, bus, Onset,
                b.UnstableSeconds!.Value, stepSeconds: Step,
                durationSeconds: Onset + b.UnstableSeconds!.Value + RmsTransientStability.PostClearWindowSeconds);
            Assert.True(atCct.StayedInSynchronism, $"bus {bus}: clearing at the CCT {b.StableSeconds:R} s lost synchronism");
            Assert.False(late.StayedInSynchronism, $"bus {bus}: clearing one step later {b.UnstableSeconds:R} s held synchronism");
        }
    }

    [Fact]
    public void Dense_and_sparse_give_the_same_bracket_bit_for_bit()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case9();
        CctBracket dense;   // N271: dense Kron test oracle (was sparseNetwork: false); production is sparse
        using (SubtransientInit.DenseKronReferenceScope()) dense = RmsTransientStability.CriticalClearingBracket(model, pf, data, 7);
        CctBracket sparse = RmsTransientStability.CriticalClearingBracket(model, pf, data, 7);
        Assert.Equal(dense, sparse);
    }

    [Fact]
    public void A_window_shorter_than_a_step_or_a_bad_step_fails_loud()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case9();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RmsTransientStability.CriticalClearingBracket(model, pf, data, 7, maxSeconds: 0.001));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RmsTransientStability.CriticalClearingBracket(model, pf, data, 7, stepSeconds: 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RmsTransientStability.CriticalClearingBracket(model, pf, data, 7, stepSeconds: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RmsTransientStability.CriticalClearingBracket(model, pf, data, 7, iterations: 0));
    }

    [Fact]
    public void A_window_that_survives_reports_its_last_grid_point_and_no_unstable_end()
    {
        (GridModel model, PowerFlowResult pf, IReadOnlyList<SubtransientInit.MachineData> data) = Case9();
        // 0.1 s (50 steps) is well inside every case9 CCT above (the smallest is 0.32 s at bus 3).
        CctBracket b = RmsTransientStability.CriticalClearingBracket(model, pf, data, 7, maxSeconds: 0.1);
        Assert.Equal(50, b.StableSteps);
        Assert.Null(b.UnstableSteps);
        Assert.Null(b.UnstableSeconds);
        Assert.False(b.AtStepResolution);
    }
}
