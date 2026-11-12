using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-661 (test only): a cost file addresses units by 1-based index in FILE order, out-of-service units included. With an
/// out-of-service unit in the middle of the fleet no test pinned the combination. Pinned here: index 3 lands on the third
/// unit in file order (not on the second in-service unit by accident of count), index 2 lands on the out-of-service unit
/// and is kept there, the fleet keeps its file order, the in-service list still has two units, and addressing by name gives
/// the same result as by index.
/// </summary>
public class Gr661CostFileOutOfServiceMidFleetTests
{
    private static GridModel Fleet() => new()
    {
        Name = "gr661",
        BaseMva = 100.0,
        Buses = new List<Bus>
        {
            new(1, BusType.Slack, BaseKv: 132.0, Name: "A"),
            new(2, BusType.Pq, Pd: 40.0, BaseKv: 132.0, Name: "B"),
        },
        Generators = new List<Generator>
        {
            new(1, Pg: 50.0, Vg: 1.0, Qmin: -50.0, Qmax: 50.0, Pmin: 0, Pmax: 100.0, MBase: 100.0, Name: "G1"),
            new(2, Pg: 0.0, Vg: 1.0, Qmin: -50.0, Qmax: 50.0, Pmin: 0, Pmax: 100.0, MBase: 100.0, Name: "G2 off", InService: false),
            new(2, Pg: 10.0, Vg: 1.0, Qmin: -50.0, Qmax: 50.0, Pmin: 0, Pmax: 100.0, MBase: 100.0, Name: "G3"),
        },
        Branches = new List<Branch> { new(1, 2, 0.01, 0.05, 0.0, RateAMva: 200.0, Name: "L12") },
        Oltcs = new List<Oltc>(),
        Boundaries = new List<Boundary>(),
    };

    private const string ByIndex = """
        { "format": "gridsim-costs/1", "generators": [
          { "index": 1, "model": "polynomial", "coefficients": [0.1, 10, 100] },
          { "index": 2, "model": "polynomial", "coefficients": [0.2, 20, 200] },
          { "index": 3, "model": "polynomial", "coefficients": [0.3, 30, 300] } ] }
        """;

    private const string ByName = """
        { "format": "gridsim-costs/1", "generators": [
          { "name": "G1", "model": "polynomial", "coefficients": [0.1, 10, 100] },
          { "name": "G2 off", "model": "polynomial", "coefficients": [0.2, 20, 200] },
          { "name": "G3", "model": "polynomial", "coefficients": [0.3, 30, 300] } ] }
        """;

    [Theory]
    [InlineData(ByIndex)]
    [InlineData(ByName)]
    public void Gr661_costs_land_on_their_units_in_file_order_with_an_out_of_service_unit_in_the_middle(string json)
    {
        GridModel m = GeneratorCostFile.Apply(Fleet(), json, "test");
        Assert.Equal(new[] { "G1", "G2 off", "G3" }, m.AllGenerators.Select(g => g.Name).ToArray());
        Assert.Equal(new[] { 0.1, 10.0, 100.0 }, m.AllGenerators[0].Cost!.Coefficients.ToArray());
        Assert.Equal(new[] { 0.2, 20.0, 200.0 }, m.AllGenerators[1].Cost!.Coefficients.ToArray());
        Assert.Equal(new[] { 0.3, 30.0, 300.0 }, m.AllGenerators[2].Cost!.Coefficients.ToArray());
        Assert.False(m.AllGenerators[1].InService);
        // The in-service list is the two live units, and each still carries its own curve.
        Assert.Equal(new[] { "G1", "G3" }, m.Generators.Select(g => g.Name).ToArray());
        Assert.Equal(new[] { 0.3, 30.0, 300.0 }, m.Generators[1].Cost!.Coefficients.ToArray());
    }

    [Fact]
    public void Gr661_a_cost_on_only_the_third_unit_does_not_reach_the_out_of_service_unit()
    {
        const string json = """{ "format": "gridsim-costs/1", "generators": [ { "index": 3, "model": "polynomial", "coefficients": [1, 2, 3] } ] }""";
        GridModel m = GeneratorCostFile.Apply(Fleet(), json, "test");
        Assert.Null(m.AllGenerators[0].Cost);
        Assert.Null(m.AllGenerators[1].Cost);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, m.AllGenerators[2].Cost!.Coefficients.ToArray());
    }
}
