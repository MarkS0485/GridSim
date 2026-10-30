using System.Globalization;
using System.Numerics;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Induction machines (PSS/E induction machine records, custom design, single cage) solved with the AC power flow
/// (<see cref="InductionMachines"/>, <see cref="HvdcCoupling"/>). The oracle is the equivalent circuit written out here
/// with complex numbers: at the solved slip and voltage it must give the schedule and the powers the bus carries.
/// </summary>
public class PsseInductionTests
{
    private static string I(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A revision 33 case: slack bus 1 (33 kV, 1.0 pu), bus 2 (33 kV) through a line of R 0.01, X 0.05 pu, and the
    /// induction machine record <paramref name="machine"/> (line 26).</summary>
    internal static string Case(string? machine = null) => $"""
        0, 100, 33, 0, 0, 50     / PSS/E-33 RAW, induction machine check
        GridSim induction machine check
        Slack and a motor bus
        1, 'SRC', 33, 3, 1, 1, 1, 1.0, 0
        2, 'MOTOR', 33, 1, 1, 1, 1, 1.0, 0
        0 / END OF BUS DATA, BEGIN LOAD DATA
        0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
        0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
        1, '1', 0, 0, 999, -999, 1, 0, 100, 0, 0.01, 0, 0, 1, 1, 100, 999, -999, 1, 1
        0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
        1, 2, '1', 0.01, 0.05, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
        0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA
        0 / END OF TRANSFORMER DATA, BEGIN AREA DATA
        0 / END OF AREA DATA, BEGIN TWO-TERMINAL DC DATA
        0 / END OF TWO-TERMINAL DC DATA, BEGIN VSC DC DATA
        0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA
        0 / END OF IMPEDANCE CORRECTION DATA, BEGIN MULTI-TERMINAL DC DATA
        0 / END OF MULTI-TERMINAL DC DATA, BEGIN MULTI-SECTION LINE DATA
        0 / END OF MULTI-SECTION LINE DATA, BEGIN ZONE DATA
        0 / END OF ZONE DATA, BEGIN INTER-AREA TRANSFER DATA
        0 / END OF INTER-AREA TRANSFER DATA, BEGIN OWNER DATA
        0 / END OF OWNER DATA, BEGIN FACTS DEVICE DATA
        0 / END OF FACTS DEVICE DATA, BEGIN SWITCHED SHUNT DATA
        0 / END OF SWITCHED SHUNT DATA, BEGIN GNE DEVICE DATA
        0 / END OF GNE DEVICE DATA, BEGIN INDUCTION MACHINE DATA
        {machine ?? Motor()}
        0 / END OF INDUCTION MACHINE DATA
        Q
        """;

    /// <summary>An induction machine record: I, ID, STAT, SCODE, DCODE, AREA, ZONE, OWNER, TCODE, BCODE, MBASE, RATEKV,
    /// PCODE, PSET, H, A, B, D, E, RA, XA, XM, R1, X1, R2, X2, X3, E1, SE1, E2, SE2, IA1, IA2, XAMULT.</summary>
    internal static string Motor(double pset = 8, int pcode = 1, double mbase = 10, double ratekv = 0, int dcode = 0, int stat = 1,
        string circuit = "0.01, 0.1, 3.0, 0.02, 0.12", string cage2 = "0, 0, 0", double se1 = 0)
        => $"2, '1', {stat}, 1, {dcode}, 1, 1, 1, 1, 1, {I(mbase)}, {I(ratekv)}, {pcode}, {I(pset)}, 1, 1, 1, 1, 1, {circuit}, {cage2}, 1, {I(se1)}, 1.2, 0, 0, 0, 1";

    private static GridModel Read(string raw) => PsseRawParser.ParseWithDiagnostics(raw, "im").Model;

    private static int Ix(PowerFlowResult pf, int bus) => pf.BusIds.ToList().IndexOf(bus);

    private static void Close(double expected, double actual, string what, double rel = 1e-12)
        => Assert.True(Math.Abs(expected - actual) <= rel * (1 + Math.Abs(expected)), $"{what}: expected {expected:R}, got {actual:R}");

    /// <summary>The equivalent circuit by hand: S drawn (pu on the machine base) and the mechanical power at slip s.</summary>
    private static (Complex S, double Mech) Circuit(double v, double s, double ra = 0.01, double xa = 0.1, double xm = 3.0, double r1 = 0.02, double x1 = 0.12)
    {
        Complex zm = new(0, xm), zr = new(r1 / s, x1);
        Complex zp = zm * zr / (zm + zr);
        Complex zin = new Complex(ra, xa) + zp;
        Complex cur = v / zin;
        Complex sIn = v * Complex.Conjugate(cur);
        double airGap = cur.Magnitude * cur.Magnitude * zp.Real;
        return (sIn, airGap * (1 - s));
    }

    [Fact]
    public void A_motor_runs_at_the_slip_that_gives_its_shaft_power_and_its_bus_carries_what_it_draws()
    {
        GridModel m = Read(Case());
        InductionMachine x = Assert.Single(m.InductionMachines);
        Assert.Equal(new InductionMachine(2, "induction machine 2 1", InductionPowerCode.Mechanical, 8, 10, 0, 0.01, 0.1, 3.0, 0.02, 0.12), x);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged, pf.InductionNote);
        InductionMachineResult r = Assert.Single(pf.InductionMachines!);
        double v = pf.Vm[Ix(pf, 2)];
        Assert.InRange(r.Slip, 1e-4, 0.1);
        (Complex s, double mech) = Circuit(v, r.Slip);
        Close(0.8, mech, "shaft power from the circuit at the solved slip and voltage");
        Close(8.0, r.MechanicalMw, "reported shaft power");
        Close(s.Real * 10, r.Mw, "P drawn", 1e-12);
        Close(s.Imaginary * 10, r.Mvar, "Q drawn", 1e-12);
        Assert.True(r.Mw > 8 && r.Mvar > 0, "a motor draws more than its shaft power and absorbs reactive power");
        // The bus's computed injection meets the machine's draw to the Newton mismatch tolerance (1e-8 pu = 1e-6 MW on the
        // 100 MVA base).
        double tolMva = new PowerFlowOptions().Tolerance * 100;
        Assert.True(Math.Abs(-r.Mw - pf.PInjMw[Ix(pf, 2)]) <= tolMva, $"bus 2 P {pf.PInjMw[Ix(pf, 2)]:R} vs {-r.Mw:R}");
        Assert.True(Math.Abs(-r.Mvar - pf.QInjMvar[Ix(pf, 2)]) <= tolMva, $"bus 2 Q {pf.QInjMvar[Ix(pf, 2)]:R} vs {-r.Mvar:R}");
    }

    [Fact]
    public void A_generator_runs_below_synchronous_speed_negative_slip_and_an_electrical_schedule_is_met_at_the_terminals()
    {
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(Read(Case(Motor(pset: -6))));
        Assert.True(pf.Converged, pf.InductionNote);
        InductionMachineResult g = Assert.Single(pf.InductionMachines!);
        Assert.True(g.Slip < 0, $"slip {g.Slip:R}");
        Close(-6.0, g.MechanicalMw, "shaft power in");
        Assert.True(g.Mw < 0 && g.Mw > -6, "it sends less than its shaft power to the bus");
        Assert.True(g.Mvar > 0, "an induction generator absorbs reactive power");
        (Complex s, _) = Circuit(pf.Vm[Ix(pf, 2)], g.Slip);
        Close(s.Real * 10, g.Mw, "P from the circuit");

        PowerFlowResult el = new NewtonRaphsonPowerFlow().Solve(Read(Case(Motor(pset: 5, pcode: 2))));
        Assert.True(el.Converged, el.InductionNote);
        InductionMachineResult e = Assert.Single(el.InductionMachines!);
        Close(5.0, e.Mw, "electrical power in");
        Assert.True(e.MechanicalMw < 5);
    }

    [Fact]
    public void Rated_kv_other_than_the_bus_base_rescales_the_terminal_voltage()
    {
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(Read(Case(Motor(ratekv: 34.65))));
        Assert.True(pf.Converged, pf.InductionNote);
        InductionMachineResult r = Assert.Single(pf.InductionMachines!);
        double vMachine = pf.Vm[Ix(pf, 2)] * 33 / 34.65;
        (Complex s, double mech) = Circuit(vMachine, r.Slip);
        Close(0.8, mech, "shaft power at the machine's own voltage base");
        Close(s.Imaginary * 10, r.Mvar, "Q");
    }

    [Fact]
    public void A_schedule_beyond_the_peak_power_is_not_converged_naming_the_machine()
    {
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(Read(Case(Motor(pset: 60))));
        Assert.False(pf.Converged);
        Assert.Contains("induction machine induction machine 2 1 at bus 2", pf.InductionNote, StringComparison.Ordinal);
        Assert.Contains("the motor would stall", pf.InductionNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Dense_and_sparse_newton_give_the_same_bits_with_a_machine()
    {
        GridModel m = Read(Case());
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(m), b = new SparseNewtonRaphsonPowerFlow().Solve(m);
        Assert.Equal(a.Vm.Select(BitConverter.DoubleToInt64Bits), b.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.InductionMachines![0], b.InductionMachines![0]);
    }

    [Fact]
    public void A_model_with_a_machine_goes_to_newton_and_the_writers_and_sweep_refuse_it()
    {
        GridModel m = Read(Case());
        Assert.False(SolverSelector.LooksRadial(m));
        Assert.Contains("does not model induction machines", Assert.Throws<InvalidOperationException>(() => new BackwardForwardSweep().Solve(m)).Message);
        // Test changed 2026-10-25 (export pack): the PSS/E RAW writer now writes this device (it used to refuse it), so the
        // refusal check became a check that the written file reads back to the same device records (strengthened).
        Assert.Equal(m.InductionMachines, PsseRawParser.ParseWithDiagnostics(PsseRawWriter.Write(m).Text, "im").Model.InductionMachines);
    }

    [Theory]
    [InlineData("dcode", "1 induction machine with a standard design code (DCODE 1 to 5; its circuit comes from design tables)(s)")]
    [InlineData("cage", "1 induction machine with a double cage (R2, X2, X3)(s)")]
    [InlineData("sat", "1 induction machine with magnetic saturation (SE1, SE2)(s)")]
    public void Machines_the_model_cannot_represent_are_refused_unless_allowed(string what, string expected)
    {
        string machine = what switch
        {
            "dcode" => Motor(dcode: 2),
            "cage" => Motor(cage2: "0.03, 0.15, 0"),
            _ => Motor(se1: 0.05),
        };
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Case(machine), "im"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Empty(PsseRawParser.ParseWithDiagnostics(Case(machine), "im", new PsseReadOptions { AllowUnmodelled = true }).Model.InductionMachines);
    }

    [Fact]
    public void A_malformed_circuit_is_refused_with_its_line_and_an_out_of_service_machine_is_counted()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Case(Motor(circuit: "0.01, 0.1, 0, 0.02, 0.12")), "im"));
        Assert.Contains("line 26 (INDUCTION MACHINE data)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("XM, R1 above 0", ex.Message, StringComparison.Ordinal);
        PsseParseResult off = PsseRawParser.ParseWithDiagnostics(Case(Motor(stat: 0)), "im");
        Assert.Empty(off.Model.InductionMachines);
        Assert.Contains(off.Notes, n => n.StartsWith("1 out-of-service induction machine(s) read past", StringComparison.Ordinal));
    }
}
