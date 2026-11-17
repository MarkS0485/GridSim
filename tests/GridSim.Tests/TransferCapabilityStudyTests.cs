using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 P4: AC-verified directional transfer on the W1 lossless parallel corridor (two 0.2 pu circuits, bus 1 slack at
/// 1.0 pu). The transfer raises the slack unit (source, bus 1) and lowers a fixed-P unit at bus 2 (sink), so the net
/// receiving withdrawal at bus 2 equals the transfer. After either outage one 0.2 pu circuit carries it.
/// <para>Closed-form oracles (per unit, X = 0.2, receiving P + jQ, sending voltage 1.0):
/// y = V^2 is the larger root of y^2 + (2XQ - 1)y + X^2(P^2 + Q^2) = 0; the sending MVA is |S_r| / V (the series
/// current times the 1.0 pu sending voltage). With Q = 0 a sending rating S gives the exact limit P = S sqrt(1 - X^2 S^2);
/// a voltage floor V gives P^2 = -(y^2 + (2XQ - 1)y) / X^2 - Q^2 with y = V^2.</para>
/// <para>Tolerances: as W1, 10x the Newton mismatch for pu quantities and that times base MVA for MVA; the bracket
/// itself is checked against the oracle evaluated at its own endpoints, so the search tolerance and the solver
/// tolerance stay separate.</para>
/// </summary>
public sealed class TransferCapabilityStudyTests
{
    private const double BaseMva = 100, X = .2;
    private static readonly double VoltTol = 10 * ContingencyAnalysis.StudyOptions.Tolerance;
    private static readonly double PowerTol = VoltTol * BaseMva;

    private static GridModel Corridor(double qd, double rateA, double rateB, double sinkPg = 100, BusType bus2 = BusType.Pq,
        double sinkQmax = 50) => new()
    {
        BaseMva = BaseMva,
        Buses = [new Bus(1, BusType.Slack), new Bus(2, bus2, Pd: 100, Qd: qd)],
        Generators = [new Generator(1, 0, Pmin: -50, Pmax: 300, Qmin: -200, Qmax: 200, Name: "source"),
            new Generator(2, sinkPg, Vg: 1.0, Pmin: 0, Pmax: 200, Qmin: -sinkQmax, Qmax: sinkQmax, Name: "sink")],
        Branches = [new Branch(1, 2, 0, X, RateAMva: rateA, RateBMva: rateB, Name: "one"),
            new Branch(1, 2, 0, X, RateAMva: rateA, RateBMva: rateB, Name: "two")]
    };

    private static TransferStudyRequest Request(double minPu, PostFaultRating rating, double maxMw = 100, double step = 10,
        double tolerance = .01, int maxTrials = 60, IReadOnlyList<TransferInterface>? interfaces = null) =>
        new([new(0, 1, 1)], [new(1, 2, 1)], [new(0, 1, 2), new(1, 1, 2)], minPu, 1.1, rating,
            new TransferSearchOptions(maxMw, step, tolerance, maxTrials, 1), interfaces);

    private static double Y(double pMw, double qMvar)
    {
        double p = pMw / BaseMva, q = qMvar / BaseMva, b = 2 * X * q - 1;
        return (-b + Math.Sqrt(b * b - 4 * X * X * (p * p + q * q))) / 2;
    }
    private static double SendingMva(double pMw) => pMw / Math.Sqrt(Y(pMw, 0));
    private static double ReceivingV(double pMw, double qMvar) => Math.Sqrt(Y(pMw, qMvar));

    [Fact]
    public void Thermal_limit_matches_closed_form_with_sending_end_controlling()
    {
        GridModel model = Corridor(0, 100, 90);
        var corridor = new TransferInterface("corridor", [new(0, 1, 2), new(1, 1, 2)]);
        TransferStudyResult r = TransferStudyRunner.Run(model, Request(.9, PostFaultRating.RateB, interfaces: [corridor]));
        double s = .9, exact = BaseMva * s * Math.Sqrt(1 - X * X * s * s);   // 88.5297... MW

        Assert.Equal("VERIFIED", r.Status);
        Assert.Equal("Limited", r.Outcome);
        Assert.Empty(r.Qualifications);
        double lo = r.LowerMw!.Value, hi = r.UpperMw!.Value;
        Assert.InRange(hi - lo, 0, .01);
        Assert.InRange(exact, lo - 1e-4, hi + 1e-4);
        Assert.True(SendingMva(lo) <= 90 + PowerTol && SendingMva(hi) >= 90 - PowerTol);

        // First observed limit: post-fault RATE_B at the sending end of the surviving circuit, outage 0 first.
        TransferLimit first = r.FirstLimit!;
        Assert.Equal((0, "BranchFrom", 1, "MVA", "upper", "B"), (first.OutageIndex!.Value, first.Kind, first.Index, first.Units, first.Side, first.Source));
        Assert.Equal(90, first.Limit);
        Assert.Equal(90 - first.Observed, first.Margin);
        Assert.InRange(Math.Abs(first.Observed - SendingMva(hi)), 0, PowerTol);
        // The simultaneous symmetric violation follows in stable order; the receiving end (= transfer MW) does not bind.
        Assert.Equal(2, r.Upper!.Violations.Count);
        Assert.Equal((1, "BranchFrom", 0), (r.Upper.Violations[1].OutageIndex!.Value, r.Upper.Violations[1].Kind, r.Upper.Violations[1].Index));
        Assert.DoesNotContain(r.Upper.Violations, v => v.Kind == "BranchTo");
        Assert.All(r.Lower!.Outages.Prepend(r.Lower.Intact), st => Assert.Equal("SECURE", st.Disposition));

        // Lossless: no slack balancing; the whole requested transfer crosses the monitored corridor.
        Assert.InRange(Math.Abs(r.Lower.SlackBalancingMw!.Value), 0, PowerTol);
        Assert.InRange(Math.Abs(r.Lower.InterfaceChangeMw[0]!.Value - lo), 0, PowerTol);
        Assert.Equal(new[] { lo, 100 - lo }, r.Lower.Schedule.Select(x => x.ScheduledMw));

        // Execution order and accounting: reference, coarse 10..90, one probe at 100, 10 bisections, two re-solves.
        Assert.Equal(new[] { "reference" }.Concat(Enumerable.Repeat("coarse", 9)).Append("probe")
            .Concat(Enumerable.Repeat("refine", 10)).Append("final-lower").Append("final-upper"), r.Trials.Select(t => t.Phase));
        Assert.Equal(Enumerable.Range(0, r.Trials.Count), r.Trials.Select(t => t.Sequence));
        Assert.Equal(new TransferAccounting(21, 2, 10, 9, 0, 1, 0, 10, 0, 23 * 3), r.Accounting);

        // The input model is untouched.
        Assert.Equal(0, model.Generators[0].Pg);
        Assert.Equal(100, model.Generators[1].Pg);
        Assert.All(model.Branches, b => Assert.True(b.InService));
    }

    [Fact]
    public void Voltage_limit_matches_closed_form()
    {
        TransferStudyResult r = TransferStudyRunner.Run(Corridor(40, 200, 200), Request(.9, PostFaultRating.RateA));
        double q = .4, y = .81;
        double exact = BaseMva * Math.Sqrt(-(y * y + (2 * X * q - 1) * y) / (X * X) - q * q);   // 66.89... MW

        Assert.Equal("VERIFIED", r.Status);
        double lo = r.LowerMw!.Value, hi = r.UpperMw!.Value;
        Assert.InRange(hi - lo, 0, .01);
        Assert.InRange(exact, lo - 1e-4, hi + 1e-4);
        Assert.True(ReceivingV(lo, 40) >= .9 - VoltTol && ReceivingV(hi, 40) <= .9 + VoltTol);

        TransferLimit first = r.FirstLimit!;
        Assert.Equal((0, "Voltage", 2, "pu", "lower"), (first.OutageIndex!.Value, first.Kind, first.Index, first.Units, first.Side));
        Assert.Equal(.9, first.Limit);
        Assert.Equal(first.Observed - .9, first.Margin);
        Assert.InRange(Math.Abs(first.Observed - ReceivingV(hi, 40)), 0, VoltTol);
        Assert.DoesNotContain(r.Upper!.Violations, v => v.Kind.StartsWith("Branch", StringComparison.Ordinal));
    }

    [Fact]
    public void Violated_reference_is_no_admissible_start_not_zero_headroom()
    {
        // At zero transfer either outage leaves bus 2 at about 0.912 pu, below a 0.92 floor.
        TransferStudyResult r = TransferStudyRunner.Run(Corridor(40, 200, 200), Request(.92, PostFaultRating.RateA));

        Assert.Equal("NOT SECURE", r.Status);
        Assert.Equal("NoAdmissibleStart", r.Outcome);
        Assert.Null(r.LowerMw);
        Assert.Null(r.UpperMw);
        Assert.Equal(TransferClass.Violation, r.Reference!.Class);
        Assert.Equal(new[] { "reference", "final-reference" }, r.Trials.Select(t => t.Phase));
        Assert.Equal(10, r.Accounting!.CoarseUnevaluated);
        TransferLimit first = r.FirstLimit!;
        Assert.Equal((0, "Voltage", 2), (first.OutageIndex!.Value, first.Kind, first.Index));
        Assert.InRange(Math.Abs(first.Margin - (ReceivingV(0, 40) - .92)), 0, VoltTol);
        Assert.Equal("SECURE", r.Reference.Intact.Disposition);
    }

    [Fact]
    public void Reactive_limit_switch_is_detected_and_qualifies_the_bound()
    {
        // Sink unit holds bus 2 at 1.0 pu with +-15 MVAr; after an outage it reaches Qmax near 70 MW of transfer.
        TransferStudyResult r = TransferStudyRunner.Run(Corridor(10, 200, 90, bus2: BusType.Pv, sinkQmax: 15),
            Request(.9, PostFaultRating.RateB, tolerance: .1));

        Assert.Equal("QUALIFIED", r.Status);
        Assert.Equal("Limited", r.Outcome);
        TransferQualification q = Assert.Single(r.Qualifications);
        Assert.Equal("ControlStateChange", q.Code);
        Assert.Equal("", r.Reference!.ControlSignature);
        Assert.Contains("outage 0:2=Pq", r.Upper!.ControlSignature, StringComparison.Ordinal);
        Assert.NotNull(r.FirstLimit);
    }

    [Fact]
    public void Participant_range_is_a_known_limit_and_is_never_clipped()
    {
        // The sink starts at 30 MW with Pmin 0, so 30 MW is the schedule limit, well before the thermal limit.
        GridModel model = Corridor(0, 100, 90, sinkPg: 30);
        model = new GridModel
        {
            BaseMva = BaseMva, Generators = model.Generators, Branches = model.Branches,
            Buses = [new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 30)]
        };
        TransferStudyResult r = TransferStudyRunner.Run(model, Request(.9, PostFaultRating.RateB, tolerance: .5));

        Assert.Equal("VERIFIED", r.Status);
        Assert.Equal(30, r.LowerMw);           // exact: the coarse point 30 is the last feasible trial
        TransferLimit first = r.FirstLimit!;
        Assert.Equal((null, "ScheduledGeneratorP", 1, "lower", "sink"), (first.OutageIndex, first.Kind, first.Index, first.Side, first.Source));
        Assert.True(first.Observed < 0);
        Assert.Equal(0, first.Limit);
        Assert.All(r.Upper!.Outages, s => Assert.Equal("UNEVALUATED", s.Disposition));
    }

    [Fact]
    public void Islanding_required_outage_leaves_the_reference_incomplete()
    {
        GridModel model = new()
        {
            BaseMva = BaseMva, Generators = Corridor(0, 100, 90).Generators,
            Buses = [new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 100), new Bus(3, BusType.Pq, Pd: 5)],
            Branches = [new Branch(1, 2, 0, X, RateAMva: 100, RateBMva: 90), new Branch(1, 2, 0, X, RateAMva: 100, RateBMva: 90),
                new Branch(2, 3, 0, .1, RateAMva: 100)]
        };
        var request = Request(.9, PostFaultRating.RateB) with { Outages = [new(0, 1, 2), new(2, 2, 3)] };
        TransferStudyResult r = TransferStudyRunner.Run(model, request);

        Assert.Equal("INCOMPLETE", r.Status);
        Assert.Equal("ReferenceIncomplete", r.Outcome);
        Assert.Null(r.LowerMw);
        Assert.Equal("ISLANDED", r.Reference!.Outages[1].Disposition);
        Assert.Equal(TransferClass.Unknown, r.Reference.Class);
    }

    [Fact]
    public void Repeat_runs_are_bit_identical()
    {
        var options = new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
        string a = JsonSerializer.Serialize(TransferStudyRunner.Run(Corridor(0, 100, 90), Request(.9, PostFaultRating.RateB)), options);
        string b = JsonSerializer.Serialize(TransferStudyRunner.Run(Corridor(0, 100, 90), Request(.9, PostFaultRating.RateB)), options);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("bus")]
    [InlineData("weight")]
    [InlineData("tolerance")]
    [InlineData("budget")]
    [InlineData("interface")]
    public void Invalid_requests_are_refused_before_any_solve(string fault)
    {
        TransferStudyRequest good = Request(.9, PostFaultRating.RateB);
        TransferStudyRequest bad = fault switch
        {
            "overlap" => good with { Sinks = [new(0, 1, 1)] },
            "bus" => good with { Sinks = [new(1, 1, 1)] },
            "weight" => good with { Sources = [new(0, 1, 0)] },
            "tolerance" => good with { Search = new(100, 10, 20, 60) },
            "budget" => good with { Search = new(100, 10, 1, 1) },
            _ => good with { Interfaces = [new("x", [new(0, 2, 1)])] },
        };
        Assert.Throws<ArgumentException>(() => TransferStudyRunner.Run(Corridor(0, 100, 90), bad));
    }

    [Fact]
    public void Comparator_record_is_unverified_with_empty_comparator_side()
    {
        TransferStudyResult r = TransferStudyRunner.Run(Corridor(0, 100, 90), Request(.9, PostFaultRating.RateB));
        TransferComparison c = TransferComparison.Unverified(r, "test", "case", "study");
        Assert.Equal("UNVERIFIED", c.Status);
        Assert.False(c.Verified);
        Assert.All(c.Verdicts, v => Assert.Null(v.Comparator));
        Assert.All(c.Runtime, t => Assert.Null(t.ComparatorSeconds));
        Assert.Contains(c.Verdicts, v => v.Item == "Limiting outage" && v.GridSim == "outage 0");
    }
}
