using System.Collections.Generic;
using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>Hypothesis-Testing Identification: on an estimate biased by a gross error, HTI flags the offending
/// measurement (test statistic above threshold) and exonerates the clean ones.</summary>
public class HypothesisTestingBadDataTests
{
    [Fact]
    public void Identify_refuses_an_estimate_of_another_model()
    {
        // GR-522: an estimate is tested only against the model (and bus order) it was made for.
        GridModel case9 = IeeeCases.Case9();
        MeasurementSet set9 = WlsEstimatorTests.ExactFullSet(case9, new NewtonRaphsonPowerFlow().Solve(case9));
        EstimationResult est9 = new WlsStateEstimator().Estimate(case9, set9);
        Assert.True(est9.Converged);
        GridModel case14 = IeeeCases.Case14();
        Assert.Throws<System.ArgumentException>(() => HypothesisTestingBadData.Identify(case14, set9, est9));
    }

    [Fact]
    public void Identifies_a_gross_error_and_exonerates_clean_measurements()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        var measurements = new List<Measurement>(WlsEstimatorTests.ExactFullSet(model, truth).Measurements);

        // Inject a gross error into one voltage measurement.
        int bad = measurements.FindIndex(m => m.Kind == MeasurementKind.VmPu);
        int badBusId = measurements[bad].Bus;
        measurements[bad] = measurements[bad] with { Value = measurements[bad].Value + 0.25 };
        var set = new MeasurementSet { Case = model.Name, BaseMva = model.BaseMva, Measurements = measurements };

        // Estimate WITHOUT removal so the error remains in the fit, then run HTI on the biased estimate.
        EstimationResult est = new WlsStateEstimator().Estimate(model, set, new EstimationOptions { DetectBadData = false });
        IReadOnlyList<HtiIdentified> flagged = HypothesisTestingBadData.Identify(model, set, est);

        Assert.Contains(flagged, f => f.Measurement.Kind == MeasurementKind.VmPu && f.Measurement.Bus == badBusId);
        // The bad voltage's estimated error recovers roughly the injected +0.25 pu.
        HtiIdentified badFlag = flagged.First(f => f.Measurement.Kind == MeasurementKind.VmPu && f.Measurement.Bus == badBusId);
        Assert.True(badFlag.TestStatistic > 3.0);
        Assert.True(System.Math.Abs(badFlag.EstimatedError - 0.25) < 0.1, $"estimated error {badFlag.EstimatedError}");
    }

    [Fact]
    public void The_audit_trail_record_carries_the_one_outcome_text()
    {
        // October 2026: EstimationAuditTrail.Outcome is the single source of an estimate's audit text; Record (which
        // had no caller and wrote a different, shorter line) now writes "estimate " + Outcome.
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(model, truth);
        EstimationResult est = new WlsStateEstimator().Estimate(model, set);
        string outcome = EstimationAuditTrail.Outcome("wls", est, set.Measurements.Count);
        Assert.StartsWith("method=wls status=Converged observable=True J=", outcome);
        Assert.Contains($" chi2=PASS dof={est.DegreesOfFreedom} removed=0 measurements={set.Measurements.Count}", outcome);

        var log = new GridSim.Core.Diagnostics.AuditLog();
        var run = new System.DateTime(2026, 10, 3, 0, 0, 0, System.DateTimeKind.Utc);
        GridSim.Core.Diagnostics.AuditLogEntry e = EstimationAuditTrail.Record(log, run, run.AddDays(-8), est, "wls", set.Measurements.Count);
        Assert.Equal("estimate " + outcome, e.Summary);
    }

    // October 2026 (docs-programme finding): Identify built a plain gain and did not exclude virtual-zero-injection
    // rows, so --hti --method constrained/feasible could remove a hard constraint that LNR marks critical.

    /// <summary>case9 exact telemetry plus a virtual-zero-injection P row at bus 5 (a load bus) that is WRONG: it says 0
    /// where the bus really draws 90 MW. Under plain WLS (DetectBadData off) the row is soft, so it is biased against
    /// the telemetry and has a large residual.</summary>
    private static (GridModel Model, MeasurementSet Set, EstimationResult Est, Measurement Constraint) WrongConstraintCase()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        var ms = new List<Measurement>(WlsEstimatorTests.ExactFullSet(model, truth).Measurements);
        var constraint = new Measurement(MeasurementKind.PInjMw, 0.0, 1.0, Bus: 5, Source: "virtual-zero-injection");
        ms.Add(constraint);
        var set = new MeasurementSet { Case = model.Name, BaseMva = model.BaseMva, Measurements = ms };
        EstimationResult est = new WlsStateEstimator().Estimate(model, set, new EstimationOptions { DetectBadData = false });
        Assert.True(est.Converged);
        return (model, set, est, constraint);
    }

    [Fact]
    public void Plain_hti_treats_a_virtual_zero_injection_row_as_ordinary_telemetry()
    {
        // The pre-fix (and still the plain-WLS) behaviour: the soft row is a measurement like any other, and a wrong
        // one is identified. This pins that the test case really does put the row in the suspect set.
        (GridModel model, MeasurementSet set, EstimationResult est, Measurement constraint) = WrongConstraintCase();
        IReadOnlyList<HtiIdentified> flagged = HypothesisTestingBadData.Identify(model, set, est);
        Assert.Contains(flagged, f => ReferenceEquals(f.Measurement, constraint));
    }

    [Fact]
    public void Under_hard_constraints_hti_never_identifies_a_virtual_zero_injection_row()
    {
        (GridModel model, MeasurementSet set, EstimationResult est, Measurement constraint) = WrongConstraintCase();
        IReadOnlyList<HtiIdentified> flagged = HypothesisTestingBadData.Identify(model, set, est, hardConstraints: true);
        Assert.DoesNotContain(flagged, f => f.Measurement.Source == "virtual-zero-injection");
        Assert.DoesNotContain(flagged, f => ReferenceEquals(f.Measurement, constraint));
    }

    [Theory]
    [InlineData(EstimationMethod.Constrained)]
    [InlineData(EstimationMethod.Feasible)]
    public void Hti_under_constrained_and_feasible_removes_the_gross_error_and_keeps_every_constraint(EstimationMethod method)
    {
        // case14 bus 7 is a true zero-injection bus (no load, no generator): its P and Q rows are hard constraints.
        // A gross error on the bus-7 to bus-8 flow sits right next to it.
        GridModel model = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        var ms = new List<Measurement>(WlsEstimatorTests.ExactFullSet(model, truth).Measurements);
        ms.RemoveAll(m => m.Bus == 7 && m.Kind is MeasurementKind.PInjMw or MeasurementKind.QInjMvar);
        ms.Add(new Measurement(MeasurementKind.PInjMw, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));
        ms.Add(new Measurement(MeasurementKind.QInjMvar, 0.0, 1.0, Bus: 7, Source: "virtual-zero-injection"));
        int bad = ms.FindIndex(m => m.Kind == MeasurementKind.QFlowMvar && m.From == 7 && m.To == 8 && m.End == BranchEnd.From);
        Assert.True(bad >= 0);
        ms[bad] = ms[bad] with { Value = ms[bad].Value + 25.0 };
        Measurement badRow = ms[bad];
        var set = new MeasurementSet { Case = model.Name, BaseMva = model.BaseMva, Measurements = ms };

        HtiEstimationResult r = HtiBadDataEstimation.Estimate(new WlsStateEstimator(), model, set,
            new EstimationOptions { Method = method, DetectBadData = true });

        Assert.DoesNotContain(r.Removed, x => x.Identified.Measurement.Source == "virtual-zero-injection");
        Assert.Contains(r.Removed, x => ReferenceEquals(x.Identified.Measurement, badRow));
        Assert.Equal(HtiStopReason.ChiSquarePassed, r.StopReason);
    }
}
