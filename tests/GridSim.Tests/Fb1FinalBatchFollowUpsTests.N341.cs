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
/// N341 (FB1, final-batch follow-ups, October 2026; Ruling R1, Mark delegated EE calls 2026-10-04). The undamped classical
/// CCT depends on the post-clear window each trial runs for (ClassicalInit.CctPostClearWindowSeconds, 5 s), because an
/// undamped swing keeps every swing it starts and a longer window can catch a later loss of synchronism. The CCT search now
/// takes an optional <c>postClearWindowSeconds</c> (default 5 s, bit for bit the search it was when omitted).
///
/// <para>The proof the window makes exact (row C6 of FINAL-BATCH-MOVES-2026-10-10.md). The classical swing of machine i,
/// <c>d delta_i / dt = ws * w_i</c>, <c>2 H_i d w_i / dt = Pm_i - Pe_i(delta)</c>, gives
/// <c>(2 H_i / ws) d2 delta_i / dt2 = Pm_i - Pe_i(delta)</c>: time enters only through <c>tau = t * sqrt(ws)</c>. So a run at
/// f0 = 60 Hz with step h, clearing after m steps and running W after the clear, is the run at 50 Hz with step
/// h * sqrt(60 / 50), clearing after the same m steps and running W * sqrt(1.2): the same trajectory in tau, sample for
/// sample (the pre-fault stretch is the steady state, where nothing moves). Hence the two searches see the same verdict at
/// every step count and return the same bracket in steps. The old R238 check (CCT60 = CCT50 / sqrt(1.2) within 3 steps at
/// the SAME 5 s window) is not this statement: the same 5 s holds more swings at 60 Hz, which is what N341 is about. That
/// test, D2NominalFrequencyTests.R238_classical_cct_scales_with_the_square_root_of_the_frequency, was row C6 and now
/// makes the exact comparison below on bus 7 (moved under Mark's go on the final-batch moves, 2026-10-10).</para>
/// </summary>
public sealed partial class Fb1FinalBatchFollowUpsTests
{
    private static readonly double SqrtOfSixFifths = Math.Sqrt(1.2);   // sqrt(60 / 50)

    // WSCC-9 at its own 60 Hz (IeeeCases.Wscc9NominalFrequencyHz), solved, with the Anderson-Fouad X'd (machine base), as the
    // CLI's classical screen uses it. The power flow does not read FrequencyHz, so one solve serves every frequency copy.
    private static (GridModel Model, PowerFlowResult Pf, double[] Xdp) Case9At60()
    {
        GridModel model = IeeeCases.Case9();
        Assert.Equal(60.0, model.FrequencyHz);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        double[] xdp = model.Generators.Select(g => IeeeCases.Wscc9TransientReactancePu[g.Bus]).ToArray();
        return (model, pf, xdp);
    }

    /// <summary>The same bracket in STEPS (everything but the step length): stable and unstable ends, the zero flag, the
    /// pocket, the scan depth and the number of distinct trials the search ran.</summary>
    private static void SameBracketInSteps(CctBracket expected, CctBracket actual, string what)
    {
        Assert.True(expected.StableSteps == actual.StableSteps, $"{what}: stable {expected.StableSteps} vs {actual.StableSteps}");
        Assert.True(expected.UnstableSteps == actual.UnstableSteps, $"{what}: unstable {expected.UnstableSteps} vs {actual.UnstableSteps}");
        Assert.True(expected.UnstableAtZero == actual.UnstableAtZero, $"{what}: zero flag");
        Assert.True(expected.PocketStableSteps == actual.PocketStableSteps,
            $"{what}: pocket {expected.PocketStableSteps} vs {actual.PocketStableSteps}");
        Assert.True(expected.ScanSteps == actual.ScanSteps, $"{what}: scan depth");
        Assert.True(expected.Trials == actual.Trials, $"{what}: trials {expected.Trials} vs {actual.Trials}");
    }

    // ---- the argument is additive: omitted = the 5 s search, bit for bit --------------------------------------------

    /// <summary>Omitting the window is exactly passing CctPostClearWindowSeconds (5 s): the whole bracket record is equal
    /// (every field, the step length included), at 50 and 60 Hz, on both CriticalClearingBracket overloads, and
    /// CriticalClearingTimeSeconds returns the same double.</summary>
    [Theory]
    [InlineData(50.0)]
    [InlineData(60.0)]
    public void N341_omitting_the_post_clear_window_is_the_5_s_search_bit_for_bit(double hz)
    {
        (GridModel model60, PowerFlowResult pf, double[] xdp) = Case9At60();
        GridModel model = model60.CopyWith(frequencyHz: hz);
        Assert.Equal(5.0, ClassicalInit.CctPostClearWindowSeconds);
        foreach (int bus in new[] { 2, 7 })
        {
            CctBracket omitted = ClassicalInit.CriticalClearingBracket(model, pf, xdp, bus, holdNonMachines: true);
            CctBracket given = ClassicalInit.CriticalClearingBracket(model, pf, xdp, bus, holdNonMachines: true,
                postClearWindowSeconds: ClassicalInit.CctPostClearWindowSeconds);
            Assert.Equal(omitted, given);
            Assert.Equal(omitted.PocketStableSteps, given.PocketStableSteps);
            Assert.Equal(omitted.Trials, given.Trials);

            ClassicalInit.FaultScreen screen = ClassicalInit.PrepareFaultScreen(model, pf, xdp, holdNonMachines: true);
            CctBracket screenOmitted = ClassicalInit.CriticalClearingBracket(screen, bus);
            CctBracket screenGiven = ClassicalInit.CriticalClearingBracket(screen, bus, postClearWindowSeconds: 5.0);
            Assert.Equal(omitted, screenOmitted);
            Assert.Equal(omitted, screenGiven);

            Assert.Equal(BitConverter.DoubleToInt64Bits(omitted.StableSeconds), BitConverter.DoubleToInt64Bits(
                ClassicalInit.CriticalClearingTimeSeconds(model, pf, xdp, bus, holdNonMachines: true, postClearWindowSeconds: 5.0)));
        }
    }

    // ---- the frequency scaling is exact once the window scales too ------------------------------------------------

    /// <summary>The 60 Hz bracket at every fault bus equals a 50 Hz search in scaled time (step 2 ms x sqrt(1.2), search
    /// window 1 s x sqrt(1.2) so both hold the same 500 steps, post-clear window 5 s x sqrt(1.2)), in steps, exactly: the
    /// same stable and unstable ends, the same pocket and the same number of trials. Bus 5 survives the whole window in
    /// both.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void N341_the_60_hz_bracket_equals_a_time_scaled_50_hz_search_exactly(int bus)
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9At60();
        CctBracket at60 = ClassicalInit.CriticalClearingBracket(model, pf, xdp, bus, holdNonMachines: true);
        double step50 = ClassicalInit.CctDefaultStepSeconds * SqrtOfSixFifths;
        CctBracket scaled50 = ClassicalInit.CriticalClearingBracket(model.CopyWith(frequencyHz: 50.0), pf, xdp, bus,
            stepSeconds: step50, maxSeconds: ClassicalInit.CctDefaultMaxSeconds * SqrtOfSixFifths, holdNonMachines: true,
            postClearWindowSeconds: ClassicalInit.CctPostClearWindowSeconds * SqrtOfSixFifths);
        SameBracketInSteps(at60, scaled50, $"bus {bus}");
        // The reported times are the same step counts on the two grids.
        Assert.Equal(at60.StableSteps * ClassicalInit.CctDefaultStepSeconds, at60.StableSeconds);
        Assert.Equal(scaled50.StableSteps * step50, scaled50.StableSeconds);
    }

    /// <summary>The identity holds at every post-clear window, not just the default (bus 7: 5 / sqrt(1.2), 5 x sqrt(1.2)
    /// and 20 s at 60 Hz, each against its time-scaled 50 Hz twin), as the final-batch probe measured trial by trial.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void N341_the_scaling_identity_holds_at_every_post_clear_window(int which)
    {
        double window60 = which switch
        {
            0 => ClassicalInit.CctPostClearWindowSeconds / SqrtOfSixFifths,
            1 => ClassicalInit.CctPostClearWindowSeconds * SqrtOfSixFifths,
            _ => 20.0,
        };
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9At60();
        CctBracket at60 = ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, holdNonMachines: true,
            postClearWindowSeconds: window60);
        CctBracket scaled50 = ClassicalInit.CriticalClearingBracket(model.CopyWith(frequencyHz: 50.0), pf, xdp, 7,
            stepSeconds: ClassicalInit.CctDefaultStepSeconds * SqrtOfSixFifths,
            maxSeconds: ClassicalInit.CctDefaultMaxSeconds * SqrtOfSixFifths, holdNonMachines: true,
            postClearWindowSeconds: window60 * SqrtOfSixFifths);
        SameBracketInSteps(at60, scaled50, $"window {window60:R} s");
    }

    // ---- what N341 is: the window changes the undamped verdict ----------------------------------------------------

    /// <summary>WSCC-9 bus 7 at 60 Hz. With the default 5 s window the bracket is 275 / 276 steps with a stable pocket at
    /// 279 (steps 268-275 hold, 276-278 lose, 279 holds, 280 on lose: the N341 measurement). With 20 s the undamped swing
    /// loses synchronism at every one of those steps, and the reported CCT drops to 218 steps (0.436 s; bracket 218 / 219, no pocket). Pinned exactly
    /// so the window dependence is on record, not to bless either number as "the" CCT (that is N341's open question:
    /// first-swing criterion, default damping or a window stated in swings).</summary>
    [Fact]
    public void N341_bus_7_at_60_hz_depends_on_the_post_clear_window()
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9At60();
        CctBracket five = ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, holdNonMachines: true);
        Assert.Equal((275, (int?)276, (int?)279, false), (five.StableSteps, five.UnstableSteps, five.PocketStableSteps, five.UnstableAtZero));
        Assert.Equal(0.55, five.StableSeconds);   // 275 * 0.002 rounds to the double nearest 0.55

        CctBracket twenty = ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, holdNonMachines: true, postClearWindowSeconds: 20.0);
        Assert.Equal((218, (int?)219, (int?)null, false),
            (twenty.StableSteps, twenty.UnstableSteps, twenty.PocketStableSteps, twenty.UnstableAtZero));
        Assert.True(twenty.StableSteps < five.StableSteps);
    }

    // ---- fail loud ---------------------------------------------------------------------------------------------------

    /// <summary>A post-clear window that is not positive and finite is refused before any trial, on every entry point,
    /// naming the argument.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void N341_a_meaningless_post_clear_window_is_refused(double window)
    {
        (GridModel model, PowerFlowResult pf, double[] xdp) = Case9At60();
        var a = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClassicalInit.CriticalClearingBracket(model, pf, xdp, 7, postClearWindowSeconds: window));
        Assert.Equal("postClearWindowSeconds", a.ParamName);
        Assert.Contains("post-clear window must be positive and finite", a.Message, StringComparison.Ordinal);
        var b = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClassicalInit.CriticalClearingTimeSeconds(model, pf, xdp, 7, postClearWindowSeconds: window));
        Assert.Equal("postClearWindowSeconds", b.ParamName);
        ClassicalInit.FaultScreen screen = ClassicalInit.PrepareFaultScreen(model, pf, xdp, holdNonMachines: true);
        var c = Assert.Throws<ArgumentOutOfRangeException>(() =>
            ClassicalInit.CriticalClearingBracket(screen, 7, postClearWindowSeconds: window));
        Assert.Equal("postClearWindowSeconds", c.ParamName);
    }
}
