using GridSim.Core.Layout;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R273: <see cref="GraphLayout.EnsureLayout"/> laid out out-of-service branches as edges (its comment claimed the
/// model had no out-of-service flag), so an opened tie pulled two separate islands into one component. An opened
/// branch must now be invisible to the layout: the result is bit-identical to the same model without that branch.
/// </summary>
public class GraphLayoutOutOfServiceTests
{
    private static GridModel TwoIslands(Branch? tie)
    {
        var branches = new List<Branch>
        {
            new(1, 2, 0.01, 0.1),
            new(2, 3, 0.01, 0.1),
            new(4, 5, 0.01, 0.1),
            new(5, 6, 0.01, 0.1),
        };
        if (tie is not null) branches.Add(tie);
        return new GridModel
        {
            BaseMva = 100,
            Buses = Enumerable.Range(1, 6).Select(i => new Bus(i, i == 1 ? BusType.Slack : BusType.Pq)).ToArray(),
            Generators = Array.Empty<Generator>(),
            Branches = branches,
        };
    }

    [Fact]
    public void An_opened_tie_lays_out_exactly_as_if_it_were_absent()
    {
        GridModel opened = GraphLayout.EnsureLayout(TwoIslands(new Branch(3, 4, 0.01, 0.1, InService: false)));
        GridModel absent = GraphLayout.EnsureLayout(TwoIslands(null));

        Assert.Equal(absent.BusCount, opened.BusCount);
        for (int i = 0; i < absent.BusCount; i++)
        {
            Assert.Equal(absent.Buses[i].X, opened.Buses[i].X);   // exact: the opened branch contributes nothing
            Assert.Equal(absent.Buses[i].Y, opened.Buses[i].Y);
        }
    }

    [Fact]
    public void An_in_service_tie_still_joins_the_islands()
    {
        // Sanity: the same tie IN service changes the layout (one component instead of two), so the test above is
        // not passing because ties are ignored altogether.
        GridModel closed = GraphLayout.EnsureLayout(TwoIslands(new Branch(3, 4, 0.01, 0.1)));
        GridModel absent = GraphLayout.EnsureLayout(TwoIslands(null));
        bool anyDifferent = false;
        for (int i = 0; i < absent.BusCount; i++)
            if (absent.Buses[i].X != closed.Buses[i].X || absent.Buses[i].Y != closed.Buses[i].Y) anyDifferent = true;
        Assert.True(anyDifferent);
    }
}
