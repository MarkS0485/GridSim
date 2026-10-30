using System.Diagnostics;
using GridSim.Core.IO;
using GridSim.Core.Layout;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Verifies the deterministic auto-layout: coordinate-less IEEE/MATPOWER cases get a
/// drawable set of coordinates inside the 0..1 box, positions are distinct and stable
/// across calls, models that already carry coordinates are left untouched, and very
/// large graphs still return quickly via the fast fallback.
/// </summary>
public class GraphLayoutTests
{
    private static GridModel Model(string name) => name switch
    {
        "9" => IeeeCases.Case9(),
        "30" => IeeeCases.Case30(),
        "118" => IeeeCases.Case118(),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [InlineData("9")]
    [InlineData("30")]
    [InlineData("118")]
    public void Imported_cases_have_no_layout(string name)
    {
        // MATPOWER/IEEE imports leave every bus at the origin.
        Assert.True(GraphLayout.HasNoLayout(Model(name)));
    }

    [Theory]
    [InlineData("9")]
    [InlineData("30")]
    [InlineData("118")]
    public void EnsureLayout_places_all_buses_in_unit_box(string name)
    {
        var laid = GraphLayout.EnsureLayout(Model(name));

        Assert.Equal(Model(name).BusCount, laid.BusCount);
        foreach (Bus b in laid.Buses)
        {
            Assert.InRange(b.X, 0.0, 1.0);
            Assert.InRange(b.Y, 0.0, 1.0);
        }
        // The model now has a layout.
        Assert.False(GraphLayout.HasNoLayout(laid));
    }

    [Theory]
    [InlineData("9")]
    [InlineData("30")]
    [InlineData("118")]
    public void EnsureLayout_positions_are_distinct(string name)
    {
        var laid = GraphLayout.EnsureLayout(Model(name));

        var seen = new HashSet<(double, double)>();
        foreach (Bus b in laid.Buses)
            Assert.True(seen.Add((b.X, b.Y)), $"duplicate position at bus {b.Id}");

        // And crucially not all stacked at the origin.
        Assert.Contains(laid.Buses, b => b.X != 0 || b.Y != 0);
    }

    [Theory]
    [InlineData("9")]
    [InlineData("30")]
    [InlineData("118")]
    public void EnsureLayout_is_deterministic(string name)
    {
        var a = GraphLayout.EnsureLayout(Model(name));
        var b = GraphLayout.EnsureLayout(Model(name));

        Assert.Equal(a.BusCount, b.BusCount);
        for (int i = 0; i < a.BusCount; i++)
        {
            Assert.Equal(a.Buses[i].X, b.Buses[i].X);   // byte-identical
            Assert.Equal(a.Buses[i].Y, b.Buses[i].Y);
        }
    }

    [Fact]
    public void EnsureLayout_leaves_already_positioned_model_unchanged()
    {
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, X: 0.2, Y: 0.3),
                new Bus(2, BusType.Pq,    X: 0.7, Y: 0.8),
            },
            Generators = Array.Empty<Generator>(),
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
        };

        Assert.False(GraphLayout.HasNoLayout(m));
        var result = GraphLayout.EnsureLayout(m);

        Assert.Same(m, result);                 // returned unchanged (same reference)
        Assert.Equal(0.2, result.Buses[0].X);
        Assert.Equal(0.3, result.Buses[0].Y);
        Assert.Equal(0.7, result.Buses[1].X);
        Assert.Equal(0.8, result.Buses[1].Y);
    }

    [Fact]
    public void EnsureLayout_disconnected_components_do_not_overlap_at_origin()
    {
        // Two separate islands, no branch between them.
        var m = new GridModel
        {
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack),
                new Bus(2, BusType.Pq),
                new Bus(3, BusType.Slack),
                new Bus(4, BusType.Pq),
            },
            Generators = Array.Empty<Generator>(),
            Branches = new[]
            {
                new Branch(1, 2, 0.01, 0.1),
                new Branch(3, 4, 0.01, 0.1),
            },
        };

        var laid = GraphLayout.EnsureLayout(m);
        var seen = new HashSet<(double, double)>();
        foreach (Bus b in laid.Buses)
        {
            Assert.InRange(b.X, 0.0, 1.0);
            Assert.InRange(b.Y, 0.0, 1.0);
            Assert.True(seen.Add((b.X, b.Y)), "islands overlap");
        }
    }
}

/// <summary>
/// GR-881: the large-graph layout test asserts a wall-clock bound (3,000 ms). Run inside the parallel suite, that bound
/// measured the machine's load as much as the layout, so it sits in its own non-parallel collection: xUnit runs it with
/// no other test alongside, and the timing is the layout's own. The assertions are unchanged.
/// </summary>
[Collection(GraphLayoutTimingTestGroup.Name)]
public class GraphLayoutTimingTests
{
    [Fact]
    public void EnsureLayout_large_graph_is_fast_and_in_box()
    {
        // A synthetic ladder graph well past the FR->ring fallback threshold.
        const int n = 5000;
        var buses = new Bus[n];
        buses[0] = new Bus(1, BusType.Slack);
        for (int i = 1; i < n; i++)
            buses[i] = new Bus(i + 1, BusType.Pq);

        var branches = new List<Branch>();
        for (int i = 0; i < n - 1; i++)
            branches.Add(new Branch(i + 1, i + 2, 0.01, 0.1));   // a long chain
        // A few cross-links so it isn't a pure line.
        for (int i = 0; i + 100 < n; i += 100)
            branches.Add(new Branch(i + 1, i + 101, 0.01, 0.1));

        var m = new GridModel
        {
            BaseMva = 100,
            Buses = buses,
            Generators = Array.Empty<Generator>(),
            Branches = branches.ToArray(),
        };

        var sw = Stopwatch.StartNew();
        var laid = GraphLayout.EnsureLayout(m);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"large-graph layout took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(n, laid.BusCount);
        foreach (Bus b in laid.Buses)
        {
            Assert.InRange(b.X, 0.0, 1.0);
            Assert.InRange(b.Y, 0.0, 1.0);
        }
    }
}

/// <summary>GR-881: the non-parallel collection for the wall-clock layout test.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GraphLayoutTimingTestGroup
{
    /// <summary>The collection name.</summary>
    public const string Name = "Graph layout timing (runs alone)";
}
