using System.Numerics;
using GridSim.Core.Diagnostics;
using GridSim.Core.Dynamics.Distributed;
using GridSim.Core.Dynamics.Equivalents;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.IO;
using GridSim.Core.IO.Json;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using GridSim.Core.PowerFlow.Equivalents;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP31: the short-circuit equivalent (<see cref="ShortCircuitEquivalent"/>) and the coherency-based dynamic
/// equivalent (<see cref="CoherencyGrouping"/>, <see cref="InertialAggregation"/>).
///
/// <para>Sources of the expected values. The exactness algebra restated in each test (a Kron reduction is exact, so the
/// boundary impedances of the reduced case are the full case's; identical machines at one bus move together, so their
/// aggregate is exact). An independent NumPy 2.5 implementation written for this package, outside the repository: dense
/// IEC 60909 sequence admittances of case14 with this file's sequence data, numpy.linalg.solve for every Thevenin impedance
/// and Schur complement, its own power flow and classical linearisation of Kundur's two-area system, Chow's slow-coherency
/// steps and the ideal-transformer aggregation. Every pinned figure below was printed by that script before it was pinned.
/// Data: MATPOWER case14 (data/matpower/lib/case14.m); P. Kundur, <i>Power System Stability and Control</i>, McGraw-Hill
/// 1994, Example 12.6 (the two-area system data).</para>
/// </summary>
public sealed class Rc5Wp31EquivalentsTests
{
    private const double Eps = 2.220446049250313e-16;

    /// <summary>
    /// The tolerance on |Ik''_reduced - Ik''_full| / Ik''_full: an estimate with an observed margin, not a proven bound
    /// (review R1 finding 6: n kappa u leaves out the LU growth factor). Each Ik'' comes from one sparse LU solve of the fault network
    /// (n = 14 buses full, 14 reduced with the fictitious buses), and the reduced network from one Kron reduction of the
    /// external part; each step is backward stable, so its relative error is at most about n * kappa * u, with
    /// kappa(Y) = 59 for the positive and 36 for the zero sequence of this case (numpy.linalg.cond) and u = Eps / 2:
    /// 14 * 59 * 1.1e-16 = 9.1e-14 per step, about 3e-13 for the three steps (no growth-factor term). 1e-12 leaves a factor
    /// above 3 on that estimate; the NumPy scripts observed at most 6.8e-16 (the review's), so it is an observed-margin bound.
    /// </summary>
    private const double ExactRel = 1e-12;

    private static readonly int[] Kept = { 1, 2, 3, 4, 5, 7, 8 };

    private static readonly PowerFlowOptions Tight = new() { EnforceQLimits = false, Tolerance = 1e-12 };

    /// <summary>
    /// case14 with base kV 132 at every bus (IEC 60909 needs one at the fault bus) and zero-sequence data: transformers
    /// 4-7 dYN (earthed at 7), 4-9 YNyn, 5-6 YNd (earthed at 5) and the 7-9 unit YNyn; machine zero-sequence impedances
    /// j0.05 (bus 1), j0.06 (bus 2) and j0.08 (bus 6) on their 100 MVA base; every other line Z0 = 3 Z1 (the default
    /// ratio). With <paramref name="mutual"/>, a zero-sequence mutual j0.1 pu between the external lines 6-12 and 6-13.
    /// </summary>
    private static GridModel Case14Sequence(bool mutual)
    {
        GridModel m = IeeeCases.Case14();
        var buses = m.Buses.Select(b => b with { BaseKv = 132 }).ToList();
        var branches = m.Branches.ToList();
        Assert.Equal((4, 7), (branches[7].From, branches[7].To));
        Assert.Equal((4, 9), (branches[8].From, branches[8].To));
        Assert.Equal((5, 6), (branches[9].From, branches[9].To));
        Assert.Equal((7, 9), (branches[14].From, branches[14].To));
        Assert.Equal((6, 12), (branches[11].From, branches[11].To));
        Assert.Equal((6, 13), (branches[12].From, branches[12].To));
        branches[7] = branches[7] with { FromWinding = WindingConnection.Delta, ToWinding = WindingConnection.WyeGrounded };
        branches[8] = branches[8] with { FromWinding = WindingConnection.WyeGrounded, ToWinding = WindingConnection.WyeGrounded };
        branches[9] = branches[9] with { FromWinding = WindingConnection.WyeGrounded, ToWinding = WindingConnection.Delta };
        branches[14] = branches[14] with { FromWinding = WindingConnection.WyeGrounded, ToWinding = WindingConnection.WyeGrounded };
        var gens = m.Generators.Select(g => g.Bus switch
        {
            1 => g with { ZeroSequenceZPu = new MachineZeroSequence(0.0, 0.05) },
            2 => g with { ZeroSequenceZPu = new MachineZeroSequence(0.0, 0.06) },
            6 => g with { ZeroSequenceZPu = new MachineZeroSequence(0.0, 0.08) },
            _ => g,
        }).ToList();
        var mutuals = mutual
            ? new List<ZeroSequenceMutual> { new(11, 6, 12, 12, 6, 13, 0.0, 0.1) }
            : new List<ZeroSequenceMutual>();
        return new GridModel
        {
            BaseMva = m.BaseMva, Name = "case14-seq", FrequencyHz = m.FrequencyHz,
            Buses = buses, Generators = gens, Branches = branches, ZeroSequenceMutuals = mutuals,
        };
    }

    private static void AssertRel(double expected, double actual, double tol, string what)
        => Assert.True(Math.Abs(actual - expected) <= tol * Math.Abs(expected),
            FormattableString.Invariant($"{what}: {actual:R} vs {expected:R}, relative {Math.Abs(actual - expected) / Math.Abs(expected):E2} > {tol:E1}"));

    // ---- 1. Short-circuit equivalent: exactness ----

    [Fact]
    public void Sc_equivalent_partition_is_the_kept_set_with_boundary_4_5_7()
    {
        // The spec's external set {9..14} has ties to 4, 7 and 6, so its boundary would be {4, 6, 7}; the boundary {4, 5, 7}
        // it names belongs to the external set {6, 9..14} (ties 4-9, 5-6, 7-9), which is the one used here.
        ShortCircuitEquivalentResult r = ShortCircuitEquivalent.Reduce(Case14Sequence(false), Kept);
        Assert.Equal(new[] { 4, 5, 7 }, r.Network.BoundaryBusIds);
        Assert.Equal(new[] { 6, 9, 10, 11, 12, 13, 14 }, r.Network.ExternalBusIds);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(7)] [InlineData(8)]
    public void Sc_equivalent_gives_the_full_three_phase_current_at_every_kept_bus(int bus)
    {
        GridModel full = Case14Sequence(false);
        GridModel red = ShortCircuitEquivalent.Reduce(full, Kept).Network.Model;
        AssertRel(Iec60909.ThreePhase(full, bus).IkppPu, Iec60909.ThreePhase(red, bus).IkppPu, ExactRel, $"3ph Ik'' at bus {bus}");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(7)] [InlineData(8)]
    public void Sc_equivalent_gives_the_full_line_to_line_current_at_every_kept_bus(int bus)
    {
        GridModel full = Case14Sequence(false);
        GridModel red = ShortCircuitEquivalent.Reduce(full, Kept).Network.Model;
        AssertRel(Iec60909.LineToLine(full, bus).IkppPu, Iec60909.LineToLine(red, bus).IkppPu, ExactRel, $"LL Ik'' at bus {bus}");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(7)] [InlineData(8)]
    public void Sc_equivalent_gives_the_full_single_line_to_earth_current_at_every_kept_bus(int bus)
    {
        GridModel full = Case14Sequence(false);
        GridModel red = ShortCircuitEquivalent.Reduce(full, Kept).Network.Model;
        AssertRel(Iec60909.SingleLineToGround(full, bus).IkppPu, Iec60909.SingleLineToGround(red, bus).IkppPu, ExactRel, $"SLG Ik'' at bus {bus}");
    }

    /// <summary>The full case's boundary currents against the independent NumPy sequence networks (c = 1.1 at 132 kV): this
    /// pins the test data (that GridSim's sequence networks are the ones the script built), so the exactness tests compare
    /// the networks intended. Bound as <see cref="ExactRel"/> (two different LU algorithms on the same matrices).</summary>
    [Theory]
    [InlineData(4, 13.291629317238353, 11.510888646414427, 10.724597757175335)]
    [InlineData(5, 13.043784619828397, 11.296248842264138, 11.84192485376285)]
    [InlineData(7, 8.256415927596228, 7.150265937508794, 7.90555382008511)]
    public void Full_case_boundary_currents_match_the_independent_script(int bus, double threePhase, double lineToLine, double slg)
    {
        GridModel full = Case14Sequence(false);
        AssertRel(threePhase, Iec60909.ThreePhase(full, bus).IkppPu, ExactRel, $"3ph at {bus}");
        AssertRel(lineToLine, Iec60909.LineToLine(full, bus).IkppPu, ExactRel, $"LL at {bus}");
        AssertRel(slg, Iec60909.SingleLineToGround(full, bus).IkppPu, ExactRel, $"SLG at {bus}");
    }

    [Fact]
    public void Sc_equivalent_writes_the_zero_sequence_blocking_and_the_sources_as_derived()
    {
        ShortCircuitEquivalentResult r = ShortCircuitEquivalent.Reduce(Case14Sequence(false), Kept);
        GridModel red = r.Network.Model;
        // Bus 5 reaches the external zero-sequence network only through the YNd 5-6 shunt: no zero-sequence coupling to 4
        // or 7 (the script's Y0_eq has exact zeros there), so those equivalent branches block the zero sequence.
        Branch b45 = red.Branches.Single(b => b.Name == "SC:4-5");
        Assert.Equal(WindingConnection.Delta, b45.FromWinding);
        Assert.Equal(WindingConnection.Delta, b45.ToWinding);
        Branch b47 = red.Branches.Single(b => b.Name == "SC:4-7");
        Assert.Null(b47.FromWinding);
        Assert.True(b47.R0 != 0 || b47.X0 != 0);
        // Bus 5's zero-sequence row sum is the YNd shunt alone: 1 / (j 0.25202), so its link carries Z0 = j0.25202.
        Branch l5 = red.Branches.Single(b => b.Name == "SC:5");
        Assert.Equal(WindingConnection.WyeGrounded, l5.FromWinding);
        Assert.Equal(WindingConnection.Delta, l5.ToWinding);
        AssertRel(0.25202, l5.X0, 4 * Eps, "bus 5 zero-sequence shunt reactance");
        Assert.True(Math.Abs(l5.R0) <= 4 * Eps * 0.25202, $"bus 5 zero-sequence shunt resistance {l5.R0}");
        // One source per boundary bus (an external machine at 6 and off-nominal taps reach all three).
        Assert.Equal(3, red.Generators.Count(g => g.Name is not null && g.Name.StartsWith("SC:", StringComparison.Ordinal)));
        Assert.Equal(7 + 3, red.BusCount);
    }

    [Fact]
    // Renamed (review R1 finding 3, ruled 2026-11-24; name only, asserts unchanged): was
    // Sc_thevenin_report_is_the_inverse_of_the_reduced_matrices, which none of its asserts checks. The inverse is checked
    // in Rc5Wp31ScWriterTests (the decoupled bus 5).
    public void Sc_thevenin_report_has_z2_equal_to_z1_and_a_finite_z0_at_every_earthed_boundary_bus()
    {
        ShortCircuitEquivalentResult r = ShortCircuitEquivalent.Reduce(Case14Sequence(false), Kept);
        Assert.Equal(new[] { 4, 5, 7 }, r.Thevenin.Select(t => t.BusId));
        foreach (SequenceTheveninImpedance t in r.Thevenin)
        {
            Assert.Equal(t.Z1Pu, t.Z2Pu);   // X2 = X''d on every external machine: Y2 = Y1
            Assert.True(t.Z1Pu.Real > 0 || t.Z1Pu.Imaginary > 0);
            Assert.True(double.IsFinite(t.Z0Pu.Magnitude), $"bus {t.BusId}: the external zero-sequence network is earthed");
        }
    }

    // ---- 2. A mutual inside the external area ----

    [Theory]
    [InlineData(4, 10.724342038431338)]
    [InlineData(5, 11.84184526894696)]
    [InlineData(7, 7.904696978535603)]
    public void Sc_equivalent_is_exact_with_a_zero_sequence_mutual_in_the_external_area(int bus, double scriptSlg)
    {
        GridModel full = Case14Sequence(true);
        GridModel red = ShortCircuitEquivalent.Reduce(full, Kept).Network.Model;
        Assert.Empty(red.ZeroSequenceMutuals);   // both coupled lines were external: the coupling is inside the equivalent
        double f = Iec60909.SingleLineToGround(full, bus).IkppPu;
        AssertRel(f, Iec60909.SingleLineToGround(red, bus).IkppPu, ExactRel, $"SLG Ik'' with mutual at bus {bus}");
        AssertRel(scriptSlg, f, ExactRel, $"SLG Ik'' with mutual at bus {bus} against the script");
    }

    [Fact]
    public void The_mutual_changes_the_earth_fault_current_so_the_test_is_not_vacuous()
    {
        // The script: bus 7 SLG 7.90555382008511 without, 7.904696978535603 with the mutual (1.1e-4 relative).
        double without = Iec60909.SingleLineToGround(Case14Sequence(false), 7).IkppPu;
        double with = Iec60909.SingleLineToGround(Case14Sequence(true), 7).IkppPu;
        Assert.True(Math.Abs(with - without) > 1e-5 * without, $"{with} vs {without}");
    }

    // ---- Short-circuit refusals ----

    [Fact]
    public void Sc_equivalent_refuses_a_converter_unit_in_the_external_network()
    {
        GridModel m = Case14Sequence(false);
        var gens = m.Generators.Select(g => g.Bus == 6 ? g with { Type = "solar" } : g).ToList();
        GridModel bad = new() { BaseMva = m.BaseMva, Name = m.Name, Buses = m.Buses, Generators = gens, Branches = m.Branches };
        var ex = Assert.Throws<InvalidOperationException>(() => ShortCircuitEquivalent.Reduce(bad, Kept));
        Assert.Contains("converter-interfaced unit", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sc_equivalent_refuses_an_external_machine_with_x2_different_from_xd()
    {
        GridModel m = Case14Sequence(false);
        var gens = m.Generators.Select(g => g.Bus == 6 ? g with { NegativeSeqXPu = 0.25 } : g).ToList();
        GridModel bad = new() { BaseMva = m.BaseMva, Name = m.Name, Buses = m.Buses, Generators = gens, Branches = m.Branches };
        var ex = Assert.Throws<InvalidOperationException>(() => ShortCircuitEquivalent.Reduce(bad, Kept));
        Assert.Contains("X2 different from X''d", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sc_equivalent_refuses_a_mutual_across_the_boundary()
    {
        GridModel m = Case14Sequence(false);
        Assert.Equal((4, 5), (m.Branches[6].From, m.Branches[6].To));
        GridModel bad = new()
        {
            BaseMva = m.BaseMva, Name = m.Name, Buses = m.Buses, Generators = m.Generators, Branches = m.Branches,
            ZeroSequenceMutuals = new List<ZeroSequenceMutual> { new(6, 4, 5, 11, 6, 12, 0.0, 0.05) },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => ShortCircuitEquivalent.Reduce(bad, Kept));
        Assert.Contains("across the boundary", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sc_equivalent_refuses_an_external_slack()
        => Assert.Throws<InvalidOperationException>(() => ShortCircuitEquivalent.Reduce(Case14Sequence(false), new[] { 2, 3, 4, 5 }));

    // ---- 3. Identical parallel machines: exact ----

    /// <summary>Bus 1: two identical GENCLS machines (100 MVA, H = 3 s, X'd = 0.3), 50 MW each at 1.02 pu; bus 2: a 150 MW +
    /// 30 Mvar load; bus 3: the slack machine (1000 MVA, H = 5 s, X'd = 0.2). Lines 1-2 and 2-3, 0.01 + j0.1 pu.</summary>
    private static GridModel ParallelCase() => new()
    {
        BaseMva = 100, Name = "parallel", FrequencyHz = 50,
        Buses = new[]
        {
            new Bus(1, BusType.Pv, BaseKv: 132), new Bus(2, BusType.Pq, Pd: 150, Qd: 30, BaseKv: 132), new Bus(3, BusType.Slack, BaseKv: 132),
        },
        Generators = new[]
        {
            new Generator(1, Pg: 50, Vg: 1.02, MBase: 100, H: 3, Name: "G1a"),
            new Generator(1, Pg: 50, Vg: 1.02, MBase: 100, H: 3, Name: "G1b"),
            new Generator(3, Pg: 0, Vg: 1.0, MBase: 1000, H: 5, Name: "G3"),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1), new Branch(2, 3, 0.01, 0.1) },
    };

    /// <summary>
    /// Two identical machines at one bus start at the same internal voltage and angle and see the same network, so they
    /// move identically for every disturbance; their sum is one machine of twice the rating with the same per-unit H and
    /// X'd (X'd 0.3 on 200 MVA is the parallel of two 0.3 on 100 MVA: 0.15 pu on the 100 MVA system base). So the
    /// aggregate is exact, not an approximation. The spec states "X' = 0.15 on 200 MVA": that is the parallel combination
    /// on the 100 MVA system base, not on 200 MVA; the code (and this test) use 0.3 on 200 MVA, the same machine.
    ///
    /// <para>The bound: both runs round differently (the aggregate's X'd and H are formed by a division), by about one ulp
    /// in the machine data. The NumPy script ran this case (RK4, 2 ms, 1 s, a bolted fault at bus 2 from 0.1 to 0.2 s) and
    /// moved the aggregate's X'd and H by one ulp: the angles then differ by 3.3e-16 relative and the speed by 1.1e-15 of
    /// its amplitude. 1e-12 (the spec's figure) holds a margin of about 1000.</para>
    /// </summary>
    [Fact]
    public void Identical_parallel_machines_aggregate_exactly_at_every_step()
    {
        GridModel model = ParallelCase();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model, Tight);
        Assert.True(pf.Converged);
        var machines = new[] { new ClassicalMachineData(0.3), new ClassicalMachineData(0.3), new ClassicalMachineData(0.2) };
        DynamicEquivalent eq = InertialAggregation.Build(model, pf, new[] { new[] { 0, 1 } }, machines, ProvenanceTag.Synthetic);

        DynamicEquivalentGroup grp = Assert.Single(eq.Groups);
        Assert.False(grp.Merged);
        Assert.Equal(1, grp.BusId);
        Assert.Equal(200.0, grp.Machine.SMva);
        Assert.Equal(3.0, grp.Machine.InertiaH);
        AssertRel(0.3, grp.Machine.XdpPu, 4 * Eps, "X'd on 200 MVA");
        Assert.Equal(model.Buses, eq.Model.Buses);       // one terminal bus: the network is not changed
        Assert.Equal(model.Branches, eq.Model.Branches);
        Assert.Equal(2, eq.Model.Generators.Count);

        // The same solved base case serves both: the buses and every bus injection are the same.
        ClassicalInit.Prepared full = ClassicalInit.FromSolvedCase(model, pf, new[] { 0.3, 0.3, 0.2 }, faultBusId: 2);
        ClassicalInit.Prepared agg = ClassicalInit.FromSolvedCase(eq.Model, pf, eq.XdpMachineBase(), faultBusId: 2);
        const double dt = 0.002, dur = 1.0, on = 0.1, clear = 0.2;
        ClassicalMultiMachine.Result rf = ClassicalMultiMachine.Simulate(full.H, new double[3], full.Pm, full.Eq, full.Delta0, full.YReduced,
            50, dt, dur, new ClassicalMultiMachine.Fault(on, clear, full.YReducedFaulted!));
        ClassicalMultiMachine.Result ra = ClassicalMultiMachine.Simulate(agg.H, new double[2], agg.Pm, agg.Eq, agg.Delta0, agg.YReduced,
            50, dt, dur, new ClassicalMultiMachine.Fault(on, clear, agg.YReducedFaulted!));
        Assert.True(rf.StayedInSynchronism);
        Assert.True(ra.StayedInSynchronism);

        int steps = rf.Machines[0].DeltaRad.Count;
        Assert.Equal(steps, ra.Machines[0].DeltaRad.Count);
        Assert.True(steps > 400, $"{steps} samples");
        double wAmp = 0;
        for (int k = 0; k < steps; k++) wAmp = Math.Max(wAmp, Math.Abs(rf.Machines[0].SpeedPu[k]));
        Assert.True(wAmp > 1e-4, $"the fault must move the machines (speed amplitude {wAmp})");
        double swing = rf.Machines[0].DeltaRad.Max() - rf.Machines[0].DeltaRad.Min();
        Assert.True(swing > 0.05, $"the fault must swing the rotors ({swing} rad)");

        for (int k = 0; k < steps; k++)
        {
            for (int member = 0; member < 2; member++)
            {
                double d = rf.Machines[member].DeltaRad[k];
                Assert.True(Math.Abs(ra.Machines[0].DeltaRad[k] - d) <= 1e-12 * Math.Max(1.0, Math.Abs(d)), FormattableString.Invariant(
                    $"step {k}: aggregate angle {ra.Machines[0].DeltaRad[k]:R} vs member {member} {d:R}"));
                Assert.True(Math.Abs(ra.Machines[0].SpeedPu[k] - rf.Machines[member].SpeedPu[k]) <= 1e-12 * wAmp, FormattableString.Invariant(
                    $"step {k}: aggregate speed {ra.Machines[0].SpeedPu[k]:R} vs member {member} {rf.Machines[member].SpeedPu[k]:R}"));
            }
            double d3 = rf.Machines[2].DeltaRad[k];
            Assert.True(Math.Abs(ra.Machines[1].DeltaRad[k] - d3) <= 1e-12 * Math.Max(1.0, Math.Abs(d3)), $"step {k}: slack machine angle");
        }

        // Terminal quantity: the electrical power the group delivers, sum of the members against the aggregate, at every
        // step outside the fault window (the unfaulted network; within a step of an edge the window decides which applies).
        double pScale = full.Pm[0] + full.Pm[1];
        for (int k = 0; k < steps; k++)
        {
            double t = k * dt;
            if (t > on - 1.5 * dt && t < clear + 1.5 * dt) continue;
            double pFull = Pe(full, rf, 0, k) + Pe(full, rf, 1, k);
            double pAgg = Pe(agg, ra, 0, k);
            Assert.True(Math.Abs(pAgg - pFull) <= 1e-12 * Math.Abs(pScale), FormattableString.Invariant($"step {k}: group Pe {pAgg:R} vs {pFull:R}"));
        }
    }

    /// <summary>Machine i's electrical power at sample k: E_i sum_j E_j (G_ij cos d_ij + B_ij sin d_ij), the unfaulted
    /// reduced network (pu on the system base).</summary>
    private static double Pe(ClassicalInit.Prepared p, ClassicalMultiMachine.Result r, int i, int k)
    {
        double s = 0;
        for (int j = 0; j < p.Eq.Length; j++)
        {
            double dij = r.Machines[i].DeltaRad[k] - r.Machines[j].DeltaRad[k];
            s += p.Eq[j] * (p.YReduced[i, j].Real * GridSim.Core.Numerics.DetMath.Cos(dij) + p.YReduced[i, j].Imaginary * GridSim.Core.Numerics.DetMath.Sin(dij));
        }
        return p.Eq[i] * s;
    }

    // ---- 4. The inertial aggregation formula ----

    [Fact]
    public void Inertial_aggregation_of_100_and_300_mva_machines_is_exact()
    {
        // S_eq = 100 + 300 = 400; H_eq = (100 x 3 + 300 x 5) / 400 = 1800 / 400 = 4.5 s; every operation is exact.
        AggregateMachine a = InertialAggregation.Aggregate(new[] { 100.0, 300.0 }, new[] { 3.0, 5.0 }, new[] { 0.0, 0.0 }, new[] { 0.3, 0.3 });
        Assert.Equal(400.0, a.SMva);
        Assert.Equal(4.5, a.InertiaH);
        Assert.Equal(0.0, a.Damping);
    }

    [Fact]
    public void Inertial_aggregation_damping_and_reactance_follow_the_rating_weights()
    {
        // D_eq = (100 x 1 + 300 x 3) / 400 = 2.5, exact. X'd: 0.3 on 100 MVA is 1.2 on 400 MVA and 0.3 on 300 MVA is 0.4 on
        // 400 MVA, in parallel 1 / (1/1.2 + 1/0.4) = 0.3 on 400 MVA; four roundings, so within 4 ulp.
        AggregateMachine a = InertialAggregation.Aggregate(new[] { 100.0, 300.0 }, new[] { 3.0, 5.0 }, new[] { 1.0, 3.0 }, new[] { 0.3, 0.3 });
        Assert.Equal(2.5, a.Damping);
        AssertRel(0.3, a.XdpPu, 4 * Eps, "X'd_eq");
        // Referred through ideal transformers of ratio 0.5 and 1: the first machine's admittance scales by 0.25.
        AggregateMachine b = InertialAggregation.Aggregate(new[] { 100.0, 300.0 }, new[] { 3.0, 5.0 }, new[] { 0.0, 0.0 }, new[] { 0.3, 0.3 }, new[] { 0.5, 1.0 });
        AssertRel(400.0 / (0.25 * 100.0 / 0.3 + 300.0 / 0.3), b.XdpPu, 4 * Eps, "referred X'd_eq");
    }

    [Fact]
    public void Inertial_aggregation_refuses_bad_members()
    {
        Assert.Throws<ArgumentException>(() => InertialAggregation.Aggregate(Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => InertialAggregation.Aggregate(new[] { 100.0 }, new[] { 0.0 }, new[] { 0.0 }, new[] { 0.3 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => InertialAggregation.Aggregate(new[] { 100.0 }, new[] { 3.0 }, new[] { 0.0 }, new[] { -0.3 }));
    }

    // ---- 5. Coherency on Kundur's two-area system ----

    /// <summary>
    /// Kundur's two-area system (Example 12.6): 100 MVA base, 60 Hz. Four 900 MVA machines (X'd 0.3, H 6.5 s for G1 and
    /// G2 and 6.175 s for G3 and G4, on 900 MVA) behind 0 + j0.15 pu step-up transformers (900 MVA, so j0.15/9 on 100 MVA);
    /// 230 kV lines at 0.0001 + j0.001 pu/km with 0.00175 pu/km charging: 5-6 25 km, 6-7 10 km, 7-8 and 8-9 two circuits of
    /// 110 km, 9-10 10 km, 10-11 25 km. Loads 967 MW + 100 Mvar with 200 Mvar of capacitors at bus 7 and 1767 MW + 100 Mvar
    /// with 350 Mvar at bus 9. G1 700 MW at 1.03 pu, G2 700 MW at 1.01, G3 the slack at 1.03, G4 700 MW at 1.01.
    /// </summary>
    private static GridModel Kundur()
    {
        const double r = 0.0001, x = 0.001, b = 0.00175, xt = 0.15 / 9.0;
        Branch Line(int f, int t, double km) => new(f, t, r * km, x * km, b * km);
        var buses = new List<Bus>
        {
            new(1, BusType.Pv, BaseKv: 20), new(2, BusType.Pv, BaseKv: 20), new(3, BusType.Slack, Vm: 1.03, BaseKv: 20), new(4, BusType.Pv, BaseKv: 20),
            new(5, BusType.Pq, BaseKv: 230), new(6, BusType.Pq, BaseKv: 230), new(7, BusType.Pq, Pd: 967, Qd: 100, Bs: 200, BaseKv: 230),
            new(8, BusType.Pq, BaseKv: 230), new(9, BusType.Pq, Pd: 1767, Qd: 100, Bs: 350, BaseKv: 230), new(10, BusType.Pq, BaseKv: 230),
            new(11, BusType.Pq, BaseKv: 230),
        };
        var gens = new[]
        {
            new Generator(1, Pg: 700, Vg: 1.03, MBase: 900, H: 6.5, Name: "G1"),
            new Generator(2, Pg: 700, Vg: 1.01, MBase: 900, H: 6.5, Name: "G2"),
            new Generator(3, Pg: 719, Vg: 1.03, MBase: 900, H: 6.175, Name: "G3"),
            new Generator(4, Pg: 700, Vg: 1.01, MBase: 900, H: 6.175, Name: "G4"),
        };
        var branches = new List<Branch>
        {
            new(1, 5, 0, xt), new(2, 6, 0, xt), new(3, 11, 0, xt), new(4, 10, 0, xt),
            Line(5, 6, 25), Line(6, 7, 10), Line(7, 8, 110), Line(7, 8, 110), Line(8, 9, 110), Line(8, 9, 110), Line(9, 10, 10), Line(10, 11, 25),
        };
        return new GridModel { BaseMva = 100, Name = "kundur-two-area", FrequencyHz = 60, Buses = buses, Generators = gens, Branches = branches };
    }

    private static readonly double[] KundurXdp = { 0.3, 0.3, 0.3, 0.3 };

    private static double InterAreaHz(GridModel m, PowerFlowResult pf, double[] xdp)
    {
        ModalResult modes = ModalAnalysis.Analyze(SmallSignalAssembler.FromSolvedCase(m, pf, xdp));
        return modes.Modes.Where(md => md.IsOscillatory).Min(md => md.FrequencyHz);
    }

    [Fact]
    public void Slow_coherency_groups_the_two_areas()
    {
        GridModel m = Kundur();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        Assert.True(pf.Converged);
        CoherencyResult c = CoherencyGrouping.FromSolvedCase(m, pf, KundurXdp, 2);
        Assert.Equal(2, c.Groups.Count);
        Assert.Equal(new[] { 0, 1 }, c.Groups[0]);   // G1, G2 (area 1)
        Assert.Equal(new[] { 2, 3 }, c.Groups[1]);   // G3, G4 (area 2)
        // The script: slow eigenvalues of M^-1/2 K_s M^-1/2 near 0 (the rigid mode) and 0.0314, then 0.138: a clear gap.
        Assert.True(c.NextEigenvalue > 3 * c.SlowEigenvalues[1], $"{c.NextEigenvalue} vs {c.SlowEigenvalues[1]}");
    }

    [Fact]
    public void The_two_area_equivalent_keeps_the_inter_area_mode_within_5_percent()
    {
        GridModel m = Kundur();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        Assert.True(pf.Converged);
        double fFull = InterAreaHz(m, pf, KundurXdp);
        // The script's classical linearisation of the full system: 0.530596340 Hz. Bound: the two linearisations differ
        // only by rounding and the power-flow stop (1e-12 pu), far below 1e-6 relative.
        AssertRel(0.530596340, fFull, 1e-6, "full inter-area mode");

        var groups = new[] { new[] { 0, 1 }, new[] { 2, 3 } };
        DynamicEquivalent eq = InertialAggregation.Build(m, pf, groups, KundurXdp.Select(v => new ClassicalMachineData(v)).ToList(), ProvenanceTag.Synthetic);
        Assert.All(eq.Groups, g => Assert.True(g.Merged));
        Assert.Equal(new[] { 12, 13 }, eq.Groups.Select(g => g.BusId));
        Assert.Equal(BusType.Slack, eq.Model.Buses.Single(b => b.Id == 13).Type);   // G3, the slack, is in group 2

        // The ideal-transformer merge preserves the power flow: the reduced case, solved from a flat start, has the full
        // case's voltages at every bus it keeps and V_eq at the equivalent buses. Bound: both solves stop at a 1e-12 pu
        // mismatch and the script's transformed solution left a 3e-14 residual; |dV| <= |J^-1| x 1e-12 with |J^-1| well
        // below 100 on this case, so 1e-9 holds a margin above 10.
        PowerFlowResult pfEq = new NewtonRaphsonPowerFlow().Solve(eq.Model, Tight with { FlatStart = true });
        Assert.True(pfEq.Converged);
        var keptIds = eq.Model.Buses.Where(b => b.Id <= 11).Select(b => b.Id).ToList();
        Assert.Equal(new[] { 5, 6, 7, 8, 9, 10, 11 }, keptIds);
        Assert.True(NetworkReduction.MaxVoltageDifference(pf, pfEq, keptIds) <= 1e-9);
        foreach (DynamicEquivalentGroup g in eq.Groups)
        {
            int i = pfEq.BusIds.ToList().IndexOf(g.BusId);
            Complex vq = GridSim.Core.Numerics.DetMath.FromPolar(pfEq.Vm[i], pfEq.VaDeg[i] * Math.PI / 180.0);
            Assert.True((vq - g.VoltagePu).Magnitude <= 1e-9, $"{g.Name}: {vq} vs {g.VoltagePu}");
        }

        // H_eq is exact (equal ratings); the script's X'd_eq on 1800 MVA, referred through |a| near 1: 0.2977986508312345 and
        // 0.2976047018853535. They depend on the solved voltages through |a|, so the voltage bound above (1e-9) applies.
        Assert.Equal(6.5, eq.Groups[0].Machine.InertiaH);
        Assert.Equal(6.175, eq.Groups[1].Machine.InertiaH, 14);
        AssertRel(0.2977986508312345, eq.Groups[0].Machine.XdpPu, 1e-9, "X'd_eq group 1 (script)");
        AssertRel(0.2976047018853535, eq.Groups[1].Machine.XdpPu, 1e-9, "X'd_eq group 2 (script)");

        double fEq = InterAreaHz(eq.Model, pfEq, eq.XdpMachineBase());
        // The script: 0.549164910 Hz, 3.50 % above the full system's. The 5 % bound is the spec's, an observed bound for
        // this aggregation (the inertial method is not exact when the members' internal voltages differ), not a derived one.
        AssertRel(0.549164910, fEq, 1e-6, "equivalent inter-area mode (script)");
        Assert.True(Math.Abs(fEq - fFull) <= 0.05 * fFull, $"inter-area mode: equivalent {fEq} Hz vs full {fFull} Hz");
    }

    [Fact]
    public void The_dynamics_document_loads_and_carries_the_aggregates()
    {
        GridModel m = Kundur();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        Assert.True(pf.Converged);
        DynamicEquivalent eq = InertialAggregation.Build(m, pf, new[] { new[] { 0, 1 }, new[] { 2, 3 } },
            KundurXdp.Select(v => new ClassicalMachineData(v)).ToList(), ProvenanceTag.Synthetic);
        Assert.DoesNotContain("\r", eq.DynamicsJson, StringComparison.Ordinal);
        string dir = TestTemp.Under("gridsim-wp31-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "eq.dynamics.json");
            File.WriteAllText(path, eq.DynamicsJson);
            MachineDynamicsSet set = MachineDynamicsLoader.Load(path, Path.Combine(CliProcess.Root(), "schemas", "v2"), eq.Model);
            Assert.Equal(2, set.Entries.Count);
            for (int k = 0; k < 2; k++)
            {
                MachineDynamicsEntry e = set.For(k) ?? throw new InvalidOperationException($"generator {k} is not bound");
                Assert.Equal(eq.Groups[k].Machine.XdpPu, e.Data.Xdp);
                Assert.Equal(eq.Groups[k].Machine.InertiaH, e.Data.H);
                Assert.Equal(0.0, e.Data.Damping);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Dynamic_equivalent_refuses_a_terminal_bus_split_between_groups()
    {
        GridModel model = ParallelCase();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model, Tight);
        var machines = new[] { new ClassicalMachineData(0.3), new ClassicalMachineData(0.3), new ClassicalMachineData(0.2) };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            InertialAggregation.Build(model, pf, new[] { new[] { 0 } }, machines, ProvenanceTag.Synthetic));
        Assert.Contains("a terminal bus belongs to one group whole", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() =>
            InertialAggregation.Build(model, pf, new[] { new[] { 0, 1 }, new[] { 1, 2 } }, machines, ProvenanceTag.Synthetic));
    }

    [Fact]
    public void Slow_coherency_refuses_a_group_count_outside_one_to_the_machine_count()
    {
        double[,] k = { { 1, -1 }, { -1, 1 } };
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherencyGrouping.SlowCoherency(new[] { 3.0, 3.0 }, k, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoherencyGrouping.SlowCoherency(new[] { 3.0, 3.0 }, k, 3));
    }

    // ---- 6. The existing equivalents are untouched ----

    /// <summary>The Ward, extended Ward and REI code is not edited: the only new line in <see cref="NetworkReduction.Reduce"/>
    /// refuses the two new methods before any computation. Their byte-identity to 2.1.138 rests on the pinned figures of
    /// Rc5Wp9NetworkReductionTests, which run unchanged (the batch verify runs that slice), and on the determinism checked
    /// here: two reductions give the same bits.</summary>
    [Theory]
    [InlineData(EquivalentMethod.ShortCircuit)]
    [InlineData(EquivalentMethod.Dynamic)]
    public void NetworkReduction_refuses_the_new_methods_by_name(EquivalentMethod method)
    {
        GridModel m = IeeeCases.Case14();
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, Tight);
        var ex = Assert.Throws<InvalidOperationException>(() => NetworkReduction.Reduce(m, pf, new ReductionSpec(new[] { 1, 2, 3, 4, 5 }, method)));
        Assert.Contains("ShortCircuitEquivalent.Reduce or InertialAggregation.Build", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_existing_methods_keep_their_enum_values()
    {
        Assert.Equal(0, (int)EquivalentMethod.Ward);
        Assert.Equal(1, (int)EquivalentMethod.ExtendedWard);
        Assert.Equal(2, (int)EquivalentMethod.Rei);
    }

    [Fact]
    public void Sc_equivalent_is_deterministic_to_the_bit()
    {
        ShortCircuitEquivalentResult a = ShortCircuitEquivalent.Reduce(Case14Sequence(true), Kept);
        ShortCircuitEquivalentResult b = ShortCircuitEquivalent.Reduce(Case14Sequence(true), Kept);
        // Compared by bits (review R1 finding 4, ruled 2026-11-24): record and Complex equality take -0.0 == +0.0, and the
        // bus 5 link's R0 is -0. Before: Assert.Equal on the branch, generator and Thevenin lists (value equality).
        static long[] Bits(params double[] v) => v.Select(BitConverter.DoubleToInt64Bits).ToArray();
        Assert.Equal(a.Network.Model.Branches.Count, b.Network.Model.Branches.Count);
        for (int i = 0; i < a.Network.Model.Branches.Count; i++)
        {
            Branch x = a.Network.Model.Branches[i], y = b.Network.Model.Branches[i];
            Assert.Equal(Bits(x.R, x.X, x.B, x.R0, x.X0, x.TapRatio, x.FromNeutralXPu, x.ToNeutralXPu), Bits(y.R, y.X, y.B, y.R0, y.X0, y.TapRatio, y.FromNeutralXPu, y.ToNeutralXPu));
            Assert.Equal((x.From, x.To, x.Name, x.FromWinding, x.ToWinding), (y.From, y.To, y.Name, y.FromWinding, y.ToWinding));
        }
        Assert.Equal(a.Network.Model.AllGenerators.Count, b.Network.Model.AllGenerators.Count);
        for (int i = 0; i < a.Network.Model.AllGenerators.Count; i++)
        {
            Generator x = a.Network.Model.AllGenerators[i], y = b.Network.Model.AllGenerators[i];
            Assert.Equal(Bits(x.MBase, x.SubtransientXdPu, x.NegativeSeqXPu, x.ZeroSequenceZPu?.RPu ?? 0, x.ZeroSequenceZPu?.XPu ?? 0),
                Bits(y.MBase, y.SubtransientXdPu, y.NegativeSeqXPu, y.ZeroSequenceZPu?.RPu ?? 0, y.ZeroSequenceZPu?.XPu ?? 0));
            Assert.Equal((x.Bus, x.Name, x.ZeroSequenceZPu is null), (y.Bus, y.Name, y.ZeroSequenceZPu is null));
        }
        Assert.Equal(a.Thevenin.Count, b.Thevenin.Count);
        for (int i = 0; i < a.Thevenin.Count; i++)
        {
            SequenceTheveninImpedance x = a.Thevenin[i], y = b.Thevenin[i];
            Assert.Equal(x.BusId, y.BusId);
            Assert.Equal(Bits(x.Z1Pu.Real, x.Z1Pu.Imaginary, x.Z2Pu.Real, x.Z2Pu.Imaginary, x.Z0Pu.Real, x.Z0Pu.Imaginary),
                Bits(y.Z1Pu.Real, y.Z1Pu.Imaginary, y.Z2Pu.Real, y.Z2Pu.Imaginary, y.Z0Pu.Real, y.Z0Pu.Imaginary));
        }
    }
}
