using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R028: AutoReference with no slack observation used to anchor every injected angle to the CASE FILE's
/// static angle at the datum bus, and emitted the datum's own row whose value was purely that stale prior.
/// A non-slack datum now needs an explicit present-state anchor (<see cref="PhaseAngleOptions.DatumAnchorDeg"/>)
/// and its own row is not emitted.
/// </summary>
public class PhaseAngleAnchorTests
{
    private static readonly DateTime T0 = new(2026, 5, 1, 20, 0, 0, DateTimeKind.Utc);
    private const double ThetaSys = 137.5;   // arbitrary common-mode phase of the absolute feed

    /// <summary>Case 14 with every case-file angle zeroed: a deliberately stale static angle set.</summary>
    private static (GridModel Stale, PowerFlowResult Truth) StaleCase()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(truth.Converged);
        GridModel stale = m.CopyWith(buses: m.Buses.Select(b => b with { VaDeg = 0.0 }).ToList());
        return (stale, truth);
    }

    /// <summary>Absolute-frame observations at three non-slack buses (no slack observation).</summary>
    private static List<PhaseAngleObs> NonSlackObs(PowerFlowResult truth, params int[] buses) =>
        buses.Select(id => new PhaseAngleObs(id, truth.VaDeg[truth.BusIds.ToList().IndexOf(id)] + ThetaSys, T0, 50.0)).ToList();

    private static double TruthVa(PowerFlowResult truth, int id) => truth.VaDeg[truth.BusIds.ToList().IndexOf(id)];

    [Fact]
    public void A_non_slack_datum_without_an_anchor_fails_loud()
    {
        (GridModel stale, PowerFlowResult truth) = StaleCase();
        var ex = Assert.Throws<InvalidOperationException>(() => PhaseAngleFeed.ToMeasurements(stale,
            NonSlackObs(truth, 9, 10, 14), new PhaseAngleOptions { Mode = PhaseAngleReference.AutoReference, TargetUtc = T0 }));
        Assert.Contains("PRESENT-STATE", ex.Message);
    }

    [Fact]
    public void A_present_state_anchor_recovers_the_true_angles_whatever_the_case_file_says()
    {
        (GridModel stale, PowerFlowResult truth) = StaleCase();
        // The old code would have anchored to the stale 0 deg and biased every row by -Va_true(9).
        (IReadOnlyList<Measurement> rows, _) = PhaseAngleFeed.ToMeasurements(stale, NonSlackObs(truth, 9, 10, 14),
            new PhaseAngleOptions { Mode = PhaseAngleReference.AutoReference, TargetUtc = T0, DatumAnchorDeg = TruthVa(truth, 9) });

        // The datum's own row (value = the anchor, a prior) is not emitted; the others are the truth.
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Bus == 9);
        foreach (Measurement r in rows)
            Assert.Equal(TruthVa(truth, r.Bus), r.Value, 9);
    }

    [Fact]
    public void A_slack_datum_needs_no_anchor_and_rejects_one()
    {
        (GridModel stale, PowerFlowResult truth) = StaleCase();
        int slack = stale.Buses.First(b => b.Type == BusType.Slack).Id;
        List<PhaseAngleObs> obs = NonSlackObs(truth, slack, 9, 10);

        (IReadOnlyList<Measurement> rows, _) = PhaseAngleFeed.ToMeasurements(stale, obs,
            new PhaseAngleOptions { Mode = PhaseAngleReference.AutoReference, TargetUtc = T0 });
        Assert.Equal(2, rows.Count);   // slack row dropped as before; both non-slack rows kept
        foreach (Measurement r in rows) Assert.Equal(TruthVa(truth, r.Bus), r.Value, 9);

        Assert.Throws<ArgumentException>(() => PhaseAngleFeed.ToMeasurements(stale, obs,
            new PhaseAngleOptions { Mode = PhaseAngleReference.AutoReference, TargetUtc = T0, DatumAnchorDeg = 1.0 }));
    }

    [Theory]
    [InlineData(PhaseAngleReference.AlreadyReferenced, 5.0)]
    [InlineData(PhaseAngleReference.Raw, 5.0)]
    [InlineData(PhaseAngleReference.AutoReference, double.NaN)]
    public void An_anchor_that_would_be_ignored_or_is_not_finite_fails_loud(PhaseAngleReference mode, double anchor)
    {
        (GridModel stale, PowerFlowResult truth) = StaleCase();
        Assert.Throws<ArgumentException>(() => PhaseAngleFeed.ToMeasurements(stale, NonSlackObs(truth, 9, 10),
            new PhaseAngleOptions { Mode = mode, TargetUtc = T0, DatumAnchorDeg = anchor }));
    }
}
