using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R087 (October 2026 review): the feasibility envelope's docs said VmMin &lt; VmMax was "validated at use"; nothing
/// validated it. <see cref="FeasibilityConstraints.Validate"/> now fails loud on an empty or meaningless envelope, and
/// the Feasible estimator calls it before solving.
/// </summary>
public class FeasibilityEnvelopeValidationTests
{
    [Fact]
    public void The_default_envelope_is_valid()
    {
        new FeasibilityConstraints().Validate();
    }

    [Theory]
    [InlineData(0.0, 0.9, 1.1, 1.5)]            // floor not positive
    [InlineData(1e-6, 1.1, 0.9, 1.5)]           // band inverted
    [InlineData(1e-6, 1.0, 1.0, 1.5)]           // band empty
    [InlineData(0.95, 0.9, 1.1, 1.5)]           // floor above the band
    [InlineData(1e-6, 0.9, double.PositiveInfinity, 1.5)]
    [InlineData(1e-6, 0.9, 1.1, 0.0)]           // angle limit not positive
    [InlineData(1e-6, 0.9, 1.1, 4.0)]           // angle limit beyond pi
    public void An_invalid_envelope_fails_loud(double floor, double vmin, double vmax, double angle)
    {
        var c = new FeasibilityConstraints { VmFloor = floor, VmMin = vmin, VmMax = vmax, AngleDiffMaxRad = angle };
        Assert.Throws<ArgumentException>(c.Validate);
    }

    [Fact]
    public void An_inverted_per_bus_band_fails_loud_naming_the_bus()
    {
        var c = new FeasibilityConstraints { PerBusVm = new Dictionary<int, (double, double)> { [7] = (1.05, 0.95) } };
        ArgumentException ex = Assert.Throws<ArgumentException>(c.Validate);
        Assert.Contains("bus 7", ex.Message);
    }

    [Fact]
    public void The_feasible_estimator_refuses_an_invalid_envelope_before_solving()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = SyntheticMeasurements.Sample(model, truth);
        var opt = new EstimationOptions
        {
            Method = EstimationMethod.Feasible,
            Feasibility = new FeasibilityConstraints { VmMin = 1.1, VmMax = 0.9 },
        };
        Assert.Throws<ArgumentException>(() => new WlsStateEstimator().Estimate(model, set, opt));
    }
}
