using System;
using GridSim.Core.Model;
using GridSim.Core.Protection.Coordination;
using GridSim.Core.Protection.ShortCircuit;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Validation of the IEC 60909-0 short-circuit duty study (<see cref="Iec60909"/>) against the standard's
/// closed forms, to the machine-epsilon standard where one exists:
///   - VOLTAGE FACTOR c from Table 1;
///   - the PEAK FACTOR kappa = 1.02 + 0.98 e^{-3R/X} and ip = kappa*sqrt(2)*Ik'';
///   - HAND NETWORKS: Ik'' = c/|Zk| against a driving-point impedance solved by hand (a single generator,
///     and a generator behind a series reactance);
///   - the DC COMPONENT exponential and the THERMAL DC factor m;
///   - the FAR-FROM-GENERATOR reductions Ib = Ik = Ik''.
/// </summary>
public class Iec60909Tests
{
    private const double BaseMva = 100.0;
    private const double Kv = 132.0;

    private static GridModel SingleGenerator() => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv) },
        Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0) },
        Branches = Array.Empty<Branch>(),
    };

    // A single generator carrying a real per-machine subtransient reactance (pu on its MBase).
    private static GridModel SingleGeneratorWithXd(double xdPu) => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv) },
        Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, SubtransientXdPu: xdPu) },
        Branches = Array.Empty<Branch>(),
    };

    // Generator (X''d) at bus 1, a purely reactive series branch to bus 2, fault at bus 2.
    private static GridModel GeneratorBehindReactance(double branchX) => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv) },
        Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0) },
        Branches = new[] { new Branch(1, 2, R: 0.0, X: branchX) },
    };

    // ---- Voltage factor c (Table 1) ----
    [Fact]
    public void Voltage_factor_follows_table_1()
    {
        Assert.Equal(1.10, Iec60909.VoltageFactor(132.0, ShortCircuitObjective.Maximum), 12);
        Assert.Equal(1.00, Iec60909.VoltageFactor(132.0, ShortCircuitObjective.Minimum), 12);
        Assert.Equal(1.05, Iec60909.VoltageFactor(0.4, ShortCircuitObjective.Maximum), 12);   // <= 1 kV
        Assert.Equal(0.95, Iec60909.VoltageFactor(0.4, ShortCircuitObjective.Minimum), 12);
    }

    // ---- Peak factor kappa and ip closed forms ----
    [Fact]
    public void Peak_factor_matches_its_closed_form_and_bounds()
    {
        Assert.Equal(2.0, Iec60909.PeakFactor(0.0), 12);                                // R = 0 => full asymmetry
        Assert.Equal(1.02 + 0.98 * Math.Exp(-3.0 * 0.1), Iec60909.PeakFactor(0.1), 12); // a resistive loop
        Assert.True(Iec60909.PeakFactor(10.0) > 1.02 && Iec60909.PeakFactor(10.0) < 1.03);
    }

    // ---- Hand network: single generator, bolted three-phase ----
    [Fact]
    public void Single_generator_bolted_fault_matches_the_hand_calculation()
    {
        // X''d = 0.20 pu on a 100 MVA machine on a 100 MVA base => Zk = j0.20 pu (purely reactive).
        Iec60909.Duty duty = Iec60909.ThreePhase(SingleGenerator(), faultBusId: 1);

        Assert.Equal(0.0, duty.TheveninRPu, 12);
        Assert.Equal(0.20, duty.TheveninXPu, 12);

        // Ik'' = c / |Zk| = 1.10 / 0.20 = 5.5 pu; kA via Ibase = baseMVA / (sqrt(3) Un).
        double iBase = BaseMva / (Math.Sqrt(3.0) * Kv);
        Assert.Equal(5.5, duty.IkppPu, 12);
        Assert.Equal(5.5 * iBase, duty.IkppKa, 10);

        Assert.Equal(2.0, duty.Kappa, 12);                             // R/X = 0
        Assert.Equal(duty.Kappa * Math.Sqrt(2.0) * duty.IkppKa, duty.PeakIpKa, 12);
    }

    // ---- Hand network: a series reactance raises Zk and lowers Ik'' exactly ----
    [Fact]
    public void A_series_reactance_reduces_the_current_by_the_series_impedance()
    {
        // Zk = X''d + Xbranch = 0.20 + 0.30 = 0.50 pu => Ik'' = 1.10 / 0.50 = 2.2 pu.
        Iec60909.Duty duty = Iec60909.ThreePhase(GeneratorBehindReactance(0.30), faultBusId: 2);

        Assert.Equal(0.50, duty.TheveninXPu, 12);
        Assert.Equal(2.2, duty.IkppPu, 12);
        Assert.True(duty.IkppPu < Iec60909.ThreePhase(SingleGenerator(), 1).IkppPu);   // farther => weaker
    }

    // ---- DC component: exact envelope, and no decay for a purely reactive loop ----
    [Fact]
    public void Dc_component_follows_the_exact_exponential_envelope()
    {
        const double f0 = 50.0, tb = 0.08;

        // Purely reactive loop (R/X = 0): no DC decay, idc(tb) = sqrt(2) Ik''.
        Iec60909.Duty reactive = Iec60909.FromThevenin(1.1, Kv, theveninRPu: 0.0, theveninXPu: 0.2, BaseMva, f0, tb);
        Assert.Equal(Math.Sqrt(2.0) * reactive.IkppKa, reactive.DcComponentKa, 12);

        // Resistive loop: idc = sqrt(2) Ik'' e^{-2 pi f tb R/X}, strictly smaller.
        Iec60909.Duty resistive = Iec60909.FromThevenin(1.1, Kv, theveninRPu: 0.05, theveninXPu: 0.2, BaseMva, f0, tb);
        double expected = Math.Sqrt(2.0) * resistive.IkppKa * Math.Exp(-2.0 * Math.PI * f0 * tb * (0.05 / 0.2));
        Assert.Equal(expected, resistive.DcComponentKa, 12);
        Assert.True(resistive.DcComponentKa < Math.Sqrt(2.0) * resistive.IkppKa);
    }

    // ---- Thermal DC factor m: the closed form, its kappa=2 limit, and Ith = Ik'' sqrt(m+1) ----
    [Fact]
    public void Thermal_factor_matches_its_closed_form_and_removable_limit()
    {
        const double f0 = 50.0, tk = 0.5;

        // kappa = 2 is the removable 0/0 singularity; the limit is 2.
        Assert.Equal(2.0, Iec60909.ThermalDcFactor(2.0, f0, tk), 12);

        // A mid kappa against a direct evaluation of the formula.
        const double kappa = 1.7;
        double l = Math.Log(kappa - 1.0);
        double denom = 2.0 * f0 * tk * l;
        double expectedM = (Math.Exp(2.0 * denom) - 1.0) / denom;
        Assert.Equal(expectedM, Iec60909.ThermalDcFactor(kappa, f0, tk), 12);

        // Ith = Ik'' sqrt(m + 1) (n = 1 far from generator), so with DC present Ith > Ik''.
        Iec60909.Duty duty = Iec60909.FromThevenin(1.1, Kv, 0.02, 0.2, BaseMva, f0, faultDurationSeconds: tk);
        Assert.Equal(duty.IkppKa * Math.Sqrt(duty.ThermalM + 1.0), duty.ThermalIthKa, 12);
        Assert.True(duty.ThermalIthKa > duty.IkppKa);
    }

    // ---- Far from generator: Ib = Ik = Ik'' (mu = 1, no AC decay) ----
    [Fact]
    public void Far_from_generator_the_breaking_and_steady_currents_equal_the_initial()
    {
        // Moved 2026-10 (R095, authorised by Mark's best-judgement ruling): this used GeneratorBehindReactance(0.30),
        // where the machine feeds Ik''_G / I_rG = 1.10 / 0.50 = 2.2 times its rating. IEC 60909-0 counts that as NEAR
        // to the generator (the far-from-generator test is Ik''_G / I_rG <= 2), so the exact equality only held because
        // ThreePhase applied mu = 1 to every fault, the defect R095 fixes. Before: GeneratorBehindReactance(0.30).
        // After: GeneratorBehindReactance(0.40), Ik''_G / I_rG = 1.10 / 0.60 = 1.83 <= 2, genuinely far, where the
        // exact reduction still holds. The 0.30 network's near-generator Ib is pinned by the next test.
        Iec60909.Duty duty = Iec60909.ThreePhase(GeneratorBehindReactance(0.40), faultBusId: 2);
        Assert.True(duty.IkppPu <= 2.0);                 // far from the generator by the IEC criterion
        Assert.True(duty.BreakingIbKa == duty.IkppKa);   // exact reduction, not approximate
        Assert.True(duty.SteadyIkKa == duty.IkppKa);
    }

    // ---- Near to generator (R095): ThreePhase folds mu in through the multiple-fed formula ----
    [Fact]
    public void Near_generator_breaking_current_follows_the_multiple_fed_formula()
    {
        // Generator X''d 0.20 behind a 0.30 line, fault at the far end: Ik'' = 1.10 / 0.50 = 2.2 pu = 2.2 x rated, so
        // mu = 0.62 + 0.72 e^(-0.32 x 2.2) at tmin = 0.10 s. The machine's terminal voltage dip relative to the source
        // is X''d / (X''d + X) = 0.4, so Ib = Ik'' - 0.4 (1 - mu) Ik'' (IEC 60909-0:2016 9.1.7).
        // Moved 2026-10 (END-PASS VERIFY row 4, end pass; IEC 60909-0:2016 contents, read in endpass-v, Ruling R1 and
        // Mark's go on the end-pass moves 2026-10-11): comment-only citation corrections, no number moves. Before -> after:
        // "10.1.6" -> "9.1.7" (meshed breaking current) here, and "(10.1.1)" -> "(9.1.1)" (single-fed) below.
        Iec60909.Duty duty = Iec60909.ThreePhase(GeneratorBehindReactance(0.30), faultBusId: 2);
        double mu = 0.62 + 0.72 * Math.Exp(-0.32 * 2.2);
        Assert.Equal(mu, Iec60909.BreakingFactorMu(2.2, 0.10), 12);
        Assert.Equal(duty.IkppKa * (1.0 - 0.4 * (1.0 - mu)), duty.BreakingIbKa, 12);
        Assert.True(duty.BreakingIbKa < duty.IkppKa);
        Assert.True(duty.SteadyIkKa == duty.IkppKa);     // lambda is deferred: Ik stays Ik''

        // A single machine at its own terminals has weight 1: Ib = mu Ik'', the single-fed form (9.1.1), which is
        // exactly SynchronousBreakingCurrentKa.
        Iec60909.Duty terminal = Iec60909.ThreePhase(SingleGenerator(), faultBusId: 1);
        double single = Iec60909.SynchronousBreakingCurrentKa(terminal.IkppKa, BaseMva / (Math.Sqrt(3.0) * Kv), 0.10);
        Assert.Equal(single, terminal.BreakingIbKa, 12);

        // The breaker minimum time delay is the duty's tb: a longer delay decays further.
        Assert.True(Iec60909.ThreePhase(GeneratorBehindReactance(0.30), 2, breakTimeSeconds: 0.25).BreakingIbKa < duty.BreakingIbKa);
    }

    // ---- Unbalanced faults keep Ib = Ik'' (no flux decay for unbalanced faults, IEC 60909-0:2016 10.2) ----
    [Fact]
    public void Unbalanced_faults_keep_the_breaking_current_equal_to_the_initial()
    {
        Iec60909.Duty ll = Iec60909.LineToLine(GeneratorBehindReactance(0.30), faultBusId: 2);
        Assert.True(ll.BreakingIbKa == ll.IkppKa);
    }

    // ---- The WSCC-9 duties pinned exactly (R095 before -> after in NOTES-FOR-MAIN) ----
    // Moved 2026-10 (N233, Mark's determinism rule 2026-10-04): the fault network is now the sparse engine at every size
    // (was the dense complex LU up to 1,500 buses), so case9's Zbus moved in its last bits. Before -> after: bus 4 Ik''
    // 2.155003982857191 -> 2.1550039828571914, Ib 2.0600168548926723 -> 2.0600168548926727; bus 5 Ik'' = Ib
    // 1.4683989584292976 -> 1.4683989584292985. Buses 1 and 7 unchanged.
    [Theory]
    [InlineData(1, 2.934393338019216, 2.3509284626333975)]
    [InlineData(4, 2.1550039828571914, 2.0600168548926727)]
    [InlineData(5, 1.4683989584292985, 1.4683989584292985)]   // every machine far: Ib = Ik'' exactly
    [InlineData(7, 1.647443765211713, 1.630308759669193)]
    public void Case9_breaking_currents_are_pinned(int bus, double ikppKa, double ibKa)
    {
        Iec60909.Duty d = Iec60909.ThreePhase(GridSim.Core.IO.IeeeCases.Case9(), bus);
        Assert.Equal(ikppKa, d.IkppKa);   // Ik'' unchanged by R095
        Assert.Equal(ibKa, d.BreakingIbKa);
    }

    // ---- Sequence faults reduce to the three-phase current when the sequences are equal ----
    [Fact]
    public void Sequence_faults_reduce_to_the_three_phase_for_symmetric_impedances()
    {
        var z = new System.Numerics.Complex(0.0, 0.2);      // Z1 = Z2 = Z0 = j0.2
        double threePhasePu = Iec60909.ThreePhase(SingleGenerator(), 1).IkppPu;   // 1.10 / 0.20 = 5.5

        Iec60909.Duty slg = Iec60909.FromSequenceImpedances(UnbalancedFaultType.SingleLineToGround, 1.1, Kv, z, z, z, BaseMva);
        Iec60909.Duty ll = Iec60909.FromSequenceImpedances(UnbalancedFaultType.LineToLine, 1.1, Kv, z, z, z, BaseMva);
        Iec60909.Duty llg = Iec60909.FromSequenceImpedances(UnbalancedFaultType.DoubleLineToGround, 1.1, Kv, z, z, z, BaseMva);

        Assert.Equal(threePhasePu, slg.IkppPu, 12);                     // |3 I1| = 3c/3Z = c/Z
        Assert.Equal(threePhasePu, llg.IkppPu, 12);                     // |3 I0| collapses to c/Z as well
        Assert.Equal(Math.Sqrt(3.0) / 2.0 * threePhasePu, ll.IkppPu, 12); // LL = (sqrt(3)/2) * three-phase
    }

    // ---- Line-to-line from the model: sqrt(3)/2 of the three-phase current, needing no zero sequence ----
    [Fact]
    public void Line_to_line_from_the_model_is_root3_over_2_of_the_three_phase()
    {
        GridModel m = SingleGenerator();
        double threePhasePu = Iec60909.ThreePhase(m, 1).IkppPu;
        Iec60909.Duty ll = Iec60909.LineToLine(m, 1);

        Assert.Equal(Math.Sqrt(3.0) / 2.0 * threePhasePu, ll.IkppPu, 12);
        // Same as the pure sequence form with Z2 = Z1 = j0.20 (synthesised machine: X2 = X''d).
        var z = new System.Numerics.Complex(0.0, 0.2);
        double pure = Iec60909.FromSequenceImpedances(UnbalancedFaultType.LineToLine, 1.1, Kv, z, z, System.Numerics.Complex.Zero, BaseMva).IkppPu;
        Assert.Equal(pure, ll.IkppPu, 12);
    }

    // ---- Single-line-to-ground against a hand-solved asymmetric sequence set ----
    [Fact]
    public void Single_line_to_ground_matches_the_hand_calculation()
    {
        // Z1 = Z2 = j0.2, Z0 = j0.6 => Ik1'' = 3c / |Z1+Z2+Z0| = 3*1.1 / 1.0 = 3.3 pu.
        var z12 = new System.Numerics.Complex(0.0, 0.2);
        var z0 = new System.Numerics.Complex(0.0, 0.6);
        Iec60909.Duty slg = Iec60909.FromSequenceImpedances(UnbalancedFaultType.SingleLineToGround, 1.1, Kv, z12, z12, z0, BaseMva);
        Assert.Equal(3.3, slg.IkppPu, 12);
    }

    // ---- The asymmetry factors use the R/X of the sequence connection's total series impedance ----
    [Fact]
    public void Unbalanced_peak_factor_uses_the_sequence_loop_r_over_x()
    {
        // Z1 = Z2 = 0.02 + j0.2, Z0 = 0.06 + j0.6 => sum = 0.1 + j1.0 => R/X = 0.1.
        var z12 = new System.Numerics.Complex(0.02, 0.2);
        var z0 = new System.Numerics.Complex(0.06, 0.6);
        Iec60909.Duty slg = Iec60909.FromSequenceImpedances(UnbalancedFaultType.SingleLineToGround, 1.1, Kv, z12, z12, z0, BaseMva);
        Assert.Equal(0.1, slg.RoverX, 12);
        Assert.Equal(Iec60909.PeakFactor(0.1), slg.Kappa, 12);
    }

    // ---- Near-to-generator breaking factor mu: closed forms at the tabulated break times ----
    [Fact]
    public void Breaking_factor_mu_matches_its_closed_forms_at_the_tabulated_times()
    {
        const double r = 5.0;   // Ik''/IrG = 5 (clearly near-generator)
        Assert.Equal(0.84 + 0.26 * Math.Exp(-0.26 * r), Iec60909.BreakingFactorMu(r, 0.02), 12);
        Assert.Equal(0.71 + 0.51 * Math.Exp(-0.30 * r), Iec60909.BreakingFactorMu(r, 0.05), 12);
        Assert.Equal(0.62 + 0.72 * Math.Exp(-0.32 * r), Iec60909.BreakingFactorMu(r, 0.10), 12);
        Assert.Equal(0.56 + 0.94 * Math.Exp(-0.38 * r), Iec60909.BreakingFactorMu(r, 0.25), 12);
        // Longer than the largest tabulated delay clamps to the 0.25 s form.
        Assert.Equal(Iec60909.BreakingFactorMu(r, 0.25), Iec60909.BreakingFactorMu(r, 1.00), 12);
        // A larger initial ratio decays harder (smaller mu) at a fixed break time.
        Assert.True(Iec60909.BreakingFactorMu(8.0, 0.10) < Iec60909.BreakingFactorMu(3.0, 0.10));
    }

    // ---- mu = 1 far from generator, and is capped at 1 ----
    [Fact]
    public void Breaking_factor_mu_is_one_far_from_generator()
    {
        Assert.Equal(1.0, Iec60909.BreakingFactorMu(2.0, 0.02), 12);   // Ik''/IrG <= 2: no AC decay
        Assert.Equal(1.0, Iec60909.BreakingFactorMu(1.0, 0.10), 12);
        Assert.True(Iec60909.BreakingFactorMu(5.0, 0.02) <= 1.0);      // never exceeds the initial current
    }

    // ---- mu interpolates linearly between the tabulated break times ----
    [Fact]
    public void Breaking_factor_mu_interpolates_linearly_between_break_times()
    {
        const double r = 5.0;
        double at02 = Iec60909.BreakingFactorMu(r, 0.02);
        double at05 = Iec60909.BreakingFactorMu(r, 0.05);
        // Halfway between 0.02 and 0.05 s is the arithmetic mean of the endpoint factors.
        Assert.Equal(0.5 * (at02 + at05), Iec60909.BreakingFactorMu(r, 0.035), 12);
    }

    // ---- Asynchronous-motor breaking factor q: closed forms, interpolation and cap ----
    [Fact]
    public void Motor_breaking_factor_q_matches_its_closed_forms()
    {
        const double m = 0.5;   // MW per pole pair; ln 0.5 < 0 so q stays below 1
        double lnM = Math.Log(m);
        Assert.Equal(1.03 + 0.12 * lnM, Iec60909.MotorBreakingFactorQ(m, 0.02), 12);
        Assert.Equal(0.79 + 0.12 * lnM, Iec60909.MotorBreakingFactorQ(m, 0.05), 12);
        Assert.Equal(0.57 + 0.12 * lnM, Iec60909.MotorBreakingFactorQ(m, 0.10), 12);
        Assert.Equal(0.26 + 0.10 * lnM, Iec60909.MotorBreakingFactorQ(m, 0.25), 12);
        // A large machine's formula value exceeds 1 and is capped.
        Assert.Equal(1.0, Iec60909.MotorBreakingFactorQ(1000.0, 0.02), 12);
    }

    // ---- Single-machine breaking current is mu * Ik'' ----
    [Fact]
    public void Single_machine_breaking_current_applies_mu_to_the_initial_current()
    {
        const double ikpp = 8.0, iRated = 1.0, tmin = 0.10;   // ratio 8 => near-generator
        double expected = Iec60909.BreakingFactorMu(ikpp / iRated, tmin) * ikpp;
        Assert.Equal(expected, Iec60909.SynchronousBreakingCurrentKa(ikpp, iRated, tmin), 12);
        Assert.True(Iec60909.SynchronousBreakingCurrentKa(ikpp, iRated, tmin) < ikpp);   // decayed below Ik''
        // Far from generator (ratio <= 2) the breaking current equals the initial current exactly.
        Assert.True(Iec60909.SynchronousBreakingCurrentKa(ikpp, 5.0, tmin) == ikpp);
    }

    // Two identical generators (bus 1, bus 2) feeding a fault at bus 3 through identical branches.
    private static GridModel TwoSymmetricGenerators() => new()
    {
        BaseMva = BaseMva,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pv, BaseKv: Kv),
            new Bus(3, BusType.Pq, BaseKv: Kv),
        },
        Generators = new[]
        {
            new Generator(Bus: 1, Pg: 0.0, MBase: 100.0), new Generator(Bus: 2, Pg: 0.0, MBase: 100.0),
        },
        Branches = new[] { new Branch(1, 3, R: 0.0, X: 0.10), new Branch(2, 3, R: 0.0, X: 0.10) },
    };

    // ---- Partial currents: a single machine carries the whole fault, and its breaking current matches ----
    [Fact]
    public void Partial_currents_put_the_whole_fault_on_a_single_machine()
    {
        GridModel m = SingleGenerator();
        Iec60909.FaultContribution fc = Iec60909.PartialThreePhase(m, 1, minTimeDelaySeconds: 0.10);

        Assert.Single(fc.Machines);
        double total = Iec60909.ThreePhase(m, 1).IkppPu;          // 5.5 pu
        Assert.Equal(total, fc.TotalIkppPu, 12);
        Assert.Equal(total, fc.Machines[0].IkppPu, 12);           // 100% from the one machine

        // The fleet breaking current is that machine's single-machine breaking current.
        double single = Iec60909.SynchronousBreakingCurrentKa(fc.Machines[0].IkppKa, fc.Machines[0].RatedCurrentKa, 0.10);
        Assert.Equal(single, fc.TotalBreakingIbKa, 10);
    }

    // ---- Partial currents: two identical machines split the fault in half, and the magnitudes sum to the total ----
    [Fact]
    public void Partial_currents_split_evenly_between_identical_machines()
    {
        GridModel m = TwoSymmetricGenerators();
        Iec60909.FaultContribution fc = Iec60909.PartialThreePhase(m, 3, minTimeDelaySeconds: 0.10);

        Assert.Equal(2, fc.Machines.Count);
        // Symmetry: each carries exactly half.
        Assert.Equal(fc.Machines[0].IkppPu, fc.Machines[1].IkppPu, 12);
        Assert.Equal(fc.TotalIkppPu / 2.0, fc.Machines[0].IkppPu, 12);

        // Lossless network: the partial magnitudes sum to the total Ik'' (all in phase).
        Assert.Equal(fc.TotalIkppPu, fc.Machines[0].IkppPu + fc.Machines[1].IkppPu, 12);
        // And the total against the driving-point duty (Z_33 = (0.20+0.10)||(0.20+0.10) = j0.15 => 7.333 pu).
        Assert.Equal(Iec60909.ThreePhase(m, 3).IkppPu, fc.TotalIkppPu, 12);

        // Moved 2026-10 (N049, authorised by Mark's best-judgement ruling): this asserted that the fleet breaking current
        // is the sum of the per-machine mu_i * Ik''_i (the single-fed form), which disagreed with ThreePhase's
        // IEC 60909-0:2016 meshed-network (multiple-fed) breaking current; that disagreement is the defect N049 removes.
        // Each machine still reports its own mu_i * Ik''_i; the fleet total is now ThreePhase's, bit for bit, and is the
        // hand formula Ib = Ik'' - sum_i (|dU''_Gi| / c) (1 - mu_i) Ik''_Gi. Here each machine's voltage difference is
        // the drop across its own j0.20 reactance out of its j0.30 path to the fault, so its weight is 0.20 / 0.30 = 2/3,
        // and the multiple-fed total lies above the single-fed sum (the conservative side for a breaker rating).
        Assert.Equal(BitConverter.DoubleToInt64Bits(Iec60909.ThreePhase(m, 3, breakTimeSeconds: 0.10).BreakingIbKa),
            BitConverter.DoubleToInt64Bits(fc.TotalBreakingIbKa));
        double handIb = fc.TotalIkppKa;
        foreach (Iec60909.MachinePartialCurrent g in fc.Machines) handIb -= (2.0 / 3.0) * (1.0 - g.Mu) * g.IkppKa;
        Assert.Equal(handIb, fc.TotalBreakingIbKa, 12);
        Assert.True(fc.Machines[0].Mu < 1.0);   // near to the generator: the decay is real, so the two forms differ
        Assert.True(fc.TotalBreakingIbKa > fc.Machines[0].BreakingIbKa + fc.Machines[1].BreakingIbKa);
    }

    // ---- Branch fault currents: a radial branch carries the whole fault current ----
    [Fact]
    public void Branch_fault_current_on_a_radial_feeder_is_the_whole_fault()
    {
        GridModel m = GeneratorBehindReactance(0.30);   // gen (j0.20) -- line (j0.30) -- fault bus 2
        var branches = Iec60909.BranchFaultCurrents(m, faultBusId: 2);
        Assert.Single(branches);

        double total = Iec60909.ThreePhase(m, 2).IkppPu;    // 2.2 pu
        Assert.Equal(total, branches[0].FromEndPu, 12);      // the branch carries the whole fault
        Assert.Equal(total, branches[0].ToEndPu, 12);        // lossless line: both ends equal
    }

    // ---- Branch fault currents: identical parallel branches each carry half ----
    [Fact]
    public void Branch_fault_currents_split_between_parallel_feeders()
    {
        GridModel m = TwoSymmetricGenerators();          // gens at 1,2 feed a fault at bus 3 via identical lines
        var branches = Iec60909.BranchFaultCurrents(m, faultBusId: 3);
        Assert.Equal(2, branches.Count);

        double total = Iec60909.ThreePhase(m, 3).IkppPu;
        Assert.Equal(total / 2.0, branches[0].ToEndPu, 12);  // each line carries half into the fault bus
        Assert.Equal(branches[0].ToEndPu, branches[1].ToEndPu, 12);
    }

    // ---- End to end: a network fault current feeds the overcurrent coordination grading ----
    [Fact]
    public void Branch_fault_current_feeds_the_coordination_grading()
    {
        GridModel m = GeneratorBehindReactance(0.30);
        double faultKa = Iec60909.BranchFaultCurrents(m, 2)[0].FromEndKa;

        // A primary and a slower backup relay, both picking up well below the fault current.
        var primary = new OvercurrentElement(OvercurrentCurve.IecStandardInverse, PickupCurrent: faultKa / 5.0, TimeDial: 0.10);
        var backup = primary with { TimeDial = 0.40 };
        var chain = OvercurrentCoordination.GradeChain(new[] { primary, backup }, faultKa, requiredMarginSeconds: 0.10);

        Assert.Single(chain);
        Assert.Equal(OvercurrentCoordination.Grade(primary, backup, faultKa, 0.10).MarginSeconds, chain[0].MarginSeconds, 12);
        Assert.True(chain[0].MarginSeconds > 0.0);           // the slower backup lags the primary
    }

    // ---- A machine's own X''d feeds the fault network, overriding the synthesised default ----
    [Fact]
    public void Per_machine_subtransient_reactance_overrides_the_synthesised_default()
    {
        // A machine carrying X''d = 0.10 pu (100 MVA on 100 MVA base) => Zk = j0.10 => Ik'' = 1.10/0.10 = 11.0.
        Iec60909.Duty stiff = Iec60909.ThreePhase(SingleGeneratorWithXd(0.10), faultBusId: 1);
        Assert.Equal(0.10, stiff.TheveninXPu, 12);
        Assert.Equal(11.0, stiff.IkppPu, 12);

        // A weaker machine (X''d = 0.40) gives a smaller current, Ik'' = 1.10/0.40 = 2.75.
        Iec60909.Duty weak = Iec60909.ThreePhase(SingleGeneratorWithXd(0.40), faultBusId: 1);
        Assert.Equal(2.75, weak.IkppPu, 12);

        // No supplied reactance still lands on the synthesised default (0.20 => 5.5), unchanged.
        Assert.Equal(0.20, Iec60909.ThreePhase(SingleGenerator(), 1).TheveninXPu, 12);
    }

    // ---- A machine's own X2 feeds the negative-sequence network and the line-to-line duty ----
    [Fact]
    public void Per_machine_negative_sequence_reactance_feeds_the_line_to_line_fault()
    {
        // Machine with X''d = 0.20, X2 = 0.24. Z1 = j0.20, Z2 = j0.24, so LL = sqrt(3) c / |Z1+Z2|.
        GridModel m = new()
        {
            BaseMva = BaseMva,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv) },
            Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0, SubtransientXdPu: 0.20, NegativeSeqXPu: 0.24) },
            Branches = Array.Empty<Branch>(),
        };
        (System.Numerics.Complex z2, _) = Iec60909.NegativeSequenceThevenin(m, 1);
        Assert.Equal(0.24, z2.Imaginary, 12);

        Iec60909.Duty ll = Iec60909.LineToLine(m, 1);
        double expected = Math.Sqrt(3.0) * 1.10 / (0.20 + 0.24);   // c=1.10 HV; |Z1+Z2| = 0.44
        Assert.Equal(expected, ll.IkppPu, 12);

        // With no separate X2, Z2 = Z1 and the LL duty is unchanged (sqrt(3)/2 of the three-phase).
        Iec60909.Duty def = Iec60909.LineToLine(SingleGenerator(), 1);
        Assert.Equal(Math.Sqrt(3.0) / 2.0 * Iec60909.ThreePhase(SingleGenerator(), 1).IkppPu, def.IkppPu, 12);
    }

    // ---- Zero-sequence network: a delta/grounded-wye transformer earths the wye bus and isolates the delta side ----
    [Fact]
    public void Zero_sequence_delta_wye_transformer_grounds_only_the_wye_side()
    {
        // Bus 1 (delta, source) -- transformer Z0T = j0.10 -- bus 2 (grounded wye).
        GridModel m = new()
        {
            BaseMva = BaseMva,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv) },
            Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0) },
            Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.08, R0: 0.0, X0: 0.10,
                FromWinding: WindingConnection.Delta, ToWinding: WindingConnection.WyeGrounded) },
        };

        // Grounded-wye bus 2: Z0 = the shunt to earth = j0.10.
        (System.Numerics.Complex z0, _, bool grounded) = Iec60909.ZeroSequenceThevenin(m, 2);
        Assert.True(grounded);
        Assert.Equal(0.10, z0.Imaginary, 12);

        // Delta bus 1: isolated from the zero sequence, so ungrounded (Z0 infinite).
        (System.Numerics.Complex z0d, _, bool groundedD) = Iec60909.ZeroSequenceThevenin(m, 1);
        Assert.False(groundedD);
        Assert.True(double.IsPositiveInfinity(z0d.Imaginary));
    }

    // ---- A neutral grounding reactance adds 3*Xn to the wye-side zero-sequence path ----
    [Fact]
    public void Zero_sequence_neutral_grounding_adds_three_xn()
    {
        GridModel m = new()
        {
            BaseMva = BaseMva,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv) },
            Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0) },
            Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.08, R0: 0.0, X0: 0.10,
                FromWinding: WindingConnection.Delta, ToWinding: WindingConnection.WyeGrounded, ToNeutralXPu: 0.05) },
        };
        (System.Numerics.Complex z0, _, _) = Iec60909.ZeroSequenceThevenin(m, 2);
        Assert.Equal(0.10 + 3.0 * 0.05, z0.Imaginary, 12);   // Z0T + 3 Xn
    }

    // ---- A line in series, and a YNyn transformer as a through path, add impedance to earth ----
    [Fact]
    public void Zero_sequence_series_path_sums_line_and_through_transformer()
    {
        // Bus 1 (delta) --Dyn Z0T=j0.10-- bus 2 (gy) --line X0=0.20-- bus 3, and bus 2 --YNyn Z0T=j0.15-- bus 4.
        GridModel m = new()
        {
            BaseMva = BaseMva,
            Buses = new[]
            {
                new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv),
                new Bus(3, BusType.Pq, BaseKv: Kv), new Bus(4, BusType.Pq, BaseKv: Kv),
            },
            Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0) },
            Branches = new[]
            {
                new Branch(1, 2, R: 0.0, X: 0.08, R0: 0.0, X0: 0.10,
                    FromWinding: WindingConnection.Delta, ToWinding: WindingConnection.WyeGrounded),
                new Branch(2, 3, R: 0.0, X: 0.10, R0: 0.0, X0: 0.20),                       // a line
                new Branch(2, 4, R: 0.0, X: 0.05, R0: 0.0, X0: 0.15,
                    FromWinding: WindingConnection.WyeGrounded, ToWinding: WindingConnection.WyeGrounded),
            },
        };

        // Bus 2 sees the shunt j0.10 (its two series branches dead-end with no further ground) => Z0 = j0.10.
        Assert.Equal(0.10, Iec60909.ZeroSequenceThevenin(m, 2).Z0Pu.Imaginary, 12);
        // Bus 3 is the line beyond bus 2: Z0 = line 0.20 + shunt 0.10 = j0.30.
        Assert.Equal(0.30, Iec60909.ZeroSequenceThevenin(m, 3).Z0Pu.Imaginary, 12);
        // Bus 4 is behind the YNyn through path: Z0 = 0.15 + shunt 0.10 = j0.25.
        Assert.Equal(0.25, Iec60909.ZeroSequenceThevenin(m, 4).Z0Pu.Imaginary, 12);
    }

    // ---- With no earthed neutral anywhere the zero-sequence network floats (ungrounded) ----
    [Fact]
    public void Zero_sequence_without_a_ground_reference_is_ungrounded()
    {
        // A lone line, no transformer grounding: no path to earth.
        GridModel m = new()
        {
            BaseMva = BaseMva,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv) },
            Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0) },
            Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.10, R0: 0.0, X0: 0.20) },
        };
        (_, _, bool grounded) = Iec60909.ZeroSequenceThevenin(m, 1);
        Assert.False(grounded);
    }

    // A grounded network: gen (X''d=0.20) at bus 1, Dyn transformer (delta at bus 1, grounded wye at bus 2,
    // X=0.08, Z0T=j0.10) to bus 2. Positive/negative Thevenin at bus 2 = j0.28; zero-sequence = j0.10.
    private static GridModel GroundedTransformerCase() => new()
    {
        BaseMva = BaseMva,
        Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv) },
        Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0) },
        Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.08, R0: 0.0, X0: 0.10,
            FromWinding: WindingConnection.Delta, ToWinding: WindingConnection.WyeGrounded) },
    };

    // ---- Single-line-to-earth from the model, wired through all three sequence networks ----
    [Fact]
    public void Single_line_to_ground_from_the_model_matches_the_hand_calculation()
    {
        GridModel m = GroundedTransformerCase();
        Assert.Equal(0.28, Iec60909.PositiveSequenceThevenin(m, 2).Z1Pu.Imaginary, 12);
        Assert.Equal(0.10, Iec60909.ZeroSequenceThevenin(m, 2).Z0Pu.Imaginary, 12);

        // Ik1'' = 3c / |Z1+Z2+Z0| = 3*1.10 / (0.28+0.28+0.10) = 3.30 / 0.66 = 5.0 pu.
        Iec60909.Duty slg = Iec60909.SingleLineToGround(m, 2);
        Assert.Equal(5.0, slg.IkppPu, 12);

        // Identical to the pure sequence form fed the same Thevenin impedances.
        var z = new System.Numerics.Complex(0.0, 0.28);
        var z0 = new System.Numerics.Complex(0.0, 0.10);
        double pure = Iec60909.FromSequenceImpedances(UnbalancedFaultType.SingleLineToGround, 1.1, Kv, z, z, z0, BaseMva).IkppPu;
        Assert.Equal(pure, slg.IkppPu, 12);
    }

    // ---- Double-line-to-earth from the model equals the sequence form on the same Thevenins ----
    [Fact]
    public void Double_line_to_ground_from_the_model_matches_the_sequence_form()
    {
        GridModel m = GroundedTransformerCase();
        Iec60909.Duty llg = Iec60909.DoubleLineToGround(m, 2);
        var z = new System.Numerics.Complex(0.0, 0.28);
        var z0 = new System.Numerics.Complex(0.0, 0.10);
        double pure = Iec60909.FromSequenceImpedances(UnbalancedFaultType.DoubleLineToGround, 1.1, Kv, z, z, z0, BaseMva).IkppPu;
        Assert.Equal(pure, llg.IkppPu, 12);
    }

    // ---- On an ungrounded bus the earth faults carry no current ----
    [Fact]
    public void Earth_faults_on_an_ungrounded_bus_are_zero()
    {
        // A lone line, no grounded transformer: bus 1 is ungrounded in the zero sequence.
        GridModel m = new()
        {
            BaseMva = BaseMva,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: Kv), new Bus(2, BusType.Pq, BaseKv: Kv) },
            Generators = new[] { new Generator(Bus: 1, Pg: 0.0, MBase: 100.0) },
            Branches = new[] { new Branch(1, 2, R: 0.0, X: 0.10, R0: 0.0, X0: 0.20) },
        };
        Assert.Equal(0.0, Iec60909.SingleLineToGround(m, 1).IkppPu, 12);
        Assert.Equal(0.0, Iec60909.DoubleLineToGround(m, 1).IkppKa, 12);
    }

    // ---- Generator impedance-correction factor K_G: closed form, reductions and monotonicity ----
    [Fact]
    public void Generator_correction_kg_matches_its_closed_form()
    {
        // Un = 21 kV, UrG = 21 kV, x''d = 0.17, cos phi = 0.85, c_max = 1.10.
        const double un = 21.0, urg = 21.0, xd = 0.17, cosPhi = 0.85, cMax = 1.10;
        double sinPhi = Math.Sqrt(1.0 - cosPhi * cosPhi);
        double expected = (un / urg) * cMax / (1.0 + xd * sinPhi);
        Assert.Equal(expected, Iec60909.GeneratorCorrectionKg(un, urg, xd, cosPhi, cMax), 12);

        // Reduction: no reactive loading correction (x''d sin phi -> 0) at matched voltage gives K_G = c_max.
        Assert.Equal(cMax, Iec60909.GeneratorCorrectionKg(un, urg, 0.0, cosPhi, cMax), 12);
        Assert.Equal(cMax, Iec60909.GeneratorCorrectionKg(un, urg, xd, 1.0, cMax), 12);   // unity PF => sin phi = 0
        // The voltage ratio scales K_G linearly.
        Assert.Equal(2.0 * expected, Iec60909.GeneratorCorrectionKg(2.0 * un, urg, xd, cosPhi, cMax), 12);
        // A larger subtransient reactance lowers the correction.
        Assert.True(Iec60909.GeneratorCorrectionKg(un, urg, 0.30, cosPhi, cMax)
                  < Iec60909.GeneratorCorrectionKg(un, urg, 0.10, cosPhi, cMax));
    }

    // ---- Breaker adequacy: each rating checked, with the signed kA margin ----
    [Fact]
    public void Breaker_adequacy_checks_each_rating_against_the_duty()
    {
        Iec60909.Duty d = Iec60909.ThreePhase(SingleGenerator(), 1);   // fault duration 1.0 s (default)

        // A device that clears every quantity by 5 kA (voltage covered) is adequate; margins are exact.
        var ok = new Iec60909.BreakerRating(RatedVoltageKv: 145.0,
            MakingPeakKa: d.PeakIpKa + 5.0, BreakingSymKa: d.BreakingIbKa + 5.0,
            ShortTimeWithstandKa: d.ThermalIthKa + 5.0, ShortTimeSeconds: 1.0);
        Iec60909.BreakerAdequacy a = Iec60909.CheckBreaker(d, ok, systemVoltageKv: 132.0);
        Assert.True(a.Adequate);
        Assert.True(a.VoltageOk && a.MakingOk && a.BreakingOk && a.ThermalOk);
        Assert.Equal(5.0, a.MakingMarginKa, 12);
        Assert.Equal(5.0, a.BreakingMarginKa, 12);
        Assert.Equal(5.0, a.ThermalMarginKa, 12);   // Tkr = Tk = 1 s, so no energy scaling

        // Undersized making rating fails the making check and the overall verdict.
        var weakMaking = ok with { MakingPeakKa = d.PeakIpKa - 1.0 };
        Iec60909.BreakerAdequacy m = Iec60909.CheckBreaker(d, weakMaking, 132.0);
        Assert.False(m.MakingOk);
        Assert.False(m.Adequate);
        Assert.Equal(-1.0, m.MakingMarginKa, 12);

        // A system voltage above the breaker's rating fails only the voltage check.
        Iec60909.BreakerAdequacy v = Iec60909.CheckBreaker(d, ok, systemVoltageKv: 245.0);
        Assert.False(v.VoltageOk);
        Assert.False(v.Adequate);
        Assert.True(v.MakingOk && v.BreakingOk && v.ThermalOk);
    }

    // ---- The thermal check is equal-energy: the rating is referred to the fault duration ----
    [Fact]
    public void Breaker_thermal_check_scales_by_equal_energy()
    {
        // A duty over Tk = 2 s; a breaker rated for Tkr = 1 s. Its effective current at 2 s is I_thr / sqrt(2).
        Iec60909.Duty d = Iec60909.FromThevenin(1.1, Kv, theveninRPu: 0.02, theveninXPu: 0.20, BaseMva,
            f0Hz: 50.0, faultDurationSeconds: 2.0);
        Assert.Equal(2.0, d.FaultDurationSeconds, 12);

        const double iThr = 30.0;
        var rating = new Iec60909.BreakerRating(RatedVoltageKv: 145.0, MakingPeakKa: 1e9, BreakingSymKa: 1e9,
            ShortTimeWithstandKa: iThr, ShortTimeSeconds: 1.0);
        Iec60909.BreakerAdequacy a = Iec60909.CheckBreaker(d, rating, 132.0);
        double expectedMargin = iThr * Math.Sqrt(1.0 / 2.0) - d.ThermalIthKa;
        Assert.Equal(expectedMargin, a.ThermalMarginKa, 12);
    }

    // ---- Transformer impedance-correction factor K_T: closed form, reduction and monotonicity ----
    [Fact]
    public void Transformer_correction_kt_matches_its_closed_form()
    {
        const double cMax = 1.10;
        Assert.Equal(0.95 * cMax / (1.0 + 0.6 * 0.12), Iec60909.TransformerCorrectionKt(0.12, cMax), 12);
        // Reduction: a zero-reactance transformer gives K_T = 0.95 c_max exactly.
        Assert.Equal(0.95 * cMax, Iec60909.TransformerCorrectionKt(0.0, cMax), 12);
        // A larger reactance lowers the correction.
        Assert.True(Iec60909.TransformerCorrectionKt(0.20, cMax) < Iec60909.TransformerCorrectionKt(0.05, cMax));
    }
}
