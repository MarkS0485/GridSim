using GridSim.Core.Analysis;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 plan W1: analytical two-bus case with two parallel 0.2 pu reactances. Every intact and post-outage
/// observable is checked against the closed form, not against a captured solver output.
/// <para>Oracle (per unit, lossless, receiving demand P + jQ, sending voltage 1.0, equivalent reactance X):
/// y = V^2 solves y^2 + (2XQ - 1)y + X^2(P^2 + Q^2) = 0 (larger root); sending Q = Q + X(P^2 + Q^2)/y.</para>
/// <para>Tolerances: Newton stops at a bus mismatch of <see cref="PowerFlowOptions.Tolerance"/> pu. Near the
/// high-voltage root dV/dQ is below 0.5 for both X = 0.1 and X = 0.2, so 10x the mismatch bounds the voltage
/// error in pu, and 10x the mismatch times base MVA bounds the MW / MVAr / MVA errors.</para>
/// </summary>
public sealed class TransmissionSecurityW1AnalyticalTests
{
    private const double BaseMva = 100, Pd = 80, Qd = 30;
    private static readonly double VoltTol = 10 * ContingencyAnalysis.StudyOptions.Tolerance;
    private static readonly double PowerTol = VoltTol * BaseMva;

    private static GridModel Model() => new()
    {
        BaseMva = BaseMva,
        Buses = [new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: Pd, Qd: Qd)],
        Generators = [new Generator(1, 80, Pmin: 0, Pmax: 200, Qmin: 0, Qmax: 100)],
        Branches = [new Branch(1, 2, 0, .2, RateAMva: 100, Name: "one"),
            new Branch(1, 2, 0, .2, RateAMva: 100, Name: "two")]
    };

    private static TransmissionSecurityRequest Request(double minPu) =>
        new([new(0, 1, 2), new(1, 1, 2)], minPu, 1.1, PostFaultRating.RateA);

    private static (double V, double SlackQMvar) Oracle(double x)
    {
        double p = Pd / BaseMva, q = Qd / BaseMva;
        double b = 2 * x * q - 1;
        double y = (-b + Math.Sqrt(b * b - 4 * x * x * (p * p + q * q))) / 2;
        return (Math.Sqrt(y), (q + x * (p * p + q * q) / y) * BaseMva);
    }

    private static LimitEvidence One(SecurityState s, string kind, int index) =>
        Assert.Single(s.Limits, l => l.Kind == kind && l.Index == index);

    [Fact]
    public void Oracle_reproduces_the_plan_table()
    {
        // Guards the oracle itself against the rounded values published in RC3 PLAN W1.
        var (vi, qi) = Oracle(.1);
        var (vo, qo) = Oracle(.2);
        Assert.InRange(Math.Abs(vi - 0.965488891873777), 0, 1e-12);
        Assert.InRange(Math.Abs(qi - 37.83119966834628), 0, 1e-9);
        Assert.InRange(Math.Abs(vo - 0.9194904559886774), 0, 1e-12);
        Assert.InRange(Math.Abs(qo - 47.268650672867075), 0, 1e-9);
    }

    [Fact]
    public void Intact_state_matches_closed_form_on_every_observable()
    {
        var result = TransmissionSecurityRunner.Run(Model(), Request(.90));
        SecurityState s = result.BaseCase;
        var (v, slackQ) = Oracle(.1);

        Assert.Null(s.OutageIndex);
        Assert.Equal("SECURE", s.Disposition);
        Assert.Equal(PowerFlowStatus.Converged, s.SolverStatus);
        Assert.InRange(Math.Abs(One(s, "Voltage", 1).Observed - 1.0), 0, VoltTol);
        Assert.InRange(Math.Abs(One(s, "Voltage", 2).Observed - v), 0, VoltTol);

        // Lossless: slack P equals demand, and AC needs no adjustment from the DC setpoint.
        Assert.InRange(Math.Abs(One(s, "GeneratorP", 0).Observed - Pd), 0, PowerTol);
        Assert.InRange(Math.Abs(One(s, "GeneratorPAdjustment", 0).Observed), 0, PowerTol);
        Assert.InRange(Math.Abs(One(s, "GeneratorQ", 0).Observed - slackQ), 0, PowerTol);
        Assert.InRange(Math.Abs(s.LossMw!.Value), 0, PowerTol);

        // Each parallel circuit carries half the sending power and has its own pair of rows.
        double sendMva = Math.Sqrt(Pd * Pd + slackQ * slackQ) / 2;   // plan: 44.247032861951
        double recvMva = Math.Sqrt(Pd * Pd + Qd * Qd) / 2;
        for (int i = 0; i < 2; i++)
        {
            LimitEvidence from = One(s, "BranchFrom", i), to = One(s, "BranchTo", i);
            Assert.InRange(Math.Abs(from.Observed - sendMva), 0, PowerTol);
            Assert.InRange(Math.Abs(to.Observed - recvMva), 0, PowerTol);
            Assert.Equal(100, from.Upper);
            Assert.Equal("A", from.Source);
            Assert.Equal(100 - from.Observed, from.UpperMargin);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Each_outage_matches_closed_form_and_keeps_parallel_identity(int opened)
    {
        GridModel model = Model();
        var result = TransmissionSecurityRunner.Run(model, Request(.90));
        int live = 1 - opened;
        SecurityState s = result.Outages[opened];
        var (v, slackQ) = Oracle(.2);

        Assert.Equal(opened, s.OutageIndex);
        Assert.Equal("SECURE", s.Disposition);
        Assert.InRange(Math.Abs(One(s, "Voltage", 2).Observed - v), 0, VoltTol);
        Assert.InRange(Math.Abs(One(s, "GeneratorP", 0).Observed - Pd), 0, PowerTol);
        Assert.InRange(Math.Abs(One(s, "GeneratorQ", 0).Observed - slackQ), 0, PowerTol);

        // The opened circuit is not evaluated; the surviving circuit carries the whole flow (plan: 92.921 MVA).
        Assert.DoesNotContain(s.Limits, l => l.Kind.StartsWith("Branch") && l.Index == opened);
        Assert.InRange(Math.Abs(One(s, "BranchFrom", live).Observed - Math.Sqrt(Pd * Pd + slackQ * slackQ)), 0, PowerTol);
        Assert.InRange(Math.Abs(One(s, "BranchTo", live).Observed - Math.Sqrt(Pd * Pd + Qd * Qd)), 0, PowerTol);

        // The open circuit's solver row is kept, at zero, under its own name (no endpoint de-duplication).
        GridModel outaged = model.CopyWith(branches: model.Branches.Select((b, i) => i == opened ? b with { InService = false } : b).ToArray());
        var flows = SolverSelector.CreateNewton(outaged).Solve(outaged, ContingencyAnalysis.StudyOptions).LineFlows;
        Assert.Equal(2, flows.Count);
        Assert.Equal(new[] { "one", "two" }, flows.Select(f => f.Name));
        Assert.Equal(0, flows[opened].PFromMw);
        Assert.Equal(0, flows[opened].QFromMvar);
        Assert.NotEqual(0, flows[live].PFromMw);
    }

    [Fact]
    public void Lower_bound_095_fails_both_outages_with_exact_signed_margin_and_passes_intact()
    {
        GridModel model = Model();
        var result = TransmissionSecurityRunner.Run(model, Request(.95));
        var (v, _) = Oracle(.2);

        Assert.Equal("NOT SECURE", result.Status);
        Assert.Equal("Violations", result.Outcome);
        Assert.Equal("SECURE", result.BaseCase.Disposition);

        // DC candidate: the single unit carries all 80 MW; DC outage flow (80 MW) fits the 100 MW rating.
        ScopfDispatch d = Assert.Single(result.CandidateDispatch);
        Assert.Equal(0, d.Index);
        Assert.InRange(Math.Abs(d.Pmw - Pd), 0, 1e-9);

        foreach (SecurityState s in result.Outages)
        {
            Assert.Equal("VIOLATIONx1", s.Disposition);
            LimitEvidence bad = Assert.Single(s.Limits, l => l.Violated);
            Assert.Equal(("Voltage", 2, "pu"), (bad.Kind, bad.Index, bad.Units));
            Assert.Equal(.95, bad.Lower);
            Assert.Equal(bad.Observed - .95, bad.LowerMargin);            // margin = observed - limit, unrounded
            Assert.InRange(Math.Abs(bad.LowerMargin!.Value - (v - .95)), 0, VoltTol); // plan: -0.030509544011323
        }

        // Each outage started from an independent copy: the input model is unchanged.
        Assert.All(model.Branches, b => Assert.True(b.InService));
        Assert.Equal(80, model.Generators[0].Pg);
    }
}
