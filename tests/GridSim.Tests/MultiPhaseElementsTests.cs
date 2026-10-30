using System;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// N062 / R059 (October 2026): each new three-phase sweep element against its defining equation on a two-bus feeder
/// (source, one branch, one bus), where the converged state can be written down by hand; the model and loader
/// validation of the new elements; and the v1 path unchanged. The IEEE 123 feeder comparison is in
/// MultiPhaseIeee123Tests.
/// </summary>
public class MultiPhaseElementsTests
{
    private const double Tol = 1e-12;   // the sweep converges to 1e-14 here; the hand formulae are exact

    private static readonly ThreePhaseSweepOptions Tight = new() { Tolerance = 1e-14, MaxIterations = 500 };

    private static readonly Complex Z = new(0.01, 0.03);   // per-unit series impedance per phase (decoupled)

    private static MultiPhaseModel TwoBus(PhaseBranch br, PhaseLoad[]? loads = null, PhaseCapacitor[]? caps = null) =>
        new(new[] { new PhaseBus(1, true, MultiPhaseModel.BalancedNominal()), new PhaseBus(2, false) },
            new[] { br }, loads ?? Array.Empty<PhaseLoad>(), 100.0, caps);

    private static MultiPhaseResult Solve(MultiPhaseModel m)
    {
        MultiPhaseResult r = new ThreePhaseSweep().Solve(m, Tight);
        Assert.True(r.Converged, $"{r.Status} after {r.Iterations}, delta {r.MaxDelta}");
        return r;
    }

    private static void Near(Complex expected, Complex actual)
    {
        Assert.True((expected - actual).Magnitude < Tol, $"expected {expected}, got {actual} (difference {(expected - actual).Magnitude:R})");
    }

    [Fact]
    public void An_ideal_regulator_multiplies_the_voltage_and_divides_the_current_by_its_ratio()
    {
        double[] a = { PhaseBranch.StepRegulatorRatio(7, 0.00625), PhaseBranch.StepRegulatorRatio(0, 0.00625), PhaseBranch.StepRegulatorRatio(-3, 0.00625) };
        Assert.Equal(1.04375, a[0], 15);
        Assert.Equal(0.98125, a[2], 15);
        var m = TwoBus(new PhaseBranch(1, 2, new PhaseImpedance(new Complex[3, 3]), RegulatorRatio: a));
        MultiPhaseResult r = Solve(m);
        Complex[] vs = MultiPhaseModel.BalancedNominal();
        for (int p = 0; p < 3; p++) Assert.Equal(vs[p] * a[p], r.V[1][p]);   // exact: no load, no drop
    }

    [Fact]
    public void A_regulator_feeding_a_line_obeys_its_ratio_and_the_line_equation()
    {
        // 1 -(regulator a)- 2 -(line Z)- 3 with a constant-power load at 3: V3 = a V1 - Z I and I = conj(S / V3).
        double[] a = { 1.05, 1.05, 1.05 };
        var m = new MultiPhaseModel(
            new[] { new PhaseBus(1, true, MultiPhaseModel.BalancedNominal()), new PhaseBus(2, false), new PhaseBus(3, false) },
            new[]
            {
                new PhaseBranch(1, 2, new PhaseImpedance(new Complex[3, 3]), RegulatorRatio: a),
                new PhaseBranch(2, 3, PhaseImpedance.Diagonal(Z)),
            },
            new[] { new PhaseLoad(3, new[] { new Complex(0.5, 0.2), new Complex(0.4, 0.1), new Complex(0.3, 0.15) }) });
        MultiPhaseResult r = Solve(m);
        for (int p = 0; p < 3; p++)
        {
            Complex i = Complex.Conjugate(m.Loads[0].SPerPhase[p] / r.V[2][p]);
            Near(r.V[1][p] - Z * i, r.V[2][p]);
            Near(r.V[0][p] * a[p], r.V[1][p]);
            Near(i, r.BranchCurrents![1][p]);   // the series current of the line
            Near(i, r.BranchCurrents[0][p]);    // the regulator's output current is the line's
        }
    }

    [Fact]
    public void A_single_phase_lateral_solves_its_phase_and_leaves_the_others_exactly_zero()
    {
        var z = new Complex[3, 3];
        z[1, 1] = Z;   // phase b only
        var s = new[] { Complex.Zero, new Complex(0.4, 0.2), Complex.Zero };
        var m = TwoBus(new PhaseBranch(1, 2, new PhaseImpedance(z), Phase.B), new[] { new PhaseLoad(2, s) });
        MultiPhaseResult r = Solve(m);
        Assert.Equal(Phase.B, r.BusPhases![1]);
        Assert.Equal(Phase.Abc, r.BusPhases[0]);
        Assert.Equal(Complex.Zero, r.V[1][0]);
        Assert.Equal(Complex.Zero, r.V[1][2]);
        Near(r.V[0][1] - Z * Complex.Conjugate(s[1] / r.V[1][1]), r.V[1][1]);
    }

    [Fact]
    public void A_constant_impedance_load_has_the_closed_form_voltage_divider_solution()
    {
        // I = V2 conj(S) (nominal 1 pu), so V2 = V1 - Z V2 conj(S): V2 = V1 / (1 + Z conj(S)) per phase.
        var s = new[] { new Complex(0.5, 0.2), new Complex(0.4, 0.1), new Complex(0.2, 0.0) };
        var m = TwoBus(new PhaseBranch(1, 2, PhaseImpedance.Diagonal(Z)), new[] { new PhaseLoad(2, s, PhaseLoadModel.ConstantImpedance) });
        MultiPhaseResult r = Solve(m);
        for (int p = 0; p < 3; p++) Near(r.V[0][p] / (1.0 + Z * Complex.Conjugate(s[p])), r.V[1][p]);
    }

    [Fact]
    public void A_constant_current_load_draws_its_nominal_current_at_its_power_factor_behind_its_voltage()
    {
        var s = new[] { new Complex(0.5, 0.2), new Complex(0.4, 0.3), new Complex(0.2, 0.05) };
        var m = TwoBus(new PhaseBranch(1, 2, PhaseImpedance.Diagonal(Z)), new[] { new PhaseLoad(2, s, PhaseLoadModel.ConstantCurrent) });
        MultiPhaseResult r = Solve(m);
        for (int p = 0; p < 3; p++)
        {
            Complex i = r.BranchCurrents![0][p];
            Assert.Equal(s[p].Magnitude, i.Magnitude, 12);                                   // |I| = |S| / 1 pu
            Assert.Equal(s[p].Phase, r.V[1][p].Phase - i.Phase, 12);                         // angle(V) - angle(I) = angle(S)
            Near(r.V[0][p] - Z * i, r.V[1][p]);
        }
    }

    [Fact]
    public void A_delta_load_draws_its_power_on_each_phase_pair_and_no_zero_sequence_current()
    {
        var sDelta = new[] { new Complex(0.3, 0.1), new Complex(0.2, 0.05), Complex.Zero };   // a-b and b-c only
        foreach (PhaseLoadModel model in Enum.GetValues<PhaseLoadModel>())
        {
            var m = TwoBus(new PhaseBranch(1, 2, PhaseImpedance.Diagonal(Z)), new[] { new PhaseLoad(2, sDelta, model, PhaseLoadConnection.Delta) });
            MultiPhaseResult r = Solve(m);
            Complex[] i = r.BranchCurrents![0];
            Near(Complex.Zero, i[0] + i[1] + i[2]);   // a delta load has no path to neutral
            Complex vab = r.V[1][0] - r.V[1][1], vbc = r.V[1][1] - r.V[1][2];
            // Recover the branch currents: with no c-a branch, I_a = I_ab and I_c = -I_bc.
            Complex iab = i[0], ibc = -i[2];
            double k = model switch
            {
                PhaseLoadModel.ConstantPower => 1.0,
                PhaseLoadModel.ConstantCurrent => vab.Magnitude / Math.Sqrt(3.0),
                _ => vab.Magnitude * vab.Magnitude / 3.0,
            };
            Near(sDelta[0] * k, vab * Complex.Conjugate(iab));
            double kbc = model switch
            {
                PhaseLoadModel.ConstantPower => 1.0,
                PhaseLoadModel.ConstantCurrent => vbc.Magnitude / Math.Sqrt(3.0),
                _ => vbc.Magnitude * vbc.Magnitude / 3.0,
            };
            Near(sDelta[1] * kbc, vbc * Complex.Conjugate(ibc));
        }
    }

    [Fact]
    public void A_capacitor_and_a_line_shunt_have_the_closed_form_solutions()
    {
        // Capacitor B at bus 2, no load: I = j B V2, so V2 = V1 / (1 + j Z B).
        double b = 0.05;
        var m = TwoBus(new PhaseBranch(1, 2, PhaseImpedance.Diagonal(Z)), caps: new[] { new PhaseCapacitor(2, new[] { b, b, 0.0 }) });
        MultiPhaseResult r = Solve(m);
        Near(r.V[0][0] / (1.0 + Z * new Complex(0, b)), r.V[1][0]);
        Near(r.V[0][1] / (1.0 + Z * new Complex(0, b)), r.V[1][1]);
        Near(r.V[0][2], r.V[1][2]);   // no unit on phase c: no current, no drop

        // Line charging Y (pi model), no load: the To-end half draws Y/2 V2 through Z, so V2 = V1 / (1 + Z Y / 2).
        Complex y = new(0, 0.02);
        var mShunt = TwoBus(new PhaseBranch(1, 2, PhaseImpedance.Diagonal(Z), ShuntY: PhaseImpedance.Diagonal(y)));
        MultiPhaseResult rs = Solve(mShunt);
        for (int p = 0; p < 3; p++) Near(rs.V[0][p] / (1.0 + Z * y / 2.0), rs.V[1][p]);
    }

    [Fact]
    public void A_closed_switch_carries_the_voltage_across_unchanged()
    {
        var s = new[] { new Complex(0.5, 0.2), new Complex(0.4, 0.1), new Complex(0.3, 0.15) };
        var m = new MultiPhaseModel(
            new[] { new PhaseBus(1, true, MultiPhaseModel.BalancedNominal()), new PhaseBus(2, false), new PhaseBus(3, false) },
            new[] { new PhaseBranch(1, 2, PhaseImpedance.Diagonal(Z)), new PhaseBranch(2, 3, new PhaseImpedance(new Complex[3, 3])) },
            new[] { new PhaseLoad(3, s) });
        MultiPhaseResult r = Solve(m);
        for (int p = 0; p < 3; p++) Assert.Equal(r.V[1][p], r.V[2][p]);   // exact: zero impedance, zero drop
    }

    [Fact]
    public void A_v1_model_is_unchanged_by_the_new_optional_parameters()
    {
        // The same feeder built with the v1 constructor and with every new parameter at its default value: identical bits.
        var s = new[] { new Complex(0.5, 0.2), new Complex(0.4, 0.1), new Complex(0.3, 0.15) };
        var m1 = TwoBus(new PhaseBranch(1, 2, PhaseImpedance.Diagonal(Z)), new[] { new PhaseLoad(2, s) });
        var m2 = TwoBus(new PhaseBranch(1, 2, PhaseImpedance.Diagonal(Z), Phase.Abc, null, null),
            new[] { new PhaseLoad(2, s, PhaseLoadModel.ConstantPower, PhaseLoadConnection.Wye) }, Array.Empty<PhaseCapacitor>());
        Assert.False(m1.HasExtendedElements);
        Assert.False(m2.HasExtendedElements);
        MultiPhaseResult r1 = new ThreePhaseSweep().Solve(m1), r2 = new ThreePhaseSweep().Solve(m2);
        Assert.Equal(r1.Iterations, r2.Iterations);
        for (int p = 0; p < 3; p++) Assert.Equal(r1.V[1][p], r2.V[1][p]);
    }

    [Fact]
    public void Inconsistent_phases_and_a_reversed_regulator_are_refused()
    {
        var zb = new Complex[3, 3];
        zb[0, 0] = Z;
        zb[1, 1] = Z;   // a phase-b entry on a branch declared phase a
        Assert.Throws<ArgumentException>(() => TwoBus(new PhaseBranch(1, 2, new PhaseImpedance(zb), Phase.A)));

        var za = new Complex[3, 3];
        za[0, 0] = Z;
        // A load on phase b of a phase-a bus.
        var bad = TwoBus(new PhaseBranch(1, 2, new PhaseImpedance(za), Phase.A), new[] { new PhaseLoad(2, new[] { Complex.Zero, new Complex(0.1, 0), Complex.Zero }) });
        Assert.Contains("does not have", Assert.Throws<ArgumentException>(() => new ThreePhaseSweep().Solve(bad)).Message, StringComparison.Ordinal);

        // A branch carrying phase b out of a phase-a bus.
        var zab = new Complex[3, 3];
        zab[0, 0] = Z;
        zab[1, 1] = Z;
        var m = new MultiPhaseModel(
            new[] { new PhaseBus(1, true), new PhaseBus(2, false), new PhaseBus(3, false) },
            new[] { new PhaseBranch(1, 2, new PhaseImpedance(za), Phase.A), new PhaseBranch(2, 3, new PhaseImpedance(zab), Phase.A | Phase.B) },
            Array.Empty<PhaseLoad>());
        Assert.Contains("has only a", Assert.Throws<ArgumentException>(() => new ThreePhaseSweep().Solve(m)).Message, StringComparison.Ordinal);

        // A regulator whose From bus is downstream.
        var rev = TwoBus(new PhaseBranch(2, 1, new PhaseImpedance(new Complex[3, 3]), RegulatorRatio: new[] { 1.0, 1.0, 1.0 }));
        Assert.Contains("faces the source", Assert.Throws<ArgumentException>(() => new ThreePhaseSweep().Solve(rev)).Message, StringComparison.Ordinal);

        // A capacitor with a negative rating.
        Assert.Throws<ArgumentException>(() => TwoBus(new PhaseBranch(1, 2, PhaseImpedance.Diagonal(Z)), caps: new[] { new PhaseCapacitor(2, new[] { -1.0, 0, 0 }) }));
    }

    // ---- loader: the new members ---------------------------------------------------------------------------------

    private const string Head = "{ \"schema\": \"gridsim-dist3ph/1\", \"name\": \"t\", \"description\": \"test\", \"baseMva\": 1.0, ";

    private static string Feeder(string body) => Head + body + " }";

    private const string Buses3 = "\"buses\": [ { \"id\": 1, \"baseKvLL\": 4.16, \"source\": {} }, { \"id\": 2, \"baseKvLL\": 4.16, \"name\": \"RG\" }, { \"id\": 3, \"baseKvLL\": 4.16 } ]";

    private const string CodeAc = "\"lineCodes\": [ { \"id\": \"ac\", \"unit\": \"ohm/mile\", " +
        "\"r\": [[0.4, 0, 0.1], [0, 0, 0], [0.1, 0, 0.4]], \"x\": [[1.0, 0, 0.4], [0, 0, 0], [0.4, 0, 1.0]], " +
        "\"b\": [[5.0, 0, -1.0], [0, 0, 0], [-1.0, 0, 5.0]], \"bUnit\": \"uS/mile\" } ]";

    private static MultiPhaseFeeder Parse(string json) => MultiPhaseJsonLoader.Parse(json, "t.json");

    private static string Refused(string json)
    {
        var ex = Assert.Throws<InvalidDataException>(() => Parse(json));
        Assert.StartsWith("t.json: ", ex.Message, StringComparison.Ordinal);
        return ex.Message;
    }

    [Fact]
    public void The_loader_reads_a_regulator_a_two_phase_line_with_charging_a_capacitor_and_a_delta_zip_load()
    {
        string json = Feeder(Buses3 + ", " + CodeAc + ", " +
            "\"regulators\": [ { \"id\": \"R\", \"from\": 1, \"to\": 2, \"tapStepPercent\": 0.625, \"taps\": [4, 0, -2] } ], " +
            "\"lines\": [ { \"id\": \"L\", \"from\": 2, \"to\": 3, \"code\": \"ac\", \"length\": 0.5, \"lengthUnit\": \"mile\", \"phases\": \"ac\" } ], " +
            "\"capacitors\": [ { \"id\": \"C\", \"bus\": 3, \"kvar\": [100, 0, 50] } ], " +
            "\"loads\": [ { \"id\": \"Z\", \"bus\": 3, \"connection\": \"wye\", \"model\": \"constant-impedance\", \"kw\": [10, 0, 20], \"kvar\": [5, 0, 10] }, " +
            "             { \"id\": \"I\", \"bus\": 3, \"connection\": \"wye\", \"model\": \"constant-current\", \"kw\": [10, 0, 0], \"kvar\": [0, 0, 0] } ]");
        MultiPhaseFeeder f = Parse(json);
        Assert.Equal(new[] { "line", "regulator" }, f.BranchKinds);   // branches are stored lines first, then regulators
        Assert.Equal("RG", f.BusLabel(2));
        Assert.Equal("3", f.BusLabel(3));
        double[] ratio = f.Model.Branches[1].RegulatorRatio!;
        Assert.Equal(1.025, ratio[0], 14);    // 1 + 0.00625 x 4
        Assert.Equal(1.0, ratio[1], 14);
        Assert.Equal(0.9875, ratio[2], 14);   // 1 - 0.00625 x 2
        Assert.Equal(Phase.A | Phase.C, f.Model.Branches[0].Phases);
        // Shunt per-unit = B (S/mile) x length x Zbase; Zbase = 4.16^2 / 1 ohm.
        double expected = 5.0e-6 * 0.5 * 4.16 * 4.16;
        Assert.True(Math.Abs(f.Model.Branches[0].ShuntY![0, 0].Imaginary - expected) <= 1e-15 * expected);
        Assert.Equal(0.0, f.Model.Branches[0].ShuntY![0, 0].Real);
        Assert.Equal(new[] { 100.0 / (1000.0 / 3.0), 0.0, 50.0 / (1000.0 / 3.0) }, f.Model.Capacitors[0].QPerPhase);
        Assert.Equal(PhaseLoadModel.ConstantImpedance, f.Model.Loads[0].Model);
        Assert.Equal(PhaseLoadModel.ConstantCurrent, f.Model.Loads[1].Model);
        Assert.True(new ThreePhaseSweep().Solve(f.Model).Converged);
    }

    [Fact]
    public void An_open_switch_is_not_a_branch_and_a_closed_one_is()
    {
        string json = Feeder(Buses3 + ", " +
            "\"switches\": [ { \"id\": \"S1\", \"from\": 1, \"to\": 2, \"closed\": true }, { \"id\": \"S2\", \"from\": 2, \"to\": 3, \"closed\": true }, " +
            "                { \"id\": \"S3\", \"from\": 1, \"to\": 3, \"closed\": false } ]");
        MultiPhaseFeeder f = Parse(json);
        Assert.Equal(2, f.Model.Branches.Count);
        Assert.Equal(new[] { "S3" }, f.OpenSwitchIds);
        Assert.Equal(new[] { "switch", "switch" }, f.BranchKinds);
    }

    [Theory]
    [InlineData("\"bUnit\": \"uS/mile\"", "\"bUnit\": \"uS/foot\"", "$.lineCodes[0].bUnit")]
    [InlineData(", \"bUnit\": \"uS/mile\"", "", "$.lineCodes[0].bUnit")]                                 // b without its unit
    [InlineData("[-1.0, 0, 5.0]]", "[-2.0, 0, 5.0]]", "$.lineCodes[0].b[0][2]")]                          // not symmetric
    [InlineData("\"phases\": \"ac\"", "\"phases\": \"abc\"", "$.lines[0].phases")]                        // code has no phase b
    [InlineData("\"phases\": \"ac\"", "\"phases\": \"a\"", "$.lines[0].phases")]                          // code has phase c entries
    [InlineData("\"phases\": \"ac\"", "\"phases\": \"ca\"", "$.lines[0].phases")]                         // not in a, b, c order
    [InlineData("\"taps\": [4, 0, -2]", "\"taps\": [4, 0.5, -2]", "$.regulators[0].taps[1]")]
    [InlineData("\"taps\": [4, 0, -2]", "\"taps\": [4, 0]", "$.regulators[0].taps")]
    [InlineData("\"tapStepPercent\": 0.625", "\"tapStepPercent\": 0", "$.regulators[0].tapStepPercent")]
    [InlineData("\"kvar\": [100, 0, 50]", "\"kvar\": [100, 10, 50]", "$.capacitors[0].kvar[1]")]          // no phase b at bus 3
    [InlineData("\"kvar\": [100, 0, 50]", "\"kvar\": [0, 0, 0]", "$.capacitors[0].kvar")]
    [InlineData("\"kw\": [10, 0, 20]", "\"kw\": [10, 5, 20]", "$.loads[0].kw")]                           // no phase b at bus 3
    [InlineData("\"model\": \"constant-impedance\"", "\"model\": \"zip\"", "$.loads[0].model")]
    [InlineData("\"name\": \"RG\"", "\"name\": \"\"", "$.buses[1].name")]
    public void The_new_members_are_strict(string from, string to, string path)
    {
        string json = Feeder(Buses3 + ", " + CodeAc + ", " +
            "\"regulators\": [ { \"id\": \"R\", \"from\": 1, \"to\": 2, \"tapStepPercent\": 0.625, \"taps\": [4, 0, -2] } ], " +
            "\"lines\": [ { \"id\": \"L\", \"from\": 2, \"to\": 3, \"code\": \"ac\", \"length\": 0.5, \"lengthUnit\": \"mile\", \"phases\": \"ac\" } ], " +
            "\"capacitors\": [ { \"id\": \"C\", \"bus\": 3, \"kvar\": [100, 0, 50] } ], " +
            "\"loads\": [ { \"id\": \"Z\", \"bus\": 3, \"connection\": \"wye\", \"model\": \"constant-impedance\", \"kw\": [10, 0, 20], \"kvar\": [5, 0, 10] } ]");
        Assert.Contains(from, json, StringComparison.Ordinal);
        string msg = Refused(json.Replace(from, to, StringComparison.Ordinal));
        Assert.Contains(path + ":", msg, StringComparison.Ordinal);
    }

    [Fact]
    public void A_regulator_tap_on_a_phase_it_does_not_have_and_a_reversed_regulator_are_refused()
    {
        string onePhase = Feeder(Buses3 + ", " +
            "\"regulators\": [ { \"id\": \"R\", \"from\": 1, \"to\": 2, \"phases\": \"a\", \"tapStepPercent\": 0.625, \"taps\": [1, 2, 0] } ], " +
            "\"switches\": [ { \"id\": \"S\", \"from\": 2, \"to\": 3, \"phases\": \"a\", \"closed\": true } ]");
        Assert.Contains("$.regulators[0].taps[1]:", Refused(onePhase), StringComparison.Ordinal);

        string reversed = Feeder(Buses3 + ", " +
            "\"switches\": [ { \"id\": \"S\", \"from\": 1, \"to\": 3, \"closed\": true } ], " +
            "\"regulators\": [ { \"id\": \"R\", \"from\": 2, \"to\": 3, \"tapStepPercent\": 0.625, \"taps\": [1, 1, 1] } ]");
        Assert.Contains("faces the source", Refused(reversed), StringComparison.Ordinal);

        string widening = Feeder(Buses3 + ", " +
            "\"switches\": [ { \"id\": \"S1\", \"from\": 1, \"to\": 2, \"phases\": \"a\", \"closed\": true }, { \"id\": \"S2\", \"from\": 2, \"to\": 3, \"closed\": true } ]");
        Assert.Contains("has only a", Refused(widening), StringComparison.Ordinal);
    }
}
