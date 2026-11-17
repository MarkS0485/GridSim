using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Analysis;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

public sealed class TransmissionSecurityStudyTests
{
    private static GridModel Model() => new()
    {
        BaseMva = 100,
        Buses = [new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 80, Qd: 30)],
        Generators = [new Generator(1, 80, Pmin: 0, Pmax: 200, Qmin: 0, Qmax: 100)],
        Branches = [new Branch(1, 2, 0, .2, RateAMva: 100, Name: "one"),
            new Branch(1, 2, 0, .2, RateAMva: 100, Name: "two")]
    };
    private static TransmissionSecurityRequest Request(double minimum = .90) =>
        new([new(0, 1, 2), new(1, 1, 2)], minimum, 1.1, PostFaultRating.RateA);

    [Theory]
    [InlineData(.9, "Verified")]
    [InlineData(.95, "Violations")]
    public void Analytical_parallel_case_checks_absolute_voltage(double lower, string outcome)
    {
        GridModel model = Model();
        var result = TransmissionSecurityRunner.Run(model, Request(lower));
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(2, result.Outages.Count);
        double x = .2, p = .8, q = .3;
        double linear = 2 * x * q - 1;
        double y = (-linear + Math.Sqrt(linear * linear - 4 * x * x * (p * p + q * q))) / 2;
        foreach (var state in result.Outages)
        {
            var voltage = Assert.Single(state.Limits, l => l.Kind == "Voltage" && l.Index == 2);
            Assert.InRange(Math.Abs(voltage.Observed - Math.Sqrt(y)), 0, 1e-7);
            Assert.Equal(lower == .95, voltage.Violated);
        }
        Assert.All(model.Branches, branch => Assert.True(branch.InService));
        Assert.Equal(80, model.Generators[0].Pg);
    }

    [Fact]
    public void Base_case_violations_are_absolute_and_cannot_be_netted_against_outages()
    {
        GridModel model = Model();
        var result = TransmissionSecurityRunner.Run(model, Request(1.0));
        Assert.Equal("Violations", result.Outcome);
        Assert.Contains(result.BaseCase.Limits, l => l.Kind == "Voltage" && l.Violated);
        Assert.All(result.Outages, state =>
            Assert.Contains(state.Limits, l => l.Kind == "Voltage" && l.Violated));
    }

    [Fact]
    public void Island_has_an_explicit_row_and_is_excluded_from_the_candidate()
    {
        // 2.1.060: an islanding outage is excluded from the DC problem rather than voiding the candidate.
        GridModel model = Model().CopyWith(branches: [Model().Branches[0]]);
        var request = Request() with { Outages = [new(0, 1, 2)] };
        var result = TransmissionSecurityRunner.Run(model, request);
        Assert.Equal("Incomplete", result.Outcome);
        Assert.Equal("NOT SECURE", result.Status);
        var row = Assert.Single(result.Outages);
        Assert.Equal("ISLANDED", row.Disposition);
        Assert.Empty(row.Limits);
        Assert.Empty(result.Dc!.Contingencies);
        Assert.Equal("SECURE", result.BaseCase.Disposition);
    }

    [Fact]
    public void Rating_B_and_A_fallback_are_distinct()
    {
        GridModel model = Model();
        model = model.CopyWith(branches: [model.Branches[0] with { RateBMva = 90 }, model.Branches[1]]);
        var result = TransmissionSecurityRunner.Run(model, Request() with { PostFaultRating = PostFaultRating.RateB });
        Assert.Equal("SECURE", result.Outages[0].Disposition);
        Assert.Contains(result.Outages[1].Limits, l => l.Kind == "BranchFrom" && l.Violated && l.Upper == 90);
    }

    [Fact]
    public void Duplicate_and_mismatched_outages_are_refused()
    {
        Assert.Throws<ArgumentException>(() => TransmissionSecurityRunner.Run(Model(),
            Request() with { Outages = [new(0, 1, 2), new(0, 1, 2)] }));
        Assert.Throws<ArgumentException>(() => TransmissionSecurityRunner.Run(Model(),
            Request() with { Outages = [new(0, 2, 1)] }));
    }

    [Fact]
    public void Repeated_runs_have_identical_numeric_bits()
    {
        var first = TransmissionSecurityRunner.Run(Model(), Request());
        var second = TransmissionSecurityRunner.Run(Model(), Request());
        Assert.Equal(first.Outages.SelectMany(s => s.Limits).Select(l => BitConverter.DoubleToInt64Bits(l.Observed)),
            second.Outages.SelectMany(s => s.Limits).Select(l => BitConverter.DoubleToInt64Bits(l.Observed)));
    }

    [Fact]
    public void Multiple_units_keep_dispatch_identity_but_cannot_claim_individual_Q()
    {
        var model = Model().CopyWith(generators:
            [new Generator(1, 40, Pmax: 100, Qmin: 0, Qmax: 100),
             new Generator(1, 40, Pmax: 100, Qmin: 0, Qmax: 100)]);
        var result = TransmissionSecurityRunner.Run(model, Request());
        Assert.Equal("Incomplete", result.Outcome);
        Assert.Equal(new[] { 0, 1 }, result.CandidateDispatch.Select(d => d.Index));
    }

    [Fact]
    public void Out_of_service_generator_is_excluded_from_dispatch_but_remains_in_case_identity()
    {
        GridModel model = Model();
        model = model.CopyWith(generators: [model.Generators[0], model.Generators[0] with { InService = false, Pg = 0 }]);
        GridModel copied = model.CopyWith(generators: model.Generators.ToArray());

        TransmissionSecurityResult result = TransmissionSecurityRunner.Run(model, Request());

        Assert.Equal("Verified", result.Outcome);
        Assert.Single(result.CandidateDispatch);
        Assert.False(model.AllGenerators[1].InService);
        Assert.Single(copied.Generators);
        Assert.Equal(2, copied.AllGenerators.Count);
        Assert.False(copied.AllGenerators[1].InService);
    }

    [Fact]
    public void Realised_single_generator_accounting_uses_ybus_injections_with_bus_shunts()
    {
        GridModel model = Model();
        Bus[] buses = model.Buses.Select(bus => bus.Id == 1 ? bus with { Gs = 8, Bs = 12 } : bus).ToArray();
        model = model.CopyWith(buses: buses);
        PowerFlowResult solved = SolverSelector.CreateNewton(model).Solve(model, ContingencyAnalysis.StudyOptions);
        Assert.True(solved.Converged);

        TransmissionSecurityResult result = TransmissionSecurityRunner.Run(model, Request());
        LimitEvidence active = Assert.Single(result.BaseCase.Limits, limit => limit.Kind == "GeneratorP");
        LimitEvidence reactive = Assert.Single(result.BaseCase.Limits, limit => limit.Kind == "GeneratorQ");
        double expectedP = buses.Sum(bus => bus.Pd) + solved.LineFlows.Sum(flow => flow.PFromMw + flow.PToMw) +
            buses.Select((bus, index) => bus.Gs * solved.Vm[index] * solved.Vm[index]).Sum();
        double expectedQ = buses.Sum(bus => bus.Qd) + solved.LineFlows.Sum(flow => flow.QFromMvar + flow.QToMvar) -
            buses.Select((bus, index) => bus.Bs * solved.Vm[index] * solved.Vm[index]).Sum();

        double tolerance = 2 * ContingencyAnalysis.StudyOptions.Tolerance * model.BaseMva;
        Assert.InRange(Math.Abs(active.Observed - expectedP), 0, tolerance);
        Assert.InRange(Math.Abs(reactive.Observed - expectedQ), 0, tolerance);
    }
}

