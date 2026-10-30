using System;
using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Dynamics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Model;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// T2 (fail-loud gate) - canonical dynamics for the physically-derived (Kron-reduced) inter-domain
/// coupling. Validated against closed-form results: the synchronising stiffness of a single line equals
/// base MVA / reactance; a stiffer line reduces inter-domain divergence; the COI invariant survives the
/// physical coupling; and the inter-domain electromechanical mode frequency matches the small-signal
/// formula omega^2 = pi*f0*K*(1/E1 + 1/E2).
///
/// (Absolute first-swing rotor-angle comparison vs published Anderson &amp; Fouad WSCC-9 / IEEE-39 curves
/// needs the per-machine classical model + a verified reference dataset and is deferred; the scipy
/// multimachine sidecar in tools/crossval provides the deeper cross-check.)
/// </summary>
public class CanonicalDynamicsTests
{
    private static GridModel TwoBusLine(double x) => new()
    {
        BaseMva = 100,
        Buses = new Bus[] { new(1, BusType.Slack), new(2, BusType.Pq) },
        Generators = new Generator[]
        {
            new(Bus: 1, Pg: 800, MBase: 1000, H: 5.0, Type: "ccgt"),   // domain 0: E = 5000 MW.s
            new(Bus: 2, Pg: 400, MBase: 500,  H: 3.0, Type: "hydro"),  // domain 1: E = 1500 MW.s
        },
        Branches = new Branch[] { new(1, 2, R: 0.0, X: x) },
    };

    [Fact]
    public void Kron_coupling_of_a_single_line_equals_base_over_reactance()
    {
        double x = 0.1;
        var model = TwoBusLine(x);
        (IReadOnlyList<SpeedDomain> domains, _) = DomainAssignment.Assign(model, new DistributedConfig());
        List<SpeedDomain> masses = domains.Where(d => d.IsSwingMass).ToList();
        double[,] k = DomainCoupling.SynchronisingMatrix(model, masses, new DistributedConfig());

        Assert.Equal(2, masses.Count);
        Assert.Equal(model.BaseMva / x, k[0, 1], 3);   // 1000 MW/rad
        Assert.Equal(k[0, 1], k[1, 0], 9);             // symmetric
    }

    [Fact]
    public void Stiffer_coupling_reduces_inter_domain_divergence()
    {
        var loss = new FrequencyEvent[] { new(1.0, -300.0) };
        double DivergenceFor(double x)
        {
            var m = new DistributedInertiaModel();
            m.Simulate(TwoBusLine(x), 1200, loss, new DynamicsConfig { InertiaModel = InertiaModel.Distributed });
            IReadOnlyList<DomainTrace> d = m.LastDistributedResult!.Domains;
            return Math.Abs(d[0].NadirHz - d[1].NadirHz);
        }
        double stiff = DivergenceFor(0.02);
        double weak = DivergenceFor(0.5);
        Assert.True(stiff < weak, $"stiffer coupling should reduce divergence: stiff={stiff:E4}, weak={weak:E4}");
    }

    [Fact]
    public void Coi_still_equals_lumped_with_physical_coupling()
    {
        var loss = new FrequencyEvent[] { new(1.0, -300.0) };
        var model = TwoBusLine(0.1);
        FrequencyResponse lumped = FrequencyDynamics.Simulate(model.SystemInertiaGvaS * 1000.0, 1200, loss);
        FrequencyResponse coi = new DistributedInertiaModel()
            .Simulate(model, 1200, loss, new DynamicsConfig { InertiaModel = InertiaModel.Distributed });

        double maxDiff = 0;
        for (int k = 0; k < lumped.Points.Count; k++)
            maxDiff = Math.Max(maxDiff, Math.Abs(lumped.Points[k].Hz - coi.Points[k].Hz));
        Assert.True(maxDiff < 1e-6, $"COI must equal lumped with Kron coupling; maxDiff={maxDiff:E3}");
    }

    [Fact]
    public void Inter_domain_mode_frequency_matches_the_small_signal_formula()
    {
        const double x = 0.1, f0 = 50.0, dt = 0.02;
        var model = TwoBusLine(x);
        (IReadOnlyList<SpeedDomain> domains, _) = DomainAssignment.Assign(model, new DistributedConfig());
        List<SpeedDomain> masses = domains.Where(d => d.IsSwingMass).ToList();
        double[,] k = DomainCoupling.SynchronisingMatrix(model, masses, new DistributedConfig());
        double e1 = masses[0].AggregateInertiaMwS, e2 = masses[1].AggregateInertiaMwS;
        double omega = Math.Sqrt(Math.PI * f0 * k[0, 1] * (1.0 / e1 + 1.0 / e2));
        double fAnalytic = omega / (2.0 * Math.PI);

        // Excite the mode with a small step, no governor/damping so the frequency difference oscillates
        // cleanly about zero.
        var cfg = new DynamicsConfig
        {
            InertiaModel = InertiaModel.Distributed,
            ResponsiveFraction = 0.0,
            DampingPctPerHz = 0.0,
            FastResponseMw = 0.0,
            DurationSeconds = 12.0,
        };
        var model2 = new DistributedInertiaModel();
        model2.Simulate(model, 1200, new FrequencyEvent[] { new(0.5, -50.0) }, cfg);
        IReadOnlyList<DomainTrace> d = model2.LastDistributedResult!.Domains;

        // Zero crossings of (f_domain0 - f_domain1) after the event.
        var crossings = new List<double>();
        int start = (int)(1.0 / dt);   // skip the first second (event at 0.5 s)
        for (int i = start; i < d[0].Points.Count - 1; i++)
        {
            double a = d[0].Points[i].Hz - d[1].Points[i].Hz;
            double b = d[0].Points[i + 1].Hz - d[1].Points[i + 1].Hz;
            if (a == 0.0 || (a < 0.0) != (b < 0.0))
            {
                double frac = a / (a - b);
                crossings.Add((i + frac) * dt);
            }
        }
        Assert.True(crossings.Count >= 4, $"need several oscillations to measure a period; got {crossings.Count}");
        double avgHalfPeriod = (crossings[^1] - crossings[0]) / (crossings.Count - 1);
        double fMeasured = 1.0 / (2.0 * avgHalfPeriod);

        Assert.True(Math.Abs(fMeasured - fAnalytic) / fAnalytic < 0.10,
            $"mode frequency: analytic={fAnalytic:F4} Hz, measured={fMeasured:F4} Hz");
    }
}
