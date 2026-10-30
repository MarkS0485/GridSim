using System.Threading;
using GridSim.Core.Abstractions;
using GridSim.Core.Model;
using GridSim.Core.Physics;
using GridSim.Core.PowerFlow;
using GridSim.Core.Storage;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// October 2026 review, WS6 power-flow group: the three engines (dense Newton, sparse Newton, backward/forward
/// sweep) and their consumers must solve the SAME physics from the same model and options.
/// <list type="bullet">
/// <item>R036: the auto-selected sweep honoured none of FlatStart / EnforceQLimits / Cancellation / ZIP, dropped
/// the phase shift of a tap-0 branch, returned an empty LineFlows on failure, and LooksRadial counted open
/// branches.</item>
/// <item>R037: the bus voltage setpoint with several generators was last-wins in Newton, max in the sweep's PV
/// buses and first-wins at the sweep's slack.</item>
/// <item>R045: storage sites overrode the bus voltage setpoint with a placeholder 1.0 pu.</item>
/// <item>R065: only the Newton mismatch honoured ZIP; the Q-limit check, the physics audit and the sweep used the
/// nominal load.</item>
/// <item>R281: the physics audit lost its LineFlow alignment after a skipped branch.</item>
/// <item>R283: a PV bus with no generator could be reverted to PV at Vm = 0.</item>
/// </list>
/// </summary>
public class PowerFlowPhysicsConsistencyTests
{
    // ---- fixtures ------------------------------------------------------------------------------------------

    /// <summary>A 4-bus radial feeder (3 in-service branches): slack 1, PQ 2, PV 3 (optional), PQ 4.</summary>
    private static GridModel Feeder(
        Branch? first = null, Bus? bus2 = null, Generator[]? gens = null, bool pv3 = false,
        double slackVaDeg = 0.0, Branch? extra = null)
    {
        var branches = new List<Branch>
        {
            first ?? new Branch(1, 2, 0.01, 0.05, Name: "L12"),
            new Branch(2, 3, 0.01, 0.05, Name: "L23"),
            new Branch(3, 4, 0.01, 0.05, Name: "L34"),
        };
        if (extra is not null) branches.Add(extra);
        return new GridModel
        {
            Name = "feeder",
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, VaDeg: slackVaDeg, BaseKv: 33),
                bus2 ?? new Bus(2, BusType.Pq, Pd: 20, Qd: 8, BaseKv: 33),
                new Bus(3, pv3 ? BusType.Pv : BusType.Pq, Pd: 10, Qd: 4, BaseKv: 33),
                new Bus(4, BusType.Pq, Pd: 15, Qd: 6, BaseKv: 33),
            },
            Generators = gens ?? new[] { new Generator(1, 0, Vg: 1.02) },
            Branches = branches,
        };
    }

    private static readonly PowerFlowOptions Tight = new() { Tolerance = 1e-10, MaxIterations = 200 };

    private static void AssertSameState(PowerFlowResult a, PowerFlowResult b, double tol)
    {
        Assert.True(a.Converged && b.Converged, "both engines must converge");
        for (int i = 0; i < a.Vm.Length; i++)
        {
            Assert.True(Math.Abs(a.Vm[i] - b.Vm[i]) < tol, $"bus {a.BusIds[i]} Vm {a.Vm[i]:R} vs {b.Vm[i]:R}");
            Assert.True(Math.Abs(a.VaDeg[i] - b.VaDeg[i]) < tol * 100, $"bus {a.BusIds[i]} Va {a.VaDeg[i]:R} vs {b.VaDeg[i]:R}");
        }
    }

    // ---- R037: one setpoint rule ---------------------------------------------------------------------------

    [Fact]
    public void Voltage_setpoint_rule_is_last_generator_at_the_bus()
    {
        GridModel m = Feeder(gens: new[] { new Generator(1, 0, Vg: 1.04), new Generator(1, 0, Vg: 1.01) });
        Assert.Equal(1.01, VoltageSetpoint.ForBus(m, 1));
        Assert.Null(VoltageSetpoint.ForBus(m, 4));
    }

    [Fact]
    public void Newton_and_sweep_hold_the_slack_at_the_same_setpoint_with_two_units()
    {
        // Before R037 the sweep took the FIRST unit (1.04) and Newton the LAST (1.01).
        GridModel m = Feeder(gens: new[] { new Generator(1, 0, Vg: 1.04), new Generator(1, 0, Vg: 1.01) });
        PowerFlowResult nr = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        PowerFlowResult bfs = new BackwardForwardSweep().Solve(m, Tight);
        Assert.Equal(1.01, nr.Vm[0]);
        Assert.Equal(1.01, bfs.Vm[0], 12);
        AssertSameState(nr, bfs, 1e-7);
    }

    [Fact]
    public void Newton_and_sweep_hold_a_pv_bus_at_the_same_setpoint_with_two_units()
    {
        // Before R037 the sweep took the HIGHEST Vg (1.05) and Newton the last one (1.03).
        GridModel m = Feeder(pv3: true, gens: new[]
        {
            new Generator(1, 0, Vg: 1.0),
            new Generator(3, 5, Vg: 1.05, Qmin: -50, Qmax: 50),
            new Generator(3, 5, Vg: 1.03, Qmin: -50, Qmax: 50),
        });
        PowerFlowResult nr = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        PowerFlowResult bfs = new BackwardForwardSweep().Solve(m, Tight);
        Assert.Equal(1.03, nr.Vm[2]);
        Assert.True(Math.Abs(bfs.Vm[2] - 1.03) < 1e-6, $"sweep PV bus held at {bfs.Vm[2]:R}");
    }

    // ---- R045: storage does not move the setpoint ----------------------------------------------------------

    private static StorageFleet IdleFleetAt(int bus) => new(new[]
    {
        new StorageSite("BESS", bus, 50, PackModel.Sized(BatteryChemistryLibrary.Lfp, 100e6, 1000, 0.5, ratedPowerW: 50e6), new IdlePolicy()),
    });

    [Fact]
    public void Storage_on_a_regulated_bus_keeps_the_existing_setpoint()
    {
        GridModel m = Feeder(gens: new[] { new Generator(1, 0, Vg: 1.04) });
        StorageFleet fleet = IdleFleetAt(1);
        fleet.Advance(0, 0.0, 1.0, 45.0, double.NaN);
        GridModel withStorage = fleet.Apply(m);

        Generator storage = withStorage.Generators[^1];
        Assert.Equal("storage", storage.Type);
        Assert.Equal(1.04, storage.Vg);   // was the placeholder 1.0, which then won the last-wins rule

        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(withStorage, Tight);
        Assert.True(r.Converged);
        Assert.Equal(1.04, r.Vm[0]);
    }

    [Fact]
    public void Storage_on_a_bus_without_a_generator_takes_the_bus_voltage()
    {
        GridModel m = Feeder(bus2: new Bus(2, BusType.Pq, Pd: 20, Qd: 8, Vm: 0.987, BaseKv: 33));
        StorageFleet fleet = IdleFleetAt(2);
        fleet.Advance(0, 0.0, 1.0, 45.0, double.NaN);
        Assert.Equal(0.987, fleet.Apply(m).Generators[^1].Vg);
    }

    [Fact]
    public void Storage_at_an_unknown_bus_fails_loud_on_apply()
    {
        GridModel m = Feeder();
        StorageFleet fleet = IdleFleetAt(99);
        fleet.Advance(0, 0.0, 1.0, 45.0, double.NaN);
        var ex = Assert.Throws<InvalidOperationException>(() => fleet.Apply(m));
        Assert.Contains("bus 99", ex.Message);
    }

    // ---- R036: the sweep honours the same inputs as Newton -------------------------------------------------

    [Fact]
    public void Sweep_keeps_the_phase_shift_of_a_tap_zero_branch()
    {
        // MATPOWER stores a unity-ratio phase shifter with ratio 0. The sweep used to map ratio 0 to Complex.One
        // and drop the 5 degree shift; Newton (AdmittanceStamp) keeps it.
        GridModel m = Feeder(first: new Branch(1, 2, 0.01, 0.05, TapRatio: 0.0, PhaseShiftDeg: 5.0, Name: "PST", IsTransformer: true));
        PowerFlowResult nr = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        PowerFlowResult bfs = new BackwardForwardSweep().Solve(m, Tight);
        AssertSameState(nr, bfs, 1e-7);
        Assert.True(bfs.VaDeg[1] < -4.0, $"the 5 degree shift must appear across the PST (bus 2 at {bfs.VaDeg[1]:F3} deg)");
    }

    [Fact]
    public void Sweep_holds_the_slack_at_its_own_angle()
    {
        GridModel m = Feeder(slackVaDeg: 12.5);
        PowerFlowResult nr = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        PowerFlowResult bfs = new BackwardForwardSweep().Solve(m, Tight);
        Assert.Equal(12.5, bfs.VaDeg[0], 10);
        AssertSameState(nr, bfs, 1e-7);
    }

    [Fact]
    public void Sweep_with_q_limits_off_holds_the_pv_setpoint_like_newton()
    {
        // A PV unit whose Qmax is far too small to hold 1.05: with EnforceQLimits off, Newton holds the setpoint
        // with unlimited Q. The sweep used to clamp regardless and let the bus sag.
        GridModel m = Feeder(pv3: true, gens: new[] { new Generator(1, 0, Vg: 1.0), new Generator(3, 0, Vg: 1.05, Qmin: -0.1, Qmax: 0.1) });
        var off = Tight with { EnforceQLimits = false };
        PowerFlowResult nr = new NewtonRaphsonPowerFlow().Solve(m, off);
        PowerFlowResult bfs = new BackwardForwardSweep().Solve(m, off);
        Assert.Equal(1.05, nr.Vm[2]);
        Assert.True(Math.Abs(bfs.Vm[2] - 1.05) < 1e-6, $"sweep PV bus at {bfs.Vm[2]:R} with limits off");

        // And with limits on, the sweep still saturates at Qmax (the bus falls below its setpoint).
        PowerFlowResult bfsOn = new BackwardForwardSweep().Solve(m, Tight);
        Assert.True(bfsOn.Vm[2] < 1.04, "with limits on, the unit saturates and the bus sags");
    }

    [Fact]
    public void Sweep_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            new BackwardForwardSweep().Solve(Feeder(), new PowerFlowOptions { Cancellation = cts.Token }));
    }

    [Fact]
    public void Sweep_rejects_a_zero_q_limit_pass_cap_like_newton()
    {
        var bad = new PowerFlowOptions { MaxQLimitPasses = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackwardForwardSweep().Solve(Feeder(), bad));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NewtonRaphsonPowerFlow().Solve(Feeder(), bad));
    }

    [Fact]
    public void Sweep_warm_start_fails_loud_on_an_unusable_stored_voltage_but_flat_start_solves()
    {
        GridModel m = Feeder(bus2: new Bus(2, BusType.Pq, Pd: 20, Qd: 8, Vm: 0.0, BaseKv: 33));
        var ex = Assert.Throws<InvalidDataException>(() => new BackwardForwardSweep().Solve(m, new PowerFlowOptions { FlatStart = false }));
        Assert.Contains("bus 2", ex.Message);
        Assert.True(new BackwardForwardSweep().Solve(m, new PowerFlowOptions { FlatStart = true }).Converged);
    }

    [Fact]
    public void Sweep_warm_and_flat_starts_reach_the_same_solution()
    {
        GridModel m = Feeder(bus2: new Bus(2, BusType.Pq, Pd: 20, Qd: 8, Vm: 0.93, VaDeg: -3, BaseKv: 33));
        PowerFlowResult warm = new BackwardForwardSweep().Solve(m, Tight with { FlatStart = false });
        PowerFlowResult flat = new BackwardForwardSweep().Solve(m, Tight with { FlatStart = true });
        AssertSameState(warm, flat, 1e-8);
    }

    [Fact]
    public void Sweep_failure_keeps_line_flows_index_aligned_with_branches()
    {
        // A disconnected network (L23 open leaves buses 3 and 4 with no path to the slack) cannot be swept.
        GridModel m = Feeder().CopyWith(branches: new[]
        {
            new Branch(1, 2, 0.01, 0.05, Name: "L12"),
            new Branch(3, 4, 0.01, 0.05, Name: "L34"),
            new Branch(2, 3, 0.01, 0.05, Name: "L23", InService: false),
        });
        PowerFlowResult r = new BackwardForwardSweep().Solve(m);
        Assert.False(r.Converged);
        Assert.Equal(m.Branches.Count, r.LineFlows.Count);
        for (int i = 0; i < m.Branches.Count; i++)
        {
            Assert.Equal(m.Branches[i].Name, r.LineFlows[i].Name);
            Assert.Equal(0.0, r.LineFlows[i].PFromMw);
        }
    }

    [Fact]
    public void LooksRadial_ignores_an_open_tie()
    {
        // A feeder plus one normally-open tie 4-1: electrically radial, so Auto picks the sweep.
        GridModel m = Feeder(extra: new Branch(4, 1, 0.01, 0.05, Name: "TIE", InService: false));
        Assert.True(SolverSelector.LooksRadial(m));
        Assert.Equal(SolverKind.DistributionSweep, SolverSelector.Resolve(m));
        // Closed, it is a loop: not radial.
        GridModel closed = Feeder(extra: new Branch(4, 1, 0.01, 0.05, Name: "TIE"));
        Assert.False(SolverSelector.LooksRadial(closed));
    }

    // ---- R065: ZIP load honoured everywhere ----------------------------------------------------------------

    private static GridModel ZipFeeder() => Feeder(bus2: new Bus(2, BusType.Pq, Pd: 20, Qd: 8, BaseKv: 33,
        Zip: new ZipLoad(Ap: 0.5, Bp: 0.3, Cp: 0.2, Aq: 0.7, Bq: 0.2, Cq: 0.1)),
        gens: new[] { new Generator(1, 0, Vg: 0.95) });

    [Fact]
    public void Sweep_solves_a_zip_load_like_newton()
    {
        GridModel m = ZipFeeder();
        PowerFlowResult nr = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        PowerFlowResult bfs = new BackwardForwardSweep().Solve(m, Tight);
        AssertSameState(nr, bfs, 1e-7);
        Assert.True(bfs.MaxMismatch < 1e-6, $"sweep mismatch against the ZIP load {bfs.MaxMismatch:E2}");
    }

    [Fact]
    public void Physics_audit_of_an_exactly_solved_zip_case_holds()
    {
        // Laws 6 (KCL) and 8 used to fail by Pd*(PMultiplier(V)-1) because the audit subtracted the nominal load.
        GridModel m = ZipFeeder();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        PhysicsLawReport audit = PhysicsLaws.Audit(m, r);
        Assert.True(audit.AllHold, string.Join("; ", audit.Violations.Select(v => $"law {v.Number} {v.Residual:E2}")));
    }

    [Fact]
    public void Q_limit_check_measures_the_generator_against_the_zip_load_actually_drawn()
    {
        // PV bus 2 at 1.05 pu with a constant-impedance reactive load Qd = 100 MVAr: at 1.05 pu it draws
        // 110.25 MVAr. Holding 1.05 against the 1.00 pu slack also exports about 52.5 MVAr, so the unit makes
        // about 162.75 MVAr. Measured against the NOMINAL 100 MVAr (the old rule) it looked like ~152.5 MVAr,
        // under the 158 MVAr Qmax, so the bus was never limited and the unit ran ~5 MVAr past its limit.
        var m = new GridModel
        {
            Name = "zip-qlimit",
            BaseMva = 100,
            Buses = new[]
            {
                new Bus(1, BusType.Slack),
                new Bus(2, BusType.Pv, Qd: 100, Zip: new ZipLoad(Aq: 1, Bq: 0, Cq: 0)),
            },
            Generators = new[] { new Generator(1, 0, Vg: 1.0), new Generator(2, 0, Vg: 1.05, Qmin: -500, Qmax: 158) },
            Branches = new[] { new Branch(1, 2, 0.0, 0.1) },
        };
        foreach (IPowerFlowSolver engine in new IPowerFlowSolver[] { new NewtonRaphsonPowerFlow(), new SparseNewtonRaphsonPowerFlow() })
        {
            PowerFlowResult r = engine.Solve(m, Tight);
            Assert.True(r.Converged);
            double qLoadActual = 100 * r.Vm[1] * r.Vm[1];
            double qGen = r.QInjMvar[1] + qLoadActual;
            Assert.True(Math.Abs(qGen - 158) < 1e-6, $"{engine.GetType().Name}: unit makes {qGen:R} MVAr against a 158 MVAr limit");
            Assert.True(r.Vm[1] < 1.05, "a limited unit cannot hold its setpoint");
        }
    }

    [Fact]
    public void Load_at_voltage_returns_nominal_load_untouched_for_constant_power()
    {
        var plain = new Bus(1, BusType.Pq, Pd: 12.3, Qd: 4.56);
        Assert.Equal((12.3, 4.56), LoadAtVoltage.Mw(plain, 0.93));
        var constZip = plain with { Zip = new ZipLoad() };   // (0,0,1): does not vary with V
        Assert.Equal((12.3, 4.56), LoadAtVoltage.Mw(constZip, 0.93));
        var z = plain with { Zip = new ZipLoad(Ap: 1, Cp: 0, Aq: 0, Bq: 1, Cq: 0) };
        (double p, double q) = LoadAtVoltage.Mw(z, 0.9);
        Assert.Equal(12.3 * (0.9 * 0.9), p, 14);
        Assert.Equal(4.56 * 0.9, q, 14);
    }

    // ---- R281: audit alignment after a skipped branch ------------------------------------------------------

    [Fact]
    public void Physics_audit_keeps_line_flow_alignment_after_a_branch_it_cannot_audit()
    {
        // Solve the feeder, then audit a model that carries an extra branch at the FRONT whose endpoint (99) is
        // not in the result, with a matching placeholder LineFlow, and one later flow tampered by +1 MW. Before
        // the fix the skipped branch did not advance the ordinal, every later branch was compared with the wrong
        // flow, the endpoint guard skipped the comparisons, and law 2 reported Holds with residual 0.
        GridModel m = Feeder();
        PowerFlowResult r = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        var flows = new List<LineFlow> { new(1, 99, 0, 0, 0, 0, 0, 0, "dangling") };
        flows.AddRange(r.LineFlows);
        flows[2] = flows[2] with { PFromMw = flows[2].PFromMw + 1.0 };
        var tampered = new PowerFlowResult
        {
            Converged = r.Converged, Iterations = r.Iterations, MaxMismatch = r.MaxMismatch, BusIds = r.BusIds,
            Vm = r.Vm, VaDeg = r.VaDeg, PInjMw = r.PInjMw, QInjMvar = r.QInjMvar,
            TotalLossMw = r.TotalLossMw, TotalLossMvar = r.TotalLossMvar, LineFlows = flows,
        };
        var branches = new List<Branch> { new(1, 99, 0.01, 0.05, Name: "dangling") };
        branches.AddRange(m.Branches);
        GridModel withDangling = m.CopyWith(branches: branches);

        LawResult ohm = PhysicsLaws.Audit(withDangling, tampered).Laws.Single(l => l.Number == 2);
        Assert.Equal(LawStatus.Violated, ohm.Status);
        Assert.Equal(1.0, ohm.Residual, 6);
    }

    // ---- R283: PV bus without a generator ------------------------------------------------------------------

    [Fact]
    public void A_pv_bus_with_no_generator_solves_exactly_as_pq_on_both_engines()
    {
        GridModel pv = Feeder(pv3: true);   // bus 3 typed PV, no unit on it
        GridModel pq = Feeder(pv3: false);
        foreach (PowerFlowOptions opt in new[]
        {
            new PowerFlowOptions(), new PowerFlowOptions { EnforceQLimits = false },
            new PowerFlowOptions { RevertQLimits = true },
        })
        {
            PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(pv, opt);
            PowerFlowResult b = new NewtonRaphsonPowerFlow().Solve(pq, opt);
            PowerFlowResult c = new SparseNewtonRaphsonPowerFlow().Solve(pv, opt);
            Assert.True(a.Converged && b.Converged && c.Converged);
            Assert.Equal(b.Vm, a.Vm);       // bit-identical to the PQ model
            Assert.Equal(b.VaDeg, a.VaDeg);
            Assert.Equal(a.Vm, c.Vm);       // dense == sparse
            Assert.Equal(a.VaDeg, c.VaDeg);
            Assert.All(a.Vm, v => Assert.True(v > 0.5));
        }
    }
}
