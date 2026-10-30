using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using GridSim.Core.Harmonics;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N167 (October 2026): the sequence fault study, the two estimators and the three-phase harmonic penetration used to
/// refuse every N062 element. Each now models the elements it can define exactly and refuses the rest BY NAME:
/// <list type="bullet">
/// <item>fault study: shunt capacitors, line charging and loads of every kind neglected (shunt elements, as loads always
/// were); closed switches add zero impedance; a lateral or regulator on the path to the fault is refused;</item>
/// <item>WLS estimator: loads and capacitors accepted (not in its measurement model); laterals, line charging,
/// regulators and closed switches refused;</item>
/// <item>branch-current estimator: every element modelled, validated on the IEEE 123 Node Test Feeder against the sweep
/// (exact measurements) and against the published results (published currents in, published voltages compared);</item>
/// <item>harmonic penetration: capacitors and line charging (G + j h B) and closed switches (one node) in Y(h);
/// laterals and regulators refused.</item>
/// </list>
/// Elements absent, every solver runs exactly its pre-N167 arithmetic (proved below where a hand-built twin exists).
/// </summary>
public class N167ExtendedElementSolversTests
{
    private static readonly Complex Z = new(0.01, 0.03);
    private static readonly Complex[] S = { new(0.5, 0.2), new(0.4, 0.1), new(0.3, 0.15) };
    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    private static void SameBits(Complex a, Complex b, string what)
    {
        Assert.True(Bits(a.Real) == Bits(b.Real) && Bits(a.Imaginary) == Bits(b.Imaginary), $"{what}: {a} vs {b}");
    }

    private static PhaseBus Src() => new(1, true, MultiPhaseModel.BalancedNominal());

    /// <summary>A mutually coupled 3x3 impedance (so sequence reduction and inversion are not trivial).</summary>
    private static PhaseImpedance Coupled(double k = 1.0)
    {
        var z = new Complex[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                z[i, j] = i == j ? new Complex(0.012, 0.035) * k : new Complex(0.003, 0.011) * k;
        return new PhaseImpedance(z);
    }

    private static PhaseImpedance Shunt(double b)
    {
        var y = new Complex[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                y[i, j] = new Complex(0, i == j ? b : -0.2 * b);
        return new PhaseImpedance(y);
    }

    private static readonly PhaseImpedance ZeroZ = new(new Complex[3, 3]);

    // =============================================================================================================
    // Fault study
    // =============================================================================================================

    /// <summary>1 - 2 - 3 three-phase lines; optional extras that the fault study neglects.</summary>
    private static MultiPhaseModel FaultFeeder(bool extras)
    {
        var buses = new List<PhaseBus> { Src(), new(2, false), new(3, false) };
        var branches = new List<PhaseBranch>
        {
            new(1, 2, Coupled(), ShuntY: extras ? Shunt(0.002) : null),
            new(2, 3, Coupled(0.5)),
        };
        var loads = new List<PhaseLoad> { new(3, S) };
        var caps = new List<PhaseCapacitor>();
        if (extras)
        {
            // A closed switch to a fourth bus, a single-phase lateral from bus 2 to a fifth (not on the path to bus 3),
            // ZIP and delta loads, and a capacitor.
            buses.Add(new PhaseBus(4, false));
            buses.Add(new PhaseBus(5, false));
            branches.Add(new PhaseBranch(3, 4, ZeroZ));
            var za = new Complex[3, 3];
            za[0, 0] = new Complex(0.02, 0.04);
            branches.Add(new PhaseBranch(2, 5, new PhaseImpedance(za), Phase.A));
            loads.Add(new PhaseLoad(3, S, PhaseLoadModel.ConstantImpedance));
            loads.Add(new PhaseLoad(2, S, PhaseLoadModel.ConstantCurrent, PhaseLoadConnection.Delta));
            caps.Add(new PhaseCapacitor(3, new[] { 0.1, 0.1, 0.1 }));
        }
        return new MultiPhaseModel(buses, branches, loads, 100.0, caps);
    }

    [Fact]
    public void The_fault_study_neglects_shunts_and_loads_and_adds_nothing_for_a_switch_or_an_off_path_lateral()
    {
        MultiPhaseModel plain = FaultFeeder(false), extended = FaultFeeder(true);
        Assert.True(extended.HasExtendedElements);
        (Complex a0, Complex a1, Complex a2) = SequenceFaultStudy.Thevenin(plain, 3);
        (Complex b0, Complex b1, Complex b2) = SequenceFaultStudy.Thevenin(extended, 3);
        SameBits(a0, b0, "Z0"); SameBits(a1, b1, "Z1"); SameBits(a2, b2, "Z2");
        // Behind the closed switch the impedance is the same, to the bit (a zero impedance adds +0).
        (Complex c0, Complex c1, Complex c2) = SequenceFaultStudy.Thevenin(extended, 4);
        SameBits(a0, c0, "Z0 at 4"); SameBits(a1, c1, "Z1 at 4"); SameBits(a2, c2, "Z2 at 4");
        Assert.Equal(Bits(SequenceFaultStudy.AllFaults(plain, 3).SingleLineGround), Bits(SequenceFaultStudy.AllFaults(extended, 3).SingleLineGround));
        // The closed form: the two path branches' sequence impedances summed (z1 = zs - zm, z0 = zs + 2 zm).
        Complex zs = new Complex(0.012, 0.035) * 1.5, zm = new Complex(0.003, 0.011) * 1.5;
        Assert.True((a1 - (zs - zm)).Magnitude < 1e-15 && (a0 - (zs + 2.0 * zm)).Magnitude < 1e-15);
    }

    [Fact]
    public void The_fault_study_refuses_a_lateral_or_a_regulator_on_the_path_naming_the_branch()
    {
        // Fault at the single-phase lateral's own bus.
        var lateral = Assert.Throws<ArgumentException>(() => SequenceFaultStudy.Thevenin(FaultFeeder(true), 5));
        Assert.Contains("sequence fault study", lateral.Message, StringComparison.Ordinal);
        Assert.Contains("branch 2-5 carries phase(s) a only", lateral.Message, StringComparison.Ordinal);

        var reg = new MultiPhaseModel(new[] { Src(), new PhaseBus(2, false), new PhaseBus(3, false) },
            new[] { new PhaseBranch(1, 2, ZeroZ, RegulatorRatio: new[] { 1.05, 1.05, 1.05 }), new PhaseBranch(2, 3, Coupled()) },
            Array.Empty<PhaseLoad>());
        var ex = Assert.Throws<ArgumentException>(() => SequenceFaultStudy.Thevenin(reg, 3));
        Assert.Contains("branch 1-2 has a step-voltage regulator", ex.Message, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // WLS estimator
    // =============================================================================================================

    [Fact]
    public void The_wls_estimator_accepts_capacitors_and_zip_and_delta_loads_and_recovers_the_sweep()
    {
        var m = new MultiPhaseModel(new[] { Src(), new PhaseBus(2, false), new PhaseBus(3, false) },
            new[] { new PhaseBranch(1, 2, Coupled()), new PhaseBranch(2, 3, Coupled(0.7)) },
            new[] { new PhaseLoad(3, S), new PhaseLoad(3, S, PhaseLoadModel.ConstantImpedance), new PhaseLoad(2, S, PhaseLoadModel.ConstantCurrent, PhaseLoadConnection.Delta) },
            100.0, new[] { new PhaseCapacitor(2, new[] { 0.2, 0.15, 0.1 }) });
        MultiPhaseResult r = new ThreePhaseSweep().Solve(m, new ThreePhaseSweepOptions { Tolerance = 1e-14, MaxIterations = 500 });
        Assert.True(r.Converged);
        var vm = new List<ThreePhaseVMag>();
        var cur = new List<ThreePhaseCurrent>();
        for (int p = 0; p < 3; p++)
        {
            vm.Add(new ThreePhaseVMag(2, p, r.V[1][p].Magnitude, 1e-3));
            vm.Add(new ThreePhaseVMag(3, p, r.V[2][p].Magnitude, 1e-3));
            cur.Add(new ThreePhaseCurrent(1, 2, p, r.BranchCurrents![0][p], 1e-3));
            cur.Add(new ThreePhaseCurrent(2, 3, p, r.BranchCurrents[1][p], 1e-3));
        }
        ThreePhaseWlsResult e = ThreePhaseWlsEstimator.Estimate(m, vm, cur);
        Assert.True(e.Converged);
        for (int i = 1; i < 3; i++)
            for (int p = 0; p < 3; p++)
                Assert.True((e.V[i][p] - r.V[i][p]).Magnitude < 1e-10, $"bus {i} phase {p}: {e.V[i][p]} vs {r.V[i][p]}");
    }

    [Fact]
    public void The_wls_estimator_refuses_laterals_line_charging_regulators_and_switches_by_name()
    {
        var withSwitch = new MultiPhaseModel(new[] { Src(), new PhaseBus(2, false), new PhaseBus(3, false) },
            new[] { new PhaseBranch(1, 2, Coupled()), new PhaseBranch(2, 3, ZeroZ, ShuntY: null) }, Array.Empty<PhaseLoad>());
        var ex = Assert.Throws<ArgumentException>(() => ThreePhaseWlsEstimator.Estimate(withSwitch, Array.Empty<ThreePhaseVMag>(), Array.Empty<ThreePhaseCurrent>()));
        Assert.Contains("three-phase WLS estimator cannot model this feeder's closed switches", ex.Message, StringComparison.Ordinal);

        var ex2 = Assert.Throws<ArgumentException>(() => ThreePhaseWlsEstimator.Estimate(FaultFeeder(true), Array.Empty<ThreePhaseVMag>(), Array.Empty<ThreePhaseCurrent>()));
        Assert.Contains("single- or two-phase branches, line charging and closed switches", ex2.Message, StringComparison.Ordinal);
    }

    // =============================================================================================================
    // Branch-current estimator on the IEEE 123 Node Test Feeder
    // =============================================================================================================

    private static string DataPath(string name) => Path.Combine(CliProcess.Root(), "data", "dist3ph", name);

    private static IEnumerable<string[]> Rows(string file) =>
        File.ReadAllLines(DataPath(file)).Where(l => l.Length > 0 && !l.StartsWith('#')).Skip(1).Select(l => l.Split(','));

    private static int Id(MultiPhaseFeeder f, string node)
    {
        foreach (KeyValuePair<int, string> kv in f.BusNames) if (kv.Value == node) return kv.Key;
        return int.Parse(node, CultureInfo.InvariantCulture);
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>The current into branch <paramref name="br"/> at its From bus, given its series current and the From
    /// bus voltage: a .* (i_series + (Y_sh / 2) (a .* v_from)), the sweep's element run forwards.</summary>
    private static Complex[] FromEndCurrent(PhaseBranch br, Complex[] iSeries, Complex[] vFrom)
    {
        Complex[] a = br.RegulatorRatio is { } r ? r.Select(x => new Complex(x, 0)).ToArray() : new[] { Complex.One, Complex.One, Complex.One };
        var vOut = new[] { vFrom[0] * a[0], vFrom[1] * a[1], vFrom[2] * a[2] };
        var i = (Complex[])iSeries.Clone();
        if (br.ShuntY is { } y)
            for (int p = 0; p < 3; p++)
                for (int q = 0; q < 3; q++) i[p] += y[p, q] * 0.5 * vOut[q];
        for (int p = 0; p < 3; p++) i[p] *= a[p];
        return i;
    }

    [Fact]
    public void The_branch_current_estimator_reproduces_the_ieee123_sweep_from_its_exact_from_end_currents()
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(DataPath("ieee123.json"));
        MultiPhaseModel m = f.Model;
        MultiPhaseElement e = m.ElementsPresent;
        Assert.Equal(MultiPhaseElement.PartialPhaseBranch | MultiPhaseElement.LineCharging | MultiPhaseElement.Regulator
            | MultiPhaseElement.Capacitor | MultiPhaseElement.VoltageDependentLoad | MultiPhaseElement.DeltaLoad | MultiPhaseElement.ClosedSwitch, e);
        MultiPhaseResult r = new ThreePhaseSweep().Solve(m);
        Assert.True(r.Converged);
        var index = new Dictionary<int, int>();
        for (int i = 0; i < r.BusIds.Count; i++) index[r.BusIds[i]] = i;

        var meas = new List<ThreePhaseCurrent>();
        for (int k = 0; k < m.Branches.Count; k++)
        {
            PhaseBranch br = m.Branches[k];
            Complex[] iFrom = FromEndCurrent(br, r.BranchCurrents![k], r.V[index[br.From]]);
            for (int p = 0; p < 3; p++)
                if (PhaseIndex.Has(br.Phases, p)) meas.Add(new ThreePhaseCurrent(br.From, br.To, p, iFrom[p], 1e-3));
        }
        Assert.Equal(265, meas.Count);

        BranchCurrentEstimate est = BranchCurrentDsse.Estimate(m, meas);
        Assert.True(est.Converged);
        double worst = 0;
        for (int i = 0; i < r.BusIds.Count; i++)
            for (int p = 0; p < 3; p++)
            {
                if (!PhaseIndex.Has(r.BusPhases![i], p)) { Assert.Equal(Complex.Zero, est.V[i][p]); continue; }   // absent phase exactly zero
                worst = Math.Max(worst, (est.V[i][p] - r.V[i][p]).Magnitude);
            }
        Assert.True(worst < 1e-10, $"largest difference from the sweep {worst:R} pu");
    }

    [Fact]
    public void The_branch_current_estimator_from_the_published_ieee123_currents_gives_the_published_voltages()
    {
        // Published branch currents in (amps and degrees, 2 decimals), published voltages compared (4 decimals). The
        // published 'FROM NODE' current is the series-section current (it matches the sweep's series current to 0.09 A,
        // MultiPhaseIeee123Tests, including at the regulators, where it is the output side). The estimator takes the
        // current into the branch at its From bus, so each published value is taken through the sweep's own element
        // forwards using PUBLISHED data only: plus the From-end half of the line charging at the published From-node
        // voltage (after the regulator ratio), then times the regulator ratio. No sweep result enters the input.
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(DataPath("ieee123.json"));
        MultiPhaseModel m = f.Model;
        var index = new Dictionary<int, int>();
        for (int i = 0; i < m.Buses.Count; i++) index[m.Buses[i].Id] = i;
        var vPub = new Dictionary<int, Complex[]>();
        foreach (string[] row in Rows("ieee123.published-voltages.csv"))
        {
            int id = Id(f, row[0]);
            if (!vPub.TryGetValue(id, out Complex[]? vv)) vPub[id] = vv = new Complex[3];
            vv["abc".IndexOf(row[1][0], StringComparison.Ordinal)] = Complex.FromPolarCoordinates(Num(row[2]), Num(row[3]) * Math.PI / 180.0);
        }

        var byBranch = new Dictionary<int, Complex[]>();
        foreach (string[] row in Rows("ieee123.published-currents.csv"))
        {
            int from = Id(f, row[0]), to = Id(f, row[1]);
            int k = Enumerable.Range(0, m.Branches.Count).Single(b => m.Branches[b].From == from && m.Branches[b].To == to);
            int p = "abc".IndexOf(row[2][0], StringComparison.Ordinal);
            if (!byBranch.TryGetValue(k, out Complex[]? ik)) byBranch[k] = ik = new Complex[3];
            ik[p] = Complex.FromPolarCoordinates(Num(row[3]) / f.AmpBase(to), Num(row[4]) * Math.PI / 180.0);
        }
        var meas = new List<ThreePhaseCurrent>();
        foreach ((int k, Complex[] iSeries) in byBranch.OrderBy(kv => kv.Key))
        {
            PhaseBranch br = m.Branches[k];
            Complex[] iFrom = FromEndCurrent(br, iSeries, vPub[br.From]);
            for (int p = 0; p < 3; p++)
                if (PhaseIndex.Has(br.Phases, p)) meas.Add(new ThreePhaseCurrent(br.From, br.To, p, iFrom[p], 1e-3));
        }
        Assert.Equal(265, meas.Count);

        BranchCurrentEstimate est = BranchCurrentDsse.Estimate(m, meas);
        Assert.True(est.Converged);
        double worstMag = 0, worstAng = 0;
        string at = "", atAng = "";
        int compared = 0;
        foreach (string[] row in Rows("ieee123.published-voltages.csv"))
        {
            int bi = index[Id(f, row[0])];
            int p = "abc".IndexOf(row[1][0], StringComparison.Ordinal);
            Complex v = est.V[bi][p];
            double dm = v.Magnitude - Num(row[2]);
            double da = (((v.Phase * 180.0 / Math.PI) - Num(row[3]) + 540.0) % 360.0) - 180.0;
            if (Math.Abs(dm) > Math.Abs(worstMag)) { worstMag = dm; at = $"{row[0]} {row[1]}"; }
            if (Math.Abs(da) > Math.Abs(worstAng)) { worstAng = da; atAng = $"{row[0]} {row[1]}"; }
            compared++;
        }
        Assert.Equal(268, compared);
        // Measured (1.95.131 + N167): largest differences 4.375740895501412E-4 pu (node 95 a) and 0.055725923274508204 deg
        // (node 110 a), against the sweep's own 5.2454E-4 pu and 0.05329 deg (MultiPhaseIeee123Tests): the estimate from
        // the published currents is as close to the published voltages as the power flow itself.
        Assert.True(Math.Abs(worstMag) < 5e-4, $"largest magnitude difference {worstMag:R} pu at {at}");
        Assert.True(Math.Abs(worstAng) < 0.06, $"largest angle difference {worstAng:R} deg at {atAng}");
    }

    [Fact]
    public void The_branch_current_estimator_refuses_a_reversed_regulator_and_a_current_on_an_absent_phase()
    {
        var reversed = new MultiPhaseModel(new[] { Src(), new PhaseBus(2, false) },
            new[] { new PhaseBranch(2, 1, ZeroZ, RegulatorRatio: new[] { 1.05, 1.05, 1.05 }) }, Array.Empty<PhaseLoad>());
        var ex = Assert.Throws<ArgumentException>(() => BranchCurrentDsse.Estimate(reversed,
            new[] { new ThreePhaseCurrent(2, 1, 0, Complex.Zero, 1e-3), new ThreePhaseCurrent(2, 1, 1, Complex.Zero, 1e-3), new ThreePhaseCurrent(2, 1, 2, Complex.Zero, 1e-3) }));
        Assert.Contains("faces the source", ex.Message, StringComparison.Ordinal);

        var za = new Complex[3, 3];
        za[0, 0] = Z;
        var lateral = new MultiPhaseModel(new[] { Src(), new PhaseBus(2, false) },
            new[] { new PhaseBranch(1, 2, new PhaseImpedance(za), Phase.A) }, Array.Empty<PhaseLoad>());
        var ex2 = Assert.Throws<ArgumentException>(() => BranchCurrentDsse.Estimate(lateral, new[] { new ThreePhaseCurrent(1, 2, 1, Complex.One, 1e-3) }));
        Assert.Contains("phase b of branch 1-2, which carries phase(s) a only", ex2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_v1_feeder_estimate_is_the_pre_n167_forward_sweep_to_the_bit()
    {
        // The v1 forward step, copied from before N167: V_child = V_parent - Z I (From upstream). Two three-phase lines.
        var m = new MultiPhaseModel(new[] { Src(), new PhaseBus(2, false), new PhaseBus(3, false) },
            new[] { new PhaseBranch(1, 2, Coupled()), new PhaseBranch(2, 3, Coupled(0.7)) }, new[] { new PhaseLoad(3, S) });
        var i12 = new[] { new Complex(0.4, -0.2), new Complex(-0.3, -0.25), new Complex(-0.1, 0.45) };
        var i23 = new[] { new Complex(0.2, -0.1), new Complex(-0.15, -0.12), new Complex(-0.05, 0.22) };
        var meas = new List<ThreePhaseCurrent>();
        for (int p = 0; p < 3; p++) { meas.Add(new(1, 2, p, i12[p], 1e-3)); meas.Add(new(2, 3, p, i23[p], 1e-3)); }
        BranchCurrentEstimate est = BranchCurrentDsse.Estimate(m, meas);
        Complex[] v1 = MultiPhaseModel.BalancedNominal();
        Complex[] d12 = m.Branches[0].Z.Multiply(i12);
        var v2 = new[] { v1[0] - d12[0], v1[1] - d12[1], v1[2] - d12[2] };
        Complex[] d23 = m.Branches[1].Z.Multiply(i23);
        var v3 = new[] { v2[0] - d23[0], v2[1] - d23[1], v2[2] - d23[2] };
        for (int p = 0; p < 3; p++) { SameBits(v2[p], est.V[1][p], $"V2 {p}"); SameBits(v3[p], est.V[2][p], $"V3 {p}"); }
    }

    // =============================================================================================================
    // Harmonic penetration
    // =============================================================================================================

    private static ThreePhaseHarmonicSource Fifth(int bus) =>
        new(bus, "test", new Dictionary<int, Complex[]> { [5] = new[] { new Complex(0.02, 0.01), new Complex(-0.015, 0.005), new Complex(0.004, -0.012) } });

    private static Complex[] Solve3(Complex[,] y, Complex[] i)
    {
        PhaseImpedance inv = new PhaseImpedance(y).Invert();
        return inv.Multiply(i);
    }

    [Fact]
    public void A_capacitor_and_line_charging_enter_y_h_as_j_h_b()
    {
        // Source - bus 2 through a coupled line with charging; a capacitor at bus 2. Bus 2's 3x3 admittance at order 5:
        // Ys(5) + (Y_sh / 2 with B x 5) + j 5 B_cap. The source-end half of the charging sits on the reference.
        const int h = 5;
        var m = new MultiPhaseModel(new[] { Src(), new PhaseBus(2, false) },
            new[] { new PhaseBranch(1, 2, Coupled(), ShuntY: Shunt(0.004)) }, Array.Empty<PhaseLoad>(), 100.0,
            new[] { new PhaseCapacitor(2, new[] { 0.3, 0.2, 0.1 }) });
        ThreePhasePenetrationResult res = ThreePhaseHarmonicPenetration.Solve(m, new[] { Fifth(2) }, new[] { h });

        var zh = new Complex[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) zh[i, j] = new Complex(Coupled()[i, j].Real, Coupled()[i, j].Imaginary * h);
        PhaseImpedance ys = new PhaseImpedance(zh).Invert();
        var y = new Complex[3, 3];
        double[] cap = { 0.3, 0.2, 0.1 };
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                Complex sh = Shunt(0.004)[i, j] * 0.5;
                y[i, j] = ys[i, j] + new Complex(sh.Real, sh.Imaginary * h) + (i == j ? new Complex(0, cap[i] * h) : Complex.Zero);
            }
        Complex[] expected = Solve3(y, Fifth(2).InjectionAt(h));
        for (int p = 0; p < 3; p++)
            Assert.True((res.Voltage[0][0][p] - expected[p]).Magnitude < 1e-12, $"phase {p}: {res.Voltage[0][0][p]} vs {expected[p]}");
    }

    [Fact]
    public void A_closed_switch_joins_its_buses_and_changes_nothing_else()
    {
        var plain = new MultiPhaseModel(new[] { Src(), new PhaseBus(2, false) }, new[] { new PhaseBranch(1, 2, Coupled()) }, Array.Empty<PhaseLoad>());
        var switched = new MultiPhaseModel(new[] { Src(), new PhaseBus(2, false), new PhaseBus(3, false) },
            new[] { new PhaseBranch(1, 2, Coupled()), new PhaseBranch(2, 3, ZeroZ) }, Array.Empty<PhaseLoad>());
        ThreePhasePenetrationResult a = ThreePhaseHarmonicPenetration.Solve(plain, new[] { Fifth(2) }, new[] { 5 });
        ThreePhasePenetrationResult b = ThreePhaseHarmonicPenetration.Solve(switched, new[] { Fifth(3) }, new[] { 5 });
        Assert.Equal(new[] { 2, 3 }, b.BusIds);
        for (int p = 0; p < 3; p++)
        {
            SameBits(a.Voltage[0][0][p], b.Voltage[0][0][p], $"bus 2 phase {p}");
            SameBits(a.Voltage[0][0][p], b.Voltage[0][1][p], $"bus 3 phase {p}");
        }
        // A source on a bus switched onto the substation would vanish into the reference: refused.
        var onSource = new MultiPhaseModel(new[] { Src(), new PhaseBus(2, false), new PhaseBus(3, false) },
            new[] { new PhaseBranch(1, 2, ZeroZ), new PhaseBranch(2, 3, Coupled()) }, Array.Empty<PhaseLoad>());
        var ex = Assert.Throws<ArgumentException>(() => ThreePhaseHarmonicPenetration.Solve(onSource, new[] { Fifth(2) }, new[] { 5 }));
        Assert.Contains("tied to the source bus by closed switches", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_harmonic_penetration_refuses_laterals_and_regulators_by_name()
    {
        var ex = Assert.Throws<ArgumentException>(() => ThreePhaseHarmonicPenetration.Solve(FaultFeeder(true), new[] { Fifth(3) }, new[] { 5 }));
        Assert.Contains("three-phase harmonic penetration cannot model this feeder's single- or two-phase branches", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("line charging and", ex.Message.Split("It supports")[0], StringComparison.Ordinal);   // charging is supported
    }
}
