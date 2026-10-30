using GridSim.Core.Dynamics;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>N158 (the N094 remainder): the CLI trip verb's two hand-built post-trip model copies are one shared helper,
/// PostTripModel.Without, which keeps out-of-service units and carries every property. The in-service fleet (what every
/// engine reads) is exactly the old copies' list, so no trip number moves.</summary>
public sealed partial class HHygieneTests
{
    /// <summary>A small model with one out-of-service unit between in-service ones.</summary>
    private static GridModel ModelWithOutOfServiceUnit()
    {
        var gens = new List<Generator>
        {
            new(1, 300, MBase: 400, H: 5, Name: "A"),
            new(2, 200, MBase: 250, H: 4, Name: "B-out", InService: false),
            new(2, 150, MBase: 200, H: 3, Name: "C"),
            new(3, 100, MBase: 120, H: 6, Name: "D"),
        };
        return new GridModel
        {
            BaseMva = 100,
            Buses = new List<Bus>
            {
                new(1, BusType.Slack, BaseKv: 400), new(2, BusType.Pv, BaseKv: 400), new(3, BusType.Pq, Pd: 500, BaseKv: 400),
            },
            Branches = new List<Branch> { new(1, 2, 0.01, 0.1), new(2, 3, 0.01, 0.1) },
            Generators = gens,
            FrequencyHz = 50.0,
            Name = "n158",
        };
    }

    [Fact]
    public void N158_post_trip_copy_has_exactly_the_old_in_service_list_and_keeps_out_of_service_units()
    {
        GridModel m = ModelWithOutOfServiceUnit();
        Generator tripped = m.Generators.Single(g => g.Name == "C");
        // The old hand-built copy's generator list, verbatim.
        List<Generator> old = m.Generators.Where(g => !ReferenceEquals(g, tripped)).ToList();

        GridModel post = PostTripModel.Without(m, tripped);
        Assert.Equal(old.Count, post.Generators.Count);
        for (int i = 0; i < old.Count; i++) Assert.Same(old[i], post.Generators[i]);   // same units, same order
        Assert.Contains(post.AllGenerators, g => g.Name == "B-out");                    // the N158 fix
        Assert.DoesNotContain(post.AllGenerators, g => ReferenceEquals(g, tripped));
        // Every other property the old copies set is the same object or value.
        Assert.Same(m.Buses, post.Buses);
        Assert.Same(m.Branches, post.Branches);
        Assert.Same(m.Oltcs, post.Oltcs);
        Assert.Same(m.Boundaries, post.Boundaries);
        Assert.Equal(m.BaseMva, post.BaseMva);
        Assert.Equal(m.FrequencyHz, post.FrequencyHz);
        Assert.Equal(m.Name, post.Name);
        Assert.Same(m.AssetDynamics, post.AssetDynamics);
        Assert.Same(m.Protections, post.Protections);
        // The inertia the swing sees is unchanged (only in-service units count).
        Assert.Equal(old.Sum(g => g.H * g.MBase), post.Generators.Sum(g => g.H * g.MBase));
    }

    [Fact]
    public void N158_a_unit_the_model_does_not_run_is_refused()
    {
        GridModel m = ModelWithOutOfServiceUnit();
        Generator outOfService = m.AllGenerators.Single(g => g.Name == "B-out");
        Assert.Throws<ArgumentException>(() => PostTripModel.Without(m, outOfService));
        Assert.Throws<ArgumentException>(() => PostTripModel.Without(m, new Generator(1, 300, MBase: 400, H: 5, Name: "A")));
    }

    [Fact]
    public void N158_the_cli_trip_verb_uses_the_shared_copy_twice()
    {
        string cli = PresentationConstantsAntiDriftTests.Source("src", "GridSim.Cli", "Program.cs");
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(cli, @"PostTripModel\.Without\(model, gen\)"));
        Assert.DoesNotContain("Generators = model.Generators.Where(g => !ReferenceEquals(g, gen)).ToList()", cli);
    }
}
