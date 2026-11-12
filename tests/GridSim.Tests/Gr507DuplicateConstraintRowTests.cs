using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-507: two identical hard zero-injection rows make the KKT constraint block singular. The Constrained method used to
/// report such a set Unobservable although the telemetry observes every state; it now refuses the set by name before
/// any arithmetic. The Feasible method keeps its re-gate (Infeasible with an observable report, pinned by
/// EstimationRobustnessFixTests), and a set with one row per quantity is untouched.
/// </summary>
public class Gr507DuplicateConstraintRowTests
{
    private static (GridModel Model, List<Measurement> Rows, MeasurementSet Full) Case14()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        MeasurementSet full = WlsEstimatorTests.ExactFullSet(m, truth);
        return (m, full.Measurements.ToList(), full);
    }

    [Fact]
    public void Gr507_the_constrained_method_refuses_a_duplicate_zero_injection_row_by_name()
    {
        (GridModel m, List<Measurement> ms, MeasurementSet full) = Case14();
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));

        var ex = Assert.Throws<InvalidOperationException>(() => new WlsStateEstimator().Estimate(
            m, full with { Measurements = ms }, new EstimationOptions { Method = EstimationMethod.Constrained }));
        Assert.Contains("PInjMw at bus 7", ex.Message, StringComparison.Ordinal);
        Assert.Contains("virtual-zero-injection", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"measurements {ms.Count - 2} and {ms.Count - 1}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Gr507_one_p_row_and_one_q_row_at_a_bus_are_not_duplicates()
    {
        (GridModel m, List<Measurement> ms, MeasurementSet full) = Case14();
        ms.RemoveAll(x => x.Bus == 7 && x.Kind is MeasurementKind.PInjMw or MeasurementKind.QInjMvar);
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));
        ms.Add(new Measurement(MeasurementKind.QInjMvar, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));

        EstimationResult est = new WlsStateEstimator().Estimate(
            m, full with { Measurements = ms }, new EstimationOptions { Method = EstimationMethod.Constrained });
        Assert.Equal(EstimationStatus.Converged, est.Status);
    }

    [Fact]
    public void Gr507_the_same_quantity_twice_as_a_free_row_is_not_refused()
    {
        // Only HARD constraint rows are refused: two ordinary meters on one quantity are redundancy, not a duplicate.
        (GridModel m, List<Measurement> ms, MeasurementSet full) = Case14();
        Measurement p7 = ms.First(x => x.Bus == 7 && x.Kind == MeasurementKind.PInjMw);
        ms.Add(p7);
        EstimationResult est = new WlsStateEstimator().Estimate(
            m, full with { Measurements = ms }, new EstimationOptions { Method = EstimationMethod.Constrained });
        Assert.Equal(EstimationStatus.Converged, est.Status);
    }

    [Fact]
    public void Gr507_the_feasible_method_still_reports_infeasible_on_an_observable_set()
    {
        (GridModel m, List<Measurement> ms, MeasurementSet full) = Case14();
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));
        EstimationResult est = new WlsStateEstimator().Estimate(
            m, full with { Measurements = ms }, new EstimationOptions { Method = EstimationMethod.Feasible });
        Assert.Equal(EstimationStatus.Infeasible, est.Status);
        Assert.True(est.Observability.Observable);
    }
}
