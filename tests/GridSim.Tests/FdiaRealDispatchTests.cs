using System.Linq;
using GridSim.Core.Estimation;
using GridSim.Core.Estimation.Security;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The FDIA screen judges KCL only where the estimated state is physically bound (its hard zero-injection rows), not
/// against the case file's static dispatch. Real telemetry never matches the case file's Pg - Pd; before, any estimate
/// whose injection at one bus differed from it by more than the 0.1 x BaseMva tolerance read "physically impossible"
/// and, with chi-square passing, "SUSPECTED FALSE-DATA INJECTION". Synthetic tests sampled from the case dispatch could
/// not see this, so here the telemetry comes from a DIFFERENT dispatch: every load 15 % heavier.
/// </summary>
public class FdiaRealDispatchTests
{
    [Fact]
    public void An_exact_estimate_of_a_dispatch_other_than_the_case_file_is_not_flagged()
    {
        GridModel caseFile = IeeeCases.Case9();

        // The "real grid": the same network carrying 15 % more load (the slack picks it up). Its injections at the
        // three load buses differ from the case file's by 13.5 to 18.75 MW, above the 10 MVA screen tolerance.
        GridModel real = caseFile.CopyWith(buses: caseFile.Buses
            .Select(b => b with { Pd = b.Pd * 1.15, Qd = b.Qd * 1.15 }).ToList());
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(real);
        Assert.True(truth.Converged);

        // Estimate the real grid's state with the case-file model, exactly as the CLI does with external telemetry.
        EstimationResult est = new WlsStateEstimator().Estimate(caseFile, WlsEstimatorTests.ExactFullSet(real, truth));
        Assert.True(est.Converged);

        FdiaScreenReport report = FdiaScreen.Screen(caseFile, est);

        Assert.True(report.ChiSquarePassed);
        Assert.False(report.PhysicallyImpossible, "a correct estimate of real telemetry broke a hard physical law");
        Assert.False(report.SuspectedFdia);
    }
}
