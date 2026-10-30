using System;
using GridSim.Core.Diagnostics;
using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>Recording estimates into the tamper-evident audit log: each estimate becomes a chained entry
/// stamped at its state time and carrying its verdict; the log verifies.</summary>
public class EstimationAuditTrailTests
{
    [Fact]
    public void Records_estimates_as_a_verifiable_chained_trail()
    {
        GridModel model = IeeeCases.Case9();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(model);
        EstimationResult est = new WlsStateEstimator().Estimate(model, WlsEstimatorTests.ExactFullSet(model, truth));

        var log = new AuditLog();
        DateTime run = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime stateTime = run.AddHours(-200);
        EstimationAuditTrail.Record(log, run, stateTime, est);
        EstimationAuditTrail.Record(log, run.AddMinutes(1), stateTime.AddMinutes(30), est);

        Assert.True(log.Verify());
        Assert.Equal(2, log.Entries.Count);
        Assert.Equal(stateTime, log.Entries[0].StateTimeUtc);
        Assert.Equal(ProvenanceTag.DerivedFromRecorded, log.Entries[0].Provenance);
        Assert.Contains("chi2=PASS", log.Entries[0].Summary);
        Assert.Contains("status=Converged", log.Entries[0].Summary);
    }
}
