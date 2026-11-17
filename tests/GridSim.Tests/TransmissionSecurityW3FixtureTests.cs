using System.Security.Cryptography;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.TransmissionSecurity;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC3 plan W3: islanding, control, generator reporting and failure-path fixtures for the preventive
/// transmission-security study. Builds on the W1 network (two parallel 0.2 pu circuits, 80 + j30 demand).
/// </summary>
public sealed class TransmissionSecurityW3FixtureTests
{
    private static readonly double PowerTol = 10 * ContingencyAnalysis.StudyOptions.Tolerance * 100;

    private static GridModel W1(double r = 0, double pd = 80, double qd = 30, double rating = 100,
        double pmax = 200, double qmax = 100) => new()
    {
        BaseMva = 100,
        Buses = [new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: pd, Qd: qd)],
        Generators = [new Generator(1, pd, Pmin: 0, Pmax: pmax, Qmin: 0, Qmax: qmax)],
        Branches = [new Branch(1, 2, r, .2, RateAMva: rating, Name: "one"),
            new Branch(1, 2, r, .2, RateAMva: rating, Name: "two")]
    };

    private static TransmissionSecurityRequest Request(params BranchOutage[] outages) =>
        new(outages.Length == 0 ? [new(0, 1, 2), new(1, 1, 2)] : outages, .90, 1.1, PostFaultRating.RateA);

    private static LimitEvidence One(SecurityState s, string kind, int index) =>
        Assert.Single(s.Limits, l => l.Kind == kind && l.Index == index);

    [Fact]
    public void Bridge_outage_is_islanded_while_the_other_rows_are_still_verified()
    {
        GridModel w1 = W1();
        GridModel model = w1.CopyWith(
            buses: [.. w1.Buses, new Bus(3, BusType.Pq, Pd: 10, Qd: 5)],
            branches: [.. w1.Branches, new Branch(1, 3, 0, .1, RateAMva: 100, Name: "bridge")]);
        var result = TransmissionSecurityRunner.Run(model, Request(new(0, 1, 2), new(1, 1, 2), new(2, 1, 3)));

        Assert.Equal(3, result.Outages.Count);
        Assert.Equal(new int?[] { 0, 1, 2 }, result.Outages.Select(s => s.OutageIndex));
        Assert.Equal("ISLANDED", result.Outages[2].Disposition);
        Assert.Empty(result.Outages[2].Limits);                       // no evidence must never read as a pass
        Assert.Equal(new[] { 0, 1 }, result.Dc!.Contingencies);       // excluded from the DC problem, not dropped
        Assert.Equal("SECURE", result.Outages[0].Disposition);
        Assert.Equal("SECURE", result.Outages[1].Disposition);
        Assert.Equal("SECURE", result.BaseCase.Disposition);
        Assert.Equal(("NOT SECURE", "Incomplete"), (result.Status, result.Outcome));
    }

    [Fact]
    public void Q_limit_switches_PV_to_PQ_without_failing_the_solve_and_reports_realised_Q()
    {
        // Unit 1 holds bus 2 at 1.0 pu but can only give 10 MVAr against a 30 MVAr local demand.
        GridModel model = new()
        {
            BaseMva = 100,
            Buses = [new Bus(1, BusType.Slack), new Bus(2, BusType.Pv, Pd: 80, Qd: 30)],
            Generators = [new Generator(1, 40, Pmin: 0, Pmax: 200, Qmin: -100, Qmax: 100),
                new Generator(2, 40, Vg: 1.0, Pmin: 0, Pmax: 200, Qmin: -10, Qmax: 10)],
            Branches = [new Branch(1, 2, 0, .2, RateAMva: 200, Name: "one"),
                new Branch(1, 2, 0, .2, RateAMva: 200, Name: "two")]
        };
        var result = TransmissionSecurityRunner.Run(model, Request());

        foreach (SecurityState s in result.Outages.Prepend(result.BaseCase))
        {
            Assert.Equal(PowerFlowStatus.Converged, s.SolverStatus);
            Assert.Equal(BusType.Pq, s.SolvedBusTypes![1]);           // a legitimate switch, not a failure
            LimitEvidence q = One(s, "GeneratorQ", 1);
            Assert.InRange(Math.Abs(q.Observed - 10), 0, PowerTol);    // held at its bound
            Assert.True(One(s, "Voltage", 2).Observed < 1.0);          // setpoint no longer held
            // Independent bus-2 Q account: unit output = local demand - import over both circuits.
            Assert.NotEqual("NO SOLUTION", s.Disposition);
        }
    }

    [Fact]
    public void Resistive_losses_raise_slack_P_above_the_DC_setpoint_and_are_checked_against_Pmax()
    {
        GridModel model = W1(r: .02, pmax: 81);
        var result = TransmissionSecurityRunner.Run(model, Request());
        Assert.InRange(Assert.Single(result.CandidateDispatch).Pmw, 80 - 1e-9, 80 + 1e-9);   // lossless DC

        foreach (SecurityState s in result.Outages.Prepend(result.BaseCase))
        {
            // Independent loss: sum of terminal active powers over the live circuits.
            GridModel solvedModel = s.OutageIndex is int k
                ? model.CopyWith(branches: model.Branches.Select((b, i) => i == k ? b with { InService = false } : b).ToArray())
                : model;
            double loss = SolverSelector.CreateNewton(solvedModel).Solve(solvedModel, ContingencyAnalysis.StudyOptions)
                .LineFlows.Sum(f => f.PFromMw + f.PToMw);
            Assert.True(loss > 0);
            LimitEvidence p = One(s, "GeneratorP", 0);
            Assert.InRange(Math.Abs(p.Observed - (80 + loss)), 0, PowerTol);
            Assert.InRange(Math.Abs(One(s, "GeneratorPAdjustment", 0).Observed - loss), 0, PowerTol);
            Assert.Equal(81 - p.Observed, p.UpperMargin);
            Assert.Equal(p.Observed > 81, p.Violated);
        }
        // Intact loss is ~0.8 MW (passes 81 MW); a single circuit roughly doubles current and quadruples loss.
        Assert.False(One(result.BaseCase, "GeneratorP", 0).Violated);
        Assert.All(result.Outages, s => Assert.True(One(s, "GeneratorP", 0).Violated));
        Assert.Equal("Violations", result.Outcome);
    }

    [Fact]
    public void Shared_bus_units_take_DC_setpoints_by_model_order_and_individual_Q_is_reported_unobservable()
    {
        GridModel model = W1().CopyWith(generators:
            [new Generator(1, 0, Pmin: 0, Pmax: 50, Qmin: 0, Qmax: 100, Name: "cheap"),
             new Generator(1, 0, Pmin: 0, Pmax: 100, Qmin: 0, Qmax: 100, Name: "dear")]);
        var result = TransmissionSecurityRunner.Run(model, Request() with { Costs = [10, 20] });

        Assert.Equal(new[] { 0, 1 }, result.CandidateDispatch.Select(d => d.Index));
        Assert.InRange(result.CandidateDispatch[0].Pmw, 50 - 1e-6, 50 + 1e-6);   // cheap unit at its range
        Assert.InRange(result.CandidateDispatch[1].Pmw, 30 - 1e-6, 30 + 1e-6);
        Assert.All(result.Outages.Prepend(result.BaseCase), s =>
        {
            Assert.Equal("INCOMPLETE", s.Disposition);
            Assert.Contains("individual realised generator allocation is unavailable", s.Reason);
            Assert.DoesNotContain(s.Limits, l => l.Kind.StartsWith("Generator"));
        });
        Assert.Equal("Incomplete", result.Outcome);
    }

    [Fact]
    public void Outage_without_an_AC_solution_is_reported_and_blocks_a_verified_verdict()
    {
        // 260 MW at unity power factor: over X = 0.1 bus 2 sits at ~0.963 pu, but a single X = 0.2 circuit can carry
        // at most 1/(2X) = 2.5 pu, so each outage has no AC solution (discriminant (1)^2 - 4(0.04)(6.76) < 0).
        var result = TransmissionSecurityRunner.Run(W1(pd: 260, qd: 0, rating: 300, pmax: 400, qmax: 400), Request());
        Assert.Equal("SECURE", result.BaseCase.Disposition);
        Assert.All(result.Outages, s =>
        {
            Assert.Equal("NO SOLUTION", s.Disposition);
            Assert.Empty(s.Limits);
            Assert.NotNull(s.Reason);
        });
        Assert.Equal(("NOT SECURE", "Incomplete"), (result.Status, result.Outcome));
    }

    [Fact]
    public void Unit_exactly_at_Pmax_is_not_a_false_violation_and_the_tolerance_is_declared()
    {
        // The DC LP puts the slack unit on Pmax = 80 MW exactly; the realised P carries Newton residual.
        var result = TransmissionSecurityRunner.Run(W1(pmax: 80), Request());
        double tolerance = ContingencyAnalysis.StudyOptions.Tolerance * 100;
        foreach (SecurityState s in result.Outages.Prepend(result.BaseCase))
        {
            LimitEvidence p = One(s, "GeneratorP", 0);
            Assert.Equal(tolerance, p.Tolerance);
            Assert.Equal(80 - p.Observed, p.UpperMargin);                // unrounded, no clamping
            Assert.InRange(Math.Abs(p.Observed - 80), 0, tolerance);
            Assert.False(p.Violated);
            Assert.All(s.Limits.Where(l => !l.Kind.StartsWith("Generator")), l => Assert.Equal(0, l.Tolerance));
        }
        Assert.Equal("Verified", result.Outcome);
    }

    [Fact]
    public void Q_held_at_its_bound_after_a_PV_to_PQ_switch_is_not_a_violation()
    {
        GridModel model = new()
        {
            BaseMva = 100,
            Buses = [new Bus(1, BusType.Slack), new Bus(2, BusType.Pv, Pd: 80, Qd: 30)],
            Generators = [new Generator(1, 40, Pmin: 0, Pmax: 200, Qmin: -100, Qmax: 100),
                new Generator(2, 40, Vg: 1.0, Pmin: 0, Pmax: 200, Qmin: -10, Qmax: 10)],
            Branches = [new Branch(1, 2, 0, .2, RateAMva: 200, Name: "one"),
                new Branch(1, 2, 0, .2, RateAMva: 200, Name: "two")]
        };
        var result = TransmissionSecurityRunner.Run(model, Request());
        Assert.All(result.Outages.Prepend(result.BaseCase), s => Assert.False(One(s, "GeneratorQ", 1).Violated));
    }

    [Fact]
    public void A_DC_formulation_refusal_is_a_candidate_failure_with_every_row_not_an_input_error()
    {
        // The only unit is out of service: the request is valid, but DC SCOPF has nothing to dispatch.
        GridModel w1 = W1();
        GridModel model = w1.CopyWith(generators: [w1.Generators[0] with { InService = false }]);
        var result = TransmissionSecurityRunner.Run(model, Request());
        Assert.Equal(("NOT SECURE", "CandidateFailure"), (result.Status, result.Outcome));
        Assert.Equal(2, result.Outages.Count);
        Assert.All(result.Outages, s => Assert.Equal("UNEVALUATED", s.Disposition));
        Assert.StartsWith("DC candidate could not be formed", result.BaseCase.Reason);
    }

    [Fact]
    public void Failed_invocations_leave_no_result_that_looks_current()
    {
        string directory = Path.Combine(Path.GetTempPath(), "gridsim-security-w3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string model = Path.Combine(directory, "parallel.m");
            File.WriteAllText(model, """
                function mpc = parallel
                mpc.version = '2';
                mpc.baseMVA = 100;
                mpc.bus = [
                1 3 0 0 0 0 1 1 0 230 1 1.1 0.9;
                2 1 80 30 0 0 1 1 0 230 1 1.1 0.9;
                ];
                mpc.gen = [1 80 0 100 0 1 100 1 200 0;];
                mpc.branch = [
                1 2 0 .2 0 100 100 100 0 0 1 -360 360;
                1 2 0 .2 0 100 100 100 0 0 1 -360 360;
                ];
                """);
            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(model)));
            string Study(object outages) => JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                @case = new { path = "parallel.m", sha256 = hash },
                outages,
                voltage = new { minPu = .9, maxPu = 1.1 },
                postFaultRating = "A"
            });
            string study = Path.Combine(directory, "study.json");

            // Bad case identity: one byte changes after the fingerprint was taken.
            File.WriteAllText(study, Study(new[] { new { index = 0, from = 1, to = 2 } }));
            File.WriteAllText(model, File.ReadAllText(model).Replace("80 30", "80 31"));
            string tampered = Path.Combine(directory, "tampered.json");
            var mismatch = CliProcess.RunBuilt("transmission-security", study, "--out", tampered);
            Assert.Equal(1, mismatch.ExitCode);
            Assert.Contains("Case SHA256 mismatch", mismatch.StdErr);
            Assert.False(File.Exists(tampered));
            Assert.Empty(mismatch.StdOut.Trim());
            File.WriteAllText(model, File.ReadAllText(model).Replace("80 31", "80 30"));

            // Duplicate outage and wrong endpoint identity both fail before physics, with no result written.
            foreach (object outages in new object[]
            {
                new[] { new { index = 0, from = 1, to = 2 }, new { index = 0, from = 1, to = 2 } },
                new[] { new { index = 1, from = 2, to = 1 } },
            })
            {
                File.WriteAllText(study, Study(outages));
                string output = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
                var refused = CliProcess.RunBuilt("transmission-security", study, "--out", output);
                Assert.Equal(1, refused.ExitCode);
                Assert.False(File.Exists(output));
            }
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }
}
