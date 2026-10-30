using System.Globalization;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Two-terminal VSC HVDC links (PSS/E VSC DC records) solved with the AC power flow (<see cref="VscDcLinks"/>,
/// <see cref="HvdcCoupling"/>): the DC current and losses against the closed form written out here, the AC buses carrying
/// the converters (a power factor converter's fixed reactive power, a voltage controlling converter holding its bus or
/// releasing it at a reactive limit), the MVA rating with its power weighting, power sent either way, the minimum loss,
/// a cross-check of the AC side against pandapower, and the refusals.
/// </summary>
public class PsseVscTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public PsseVscTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static string I(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A revision 33 case: slack bus 1 (345 kV) feeding buses 2 and 3 through 0.01 pu lines; a VSC link from
    /// converter 1 at bus 2 (TYPE 2, a power order <paramref name="order"/> MW drawn from bus 2) to converter 2 at bus 3
    /// (TYPE 1, <paramref name="vdc"/> kV), DC resistance <paramref name="rdc"/> ohms.</summary>
    internal static string Case(double order = 200, double vdc = 300, double rdc = 2, string loss1 = "500, 2, 100", string loss2 = "500, 2, 100",
        int mode1 = 2, double acset1 = 0.95, int mode2 = 1, double acset2 = 1.02, string q2 = "300, -300", string smax1 = "0, 0", double pwf1 = 1.0,
        int type1 = 2, int type2 = 1, int remot2 = 0, int mdc = 1) => $"""
        0, 100, 33, 0, 0, 50     / PSS/E-33 RAW, VSC check
        GridSim VSC check
        Slack and two converter buses
        1, 'SRC', 345, 3, 1, 1, 1, 1.0, 0
        2, 'CONV1', 345, 1, 1, 1, 1, 1.0, 0
        3, 'CONV2', 345, 1, 1, 1, 1, 1.0, 0
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
        0 / END OF TWO-TERMINAL DC DATA, BEGIN VSC DC DATA
        'VSC A', {mdc}, {I(rdc)}, 1, 1
        2, {type1}, {mode1}, {I(type1 == 1 ? vdc : order)}, {I(acset1)}, {loss1}, {smax1}, {I(pwf1)}, 300, -300, 0, 100
        3, {type2}, {mode2}, {I(type2 == 1 ? vdc : order)}, {I(acset2)}, {loss2}, 0, 0, 1, {q2}, {remot2}, 100
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

    private static (GridModel Model, PowerFlowResult Pf) Solve(string raw)
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(raw, "vsc").Model;
        return (m, new NewtonRaphsonPowerFlow().Solve(m));
    }

    private static int Ix(PowerFlowResult pf, int bus) => pf.BusIds.ToList().IndexOf(bus);

    private static void Close(double expected, double actual, string what, double rel = 1e-12)
        => Assert.True(Math.Abs(expected - actual) <= rel * (1 + Math.Abs(expected)), $"{what}: expected {expected:R}, got {actual:R}");

    [Fact]
    public void The_dc_current_and_losses_follow_the_closed_form()
    {
        // 200 MW drawn at bus 2; 300 kV held at bus 3; R 2 ohms; losses 500 kW + 2 kW/A each (minimum 100 kW).
        // P = (V + R I) I + A + B I  =>  R I^2 + (V + B) I + (A - P) = 0 with I in kA, A in MW, B in MW/kA.
        double r = 2, v = 300, a = 0.5, b = 2, p = 200;
        double iKa = (-(v + b) + Math.Sqrt((v + b) * (v + b) - 4 * r * (a - p))) / (2 * r);
        (GridModel m, PowerFlowResult pf) = Solve(Case());
        Assert.True(pf.Converged, pf.VscNote);
        VscLinkResult d = Assert.Single(pf.VscLinks!);
        Close(iKa * 1000, d.CurrentA, "current", 1e-12);
        Close(-200.0, d.First.Mw, "converter 1 draws its order");
        Close(a + b * iKa, d.First.LossMw, "converter 1 loss");
        Close(v * iKa - (a + b * iKa), d.Second.Mw, "converter 2 delivers V I less its loss");
        Close(v + r * iKa, d.First.DcKv, "DC voltage at converter 1");
        Assert.Equal(300.0, d.Second.DcKv);
        // Power balance: what goes in comes out, less the two converter losses and R I^2.
        Close(200.0 - d.First.LossMw - d.Second.LossMw - r * iKa * iKa, d.Second.Mw, "balance", 1e-12);
    }

    [Fact]
    public void The_ac_buses_carry_the_converters()
    {
        (_, PowerFlowResult pf) = Solve(Case());
        VscLinkResult d = Assert.Single(pf.VscLinks!);
        // Converter 1 (MODE 2, pf 0.95): P -200 MW, Q = P sqrt(1 - pf^2) / pf, injected at bus 2 (no other device).
        double q1 = -200 * Math.Sqrt(1 - 0.95 * 0.95) / 0.95;
        Close(q1, d.First.Mvar, "converter 1 Q");
        Close(-200.0, pf.PInjMw[Ix(pf, 2)], "bus 2 P", 1e-9);
        Close(q1, pf.QInjMvar[Ix(pf, 2)], "bus 2 Q", 1e-9);
        // Converter 2 (MODE 1) holds bus 3 at 1.02 pu, injecting its power; its Q is the bus's.
        Assert.Equal(1.02, pf.Vm[Ix(pf, 3)]);
        Close(d.Second.Mw, pf.PInjMw[Ix(pf, 3)], "bus 3 P", 1e-9);
        Close(d.Second.Mvar, pf.QInjMvar[Ix(pf, 3)], "bus 3 Q", 1e-12);
        Assert.InRange(d.Second.Mvar, -300.0, 300.0);
    }

    [Fact]
    public void A_voltage_controlling_converter_at_its_reactive_limit_releases_its_bus()
    {
        // MAXQ 5 Mvar: holding 1.02 pu needs more, so the converter sits at 5 Mvar and bus 3 falls below 1.02.
        (_, PowerFlowResult pf) = Solve(Case(q2: "5, -300"));
        Assert.True(pf.Converged, pf.VscNote);
        VscLinkResult d = Assert.Single(pf.VscLinks!);
        Close(5.0, d.Second.Mvar, "Q at MAXQ", 1e-9);
        Assert.True(pf.Vm[Ix(pf, 3)] < 1.02, $"bus 3 at {pf.Vm[Ix(pf, 3)]:R}");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.0)]
    [InlineData(0.5)]
    public void An_exceeded_mva_rating_is_met_by_the_power_weighting(double pwf)
    {
        // Converter 1 (200 MW at pf 0.95: 210.5 MVA) rated 180 MVA. PWF 1 would cut only Q, but at a fixed power factor Q
        // follows P, so P and Q fall together; the result sits on the rating.
        (_, PowerFlowResult pf) = Solve(Case(smax1: "180, 0", pwf1: pwf));
        Assert.True(pf.Converged, pf.VscNote);
        VscConverterResult c = Assert.Single(pf.VscLinks!).First;
        Assert.True(c.Limited);
        Close(180.0, Math.Sqrt(c.Mw * c.Mw + c.Mvar * c.Mvar), "S on the rating", 1e-9);
        Close(-180.0 * 0.95, c.Mw, "P at pf 0.95 on the rating", 1e-9);
        Close(c.OrderMw, -c.Mw, "the reduced order is what is drawn");
    }

    [Fact]
    public void The_weighting_splits_the_reduction_between_p_and_q()
    {
        // The reduction itself: P' = P (1 - (1 - w) k), Q' = Q (1 - w k) on the circle.
        (double p, double q) = VscDcLinks.Reduce(300, 200, 250, 0.5)!.Value;
        Close(250 * 250, p * p + q * q, "on the circle");
        double k = (1 - p / 300) / 0.5;
        Close(1 - 0.5 * k, q / 200, "Q fraction from the same k");
        Assert.Equal((300.0, 0.0), VscDcLinks.Reduce(300, 400, 300, 1.0)!.Value);   // PWF 1: Q alone
        Assert.Equal((250.0, 0.0), VscDcLinks.Reduce(250, 0, 250, 0.3)!.Value);     // already on the circle
        Assert.Null(VscDcLinks.Reduce(400, 0, 300, 1.0));                             // Q alone cannot fix it
    }

    [Fact]
    public void Power_sent_to_the_ac_side_reverses_the_current_and_losses_stay_positive()
    {
        // Order -150 MW: converter 1 sends 150 MW into bus 2; the current runs from converter 2.
        // With J = -I: R J^2 - (V - B) J + (A - P) = 0, P = -150.
        double r = 2, v = 300, a = 0.5, b = 2, p = -150;
        double j = ((v - b) - Math.Sqrt((v - b) * (v - b) - 4 * r * (a - p))) / (2 * r);
        (_, PowerFlowResult pf) = Solve(Case(order: -150));
        VscLinkResult d = Assert.Single(pf.VscLinks!);
        Close(-j * 1000, d.CurrentA, "reversed current", 1e-12);
        Close(150.0, d.First.Mw, "converter 1 injects 150 MW");
        Assert.True(d.First.LossMw > 0 && d.Second.LossMw > 0);
        Close(-(v * j) - (a + b * j), d.Second.Mw, "converter 2 draws V J plus its loss");
    }

    [Fact]
    public void The_minimum_loss_applies_when_the_linear_loss_is_below_it()
    {
        // ALOSS 50 kW, BLOSS 0.01 kW/A, MINLOSS 500 kW: at ~0.67 kA the linear loss is 56.7 kW, so 500 kW applies.
        (_, PowerFlowResult pf) = Solve(Case(loss1: "50, 0.01, 500", loss2: "50, 0.01, 500"));
        VscLinkResult d = Assert.Single(pf.VscLinks!);
        Assert.Equal(0.5, d.First.LossMw);
        Assert.Equal(0.5, d.Second.LossMw);
        double iKa = (-300 + Math.Sqrt(300.0 * 300 - 4 * 2 * (0.5 - 200))) / (2 * 2);
        Close(iKa * 1000, d.CurrentA, "current with the constant minimum loss", 1e-12);
    }

    [Fact]
    public void The_ac_side_matches_pandapower_with_the_link_as_a_dc_line_of_the_same_loss()
    {
        // Cross-check (pandapower 3.5.6, scipy 1.18.1, numpy 2.4.6, pandas 2.3.3, in an isolated virtual environment):
        // both converters voltage-controlling (bus 2 at 1.0 pu, bus 3 at 1.02 pu), so the link is pandapower's dcline from
        // bus 2 to bus 3 with p_mw 200 and loss_mw equal to this link's total loss, vm_from 1.0, vm_to 1.02, the lines as
        // 1 km of 11.9025 ohm (0.01 pu on 100 MVA, 345 kV), tolerance_mva 1e-10. pandapower's bus angles and the
        // converters' reactive power (its res_dcline q_from / q_to are consumption, so the injections are their negatives)
        // are pinned below.
        (_, PowerFlowResult pf) = Solve(Case(mode1: 1, acset1: 1.0));
        Assert.True(pf.Converged, pf.VscNote);
        VscLinkResult d = Assert.Single(pf.VscLinks!);
        double totalLoss = 200 - d.Second.Mw;   // in at bus 2 less out at bus 3
        Close(PandapowerTotalLossMw, totalLoss, "the loss handed to pandapower", 1e-12);
        Close(PandapowerVaDeg2, pf.VaDeg[Ix(pf, 2)], "bus 2 angle", 1e-7);
        Close(PandapowerVaDeg3, pf.VaDeg[Ix(pf, 3)], "bus 3 angle", 1e-7);
        Close(PandapowerQ2, d.First.Mvar, "converter 1 Q", 1e-6);
        Close(PandapowerQ3, d.Second.Mvar, "converter 2 Q", 1e-6);
        _out.WriteLine($"deltas vs pandapower: loss {totalLoss - PandapowerTotalLossMw:R} MW, Va2 {pf.VaDeg[Ix(pf, 2)] - PandapowerVaDeg2:R} deg, " +
            $"Va3 {pf.VaDeg[Ix(pf, 3)] - PandapowerVaDeg3:R} deg, Q2 {d.First.Mvar - PandapowerQ2:R} Mvar, Q3 {d.Second.Mvar - PandapowerQ3:R} Mvar");
    }

    // From the pandapower run (see the test above).
    private const double PandapowerTotalLossMw = 4.496144525552211, PandapowerVaDeg2 = -1.1459919983885922, PandapowerVaDeg3 = 1.0982580169675034,
        PandapowerQ2 = 2.0002000400116913, PandapowerQ3 = 205.87378767576666;

    [Fact]
    public void Dense_and_sparse_newton_give_the_same_bits_with_a_link()
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(Case(), "vsc").Model;
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(m), b = new SparseNewtonRaphsonPowerFlow().Solve(m);
        Assert.Equal(a.Vm.Select(BitConverter.DoubleToInt64Bits), b.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.VscLinks![0], b.VscLinks![0]);
    }

    [Fact]
    public void A_model_with_a_link_goes_to_newton_and_the_writers_and_sweep_refuse_it()
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(Case(), "vsc").Model;
        Assert.False(SolverSelector.LooksRadial(m));
        Assert.Contains("does not model VSC HVDC links", Assert.Throws<InvalidOperationException>(() => new BackwardForwardSweep().Solve(m)).Message);
        Assert.Contains("1 VSC HVDC link(s) (VSC A)", Assert.Throws<InvalidDataException>(() => GridSim.Core.IO.Matpower.MatpowerWriter.Write(m)).Message);
        // Test changed 2026-10-25 (export pack): the PSS/E RAW writer now writes this device (it used to refuse it), so the
        // refusal check became a check that the written file reads back to the same device records (strengthened).
        Assert.Equal(m.VscLinks, PsseRawParser.ParseWithDiagnostics(PsseRawWriter.Write(m).Text, "vsc").Model.VscLinks);
        GridModel lcc = PsseRawParser.ParseWithDiagnostics(PsseLccTests.Case(), "lcc").Model;
        Assert.Equal(lcc.LccLinks, PsseRawParser.ParseWithDiagnostics(PsseRawWriter.Write(lcc).Text, "lcc").Model.LccLinks);
    }

    [Theory]
    [InlineData("types", "a link needs one DC voltage converter (TYPE 1) and one power converter (TYPE 2)")]
    [InlineData("pf", "a power factor must be in -1..1 and not 0")]
    [InlineData("pwf", "the power weighting fraction must be in 0..1")]
    [InlineData("q", "MAXQ -300 is below MINQ 300")]
    public void Malformed_vsc_records_are_refused_naming_the_line(string what, string expected)
    {
        string raw = what switch
        {
            "types" => Case(type1: 1),
            "pf" => Case(acset1: 0),
            "pwf" => Case(pwf1: 1.5),
            _ => Case(q2: "-300, 300"),
        };
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(raw, "bad"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains("line ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_remote_regulating_converter_is_refused_unless_allowed_and_an_out_of_service_link_is_skipped()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Case(remot2: 1), "remote"));
        Assert.Contains("1 VSC converter regulating a remote bus(s)", ex.Message, StringComparison.Ordinal);
        Assert.Empty(PsseRawParser.ParseWithDiagnostics(Case(remot2: 1), "remote", new PsseReadOptions { AllowUnmodelled = true }).Model.VscLinks);
        var off = PsseRawParser.ParseWithDiagnostics(Case(mdc: 0), "off");
        Assert.Empty(off.Model.VscLinks);
        Assert.Contains(off.Notes, n => n.StartsWith("skipped 1 out-of-service VSC DC link(s)", StringComparison.Ordinal));
    }
}
