using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-583: a three-phase measurement whose phase is not 0, 1 or 2 is refused by name before an estimator indexes a phase
/// array with it (it used to surface as an IndexOutOfRangeException).
/// </summary>
public sealed class ThreePhasePhaseIndexTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void A_voltage_measurement_on_a_phase_outside_0_to_2_is_refused(int phase)
    {
        var ex = Assert.Throws<System.ArgumentException>(() => ThreePhaseSigmaChecks.Require(
            "test", new[] { new ThreePhaseVMag(2, phase, 1.0, 0.01) }, null));
        Assert.Contains("not 0, 1 or 2", ex.Message, System.StringComparison.Ordinal);
        Assert.Contains("bus 2", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_current_measurement_on_a_phase_outside_0_to_2_is_refused()
    {
        var ex = Assert.Throws<System.ArgumentException>(() => ThreePhaseSigmaChecks.Require(
            "test", null, new[] { new ThreePhaseCurrent(0, 1, 5, Complex.One, 0.01) }));
        Assert.Contains("branch 0->1", ex.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_phases_with_usable_sigmas_pass()
        => ThreePhaseSigmaChecks.Require("test", new[] { new ThreePhaseVMag(2, 0, 1.0, 0.01), new ThreePhaseVMag(2, 2, 1.0, 0.01) },
            new[] { new ThreePhaseCurrent(0, 1, 1, Complex.One, 0.01) });
}
