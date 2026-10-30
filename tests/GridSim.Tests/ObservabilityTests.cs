using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public class ObservabilityTests
{
    [Fact]
    public void A_fully_measured_case_reports_full_rank()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult est = new WlsStateEstimator().Estimate(model, WlsEstimatorTests.ExactFullSet(model, truth));

        ObservabilityReport obs = est.Observability;
        Assert.True(obs.Observable);
        Assert.Equal(obs.StateSize, obs.Rank);
        Assert.Equal(2 * model.BusCount - 1, obs.StateSize); // one slack: n-1 angles + n magnitudes
        Assert.Empty(obs.UnobservableBuses);
        Assert.Empty(obs.UnmeasuredIslandBuses);
    }

    [Fact]
    public void Voltage_only_measurements_leave_every_angle_unobservable()
    {
        GridModel model = IeeeCases.Case9();
        var ms = model.Buses
            .Select(b => new Measurement(MeasurementKind.VmPu, 1.0, 0.004, Bus: b.Id))
            .ToArray();
        var set = new MeasurementSet { Case = model.Name, BaseMva = model.BaseMva, Measurements = ms };

        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(EstimationStatus.Unobservable, est.Status);

        ObservabilityReport obs = est.Observability;
        Assert.False(obs.Observable);
        // All 8 non-slack angles are free; every magnitude is pinned directly.
        Assert.Equal(model.BusCount, obs.Rank);
        Assert.Equal(model.BusCount - 1, obs.UnobservableStates.Count);
        Assert.All(obs.UnobservableStates, s => Assert.StartsWith("Va@", s, StringComparison.Ordinal));
    }

    [Fact]
    public void An_unmeasured_island_is_reported_by_bus_id()
    {
        // Two electrical islands; telemetry only on the first.
        var model = new GridModel
        {
            BaseMva = 100,
            Name = "two-islands",
            Buses = new[]
            {
                new Bus(1, BusType.Slack),
                new Bus(2, BusType.Pq, Pd: 40),
                new Bus(3, BusType.Slack),
                new Bus(4, BusType.Pq, Pd: 25),
            },
            Generators = new[] { new Generator(1, 40), new Generator(3, 25) },
            Branches = new[]
            {
                new Branch(1, 2, 0.01, 0.1),
                new Branch(3, 4, 0.01, 0.1),
            },
        };
        var set = new MeasurementSet
        {
            Case = model.Name,
            Measurements = new[]
            {
                new Measurement(MeasurementKind.VmPu, 1.0, 0.004, Bus: 1),
                new Measurement(MeasurementKind.VmPu, 0.99, 0.004, Bus: 2),
                new Measurement(MeasurementKind.PInjMw, -40, 1.0, Bus: 2),
                new Measurement(MeasurementKind.QInjMvar, 0, 1.0, Bus: 2),
            },
        };

        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(EstimationStatus.Unobservable, est.Status);
        Assert.Equal(new[] { 3, 4 }, est.Observability.UnmeasuredIslandBuses);
        Assert.All(est.Vm, v => Assert.True(double.IsFinite(v)));
    }

    [Fact]
    public void Stripping_a_bus_of_all_electrical_visibility_names_it()
    {
        // Radial tail: 1(slack) -- 2 -- 3. Measure island fully EXCEPT anything electrically
        // adjacent to bus 3 (no injection at 2 or 3, no flow on 2-3): bus 3's state is free.
        var model = new GridModel
        {
            BaseMva = 100,
            Name = "radial-tail",
            Buses = new[]
            {
                new Bus(1, BusType.Slack),
                new Bus(2, BusType.Pq, Pd: 20),
                new Bus(3, BusType.Pq, Pd: 10),
            },
            Generators = new[] { new Generator(1, 30) },
            Branches = new[]
            {
                new Branch(1, 2, 0.01, 0.1),
                new Branch(2, 3, 0.01, 0.1),
            },
        };
        var set = new MeasurementSet
        {
            Case = model.Name,
            Measurements = new[]
            {
                new Measurement(MeasurementKind.VmPu, 1.0, 0.004, Bus: 1),
                new Measurement(MeasurementKind.VmPu, 0.99, 0.004, Bus: 2),
                new Measurement(MeasurementKind.PFlowMw, 20.5, 1.0, From: 1, To: 2),
                new Measurement(MeasurementKind.QFlowMvar, 3.0, 1.0, From: 1, To: 2),
            },
        };

        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        Assert.Equal(EstimationStatus.Unobservable, est.Status);
        Assert.Contains(3, est.Observability.UnobservableBuses);
        Assert.Contains("Va@bus 3", est.Observability.UnobservableStates);
        Assert.Contains("Vm@bus 3", est.Observability.UnobservableStates);
    }
}
