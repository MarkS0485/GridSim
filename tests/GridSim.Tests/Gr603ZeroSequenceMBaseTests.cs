using System.Numerics;
using GridSim.Core.Model;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-603: every near-generator IEC 60909 path refuses a machine whose MBase is not a finite number above 0 (RatedMva),
/// except the zero-sequence network, which took a machine's own zero-sequence impedance on the SYSTEM base when MBase
/// was 0 or less (and turned a NaN MBase into a NaN shunt). It now goes through the same refusal. A unit with a valid
/// MBase is scaled exactly as before.
/// </summary>
public class Gr603ZeroSequenceMBaseTests
{
    private static GridModel OneMachine(double mbase) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 10, BaseKv: 132) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        Generators = new[]
        {
            new Generator(1, 10, MBase: mbase, Name: "G1") { ZeroSequenceZPu = new MachineZeroSequence(0.01, 0.05) },
        },
    };

    [Theory]
    [InlineData(0.0)]
    [InlineData(-50.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Gr603_a_grounded_machine_without_a_valid_mbase_is_refused_by_name(double mbase)
    {
        var ex = Assert.ThrowsAny<Exception>(() => Iec60909.ZeroSequenceThevenin(OneMachine(mbase), 1));
        Assert.Contains("MBase", ex.Message);
        Assert.Contains("G1", ex.Message);
    }

    [Fact]
    public void Gr603_a_valid_mbase_still_gives_a_grounded_finite_zero_sequence_impedance()
    {
        (Complex z0, _, bool grounded) = Iec60909.ZeroSequenceThevenin(OneMachine(200.0), 1);
        Assert.True(grounded);
        Assert.True(double.IsFinite(z0.Real) && double.IsFinite(z0.Imaginary));
    }
}
