using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.Qsts;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Hosting capacity (<see cref="HostingCapacity"/>): the bisection brackets an analytic over-voltage limit on a two-bus
/// feeder to its tolerance; thermal, reverse-power and under-voltage limits are found and named; the snapshot method is
/// no less conservative than the time series; base-case violations are not blamed on the resource; and the answer does
/// not depend on the thread count.
/// </summary>
public sealed class HostingCapacityTests
{
    private static readonly PowerFlowOptions Opt = new() { Tolerance = 1e-10 };

    /// <summary>Slack bus 1 at 1.0 pu, one branch R + jX (pu on 10 MVA, rated <paramref name="rateMva"/>), PQ bus 2 with
    /// no demand.</summary>
    private static GridModel TwoBus(double r, double x, double rateMva = 0) => new()
    {
        BaseMva = 10, Name = "two-bus",
        Buses = new List<Bus> { new(1, BusType.Slack, BaseKv: 11), new(2, BusType.Pq, BaseKv: 11) },
        Branches = new List<Branch> { new(1, 2, r, x, RateAMva: rateMva, Name: "L12") },
        Generators = new List<Generator> { new(1, 0) },
    };

    /// <summary>
    /// The PV injection (MW) that puts bus 2 at exactly <paramref name="vm"/> pu with no reactive power: with y = 1 / (R +
    /// jX) = g + jb, the injection at bus 2 is P = g Vm^2 - Vm (g cos d + b sin d), Q = -b Vm^2 - Vm (g sin d - b cos d).
    /// Q = 0 gives g sin d - b cos d = -b Vm, i.e. sin(d - phi) = -b Vm / |y| with phi = atan2(b, g); the smaller root d.
    /// </summary>
    private static double AnalyticPvMw(double r, double x, double vm, double baseMva)
    {
        double den = r * r + x * x, g = r / den, b = -x / den;
        double ymag = Math.Sqrt(g * g + b * b), phi = Math.Atan2(b, g);
        // Two roots: phi + asin(z) and phi + pi - asin(z); the operating point is the one with the smaller angle.
        double z = Math.Asin(-b * vm / ymag);
        double Wrap(double v) => Math.IEEERemainder(v, 2 * Math.PI);
        double d1 = Wrap(phi + z), d2 = Wrap(phi + Math.PI - z);
        double d = Math.Abs(d1) <= Math.Abs(d2) ? d1 : d2;
        double p = g * vm * vm - vm * (g * Math.Cos(d) + b * Math.Sin(d));
        return p * baseMva;
    }

    private static HostingStudy Snap(double maxMw, double tol, params HostingSnapshot[] snaps) => new()
    {
        MaxMw = maxMw, ToleranceMw = tol, Snapshots = snaps.Length == 0 ? null : snaps,
        Base = new QstsStudy { StepSeconds = 1, Steps = 1, Limits = new QstsLimits(0.94, 1.05, 100) },
    };

    [Fact]
    public void The_bisection_brackets_the_analytic_overvoltage_limit_to_its_tolerance()
    {
        GridModel m = TwoBus(0.05, 0.05);
        double exact = AnalyticPvMw(0.05, 0.05, 1.05, 10);
        Assert.True(exact > 1 && exact < 20, $"exact {exact}");
        HostingResult r = HostingCapacity.Run(m, Snap(20, 0.001, new HostingSnapshot(1, 1)), () => new NewtonRaphsonPowerFlow(), Opt);
        HostingLocationResult l = Assert.Single(r.Locations);
        Assert.Equal(2, l.Bus);
        Assert.Equal(HostingConstraint.Overvoltage, l.Primary);
        Assert.True(l.HostingMw <= exact && exact <= l.FailingMw, $"[{l.HostingMw}, {l.FailingMw}] vs exact {exact}");
        Assert.True(l.FailingMw - l.HostingMw <= 0.001);
        // 20 MW down to 0.001 MW: the evaluation at MaxMw plus ceil(log2(20 / 0.001)) = 15 halvings.
        Assert.Equal(1 + 15, l.Evaluations);
    }

    [Fact]
    public void A_thermal_limit_is_found_and_named()
    {
        GridModel m = TwoBus(0.002, 0.01, rateMva: 2.0);
        HostingResult r = HostingCapacity.Run(m, Snap(10, 0.001, new HostingSnapshot(1, 1)), () => new NewtonRaphsonPowerFlow(), Opt);
        HostingLocationResult l = r.Locations[0];
        Assert.Equal(HostingConstraint.Thermal, l.Primary);
        Assert.InRange(l.HostingMw, 1.9, 2.0);
    }

    [Fact]
    public void Reverse_power_through_the_source_transformer_limits_pv_at_about_the_feeder_demand()
    {
        GridModel m = QstsEngineTests.Feeder(oltc: false);
        HostingStudy s = Snap(20, 0.001, new HostingSnapshot(1, 1)) with
        {
            Candidates = new[] { 6 }, ReverseFlowBranch = "T1",
            Base = new QstsStudy { StepSeconds = 1, Steps = 1, Limits = new QstsLimits(0.8, 1.2, 1000) },
        };
        HostingResult r = HostingCapacity.Run(m, s, () => new BackwardForwardSweep(), Opt);
        HostingLocationResult l = r.Locations[0];
        Assert.Equal(HostingConstraint.ReversePower, l.Primary);
        // Forward flow at the passing size, reverse at the failing size.
        double Flow(double mw)
        {
            QstsStudy q = HostingCapacity.StudiesFor(s, 6, mw).Single();
            return new QstsEngine(new BackwardForwardSweep(), Opt).Run(m, q).LastResult!.LineFlows[0].PFromMw;
        }
        Assert.True(Flow(l.HostingMw) >= 0);
        Assert.True(Flow(l.FailingMw) < 0);
        Assert.InRange(l.HostingMw, 4.0, 4.5);   // 4 MW of demand plus the feeder losses
    }

    [Fact]
    public void Added_load_is_limited_by_undervoltage()
    {
        GridModel m = QstsEngineTests.Feeder(oltc: false, loadMw: 0.5);
        HostingStudy s = Snap(20, 0.001, new HostingSnapshot(1, 1)) with { Resource = HostedResource.Load, Candidates = new[] { 6 } };
        HostingLocationResult l = HostingCapacity.Run(m, s, () => new BackwardForwardSweep(), Opt).Locations[0];
        Assert.Equal(HostingConstraint.Undervoltage, l.Primary);
        QstsResult atPass = new QstsEngine(new BackwardForwardSweep(), Opt).Run(m, HostingCapacity.StudiesFor(s, 6, l.HostingMw).Single());
        Assert.True(atPass.Steps[0].VminPu >= 0.94);
    }

    [Fact]
    public void A_base_case_violation_is_not_blamed_on_the_resource()
    {
        // At full demand bus 6 is below 0.94 pu before any PV: PV raises it, so the under-voltage must not limit PV.
        GridModel m = QstsEngineTests.Feeder(oltc: false, loadMw: 1.6);
        HostingStudy s = Snap(20, 0.01, new HostingSnapshot(1, 1)) with { Candidates = new[] { 6 } };
        HostingLocationResult l = HostingCapacity.Run(m, s, () => new BackwardForwardSweep(), Opt).Locations[0];
        Assert.Contains(l.BaseViolations, v => v.Constraint == HostingConstraint.Undervoltage);
        Assert.NotEqual(HostingConstraint.Undervoltage, l.Primary);
        Assert.True(l.HostingMw > 1);
    }

    [Fact]
    public void The_answer_does_not_depend_on_the_thread_count()
    {
        GridModel m = QstsEngineTests.Feeder(oltc: false);
        HostingStudy s = Snap(20, 0.01) with { ReverseFlowBranch = "T1" };
        HostingResult one = HostingCapacity.Run(m, s, () => new BackwardForwardSweep(), Opt);
        HostingResult four = HostingCapacity.Run(m, s with { Threads = 4 }, () => new BackwardForwardSweep(), Opt);
        Assert.Equal(5, one.Locations.Count);   // every PQ bus: 2..6
        for (int i = 0; i < one.Locations.Count; i++)
        {
            Assert.Equal(one.Locations[i].Bus, four.Locations[i].Bus);
            Assert.Equal(one.Locations[i].HostingMw, four.Locations[i].HostingMw);
            Assert.Equal(one.Locations[i].FailingMw, four.Locations[i].FailingMw);
            Assert.Equal(one.Locations[i].Primary, four.Locations[i].Primary);
        }
        Assert.All(one.Locations, l => Assert.True(l.LimitedWithinRange));
    }

    [Fact]
    public void The_snapshot_at_minimum_demand_and_full_sun_is_no_less_conservative_than_the_time_series()
    {
        GridModel m = QstsEngineTests.DayFeeder();
        QstsStudy day = QstsEngineTests.DayStudy() with { Pv = Array.Empty<QstsPvUnit>(), Limits = new QstsLimits(0.9, 1.05, 100) };
        var qsts = new HostingStudy { Method = HostingMethod.Qsts, MaxMw = 20, ToleranceMw = 0.01, Base = day, ResourceProfile = "solar", Candidates = new[] { 6 } };
        // Minimum of the day's load shape (0.6) with full sun.
        var snap = new HostingStudy { MaxMw = 20, ToleranceMw = 0.01, Base = day, Candidates = new[] { 6 }, Snapshots = new[] { new HostingSnapshot(0.6, 1.0) } };
        HostingLocationResult lq = HostingCapacity.Run(m, qsts, () => new BackwardForwardSweep(), Opt).Locations[0];
        HostingLocationResult ls = HostingCapacity.Run(m, snap, () => new BackwardForwardSweep(), Opt).Locations[0];
        Assert.True(lq.LimitedWithinRange && ls.LimitedWithinRange);
        Assert.True(ls.HostingMw <= lq.HostingMw + 0.01, $"snapshot {ls.HostingMw} vs qsts {lq.HostingMw}");
    }

    [Fact]
    public void The_tap_operation_criterion_holds_the_passing_size_within_its_allowance()
    {
        GridModel m = QstsEngineTests.DayFeeder();
        QstsStudy day = QstsEngineTests.DayStudy() with { Pv = Array.Empty<QstsPvUnit>(), Limits = new QstsLimits(0.5, 1.5, 1000) };
        var s = new HostingStudy
        {
            Method = HostingMethod.Qsts, MaxMw = 10, ToleranceMw = 0.01, Base = day, ResourceProfile = "solar", Candidates = new[] { 4 },
            TapIncreasePercent = 50,
        };
        HostingLocationResult l = HostingCapacity.Run(m, s, () => new BackwardForwardSweep(), Opt).Locations[0];
        int baseOps = new QstsEngine(new BackwardForwardSweep(), Opt).Run(m, day).TotalTapOperations;
        int passOps = new QstsEngine(new BackwardForwardSweep(), Opt).Run(m, HostingCapacity.StudiesFor(s, 4, l.HostingMw).Single()).TotalTapOperations;
        Assert.True(passOps <= baseOps * 1.5, $"pass {passOps} base {baseOps}");
        if (l.LimitedWithinRange) Assert.NotEmpty(l.Limiting);
    }

    [Fact]
    public void A_refused_study_lists_its_problems()
    {
        var ex = Assert.Throws<InvalidDataException>(() => HostingCapacity.Run(TwoBus(0.05, 0.05),
            Snap(0, 1) with { Candidates = new[] { 7, 7 }, Method = HostingMethod.Snapshot, TapIncreasePercent = 5 }, () => new NewtonRaphsonPowerFlow()));
        foreach (string part in new[] { "maxMw", "toleranceMw", "listed twice", "candidate bus 7", "tap-operation criterion" })
            Assert.Contains(part, ex.Message, StringComparison.Ordinal);
    }
}
