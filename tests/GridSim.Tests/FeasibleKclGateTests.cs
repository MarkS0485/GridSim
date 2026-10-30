using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R011 / R012: the Feasible estimator's acceptance gate used to score Kirchhoff's current law (laws 6
/// and 8) against the case file's static Pg - Pd at 1e-4 MVA, so every real or noisy estimate was
/// demoted to Infeasible. The gate now audits KCL against the injections the estimate is physically
/// bound to (its hard zero-injection constraints) through <see cref="PhysicsLaws.AuditAgainst"/>; the
/// plain <see cref="PhysicsLaws.Audit"/> used for power-flow solutions is unchanged.
/// </summary>
public class FeasibleKclGateTests
{
    // IEEE 14-bus bus 7 carries no load and no generator: a genuine zero-injection (switching) node.
    private const int ZeroInjectionBus = 7;

    private static (GridModel Model, PowerFlowResult Truth) Case14Truth()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        Assert.True(truth.Converged);
        return (m, truth);
    }

    /// <summary>Nominal-noise synthetic telemetry with bus 7's injection replaced by hard zero-injection
    /// constraints - the realistic Feasible-method input.</summary>
    private static MeasurementSet NoisySetWithZeroInjection(GridModel m, PowerFlowResult truth, double noiseScale = 1.0)
    {
        MeasurementSet set = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = noiseScale });
        var ms = set.Measurements
            .Where(x => !(x.Bus == ZeroInjectionBus && x.Kind is MeasurementKind.PInjMw or MeasurementKind.QInjMvar))
            .ToList();
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 1.0, Bus: ZeroInjectionBus, Source: "virtual-zero-injection"));
        ms.Add(new Measurement(MeasurementKind.QInjMvar, 0.0, 1.0, Bus: ZeroInjectionBus, Source: "virtual-zero-injection"));
        return set with { Measurements = ms };
    }

    private static LawResult Law(PhysicsLawReport report, int number) => report.Laws.Single(l => l.Number == number);

    [Fact]
    public void Noisy_feasible_estimate_is_not_demoted_by_the_static_dispatch()
    {
        // The R011 failure scenario: nominal noise (NoiseScale 1). The estimate converges; under the old
        // gate its injections differ from the case file's Pd by about the 1 MW sigma and law 6 failed.
        (GridModel m, PowerFlowResult truth) = Case14Truth();
        MeasurementSet set = NoisySetWithZeroInjection(m, truth);

        EstimationResult est = new WlsStateEstimator().Estimate(
            m, set, new EstimationOptions { Method = EstimationMethod.Feasible });

        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.True(est.Converged);
        Assert.NotNull(est.Physics);
        Assert.True(est.Physics!.Gate(PhysicsCheckRegistry.HardGateLaws));

        // KCL was genuinely checked (not vacuous): at the one zero-injection bus, to the audit tolerance.
        LawResult kcl = Law(est.Physics, 6);
        Assert.Equal(LawStatus.Holds, kcl.Status);
        Assert.True(kcl.Residual <= new FeasibilityConstraints().AuditTolMva, $"zero-injection residual {kcl.Residual} MW");
        Assert.Contains("zero-injection", kcl.Detail);
        Assert.Equal(LawStatus.Holds, Law(est.Physics, 8).Status);

        // Proof the old gate would have rejected this very state: the plain static-dispatch audit of the
        // same projected state violates KCL by far more than 1e-4 MVA.
        PhysicsLawReport old = PhysicsLaws.Audit(m, est.ToPowerFlowResult(m), new FeasibilityConstraints().AuditTolMva);
        Assert.Equal(LawStatus.Violated, Law(old, 6).Status);
        Assert.True(Law(old, 6).Residual > 0.01, $"static-dispatch KCL residual {Law(old, 6).Residual} MW");
    }

    [Fact]
    public void Noisy_feasible_estimate_without_zero_injections_reports_KCL_not_applicable()
    {
        // No hard zero-injection rows: the estimate is bound to no nodal injection, so KCL has nothing to
        // compare and must say so (NotApplicable), never a vacuous Holds and never a static-dispatch Violated.
        (GridModel m, PowerFlowResult truth) = Case14Truth();
        MeasurementSet set = SyntheticMeasurements.Sample(m, truth, new SyntheticMeasurementOptions { NoiseScale = 1.0 });

        EstimationResult est = new WlsStateEstimator().Estimate(
            m, set, new EstimationOptions { Method = EstimationMethod.Feasible });

        Assert.Equal(EstimationStatus.Converged, est.Status);
        Assert.Equal(LawStatus.NotApplicable, Law(est.Physics!, 6).Status);
        Assert.Equal(LawStatus.NotApplicable, Law(est.Physics!, 8).Status);
        Assert.True(double.IsNaN(Law(est.Physics!, 6).Residual));
        // The identities of the projected state are still gated and still hold.
        Assert.Equal(LawStatus.Holds, Law(est.Physics!, 1).Status);
        Assert.Equal(LawStatus.Holds, Law(est.Physics!, 2).Status);
        Assert.Equal(LawStatus.Holds, Law(est.Physics!, 3).Status);
    }

    [Fact]
    public void Gate_still_demotes_a_state_that_breaks_a_zero_injection_constraint()
    {
        // Failure path: a zero tolerance makes the (machine-epsilon) zero-injection residual a violation,
        // so Ring C must still demote the estimate - the gate is live, not disabled.
        // Test changed 2026-10-19: GR-516 (1.95.338) made FeasibilityConstraints.Validate refuse a tolerance of 0, so the
        // smallest positive double stands in for it. The audit tests residual <= tol, so this still flags every residual
        // that is not exactly zero (or the one smallest denormal), exactly as the zero tolerance did.
        (GridModel m, PowerFlowResult truth) = Case14Truth();
        MeasurementSet set = NoisySetWithZeroInjection(m, truth);

        EstimationResult est = new WlsStateEstimator().Estimate(m, set, new EstimationOptions
        {
            Method = EstimationMethod.Feasible,
            Feasibility = new FeasibilityConstraints { AuditTolMva = double.Epsilon },
        });

        Assert.Equal(EstimationStatus.Infeasible, est.Status);
        Assert.False(est.Converged);
        Assert.NotEmpty(est.Physics!.Violations);
    }

    [Fact]
    public void AuditAgainst_flags_a_bus_whose_network_injection_differs_from_the_specification()
    {
        (GridModel m, PowerFlowResult truth) = Case14Truth();
        int i4 = truth.BusIds.ToList().IndexOf(4);
        double actual = truth.PInjMw[i4];
        Assert.True(Math.Abs(actual) > 1.0, "bus 4 must carry real load for this test");

        // Specify bus 4 as a zero-injection node although it draws real power: law 6 must be Violated by
        // exactly the bus's injection magnitude (to rounding), and law 8 with it.
        var spec = new SpecifiedInjections(new Dictionary<int, double> { [4] = 0.0 }, new Dictionary<int, double>(), "test");
        PhysicsLawReport r = PhysicsLaws.AuditAgainst(m, truth, spec, 1e-4);
        Assert.Equal(LawStatus.Violated, Law(r, 6).Status);
        Assert.Equal(LawStatus.Violated, Law(r, 8).Status);
        Assert.Equal(Math.Abs(actual), Law(r, 6).Residual, 6);
        Assert.False(r.Gate(PhysicsCheckRegistry.HardGateLaws));
    }

    [Fact]
    public void AuditAgainst_with_the_static_dispatch_reproduces_the_plain_audit_bit_for_bit()
    {
        // Orchestration proof: specifying exactly the plain audit's convention (P at every non-slack bus,
        // Q at every PQ bus, values Pg - Pd) gives bit-identical residuals for every law.
        (GridModel m, PowerFlowResult truth) = Case14Truth();
        var p = new Dictionary<int, double>();
        var q = new Dictionary<int, double>();
        foreach (Bus b in m.Buses)
        {
            // Same accumulation order as the audit: minus demand first, then plus each generator.
            double sp = -b.Pd, sq = -b.Qd;
            foreach (Generator g in m.Generators.Where(g => g.Bus == b.Id)) { sp += g.Pg; sq += g.Qg; }
            if (b.Type != BusType.Slack) p[b.Id] = sp;
            if (b.Type == BusType.Pq) q[b.Id] = sq;
        }
        PhysicsLawReport plain = PhysicsLaws.Audit(m, truth);
        PhysicsLawReport spec = PhysicsLaws.AuditAgainst(m, truth, new SpecifiedInjections(p, q, "static dispatch"), 1e-6);
        foreach (LawResult law in plain.Laws)
        {
            LawResult other = Law(spec, law.Number);
            Assert.Equal(law.Status, other.Status);
            Assert.Equal(law.Residual, other.Residual);
        }
    }

    [Fact]
    public void AuditAgainst_fails_loud_on_bad_specifications()
    {
        (GridModel m, PowerFlowResult truth) = Case14Truth();
        var empty = new Dictionary<int, double>();
        Assert.Throws<ArgumentNullException>(() => PhysicsLaws.AuditAgainst(m, truth, null!, 1e-4));
        Assert.Throws<ArgumentException>(() => PhysicsLaws.AuditAgainst(m, truth,
            new SpecifiedInjections(new Dictionary<int, double> { [9999] = 0.0 }, empty, "test"), 1e-4));
        Assert.Throws<ArgumentException>(() => PhysicsLaws.AuditAgainst(m, truth,
            new SpecifiedInjections(empty, new Dictionary<int, double> { [4] = double.NaN }, "test"), 1e-4));
    }

    [Fact]
    public void Contradictory_zero_injection_constraints_fail_loud()
    {
        GridModel m = IeeeCases.Case14();
        var set = new MeasurementSet
        {
            Case = m.Name,
            BaseMva = m.BaseMva,
            Measurements = new List<Measurement>
            {
                new(MeasurementKind.VmPu, 1.0, 0.004, Bus: 1),
                new(MeasurementKind.PInjMw, 0.0, 1.0, Bus: ZeroInjectionBus, Source: "virtual-zero-injection"),
                new(MeasurementKind.PInjMw, 5.0, 1.0, Bus: ZeroInjectionBus, Source: "virtual-zero-injection"),
            },
        };
        var mm = new MeasurementModel(m, set);
        ArgumentException ex = Assert.Throws<ArgumentException>(() => WlsStateEstimator.ZeroInjectionSpecification(mm));
        Assert.Contains("disagree", ex.Message);
    }
}
