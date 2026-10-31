using GridSim.Core.Dynamics.Models.Wecc;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GitHub issue #5: REEC_A with PFlag = 1, the order of the Pref rate limit and the speed multiplication, confirmed
/// against the REEC_A block diagram of EPRI 3002006525 (2015, Figure 3-1) and EPRI 3002027129 (2023): Pref passes the
/// dPmax / dPmin rate limiter (state s6) first, and the speed wg multiplies the limiter's output before the Tpord lag.
/// The test makes the two orders give different numbers (a binding rate limit while the speed moves) and pins the
/// diagram's.
/// </summary>
public sealed class ReecPFlagOrderTests
{
    private static ReecParameters ReecA() => new()
    {
        Variant = ReecVariant.A, PfFlag = 0, VFlag = 1, QFlag = 1, PFlag = 1, PqFlag = 0,
        Vdip = 0.9, Vup = 1.1, Trv = 0.02, Dbd1 = -0.05, Dbd2 = 0.05, Kqv = 2.0, Iqh1 = 1.05, Iql1 = -1.05, Vref0 = 0.0,
        Tp = 0.05, Qmax = 0.5, Qmin = -0.5, Vmax = 1.1, Vmin = 0.9, Kqp = 0.0, Kqi = 0.1, Kvp = 0.0, Kvi = 40.0, Tiq = 0.02,
        DPmax = 0.1, DPmin = -0.1, Pmax = 2.0, Pmin = 0.0, Imax = 2.0, Tpord = 0.02,
        Vq = new[] { 0.0, 0.2, 0.5, 1.0 }, Iq = new[] { 2.0, 2.0, 2.0, 2.0 }, Vp = new[] { 0.0, 0.2, 0.5, 1.0 }, Ip = new[] { 2.0, 2.0, 2.0, 2.0 },
    };

    [Fact]
    public void With_PFlag_1_the_rate_limit_acts_on_Pref_and_the_speed_multiplies_after_it()
    {
        const double dt = 0.01, p0 = 0.8, wg = 1.2;
        var reec = new Reec(ReecA(), 1.0, p0, 0.0, p0, 0.0, wg0: 1.0);
        Assert.Equal(p0, reec.PrefInitial);
        // A Pref step to 1.0 while the speed has risen to 1.2: the 0.1 pu/s limit binds.
        var u = new ReecInputs(Vt: 1.0, Pe: p0, Qgen: 0.0, Pref: 1.0, Qext: 0.0, Wg: wg);
        ReecOutputs o = reec.Evaluate(u, 0.0, dt);
        // Before any advance the limiter still holds the initial Pref; the speed already multiplies it.
        Assert.Equal(p0 * wg, o.Psel);
        reec.Advance(u, o, dt);
        ReecOutputs o1 = reec.Evaluate(u, dt, dt);
        // Diagram order: s6 = 0.8 + 0.1 x 0.01 = 0.801 (Pref limited), Psel = 0.801 x 1.2 = 0.9612.
        double diagram = (p0 + 0.1 * dt) * wg;
        // The other reading (limit the product): the product starts at 0.8 x 1 and moves at most 0.1 x 0.01 -> 0.801.
        double other = p0 + 0.1 * dt;
        Assert.True(Math.Abs(o1.Psel - diagram) < 1e-12, $"Psel {o1.Psel:R}, diagram order {diagram:R}");
        Assert.True(Math.Abs(o1.Psel - other) > 0.1, $"Psel {o1.Psel:R} matches the product-limited reading {other:R}");
    }
}
