using System.Linq;
using GridSim.Core.IO.Json;
using GridSim.Core.IO.Matpower;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// A cost file's <c>index</c> is a 1-based position in the case's generator list in FILE order, out-of-service units
/// included. Two things broke that: a redispatch copy (Dispatcher.Balance through CopyWith) appended the out-of-service
/// units at the end, and the MATPOWER parser skips a generator row that has no bus to sit on. Now CopyWith keeps each
/// unit in its file position, and a cost file refuses "index" on a case whose loader dropped generator rows.
/// </summary>
public class GeneratorFileOrderTests
{
    private static GridModel ThreeUnits() => new()
    {
        Name = "three units",
        BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 50), new Bus(3, BusType.Pv) },
        Generators = new[]
        {
            new Generator(1, 10, Pmax: 100, Name: "G1"),
            new Generator(3, 20, Pmax: 100, Name: "G2", InService: false),
            new Generator(3, 30, Pmax: 100, Name: "G3"),
        },
        Branches = new[] { new Branch(1, 2, 0, 0.1), new Branch(2, 3, 0, 0.1) },
    };

    [Fact]
    public void A_redispatch_copy_keeps_every_unit_in_its_file_position()
    {
        GridModel m = ThreeUnits();
        // A redispatch: the in-service list replaced one for one with new set-points.
        var redispatched = m.Generators.Select(g => g with { Pg = g.Pg + 1.0 }).ToList();
        GridModel copy = m.CopyWith(generators: redispatched);

        Assert.Equal(new[] { "G1", "G2", "G3" }, copy.AllGenerators.Select(g => g.Name).ToArray());
        Assert.Equal(new[] { 11.0, 20.0, 31.0 }, copy.AllGenerators.Select(g => g.Pg).ToArray());
        // The in-service list is exactly the list given, as before.
        Assert.Same(redispatched, copy.Generators);
    }

    [Fact]
    public void A_cost_file_index_lands_on_the_unit_in_that_file_position_after_a_redispatch()
    {
        GridModel m = ThreeUnits();
        GridModel copy = m.CopyWith(generators: m.Generators.Select(g => g with { Pg = g.Pg + 1.0 }).ToList());
        const string json = """
            { "format": "gridsim-costs/1", "generators": [
                { "index": 2, "model": "polynomial", "coefficients": [42, 0] } ] }
            """;
        GridModel costed = GeneratorCostFile.Apply(copy, json, "order.json");
        Generator g2 = costed.AllGenerators.Single(g => g.Name == "G2");
        Assert.NotNull(g2.Cost);
        Assert.Equal(new double[] { 42, 0 }, g2.Cost!.Coefficients);
        Assert.Null(costed.AllGenerators.Single(g => g.Name == "G3").Cost);
    }

    [Fact]
    public void A_cost_file_index_is_refused_when_the_matpower_loader_dropped_a_generator_row()
    {
        // The second mpc.gen row names bus 9, which mpc.bus never defines: the parser skips it, so row 3 becomes
        // AllGenerators position 2.
        const string mfile = @"function mpc = dropped
mpc.baseMVA = 100;
mpc.bus = [ 1 3 0 0 0 0 1 1 0 132 1 1.1 0.9; 2 1 50 10 0 0 1 1 0 132 1 1.1 0.9; ];
mpc.gen = [ 1 30 0 100 -100 1 100 1 100 0; 9 10 0 100 -100 1 100 1 100 0; 2 20 0 100 -100 1 100 1 100 0; ];
mpc.branch = [ 1 2 0 0.1 0 0 0 0 0 0 1 -360 360; ];";
        GridModel m = MatpowerParser.Parse(mfile);
        Assert.Equal(2, m.AllGenerators.Count);
        Assert.Equal(1, m.DroppedGeneratorRows);

        const string json = """
            { "format": "gridsim-costs/1", "generators": [
                { "index": 2, "model": "polynomial", "coefficients": [10, 0] } ] }
            """;
        var ex = Assert.Throws<InvalidDataException>(() => GeneratorCostFile.Apply(m, json, "costs.json"));
        Assert.Contains("skipped 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_matpower_case_that_keeps_every_row_reports_none_dropped()
    {
        GridModel m = GridSim.Core.IO.IeeeCases.Case14();
        Assert.Equal(0, m.DroppedGeneratorRows);
    }
}
