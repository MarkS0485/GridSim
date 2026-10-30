using System;
using System.Numerics;
using GridSim.Core.Dynamics.Impedance;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-096: the Nyquist encirclement count unwraps the smallest-angle phase step between consecutive grid points, so a
/// contour sampled too coarsely (a step near half a turn) can be counted wrongly. The verdict now carries the largest
/// step and trips a guard above a quarter turn.
/// </summary>
public class NyquistPhaseStepGuardTests
{
    private static double[] Grid(int n)
    {
        var f = new double[n];
        for (int i = 0; i < n; i++) f[i] = 1.0 + i;
        return f;
    }

    [Fact]
    public void A_flat_return_ratio_has_no_phase_step_and_does_not_trip_the_guard()
    {
        var l = new Complex[8];
        for (int i = 0; i < l.Length; i++) l[i] = new Complex(0.1, 0.0);
        NyquistVerdict v = MinorLoopNyquist.FromReturnRatio(Grid(l.Length), l);
        Assert.Equal(0, v.Encirclements);
        Assert.Equal(0.0, v.MaxPhaseStepRad);
        Assert.False(v.PhaseStepGuardTripped);
    }

    [Fact]
    public void A_return_difference_turning_a_third_of_a_turn_per_point_trips_the_guard()
    {
        // 1 + L = 0.5 exp(j k 2pi/3): each step is 120 degrees, above the 90-degree trusted step.
        var l = new Complex[6];
        for (int k = 0; k < l.Length; k++)
            l[k] = Complex.FromPolarCoordinates(0.5, k * 2.0 * Math.PI / 3.0) - Complex.One;
        NyquistVerdict v = MinorLoopNyquist.FromReturnRatio(Grid(l.Length), l);
        Assert.True(v.MaxPhaseStepRad > MinorLoopNyquist.MaxTrustedPhaseStepRad);
        Assert.True(v.PhaseStepGuardTripped);
    }

    [Fact]
    public void A_finely_sampled_circle_does_not_trip_the_guard()
    {
        // 1 + L = 0.5 exp(j k pi/36): 5-degree steps over the positive arm.
        var l = new Complex[37];
        for (int k = 0; k < l.Length; k++)
            l[k] = Complex.FromPolarCoordinates(0.5, k * Math.PI / 36.0) - Complex.One;
        NyquistVerdict v = MinorLoopNyquist.FromReturnRatio(Grid(l.Length), l);
        Assert.False(v.PhaseStepGuardTripped);
        Assert.True(v.MaxPhaseStepRad < MinorLoopNyquist.MaxTrustedPhaseStepRad);
    }
}
