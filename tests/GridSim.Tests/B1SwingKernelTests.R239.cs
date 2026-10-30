using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R239: the "H > 0 means synchronous" override is for unknown or blank type strings only. It used to fire for every
/// type PoleMap mapped to zero poles, so a battery (or wind farm) whose data carried a synthetic-inertia H was merged into
/// the turbo-alternator domain as real rotating mass, against PoleMap and the "virtual inertia is never real mass" rule.
/// </summary>
public sealed partial class B1SwingKernelTests
{
    [Theory]
    [InlineData("storage", DomainKind.SyntheticInertia)]
    [InlineData("battery", DomainKind.SyntheticInertia)]
    [InlineData("bess", DomainKind.SyntheticInertia)]
    [InlineData("wind-onshore", DomainKind.Pll)]
    [InlineData("wind-offshore", DomainKind.Pll)]
    [InlineData("onshore", DomainKind.Pll)]
    [InlineData("offshore", DomainKind.Pll)]
    [InlineData("solar", DomainKind.Pll)]
    [InlineData("pv", DomainKind.Pll)]
    [InlineData("tidal", DomainKind.Pll)]
    [InlineData("interconnector", DomainKind.Pll)]
    public void R239_a_known_converter_type_with_H_stays_out_of_the_synchronous_domains(string type, DomainKind kind)
    {
        Assert.True(PoleMap.ForType(type).IsKnownType);
        GridModel m = new()
        {
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq) },
            Generators = new Generator[]
            {
                new(Bus: 1, Pg: 800, MBase: 1000, H: 5.0, Type: "ccgt"),
                new(Bus: 2, Pg: 100, MBase: 200, H: 2.0, Type: type),   // a synthetic-inertia figure in the data
            },
            Branches = new Branch[] { new(1, 2, R: 0.0, X: 0.05) },
        };
        (IReadOnlyList<SpeedDomain> domains, _) = DomainAssignment.Assign(m);
        SpeedDomain conv = domains.Single(d => d.MemberGenIndices.Contains(1));
        Assert.Equal(kind, conv.Kind);
        Assert.Equal(0, conv.Poles);
        Assert.False(conv.IsSwingMass);
        SpeedDomain turbo = domains.Single(d => d.MemberGenIndices.Contains(0));
        Assert.Equal(new[] { 0 }, turbo.MemberGenIndices);   // before R239 the converter was merged in here
        Assert.Equal(5000.0, turbo.AggregateInertiaMwS);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("other")]
    [InlineData("grid")]
    [InlineData("something-new")]
    public void R239_an_unknown_type_with_H_is_still_a_two_pole_synchronous_machine(string? type)
    {
        Assert.False(PoleMap.ForType(type).IsKnownType);
        GridModel m = new()
        {
            BaseMva = 100,
            Buses = new Bus[] { new(1, BusType.Slack) },
            Generators = new Generator[] { new(Bus: 1, Pg: 100, MBase: 200, H: 3.0, Type: type ?? string.Empty) },
            Branches = Array.Empty<Branch>(),
        };
        SpeedDomain d = Assert.Single(DomainAssignment.Assign(m).domains);
        Assert.Equal(2, d.Poles);
        Assert.Equal(DomainKind.Synchronous, d.Kind);
        Assert.True(d.IsSwingMass);
    }

    [Fact]
    public void R239_the_synchronous_types_are_unchanged()
    {
        foreach ((string t, int poles) in new[] { ("nuclear", 2), ("ccgt", 2), ("coal", 2), ("chp", 2), ("recip", 4), ("hydro", 8), ("pumped-storage", 8) })
        {
            PoleMap.MachineSpeed s = PoleMap.ForType(t);
            Assert.Equal(poles, s.Poles);
            Assert.Equal(DomainKind.Synchronous, s.Kind);
            Assert.True(s.IsKnownType);
        }
    }
}
