using System.Globalization;
using System.Numerics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Dynamics.Models.Iec61400;
using GridSim.Core.Numerics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 batch-2 verify (2.1.130), the WP23 review's items on the IEC 61400-27-1 style Type 4 turbines:
/// <list type="bullet">
/// <item>the protection's frequency measurement is the terminal angle's derivative through Dynawo's Derivative block
/// (T = tffilt / 20), checked against its closed form on an angle ramp;</item>
/// <item>a disturbance at t = 0 leaves the start unchanged (nothing is read from the first step);</item>
/// <item>test 7: Dynawo's reference curves for its Neplan WT4A and WT4B 2020 examples, and the reasons, each checked on the
/// data, why they cannot be compared with the model as built (technical ch. 24).</item>
/// </list>
/// </summary>
public sealed class Rc5Batch2VerifyIec61400Tests
{
    private static Iec61400Protection Protection(double tffilt) => new(1.2, 0.85, 1.1, 0.9, Iec61400Table.Constant(1.0), Iec61400Table.Constant(1.0),
        Iec61400Table.Constant(1.0), Iec61400Table.Constant(1.0), 0.0, tffilt, 1.0);

    private static Iec61400State Model(Iec61400Protection? protection) =>
        (Iec61400State)new RmsConverterUnit(2, "wt", 1.0, 0.5, 0.1, Complex.One, new RmsConverterParameters())
        {
            Iec61400 = Rc5Wp23Iec61400Tests.Unit(Iec61400TurbineType.Type4A, Iec61400QControlMode.OpenLoopReactivePower, protection: protection),
        }.CreateModel(new[] { 2 });

    /// <summary>
    /// The terminal angle advances at a constant rate, 1 % above nominal (50 Hz): theta_n = 2 pi 50 x 0.01 x n dt. Dynawo's
    /// Derivative block s / (1 + s T) is the derivative through a lag of T = tffilt / 20. The angle's change over a step is
    /// exactly the ramp's derivative, 0.01 pu, from the second step on (the first step sees the start angle), and the lag
    /// is the exact zero-order hold, so the input to the protection after step n is 0.01 (1 - a^n), a = exp(-dt / T). With
    /// tffilt = 0 it is the difference itself, 0.01. Bound: the angle (DetMath.Phase of DetMath.FromPolar, each within a few
    /// ulp) is off by at most 3e-16 rad at these angles (under 0.35 rad), so the change by 6e-16 rad, 3.9e-16 pu after the
    /// division by 2 pi 50 dt = pi / 2; the lag's two roundings on values under 0.01 add under 4e-18. Asserted at 1e-15.
    /// </summary>
    [Theory]
    [InlineData(0.01, 0.005)]
    [InlineData(0.1, 0.005)]
    [InlineData(0.0, 0.005)]
    public void The_protection_frequency_is_the_angle_derivative_through_a_lag_of_tffilt_over_20(double tffilt, double dt)
    {
        Iec61400State m = Model(Protection(tffilt));
        double w = 2.0 * Math.PI * 50.0 * 0.01;
        double a = tffilt > 0.0 ? Math.Exp(-dt / (tffilt / 20.0)) : 0.0;
        for (int n = 0; n <= 20; n++)
        {
            Complex v = DetMath.FromPolar(1.0, w * n * dt);
            m.Commit(v);
            m.Step(n * dt, v, new[] { v }, dt);
            double expected = n == 0 ? 0.0 : 0.01 * (1.0 - Math.Pow(a, n));
            Assert.True(Math.Abs(m.ProtectionFrequencyInput - expected) <= 1e-15, $"step {n}: {m.ProtectionFrequencyInput:R} vs {expected:R}");
            Assert.Null(m.TripReason);
        }
    }

    /// <summary>
    /// A disturbance in force at t = 0: the first commit sees a terminal voltage of 0.5 pu at 0.4 rad instead of the solved
    /// 1 pu at 0. Nothing is read from it: the initial currents come from the solved point and the injected current's angle
    /// from the PLL's start, so the first injection and the first recorded state row are the undisturbed ones, bit for bit.
    /// </summary>
    [Fact]
    public void A_disturbance_at_t0_leaves_the_start_unchanged()
    {
        Iec61400State quiet = Model(Protection(0.01));
        Iec61400State disturbed = Model(Protection(0.01));
        Complex a = quiet.Commit(Complex.One);
        Complex b = disturbed.Commit(DetMath.FromPolar(0.5, 0.4));
        Assert.Equal(a, b);
        Assert.Equal(quiet.ActiveCurrent, disturbed.ActiveCurrent);
        Assert.Equal(quiet.ReactiveCurrent, disturbed.ReactiveCurrent);
        Assert.Equal(quiet.Trace!.Samples[0], disturbed.Trace!.Samples[0]);
        Assert.Equal(0, disturbed.FrtState);
    }

    private static List<double[]> Curve(string name)
    {
        string path = Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "dynawo-iec-2020", name);
        string[] lines = File.ReadAllText(path).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        // The files hold time, P, Q and U only: no voltage phase.
        Assert.Equal("time,P,Q,U", lines[0]);
        return lines.Skip(1).Select(l => l.Split(',').Select(x => double.Parse(x, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray()).ToList();
    }

    /// <summary>
    /// Test 7. Dynawo ships reference curves for its Neplan WT4A and WT4B 2020 examples (commit 43812dfb; the copy here is
    /// decimated to 50 ms, MPL-2.0, see fixtures/dynawo-iec-2020/ATTRIBUTION.txt). The comparison they invite (drive the
    /// terminal with the recorded voltage and compare P and Q) cannot be made with the model as built. Each reason is checked
    /// on the data: (1) the files hold no voltage phase; (2) P moves from 1 to 0.5 pu between 2 s and 2.5 s while U moves by
    /// under 0.02 pu, a step of the active power reference, which GridSim's turbine has no input for (its references are
    /// fixed at their initial values); (3) Q moves from -0.21 toward a 0.2 pu reference at 4 s, again with no voltage
    /// disturbance before it, and settles at 0.085 pu with U at 1.096 pu, held at a limit, the state both faults start
    /// from; GridSim starts a unit only at an unconstrained equilibrium. The faults themselves are in the data at 6 s
    /// (U under 0.8 pu) and 12 s (under 0.8 pu), each recovering to 1.096 pu. Reason (4), the measurement at the grid side
    /// of Dynawo's ElecSystem (not built here), is in the examples' parameters (RLvTr, XLvTr, GLvTr, BLvTr), not the data.
    /// </summary>
    [Theory]
    [InlineData("wt4a-2020-50ms.csv")]
    [InlineData("wt4b-2020-50ms.csv")]
    public void Test7_dynawo_reference_curves_and_why_they_cannot_be_compared(string file)
    {
        List<double[]> c = Curve(file);
        Assert.Equal(501, c.Count);
        for (int k = 0; k < c.Count; k++) Assert.True(Math.Abs(c[k][0] - 0.05 * k) <= 1e-12, $"row {k}: t {c[k][0]:R}");
        double[] At(double t) => c[(int)Math.Round(t / 0.05)];
        // (2) the active power reference step at 2 s.
        Assert.True(At(2.0)[1] > 0.99 && Math.Abs(At(2.5)[1] - 0.5) < 0.01, $"P {At(2.0)[1]:R} -> {At(2.5)[1]:R}");
        Assert.True(c.Take(41).All(r => Math.Abs(r[3] - c[0][3]) < 1e-3), "U moves before 2 s");
        Assert.True(Math.Abs(At(2.5)[3] - At(2.0)[3]) < 0.02, $"U {At(2.0)[3]:R} -> {At(2.5)[3]:R}");
        // (3) the reactive reference step at 4 s, and the limited state the faults start from.
        Assert.True(Math.Abs(At(4.0)[2] + 0.21) < 0.01 && At(4.05)[2] > -0.1, $"Q {At(4.0)[2]:R} -> {At(4.05)[2]:R}");
        Assert.True(Math.Abs(At(4.0)[3] - At(2.5)[3]) < 0.01, "U moves between 2.5 s and 4 s");
        double qPre = At(5.95)[2], uPre = At(5.95)[3];
        Assert.True(qPre > 0.08 && qPre < 0.09 && qPre < 0.2 - 0.1, $"Q before the first fault {qPre:R}");
        Assert.True(uPre > 1.09, $"U before the first fault {uPre:R}");
        Assert.True(Math.Abs(At(5.95)[2] - At(5.0)[2]) < 0.03 && Math.Abs(At(5.95)[2] - At(6.0)[2]) < 1e-4, "Q not settled before 6 s");
        // The two faults.
        Assert.True(At(6.05)[3] < 0.8 && At(6.2)[3] < 0.8 && At(6.3)[3] > 1.09, "fault at 6 s");
        Assert.True(At(12.1)[3] < 0.8 && At(12.2)[3] > 1.09, "fault at 12 s");
    }
}
