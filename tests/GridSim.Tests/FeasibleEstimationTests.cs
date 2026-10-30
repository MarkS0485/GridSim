using GridSim.Core.Estimation;
using GridSim.Core.IO;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// The physics-constrained estimation method (<see cref="EstimationMethod.Feasible"/>) must keep the
/// solve inside the physically-admissible region: voltages strictly positive during iteration (Ring A),
/// and a converged state that breaks a hard physical law rejected as
/// <see cref="EstimationStatus.Infeasible"/> rather than shipped (Ring C). On clean data the guard and
/// gate are inert, so it recovers the true state exactly like the equality-constrained method.
/// </summary>
public class FeasibleEstimationTests
{
    private static (GridModel Model, PowerFlowResult Truth) Case(string name)
    {
        GridModel m = name switch
        {
            "14" => IeeeCases.Case14(),
            "30" => IeeeCases.Case30(),
            _ => IeeeCases.Case14(),
        };
        // Q-limit-enforced truth so the operating point respects generator reactive capability - an
        // operationally-feasible state the estimator can recover and the gate accept.
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        return (m, truth);
    }

    [Theory]
    [InlineData("14")]
    [InlineData("30")]
    public void Feasible_recovers_the_true_state_and_passes_the_gate(string name)
    {
        (GridModel m, PowerFlowResult truth) = Case(name);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(m, truth);

        EstimationResult est = new WlsStateEstimator().Estimate(
            m, set, new EstimationOptions { Method = EstimationMethod.Feasible });

        Assert.Equal(EstimationStatus.Converged, est.Status);
        for (int i = 0; i < est.Vm.Length; i++)
            Assert.Equal(truth.Vm[i], est.Vm[i], 6);

        // The acceptance gate ran and the state is physically admissible.
        Assert.NotNull(est.Physics);
        Assert.True(est.Physics!.Gate(GridSim.Core.Physics.PhysicsCheckRegistry.HardGateLaws));
    }

    [Fact]
    public void Feasible_keeps_every_voltage_strictly_positive()
    {
        (GridModel m, PowerFlowResult truth) = Case("14");
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(m, truth);

        EstimationResult est = new WlsStateEstimator().Estimate(
            m, set, new EstimationOptions { Method = EstimationMethod.Feasible });

        foreach (double vm in est.Vm) Assert.True(vm > 0.0, $"voltage magnitude {vm} is not strictly positive");
    }

    [Fact]
    public void Feasible_matches_Constrained_on_clean_data()
    {
        (GridModel m, PowerFlowResult truth) = Case("14");
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(m, truth);

        EstimationResult constrained = new WlsStateEstimator().Estimate(
            m, set, new EstimationOptions { Method = EstimationMethod.Constrained });
        EstimationResult feasible = new WlsStateEstimator().Estimate(
            m, set, new EstimationOptions { Method = EstimationMethod.Feasible });

        // Same backend + inert guard on clean data => the estimated state is identical.
        for (int i = 0; i < feasible.Vm.Length; i++)
        {
            Assert.Equal(constrained.Vm[i], feasible.Vm[i], 9);
            Assert.Equal(constrained.VaDeg[i], feasible.VaDeg[i], 9);
        }
    }

    [Fact]
    public void Operational_limits_are_reported_but_do_not_demote_by_default()
    {
        // case30's base operating point runs a branch over its thermal rating - a real operational
        // stress, not a physics-impossibility. The gate reports it but keeps the state Converged.
        (GridModel m, PowerFlowResult truth) = Case("30");
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(m, truth);

        EstimationResult lenient = new WlsStateEstimator().Estimate(
            m, set, new EstimationOptions { Method = EstimationMethod.Feasible });
        Assert.Equal(EstimationStatus.Converged, lenient.Status);
        Assert.NotEmpty(lenient.LimitViolations); // the overload is surfaced

        // Strict operational screening turns the same breach into an Infeasible verdict.
        EstimationResult strict = new WlsStateEstimator().Estimate(
            m, set, new EstimationOptions
            {
                Method = EstimationMethod.Feasible,
                Feasibility = new FeasibilityConstraints { RejectOnLimitViolation = true },
            });
        Assert.Equal(EstimationStatus.Infeasible, strict.Status);
    }

    [Fact]
    public void Ring_B_holds_generator_reactive_at_its_capability_at_solve_time()
    {
        // Truth (Q-limits off) puts real reactive on the PV buses. Pick the non-slack generator bus
        // carrying the most reactive and TIGHTEN its Qmax below that, so the true operating point
        // violates the capability. The measurements are physical (built from truth); WLS fits them and
        // reproduces the violation, while the feasible method's active set must hold Qg at the new Qmax.
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m); // Q-limits OFF
        var busIdx = new Dictionary<int, int>();
        for (int i = 0; i < truth.BusIds.Count; i++) busIdx[truth.BusIds[i]] = i;
        var qdById = new Dictionary<int, double>();
        foreach (Bus b in m.Buses) qdById[b.Id] = b.Qd;

        int targetBus = -1; double bestQg = double.NegativeInfinity;
        foreach (Generator g in m.Generators)
        {
            if (m.Buses.First(b => b.Id == g.Bus).Type == BusType.Slack) continue;
            double qg = truth.QInjMvar[busIdx[g.Bus]] + qdById[g.Bus];
            if (qg > bestQg) { bestQg = qg; targetBus = g.Bus; }
        }
        Assert.True(targetBus >= 0 && bestQg > 0, "need a non-slack PV bus with positive reactive");

        double newQmax = bestQg - 5.0; // MVAr, comfortably below the true Qg
        var tightened = Copy(m, m.Generators.Select(g => g.Bus == targetBus ? g with { Qmax = newQmax } : g).ToList());

        MeasurementSet set = WlsEstimatorTests.ExactFullSet(m, truth);
        var feas = new FeasibilityConstraints();

        EstimationResult wls = new WlsStateEstimator().Estimate(tightened, set, new EstimationOptions { Method = EstimationMethod.Wls });
        double qgWls = wls.ToPowerFlowResult(tightened).QInjMvar[busIdx[targetBus]] + qdById[targetBus];
        Assert.True(qgWls > newQmax + 1.0, $"WLS should reproduce the violation: Qg {qgWls} vs Qmax {newQmax}");

        EstimationResult fea = new WlsStateEstimator().Estimate(tightened, set, new EstimationOptions { Method = EstimationMethod.Feasible });
        double qgFea = fea.ToPowerFlowResult(tightened).QInjMvar[busIdx[targetBus]] + qdById[targetBus];
        Assert.True(qgFea <= newQmax + 0.5, $"Ring B must hold Qg at capability: Qg {qgFea} vs Qmax {newQmax}");
    }

    private static GridModel Copy(GridModel m, IReadOnlyList<Generator> generators, IReadOnlyList<Branch>? branches = null) => new()
    {
        BaseMva = m.BaseMva,
        Buses = m.Buses,
        Generators = generators,
        Branches = branches ?? m.Branches,
        Oltcs = m.Oltcs,
        Boundaries = m.Boundaries,
        FrequencyHz = m.FrequencyHz,
        Name = m.Name,
        AssetDynamics = m.AssetDynamics,
        Protections = m.Protections,
    };

    [Fact]
    public void Ring_B_holds_across_branch_angle_differences_under_the_cap()
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { EnforceQLimits = true });
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(m, truth);

        double MaxAngleDiffRad(EstimationResult est)
        {
            var vaDeg = new Dictionary<int, double>();
            for (int i = 0; i < est.BusIds.Count; i++) vaDeg[est.BusIds[i]] = est.VaDeg[i];
            double max = 0;
            foreach (Branch br in m.Branches)
                if (vaDeg.TryGetValue(br.From, out double a) && vaDeg.TryGetValue(br.To, out double b))
                    max = System.Math.Max(max, System.Math.Abs((a - b) * System.Math.PI / 180.0));
            return max;
        }

        // Unconstrained recovers the truth's angle spread; tighten the cap below it.
        EstimationResult wls = new WlsStateEstimator().Estimate(m, set, new EstimationOptions { Method = EstimationMethod.Wls });
        double trueMax = MaxAngleDiffRad(wls);
        double cap = trueMax * 0.7;

        EstimationResult fea = new WlsStateEstimator().Estimate(m, set, new EstimationOptions
        {
            Method = EstimationMethod.Feasible,
            Feasibility = new FeasibilityConstraints { AngleDiffMaxRad = cap },
        });
        Assert.True(MaxAngleDiffRad(fea) <= cap + 1e-3, $"angle-diff cap {cap} not held: got {MaxAngleDiffRad(fea)}");
    }

    [Fact]
    public void Ring_B_holds_a_branch_at_its_thermal_rating_under_the_limit()
    {
        // Truth carries natural flows. Find the branch carrying the most apparent power and TIGHTEN its
        // RateA below that, so the true operating point overloads it. WLS fits the (physical) measurements
        // and reproduces the overload; the feasible method's active set must pull |S| onto the new rating.
        GridModel m = IeeeCases.Case14();
        PowerFlowResult truth = new NewtonRaphsonPowerFlow().Solve(m);
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(m, truth);

        static double SFrom(LineFlow lf) => System.Math.Sqrt(lf.PFromMw * lf.PFromMw + lf.QFromMvar * lf.QFromMvar);
        LineFlow busiest = truth.LineFlows.MaxBy(SFrom)!;
        double trueS = SFrom(busiest);
        Assert.True(trueS > 0, "need a loaded branch");
        double newRate = trueS * 0.7; // MVA, comfortably below the true flow

        var tightenedBranches = m.Branches.Select(br =>
            br.From == busiest.From && br.To == busiest.To ? br with { RateAMva = newRate } : br).ToList();
        GridModel tightened = Copy(m, m.Generators, tightenedBranches);

        double TargetSFrom(EstimationResult est)
        {
            PowerFlowResult pf = est.ToPowerFlowResult(tightened);
            LineFlow lf = pf.LineFlows.First(l => l.From == busiest.From && l.To == busiest.To);
            return SFrom(lf);
        }

        EstimationResult wls = new WlsStateEstimator().Estimate(tightened, set, new EstimationOptions { Method = EstimationMethod.Wls });
        Assert.True(TargetSFrom(wls) > newRate + 1.0, $"WLS should reproduce the overload: |S| {TargetSFrom(wls)} vs rating {newRate}");

        EstimationResult fea = new WlsStateEstimator().Estimate(tightened, set, new EstimationOptions
        {
            Method = EstimationMethod.Feasible,
            Feasibility = new FeasibilityConstraints { EnforceBranchRatings = true },
        });
        Assert.True(TargetSFrom(fea) <= newRate + 0.5, $"Ring B must hold the branch at rating: |S| {TargetSFrom(fea)} vs rating {newRate}");
    }

    [Fact]
    public void The_acceptance_gate_demotes_to_Infeasible_when_a_hard_law_fails()
    {
        (GridModel m, PowerFlowResult truth) = Case("14");
        MeasurementSet set = WlsEstimatorTests.ExactFullSet(m, truth);

        // A zero audit tolerance makes even the machine-epsilon law residuals "violations", so the gate
        // MUST demote the (otherwise converged) estimate to Infeasible - exercising the full Ring C path.
        // Test changed 2026-10-19: GR-516 (1.95.338) made FeasibilityConstraints.Validate refuse a tolerance of 0, so the
        // smallest positive double stands in for it. The audit tests residual <= tol, so this still flags every residual
        // that is not exactly zero (or the one smallest denormal), exactly as the zero tolerance did.
        EstimationResult est = new WlsStateEstimator().Estimate(
            m, set, new EstimationOptions
            {
                Method = EstimationMethod.Feasible,
                Feasibility = new FeasibilityConstraints { AuditTolMva = double.Epsilon },
            });

        Assert.Equal(EstimationStatus.Infeasible, est.Status);
        Assert.False(est.Converged);
        Assert.NotNull(est.Physics);
        Assert.NotEmpty(est.Physics!.Violations);
    }
}
