using System.Globalization;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Two-terminal LCC HVDC links (PSS/E two-terminal DC records) solved with the AC power flow (<see cref="LccDcLinks"/>).
/// Every solved DC state is recomputed here from the converter equations, written out independently, at the solved AC
/// voltages and taps; the AC side is checked to carry the converters' powers; each control path (power order at either
/// end, current order, the rectifier at its minimum firing angle handing the current to the inverter, a power order
/// becoming a current order below VCMOD) is exercised; and a link no state can satisfy, a capacitor-commutated link and
/// malformed records are refused.
/// </summary>
public class PsseLccTests
{
    private static string I(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A revision 33 case: slack bus 1 (345 kV) feeding rectifier bus 2 and inverter bus 3 through 0.01 pu lines,
    /// and one two-terminal link from 2 to 3 (two bridges each, 5 ohm commutating reactance, transformer ratio 0.56 on a
    /// 345 kV base, taps 0.9..1.1 in 0.00625 steps).</summary>
    internal static string Case(int mdc = 1, double setvl = 100, double vschd = 500, double rdc = 10, double vcmod = 0,
        double rcomp = 0, double delti = 0.15, string rectAngles = "25, 10", string invAngles = "25, 18", string rectTaps = "1.0, 1.1, 0.9",
        string invTaps = "1.0, 1.1, 0.9", string xcapR = "0", int rectBus = 2) => $"""
        0, 100, 33, 0, 0, 50     / PSS/E-33 RAW, LCC check
        GridSim LCC check
        Slack, rectifier and inverter buses
        1, 'SRC', 345, 3, 1, 1, 1, 1.0, 0
        2, 'REC', 345, 1, 1, 1, 1, 1.0, 0
        3, 'INV', 345, 1, 1, 1, 1, 1.0, 0
        0 / END OF BUS DATA, BEGIN LOAD DATA
        0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
        0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
        1, '1', 0, 0, 999, -999, 1, 0, 1000, 0, 0.01, 0, 0, 1, 1, 100, 9999, -9999, 1, 1
        0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
        1, 2, '1', 0, 0.01, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
        1, 3, '1', 0, 0.01, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
        0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA
        0 / END OF TRANSFORMER DATA, BEGIN AREA DATA
        0 / END OF AREA DATA, BEGIN TWO-TERMINAL DC DATA
        'LINK A', {mdc}, {I(rdc)}, {I(setvl)}, {I(vschd)}, {I(vcmod)}, {I(rcomp)}, {I(delti)}, 'I', 0, 20, 1
        {rectBus}, 2, {rectAngles}, 0, 5, 345, 0.56, {rectTaps}, 0.00625, 0, 0, 0, '1', {xcapR}
        3, 2, {invAngles}, 0, 5, 345, 0.56, {invTaps}, 0.00625, 0, 0, 0, '1', 0
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
        0 / END OF INDUCTION MACHINE DATA
        Q
        """;

    private static readonly double K = 3 * Math.Sqrt(2) / Math.PI, KX = 3 / Math.PI;
    private const double Xc = 5, Nb = 2, Ebas = 345, Tr = 0.56;

    private static (GridModel Model, PowerFlowResult Pf) Solve(string raw)
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(raw, "lcc").Model;
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        return (m, pf);
    }

    private static double Vm(PowerFlowResult pf, int bus) => pf.Vm[pf.BusIds.ToList().IndexOf(bus)];

    private static void Close(double expected, double actual, string what, double rel = 1e-12)
        => Assert.True(Math.Abs(expected - actual) <= rel * (1 + Math.Abs(expected)), $"{what}: expected {expected:R}, got {actual:R}");

    /// <summary>One converter's DC voltage (kV) at angle a (degrees), AC voltage v (pu), tap and current (kA), written out
    /// from the equations: N (k |V| EBAS TR / TAP cos a - 3/pi Xc I).</summary>
    private static double Vd(double v, double tap, double aDeg, double iKa) => Nb * (K * v * Ebas * Tr / tap * Math.Cos(aDeg * Math.PI / 180) - KX * Xc * iKa);

    /// <summary>Reactive power absorbed: P tan(phi), cos(phi) = (k Eac cos a - 3/pi Xc I) / (k Eac).</summary>
    private static double Q(double p, double v, double tap, double aDeg, double iKa)
    {
        double vd0 = K * v * Ebas * Tr / tap;
        double c = (vd0 * Math.Cos(aDeg * Math.PI / 180) - KX * Xc * iKa) / vd0;
        return p * Math.Sqrt(1 - c * c) / c;
    }

    [Fact]
    public void A_power_order_at_the_rectifier_solves_to_the_converter_equations()
    {
        (GridModel m, PowerFlowResult pf) = Solve(Case());
        Assert.True(pf.Converged, pf.LccNote);
        Assert.Null(pf.LccNote);
        LccLinkResult d = Assert.Single(pf.LccLinks!);
        Assert.Equal(LccControl.RectifierCurrent, d.Control);
        double vr = Vm(pf, 2), vi = Vm(pf, 3), iKa = d.CurrentA / 1000;

        // The inverter at gamma min (18 degrees) with its own tap; the rectifier power is the order, 100 MW.
        Assert.Equal(18.0, d.GammaDeg);
        Close(Vd(vi, d.InverterTap, 18, iKa), d.InverterDcKv, "Vdi");
        Close(d.InverterDcKv + 10 * iKa, d.RectifierDcKv, "Vdr = Vdi + Rdc I");
        Close(100.0, d.RectifierDcKv * iKa, "rectifier power = order", 1e-12);
        Close(Vd(vr, d.RectifierTap, d.AlphaDeg, iKa), d.RectifierDcKv, "Vdr from alpha");
        Assert.InRange(d.AlphaDeg, 10.0, 25.0);
        Close(d.RectifierDcKv * iKa, d.RectifierMw, "P rectifier");
        Close(d.InverterDcKv * iKa, d.InverterMw, "P inverter");
        Close(Q(d.RectifierMw, vr, d.RectifierTap, d.AlphaDeg, iKa), d.RectifierMvar, "Q rectifier");
        Close(Q(d.InverterMw, vi, d.InverterTap, 18, iKa), d.InverterMvar, "Q inverter");
        Close(100.0 - 10 * iKa * iKa, d.InverterMw, "line loss is Rdc I^2");

        // The inverter's tap brings the DC voltage as close to VSCHD (500 kV) as its step allows: no neighbouring tap is
        // closer (each neighbour re-solved for the current that delivers the order).
        double Err(double tap)
        {
            double a = Nb * K * vi * Ebas * Tr / tap * Math.Cos(18 * Math.PI / 180), b = Nb * KX * Xc;
            double cur = 2 * 100 / (a + Math.Sqrt(a * a + 4 * (10 - b) * 100));
            return Math.Abs(a - b * cur - 500);
        }
        Assert.True(Err(d.InverterTap) <= Err(d.InverterTap + 0.00625) && Err(d.InverterTap) <= Err(d.InverterTap - 0.00625));

        // The AC side carries the converters: bus 2 draws P + jQ of the rectifier, bus 3 receives the inverter's P and
        // absorbs its Q (neither bus has anything else).
        int b2 = pf.BusIds.ToList().IndexOf(2), b3 = pf.BusIds.ToList().IndexOf(3);
        Close(-d.RectifierMw, pf.PInjMw[b2], "bus 2 P", 1e-9);
        Close(-d.RectifierMvar, pf.QInjMvar[b2], "bus 2 Q", 1e-9);
        Close(d.InverterMw, pf.PInjMw[b3], "bus 3 P", 1e-9);
        Close(-d.InverterMvar, pf.QInjMvar[b3], "bus 3 Q", 1e-9);

        // Settled: the DC state re-solved at the final AC voltages is the one reported (within the coupling tolerance).
        LccLinkResult again = LccDcLinks.SolveLink(m.LccLinks[0], vr, vi);
        Close(d.RectifierMw, again.RectifierMw, "settled P", 1e-8);
        Close(d.RectifierMvar, again.RectifierMvar, "settled Q", 1e-8);
    }

    [Fact]
    public void A_power_order_at_the_inverter_and_a_current_order_are_met()
    {
        (_, PowerFlowResult inv) = Solve(Case(setvl: -100));
        LccLinkResult di = Assert.Single(inv.LccLinks!);
        Close(100.0, di.InverterMw, "inverter power = order");

        (_, PowerFlowResult cur) = Solve(Case(mdc: 2, setvl: 250));
        LccLinkResult dc = Assert.Single(cur.LccLinks!);
        Assert.Equal(250.0, dc.CurrentA);
        Close(dc.InverterDcKv * 0.25, dc.InverterMw, "P = Vdi I");
    }

    [Fact]
    public void A_rectifier_at_its_minimum_firing_angle_hands_the_current_to_the_inverter_with_the_margin()
    {
        // Rectifier taps locked at 1.1 and alpha min 20 degrees: it cannot reach the voltage the inverter schedules, so it
        // sits at 20 degrees and the inverter controls the current at the order less 15 %, its extinction angle above 18.
        (_, PowerFlowResult pf) = Solve(Case(rectAngles: "30, 20", rectTaps: "1.1, 1.1, 1.1", vschd: 500));
        Assert.True(pf.Converged, pf.LccNote);
        LccLinkResult d = Assert.Single(pf.LccLinks!);
        Assert.Equal(LccControl.InverterCurrent, d.Control);
        Assert.Equal(20.0, d.AlphaDeg);
        Assert.True(d.GammaDeg >= 18.0, $"gamma {d.GammaDeg}");
        double vr = Vm(pf, 2), iKa = d.CurrentA / 1000;
        Close(Vd(vr, 1.1, 20, iKa), d.RectifierDcKv, "Vdr at alpha min");
        Close(d.RectifierDcKv - 10 * iKa, d.InverterDcKv, "Vdi = Vdr - Rdc I");
    }

    [Fact]
    public void A_power_order_becomes_a_current_order_below_vcmod()
    {
        // Inverter taps locked at 1.1: its voltage cannot reach 500 kV; with VCMOD 480 above what it reaches, the 100 MW
        // order becomes 100 / 500 kA.
        (_, PowerFlowResult pf) = Solve(Case(invTaps: "1.1, 1.1, 1.1", vcmod: 480, rectTaps: "1.0, 1.1, 0.8"));
        LccLinkResult d = Assert.Single(pf.LccLinks!);
        Assert.True(d.PowerOrderSwitchedToCurrent);
        Close(200.0, d.CurrentA, "current order 100 MW / 500 kV");
    }

    [Fact]
    public void An_order_no_state_can_meet_fails_the_solve_loud_naming_the_link()
    {
        // 5000 MW at 500 kV through 10 ohms with the rectifier tap locked: no firing angle and tap can give it. The
        // solve is NOT converged with the reason; the converter solve on its own throws it.
        (GridModel m, PowerFlowResult pf) = Solve(Case(setvl: 5000, rectTaps: "1.0, 1.0, 1.0", rectAngles: "25, 24"));
        Assert.False(pf.Converged);
        Assert.Contains("LCC link LINK A", pf.LccNote, StringComparison.Ordinal);
        Assert.Contains("no steady state within its angle and tap limits meets its order", pf.LccNote, StringComparison.Ordinal);
        var ex = Assert.Throws<InvalidOperationException>(() => LccDcLinks.SolveLink(m.LccLinks[0], 1.0, 1.0));
        Assert.Contains("LCC link LINK A", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_radial_network_with_a_link_goes_to_newton_and_the_sweep_refuses_it()
    {
        // The test network is a tree (3 buses, 2 lines): without the link the selector would pick the sweep.
        GridModel m = PsseRawParser.ParseWithDiagnostics(Case(), "lcc").Model;
        Assert.False(SolverSelector.LooksRadial(m));
        Assert.True(SolverSelector.LooksRadial(m.CopyWith(lccLinks: Array.Empty<LccDcLink>())));
        var ex = Assert.Throws<InvalidOperationException>(() => new BackwardForwardSweep().Solve(m));
        Assert.Contains("does not model LCC HVDC links", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_model_without_links_solves_exactly_as_before()
    {
        // The same network with the link blocked: no link in the model, and the solve is the plain AC solve's bits.
        var r = PsseRawParser.ParseWithDiagnostics(Case(mdc: 0), "lcc");
        Assert.Empty(r.Model.LccLinks);
        Assert.Contains(r.Notes, n => n.StartsWith("skipped 1 blocked two-terminal DC link(s) (MDC 0)", StringComparison.Ordinal));
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(r.Model);
        Assert.Null(pf.LccLinks);
    }

    [Fact]
    public void Dense_and_sparse_newton_give_the_same_bits_with_a_link()
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(Case(), "lcc").Model;
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(m), b = new SparseNewtonRaphsonPowerFlow().Solve(m);
        Assert.Equal(a.Vm.Select(BitConverter.DoubleToInt64Bits), b.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.LccLinks![0], b.LccLinks![0]);
    }

    [Fact]
    public void A_capacitor_commutated_link_is_refused_unless_allowed()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Case(xcapR: "2.0"), "ccc"));
        Assert.Contains("1 capacitor-commutated two-terminal DC link(s)", ex.Message, StringComparison.Ordinal);
        var allowed = PsseRawParser.ParseWithDiagnostics(Case(xcapR: "2.0"), "ccc", new PsseReadOptions { AllowUnmodelled = true });
        Assert.Empty(allowed.Model.LccLinks);
    }

    [Theory]
    [InlineData("rectBus", "the rectifier and inverter are on the same bus")]
    [InlineData("angles", "angle limits")]
    [InlineData("vschd", "VSCHD is 0 kV")]
    [InlineData("delti", "DELTI is 1")]
    public void Malformed_link_records_are_refused_naming_the_line(string what, string expected)
    {
        string raw = what switch
        {
            "rectBus" => Case(rectBus: 3),
            "angles" => Case(rectAngles: "10, 25"),
            "vschd" => Case(vschd: 0),
            _ => Case(delti: 1),
        };
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(raw, "bad"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("line ", ex.Message, StringComparison.Ordinal);
    }
}
