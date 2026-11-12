using System;
using System.IO;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-076 (November 2026): every Slack bus is an angle reference held at its case-file angle, so a second slack in one
/// island was a hard angle constraint the WLS estimate was forced to whatever the telemetry said. WLS state estimation now
/// refuses such a model by name before any work. Slacks in separate islands (joined only by an out-of-service branch,
/// or not at all) are one per island and are accepted. The Kalman filters keep their own rule (B185).
/// </summary>
public sealed class Gr076SlacksSharingAnIslandTests
{
    private static GridModel Case9WithBus2AsSlack()
    {
        GridModel published = GridSim.Core.IO.IeeeCases.Case9();
        return published.CopyWith(buses: published.Buses
            .Select(b => b.Id == 2 ? b with { Type = BusType.Slack } : b).ToArray());
    }

    [Fact]
    public void Wls_refuses_two_slacks_in_one_island_naming_both()
    {
        GridModel published = GridSim.Core.IO.IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(published);
        GridModel twoSlack = Case9WithBus2AsSlack();
        MeasurementSet exact = WlsEstimatorTests.ExactFullSet(twoSlack, truth);

        var ex = Assert.Throws<InvalidDataException>(() => new WlsStateEstimator().Estimate(twoSlack, exact));

        Assert.Contains("WLS state estimation", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Slack buses 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains(" and 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("same island", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void One_slack_per_island_is_accepted()
    {
        var model = new GridModel
        {
            BaseMva = 100,
            Name = "two-islands",
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq), new Bus(3, BusType.Slack), new Bus(4, BusType.Pq) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1), new Branch(3, 4, 0.01, 0.1) },
            Generators = Array.Empty<Generator>(),
        };
        MeasurementModel.RefuseSlacksSharingAnIsland(model, "test");   // does not throw
    }

    [Fact]
    public void An_out_of_service_branch_does_not_join_two_islands()
    {
        var model = new GridModel
        {
            BaseMva = 100,
            Name = "open-tie",
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq), new Bus(3, BusType.Slack) },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1) with { InService = false } },
            Generators = Array.Empty<Generator>(),
        };
        MeasurementModel.RefuseSlacksSharingAnIsland(model, "test");   // does not throw

        GridModel closed = model.CopyWith(branches: new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1) });
        var ex = Assert.Throws<InvalidDataException>(() => MeasurementModel.RefuseSlacksSharingAnIsland(closed, "test"));
        Assert.Contains("Slack buses 1 and 3", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_named_slack_is_described_by_id_and_name()
    {
        var model = new GridModel
        {
            BaseMva = 100,
            Name = "named",
            Buses = new[] { new Bus(1, BusType.Slack, Name: "North"), new Bus(2, BusType.Slack, Name: "South") },
            Branches = new[] { new Branch(1, 2, 0.01, 0.1) },
            Generators = Array.Empty<Generator>(),
        };
        var ex = Assert.Throws<InvalidDataException>(() => MeasurementModel.RefuseSlacksSharingAnIsland(model, "test"));
        Assert.Contains("1 ('North') and 2 ('South')", ex.Message, StringComparison.Ordinal);
    }
}
