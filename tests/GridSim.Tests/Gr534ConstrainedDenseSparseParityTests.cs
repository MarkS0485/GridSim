using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// GR-534: ch. 36 listed as a coverage gap that no test compared the dense and sparse estimator back ends EXACTLY on the
/// constrained and feasible gates with a free weight of 1e8 beside pseudo rows (the combination where the KKT equality
/// weighting and the relative rank floor are most stretched). This is that test: case14, hard zero-injection rows at
/// bus 7, a free VmPu row at sigma 1e-4 pu (weight 1e8) and weak pseudo-demand injection rows, solved by both back ends,
/// compared bit for bit on status, iterations, the state and J.
/// </summary>
public class Gr534ConstrainedDenseSparseParityTests
{
    private static MeasurementSet Set(GridModel m)
    {
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        MeasurementSet sampled = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = 1.0 });
        var ms = sampled.Measurements
            .Where(x => !(x.Bus == 7 && x.Kind is MeasurementKind.PInjMw or MeasurementKind.QInjMvar))
            .ToList();
        // Hard zero-injection equalities at bus 7 (case14's zero-injection bus).
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));
        ms.Add(new Measurement(MeasurementKind.QInjMvar, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));
        // A free row at weight 1e8 (sigma 1e-4 pu) on the slack voltage, at its true value.
        int slackIdx = truth.BusIds.ToList().IndexOf(m.Buses.First(b => b.Type == BusType.Slack).Id);
        ms.Add(new Measurement(MeasurementKind.VmPu, truth.Vm[slackIdx], 1e-4, Bus: truth.BusIds[slackIdx], Source: "tight-pmu"));
        // Weak pseudo rows: the case-file demand at three load buses, sigma 10 MW / 10 MVAr.
        foreach (int bus in new[] { 9, 13, 14 })
        {
            Bus b = m.Buses.Single(x => x.Id == bus);
            ms.Add(new Measurement(MeasurementKind.PInjMw, -b.Pd, 10.0, Bus: bus, Source: "pseudo-demand"));
            ms.Add(new Measurement(MeasurementKind.QInjMvar, -b.Qd, 10.0, Bus: bus, Source: "pseudo-demand"));
        }
        return sampled with { Measurements = ms };
    }

    private static void Bits(string what, double a, double b)
    {
        if (BitConverter.DoubleToInt64Bits(a) != BitConverter.DoubleToInt64Bits(b))
            Assert.Fail($"{what}: dense {a:R} vs sparse {b:R} (diff {a - b:E3})");
    }

    [Theory]
    [InlineData(EstimationMethod.Constrained)]
    [InlineData(EstimationMethod.Feasible)]
    public void Gr534_dense_and_sparse_back_ends_agree_bit_for_bit(EstimationMethod method)
    {
        GridModel m = IeeeCases.Case14();
        MeasurementSet set = Set(m);

        EstimationResult dense = new WlsStateEstimator().Estimate(m, set, new EstimationOptions { Method = method });
        EstimationResult sparse = new WlsStateEstimator().Estimate(m, set,
            new EstimationOptions { Method = method, SparseBusThreshold = 0 });

        Assert.Equal(dense.Status, sparse.Status);
        Assert.Equal(dense.Iterations, sparse.Iterations);
        Assert.Equal(dense.BusIds, sparse.BusIds);
        for (int i = 0; i < dense.Vm.Length; i++)
        {
            Bits($"{method} Vm[{i}]", dense.Vm[i], sparse.Vm[i]);
            Bits($"{method} VaDeg[{i}]", dense.VaDeg[i], sparse.VaDeg[i]);
        }
        Bits($"{method} J", dense.ObjectiveJ, sparse.ObjectiveJ);
        Assert.Equal(dense.Observability.Observable, sparse.Observability.Observable);
    }
}
