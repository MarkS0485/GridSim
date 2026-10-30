using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The sparse backend's observability diagnosis (PERF answers doc's deferred "sparse null-space"): within the
/// size cap the sparse estimator now names WHY a connected area is unobservable - the free state directions -
/// and distinguishes a metering-deficient bus from a topological island, matching the dense diagnosis, where
/// it previously reported only Rank = -1 plus the island sweep.
/// </summary>
public class SparseObservabilityDiagnosisTests
{
    // 3-bus radial: 1 (slack, generation) - 2 - 3. Measurements pin bus 1/2 fully but leave bus 3's ANGLE
    // free: nothing measures line 2-3's flow or a bus-3 injection, only bus 3's voltage magnitude. So Va@3 is
    // unobservable - yet bus 3 is connected and metered (Vm@3), so it is NOT an unmeasured island.
    private static (GridModel Model, MeasurementSet Set) MeteringDeficientCase()
    {
        var model = new GridModel
        {
            BaseMva = 100,
            Name = "metering-deficient",
            Buses = new[]
            {
                new Bus(1, BusType.Slack, Vm: 1.0),
                new Bus(2, BusType.Pq, Pd: 30, Qd: 10),
                new Bus(3, BusType.Pq, Pd: 20, Qd: 6),
            },
            Generators = new[] { new Generator(1, 50) },
            Branches = new[]
            {
                new Branch(1, 2, 0.02, 0.08),
                new Branch(2, 3, 0.03, 0.10),
            },
        };
        var set = new MeasurementSet
        {
            Case = model.Name,
            Measurements = new[]
            {
                new Measurement(MeasurementKind.VmPu, 1.00, 0.004, Bus: 1),
                new Measurement(MeasurementKind.VmPu, 0.98, 0.004, Bus: 2),
                new Measurement(MeasurementKind.VmPu, 0.97, 0.004, Bus: 3),   // bus 3 IS metered (not an island)
                new Measurement(MeasurementKind.PInjMw, 50, 1.0, Bus: 1),     // bus 1 injection = line 1-2 flow only
                new Measurement(MeasurementKind.QInjMvar, 15, 1.0, Bus: 1),
                // No bus-2 injection (it would see line 2-3) and nothing on line 2-3 => Va@3 is a free direction.
            },
        };
        return (model, set);
    }

    private static readonly EstimationOptions Sparse = new() { SparseBusThreshold = 0 };

    [Fact]
    public void Sparse_backend_names_the_free_direction_and_matches_the_dense_diagnosis()
    {
        (GridModel model, MeasurementSet set) = MeteringDeficientCase();

        EstimationResult dense = new WlsStateEstimator().Estimate(model, set);
        EstimationResult sparse = new WlsStateEstimator().Estimate(model, set, Sparse);

        // Both diagnose it as unobservable...
        Assert.Equal(EstimationStatus.Unobservable, dense.Status);
        Assert.Equal(EstimationStatus.Unobservable, sparse.Status);

        // ...and the sparse path now (within the cap) names the same free direction the dense path does -
        // Va@bus 3 - having previously reported nothing here (Rank = -1, empty UnobservableStates).
        Assert.Contains("Va@bus 3", dense.Observability.UnobservableStates);
        Assert.Contains("Va@bus 3", sparse.Observability.UnobservableStates);
        Assert.Equal(dense.Observability.Rank, sparse.Observability.Rank);
        Assert.True(sparse.Observability.Rank >= 0, "the sparse rank should be computed within the cap");
        Assert.Contains(3, sparse.Observability.UnobservableBuses);

        // Crucially: bus 3 is metering-deficient, NOT an unmeasured island (it carries Vm@3 and a branch).
        Assert.DoesNotContain(3, sparse.Observability.UnmeasuredIslandBuses);
        Assert.Empty(sparse.Observability.UnmeasuredIslandBuses);
    }

    [Fact]
    public void An_observable_area_reports_full_rank_on_the_sparse_path()
    {
        // Add the missing bus-3 injection so Va@3 becomes observable; the sparse path should now report full
        // rank and observability, not Rank = -1.
        (GridModel model, MeasurementSet set) = MeteringDeficientCase();
        var ms = set.Measurements.ToList();
        ms.Add(new Measurement(MeasurementKind.PInjMw, -20, 1.0, Bus: 3));
        ms.Add(new Measurement(MeasurementKind.QInjMvar, -6, 1.0, Bus: 3));
        MeasurementSet observable = set with { Measurements = ms };

        EstimationResult sparse = new WlsStateEstimator().Estimate(model, observable, Sparse);
        Assert.NotEqual(EstimationStatus.Unobservable, sparse.Status);
        Assert.Equal(sparse.Observability.StateSize, sparse.Observability.Rank);
        Assert.Empty(sparse.Observability.UnobservableStates);
    }
}
