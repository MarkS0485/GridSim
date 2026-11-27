using System.Numerics;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Bug hunt of 2026-11-25, batch 5: the review of batch 4 (REVIEW-B5.md). R5-01 is asserted in the Rc5Wp17/Wp18 bit
/// identity tests, R5-02 in BugHunt1125RefusalExitTests, R5-03 in EmtUntappedBitIdentityTests; the rest is here.
/// </summary>
public sealed class BugHunt1125Review5Tests
{
    [Fact]
    public void R5_05_a_converter_dispatched_at_its_limit_starts_whichever_side_of_it_the_solve_lands()
    {
        var par = new RmsConverterParameters();   // Imax 1.2 on a rating of 1 pu
        double at = par.Imax;
        // A few ulps above the limit, as a solve of a unit dispatched at it can give: accepted.
        double above = Math.BitIncrement(Math.BitIncrement(Math.BitIncrement(Math.BitIncrement(at))));
        _ = new RmsConverterState(new RmsConverterUnit(4, "AT", 1.0, above, 0.0, Complex.One, par));
        _ = new RmsConverterState(new RmsConverterUnit(4, "AT", 1.0, at, 0.0, Complex.One, par));
        // Clearly above (1e-6 relative): refused as before.
        Assert.Throws<ArgumentOutOfRangeException>(() => new RmsConverterState(new RmsConverterUnit(4, "OVER", 1.0, at * (1.0 + 1e-6), 0.0, Complex.One, par)));
    }

    [Fact]
    public void R5_06_the_largest_travel_time_rounding_is_reported()
    {
        const double dt = 10e-6;
        var elements = new List<EmtElement>
        {
            new VoltageSource(1, 0, Rs: 100.0, Emf: _ => 1.0),
            new BergeronLine(2, 0, 1, 100.0, 1.4 * dt),   // one step: 28.6 % shorter
            new BergeronLine(3, 1, 2, 100.0, 5.0 * dt),   // exact
            new ShuntElement(4, 2, G: 0.01),
        };
        var e = new EmtEngine(new EmtNetwork(3, elements), dt);
        Assert.Equal(2, e.LargestTravelTimeRoundingLine);
        Assert.Equal(0.4 / 1.4, e.LargestTravelTimeRounding, 12);
        Assert.True(e.LargestTravelTimeRounding > EmtEngine.TravelTimeRoundingNoteThreshold);
        var exact = new EmtEngine(new EmtNetwork(3, new List<EmtElement> { elements[0], new BergeronLine(2, 0, 1, 100.0, 3.0 * dt), elements[2], elements[3] }), dt);
        Assert.True(exact.LargestTravelTimeRounding < 1e-12);
    }

    [Fact]
    public void R5_12_devices_that_nearly_cancel_a_varying_zip_load_are_refused()
    {
        var bus = new Bus(2, BusType.Pq, Pd: 3.0, Qd: 1.0, Zip: new ZipLoad(Ap: 1, Bp: 0, Cp: 0, Aq: 1, Bq: 0, Cq: 0));
        var ex = Assert.Throws<InvalidOperationException>(() => QstsDeviceDemand.WithConstantPowerDevices(bus, 0.0, 3.0 * (1.0 - 1e-12), 0.0, 0.0, 0.0));
        Assert.Contains("cancel the real load exactly", ex.Message, StringComparison.Ordinal);
        // A clear margin (1e-6 of the load) is re-based as before.
        Bus b = QstsDeviceDemand.WithConstantPowerDevices(bus, 0.0, 3.0 * (1.0 - 1e-6), 0.0, 0.0, 0.0);
        Assert.Equal(3.0 * 0.95 * 0.95 - 3.0 * (1.0 - 1e-6), b.Pd * b.Zip!.PMultiplier(0.95), 9);
    }
}
