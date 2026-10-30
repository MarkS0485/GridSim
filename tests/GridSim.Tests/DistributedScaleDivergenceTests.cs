using System;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// T5 (fail-loud gate) - the scale-divergence report. On a multi-domain case with no GB-scale reference,
/// the distributed model diverges from the lumped model in a BOUNDED, mechanism-attributed way: the COI
/// stays put (machine-eps, no propagation) while a real per-domain spatial spread appears. The report is
/// tagged <see cref="ProvenanceTag.UnvalidatedAtScale"/>; nothing asserts the distributed result is more accurate.
/// </summary>
public class DistributedScaleDivergenceTests
{
    private static GridModel TwoDomainModel() => new()
    {
        BaseMva = 100,
        Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq) },
        Generators = new Generator[]
        {
            new(Bus: 1, Pg: 800, MBase: 1000, H: 5.0, Type: "ccgt"),
            new(Bus: 2, Pg: 400, MBase: 500,  H: 3.0, Type: "hydro"),
        },
        Branches = Array.Empty<Branch>(),
    };

    [Fact]
    public void Scale_divergence_is_bounded_mechanism_attributed_and_unvalidated()
    {
        var loss = new FrequencyEvent[] { new(1.0, -300.0) };
        var model = TwoDomainModel();
        FrequencyResponse lumped = FrequencyDynamics.Simulate(model.SystemInertiaGvaS * 1000.0, 1200, loss);

        var dm = new DistributedInertiaModel();
        FrequencyResponse coi = dm.Simulate(model, 1200, loss, new DynamicsConfig { InertiaModel = InertiaModel.Distributed });

        DivergenceReport report = DivergenceReport.Compute(lumped, coi, dm.LastDistributedResult!);

        // COI is preserved (no propagation) - the divergence lives in the spatial spread, not the aggregate.
        Assert.True(report.CoiNadirDeltaHz < 1e-6, $"COI must be preserved; delta={report.CoiNadirDeltaHz:E3}");
        // A real, bounded spatial spread exists (the information a lumped scalar cannot show).
        Assert.True(report.PerDomainNadirSpreadHz > 1e-3, "domains must diverge spatially");
        Assert.True(report.PerDomainNadirSpreadHz < 5.0, "spread must be bounded");
        // Attributed to a named mechanism and honestly tagged - no accuracy claim.
        Assert.False(string.IsNullOrWhiteSpace(report.Mechanism));
        Assert.Equal(ProvenanceTag.UnvalidatedAtScale, report.Provenance);
    }
}
