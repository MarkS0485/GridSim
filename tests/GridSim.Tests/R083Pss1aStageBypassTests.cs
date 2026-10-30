using System;
using GridSim.Core.Dynamics.Models;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R083 (October 2026): a PSS1A lead-lag stage with both time constants zero is the unity transfer function and is
/// bypassed (published data switches a stage off with T3 = T4 = 0); it used to be refused. A zero lag with a non-zero
/// lead (a pure derivative) is still refused. Unity is checked against a stage with equal lead and lag, which is also
/// exactly unity in the exact-discretised block (feedthrough 1, lag state identically 0).
/// </summary>
public class R083Pss1aStageBypassTests
{
    [Fact]
    public void A_switched_off_stage_is_exactly_unity()
    {
        var off = new Pss1a(gain: 10.0, washoutSeconds: 5.0, 0.2, 0.05, 0.0, 0.0);
        var unity = new Pss1a(gain: 10.0, washoutSeconds: 5.0, 0.2, 0.05, 0.1, 0.1);
        off.Initialize(0.0);
        unity.Initialize(0.0);
        for (int k = 0; k < 400; k++)
        {
            double u = k < 50 ? 0.0 : 0.01 * Math.Sin(0.05 * k);   // a step-in swing
            double a = off.Step(u, 0.01), b = unity.Step(u, 0.01);
            Assert.Equal(BitConverter.DoubleToInt64Bits(b), BitConverter.DoubleToInt64Bits(a));
        }
    }

    [Fact]
    public void Both_stages_off_leaves_the_washed_out_gain()
    {
        var pss = new Pss1a(gain: 2.0, washoutSeconds: 1.0, 0.0, 0.0, 0.0, 0.0);
        pss.Initialize(0.0);
        // With both stages off the output is the washout of Ks u: a unit step gives Ks x exp(-t / Tw) after a step dt.
        double y = pss.Step(1.0, 0.1);
        Assert.Equal(2.0 * Math.Exp(-0.1), y, 12);
    }

    [Fact]
    public void A_zero_lag_with_a_non_zero_lead_is_refused()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Pss1a(10.0, 5.0, 0.2, 0.0, 0.0, 0.0));
        Assert.Contains("pure derivative", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pss1a(10.0, 5.0, 0.2, 0.05, 0.3, 0.0));
    }
}
