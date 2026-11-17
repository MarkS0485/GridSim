using GridSim.Core.Analysis;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 plan W2: post-fault rating selection on the W1 network. Pins the effective rating and its source on
/// every evaluated row, the B-to-A fallback for absent and non-positive B, and the exact boundary (a zero
/// margin passes; margin = limit - observed with no clamping).
/// </summary>
public sealed class TransmissionSecurityW2RatingTests
{
    private static GridModel Model(double rateB0, double rateB1) => new()
    {
        BaseMva = 100,
        Buses = [new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 80, Qd: 30)],
        Generators = [new Generator(1, 80, Pmin: 0, Pmax: 200, Qmin: 0, Qmax: 100)],
        Branches = [new Branch(1, 2, 0, .2, RateAMva: 100, RateBMva: rateB0, Name: "one"),
            new Branch(1, 2, 0, .2, RateAMva: 100, RateBMva: rateB1, Name: "two")]
    };

    private static TransmissionSecurityRequest Request(PostFaultRating rating = PostFaultRating.RateB) =>
        new([new(0, 1, 2), new(1, 1, 2)], .90, 1.1, rating);

    private static LimitEvidence From(SecurityState s, int branch) =>
        Assert.Single(s.Limits, l => l.Kind == "BranchFrom" && l.Index == branch);

    [Fact]
    public void Explicit_B_fails_and_absent_B_falls_back_to_A_with_recorded_source()
    {
        var result = TransmissionSecurityRunner.Run(Model(90, 0), Request());

        Assert.NotNull(result.Dc);
        Assert.True(result.Dc!.Optimal);   // DC outage flow 80 MW fits both 90 MW (B) and 100 MW (A)

        // Intact rows always use RateA.
        foreach (int i in new[] { 0, 1 })
            Assert.Equal((100.0, "A"), (From(result.BaseCase, i).Upper!.Value, From(result.BaseCase, i).Source));

        // Opening branch 1 leaves branch 0 at ~92.921 MVA against its explicit 90 MVA B rating.
        SecurityState openOne = result.Outages[1];
        LimitEvidence b = From(openOne, 0);
        Assert.Equal((90.0, "B"), (b.Upper!.Value, b.Source));
        Assert.True(b.Violated);
        Assert.Equal(90 - b.Observed, b.UpperMargin);
        Assert.InRange(b.Observed, 92.92, 92.93);
        // Receiving end carries the demand only, hypot(80, 30) = 85.44 MVA, inside 90: one violation, not two.
        LimitEvidence bTo = Assert.Single(openOne.Limits, l => l.Kind == "BranchTo" && l.Index == 0);
        Assert.Equal((90.0, "B"), (bTo.Upper!.Value, bTo.Source));
        Assert.False(bTo.Violated);
        Assert.Equal("VIOLATIONx1", openOne.Disposition);

        // Opening branch 0 leaves branch 1 carrying the same physical flow; its B is absent so A applies.
        SecurityState openZero = result.Outages[0];
        LimitEvidence a = From(openZero, 1);
        Assert.Equal((100.0, "fallback A"), (a.Upper!.Value, a.Source));
        Assert.False(a.Violated);
        Assert.Equal(BitConverter.DoubleToInt64Bits(b.Observed), BitConverter.DoubleToInt64Bits(a.Observed));
        Assert.Equal("SECURE", openZero.Disposition);

        Assert.Equal("Violations", result.Outcome);
    }

    [Fact]
    public void Negative_B_is_not_a_rating_and_falls_back_to_A()
    {
        var result = TransmissionSecurityRunner.Run(Model(-5, -5), Request());
        foreach (SecurityState s in result.Outages)
        {
            LimitEvidence live = Assert.Single(s.Limits, l => l.Kind == "BranchFrom");
            Assert.Equal((100.0, "fallback A"), (live.Upper!.Value, live.Source));
        }
        Assert.Equal("Verified", result.Outcome);
    }

    [Fact]
    public void Requesting_A_post_fault_labels_every_row_A()
    {
        var result = TransmissionSecurityRunner.Run(Model(90, 0), Request(PostFaultRating.RateA));
        Assert.All(result.Outages.SelectMany(s => s.Limits).Where(l => l.Kind.StartsWith("Branch")),
            l => Assert.Equal((100.0, "A"), (l.Upper!.Value, l.Source)));
        Assert.Equal("Verified", result.Outcome);
    }

    [Fact]
    public void Rating_exactly_equal_to_the_observed_flow_is_a_zero_margin_pass()
    {
        // Runs are bit-repeatable (TransmissionSecurityStudyTests), so the first run's observed MVA is the
        // exact value the second run will see.
        double observed = From(TransmissionSecurityRunner.Run(Model(90, 0), Request()).Outages[1], 0).Observed;
        GridModel model = Model(90, 0);
        model = model.CopyWith(branches: [model.Branches[0] with { RateBMva = observed }, model.Branches[1]]);

        LimitEvidence edge = From(TransmissionSecurityRunner.Run(model, Request()).Outages[1], 0);
        Assert.Equal(BitConverter.DoubleToInt64Bits(observed), BitConverter.DoubleToInt64Bits(edge.Observed));
        Assert.Equal(0.0, edge.UpperMargin);
        Assert.False(edge.Violated);
    }
}
