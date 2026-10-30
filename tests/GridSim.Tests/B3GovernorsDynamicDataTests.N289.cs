using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N289: GridModel.SystemInertiaGvaS summed H x MBase over every unit, converter types included, so a battery carrying a
/// synthetic-inertia H counted as rotating mass in every lumped engine while the distributed model (R239) left it out.
/// The lumped total now counts rotating mass only, by the same technology map, and equals the synchronous domains' energy.
/// </summary>
public sealed partial class B3GovernorsDynamicDataTests
{
    private static GridModel N289Model(params Generator[] gens) => new()
    {
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 400), new Bus(2, BusType.Pq, Pd: 300, BaseKv: 400) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        Generators = gens,
    };

    [Theory]
    [InlineData("storage")]
    [InlineData("battery")]
    [InlineData("bess")]
    [InlineData("wind-offshore")]
    [InlineData("solar")]
    [InlineData("interconnector")]
    [InlineData("hvdc")]
    public void N289_a_known_converter_type_adds_no_inertia_whatever_its_H(string type)
    {
        GridModel m = N289Model(
            new Generator(1, 200, MBase: 250, H: 4, Type: "ccgt"),
            new Generator(2, 100, MBase: 120, H: 5, Type: type));
        Assert.Equal(4.0 * 250.0 / 1000.0, m.SystemInertiaGvaS);
    }

    [Fact]
    public void N289_synchronous_and_unknown_types_still_count_exactly_as_before()
    {
        // Known synchronous types and unknown / blank types (the WSCC-style cases) keep their H; the sum runs over the
        // same terms in the same order as the old expression, so it is bit for bit the old total.
        Generator[] gens =
        {
            new(1, 200, MBase: 250, H: 4, Type: "ccgt"),
            new(1, 50, MBase: 60, H: 3.5, Type: "hydro"),
            new(2, 70, MBase: 100, H: 6.4, Type: ""),
            new(2, 10, MBase: 20, H: 2.2, Type: "other"),
            new(2, 30, MBase: 40, H: 3.0, Type: "pumped-storage"),
        };
        GridModel m = N289Model(gens);
        double old = gens.Sum(g => g.H * g.MBase) / 1000.0;   // the pre-N289 expression, verbatim
        AssertBits(old, m.SystemInertiaGvaS, "system inertia");
    }

    [Fact]
    public void N289_the_lumped_total_equals_the_synchronous_domains_energy_of_the_distributed_model()
    {
        GridModel m = N289Model(
            new Generator(1, 200, MBase: 250, H: 4, Type: "ccgt"),
            new Generator(1, 80, MBase: 100, H: 3, Type: "hydro"),
            new Generator(2, 100, MBase: 120, H: 5, Type: "battery"),
            new Generator(2, 60, MBase: 90, H: 2, Type: "wind-onshore"),
            new Generator(2, 40, MBase: 50, H: 2.5, Type: ""));
        (IReadOnlyList<SpeedDomain> domains, _) = DomainAssignment.Assign(m);
        double synchronous = domains.Where(d => d.Kind == DomainKind.Synchronous).Sum(d => d.AggregateInertiaMwS);
        Assert.Equal(synchronous / 1000.0, m.SystemInertiaGvaS, 12);
        Assert.Equal((4.0 * 250 + 3.0 * 100 + 2.5 * 50) / 1000.0, m.SystemInertiaGvaS, 12);
    }
}
