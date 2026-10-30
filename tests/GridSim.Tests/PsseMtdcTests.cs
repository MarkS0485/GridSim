using System.Globalization;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Multi-terminal line-commutated DC links (PSS/E multi-terminal DC records) solved with the AC power flow
/// (<see cref="MtdcLinks"/>): the DC network (Kirchhoff's current law, Ohm's law on every DC line), the order-holding
/// converters' currents from their orders, the voltage converter at its minimum angle holding the voltage nearest its
/// schedule by tap, each converter's angle from the converter equation, the AC buses carrying the converters; a state no
/// angle and tap can reach, and the refusals.
/// </summary>
public class PsseMtdcTests
{
    private static string I(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A revision 33 case: slack bus 1 feeding buses 2, 3, 4 through 0.01 pu lines; a three-terminal link with
    /// rectifiers at buses 2 and 3 (orders <paramref name="o2"/>, <paramref name="o3"/>) on DC buses 1 and 2, and the voltage
    /// converter (an inverter) at bus 4 on DC bus 3 holding <paramref name="vsch"/> kV; DC lines 1-3 and 2-3 of 5 ohms.</summary>
    internal static string Case(int mdc = 1, double o2 = 100, double o3 = 50, double vsch = 400, string rectAngles = "35, 10", string vTaps = "1.0, 1.1, 0.9", string rectTaps = "1.0, 1.1, 0.9",
        int idc2 = 0, int vconv = 4, double rdc = 5, int dcBusOf3 = 3) => $"""
        0, 100, 33, 0, 0, 50     / PSS/E-33 RAW, MTDC check
        GridSim MTDC check
        Slack and three converter buses
        1, 'SRC', 345, 3, 1, 1, 1, 1.0, 0
        2, 'REC A', 345, 1, 1, 1, 1, 1.0, 0
        3, 'REC B', 345, 1, 1, 1, 1, 1.0, 0
        4, 'INV', 345, 1, 1, 1, 1, 1.0, 0
        0 / END OF BUS DATA, BEGIN LOAD DATA
        0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
        0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
        1, '1', 0, 0, 999, -999, 1, 0, 1000, 0, 0.01, 0, 0, 1, 1, 100, 9999, -9999, 1, 1
        0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
        1, 2, '1', 0, 0.01, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
        1, 3, '1', 0, 0.01, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
        1, 4, '1', 0, 0.01, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
        0 / END OF BRANCH DATA, BEGIN TRANSFORMER DATA
        0 / END OF TRANSFORMER DATA, BEGIN AREA DATA
        0 / END OF AREA DATA, BEGIN TWO-TERMINAL DC DATA
        0 / END OF TWO-TERMINAL DC DATA, BEGIN VSC DC DATA
        0 / END OF VSC DC DATA, BEGIN IMPEDANCE CORRECTION DATA
        0 / END OF IMPEDANCE CORRECTION DATA, BEGIN MULTI-TERMINAL DC DATA
        'MT1', 3, 3, 2, {mdc}, {vconv}, 0, 0
        2, 2, {rectAngles}, 0, 5, 345, 0.5, {rectTaps}, 0.00625, {I(o2)}, 1, 0, 1
        3, 2, {rectAngles}, 0, 5, 345, 0.5, {rectTaps}, 0.00625, {I(o3)}, 1, 0, 1
        4, 2, 25, 18, 0, 5, 345, 0.5, {vTaps}, 0.00625, {I(vsch)}, 1, 0, 1
        1, 2, 1, 1, 'D1', 0, 0, 1
        2, 3, 1, 1, 'D2', {idc2}, 0, 1
        {dcBusOf3}, 4, 1, 1, 'D3', 0, 0, 1
        1, 3, '1', 1, {I(rdc)}, 0
        2, 3, '1', 1, {I(rdc)}, 0
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

    private static (GridModel Model, PowerFlowResult Pf) Solve(string raw)
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(raw, "mtdc").Model;
        return (m, new NewtonRaphsonPowerFlow().Solve(m));
    }

    private static int Ix(PowerFlowResult pf, int bus) => pf.BusIds.ToList().IndexOf(bus);

    private static void Close(double expected, double actual, string what, double rel = 1e-12)
        => Assert.True(Math.Abs(expected - actual) <= rel * (1 + Math.Abs(expected)), $"{what}: expected {expected:R}, got {actual:R}");

    /// <summary>A converter's DC voltage from the equation: N (k |V| EBAS TR / TAP cos(angle) - 3/pi Xc I).</summary>
    private static double Vd(double v, double tap, double angDeg, double iKa) => 2 * (K * v * 345 * 0.5 / tap * Math.Cos(angDeg * Math.PI / 180) - KX * 5 * iKa);

    [Fact]
    public void The_dc_network_and_every_converter_follow_the_equations()
    {
        (_, PowerFlowResult pf) = Solve(Case());
        Assert.True(pf.Converged, pf.MtdcNote);
        MtdcLinkResult d = Assert.Single(pf.MtdcLinks!);
        MtdcConverterResult a = d.Converters[0], b = d.Converters[1], v = d.Converters[2];
        double v1 = d.DcBusKv[0], v2 = d.DcBusKv[1], v3 = d.DcBusKv[2];

        // Orders: rectifier currents are power over their DC bus voltage.
        Close(100.0 / v1 * 1000, a.CurrentA, "rectifier A current");
        Close(50.0 / v2 * 1000, b.CurrentA, "rectifier B current");
        // Ohm's law on each DC line and Kirchhoff's current law at DC bus 3 (the inverter takes what arrives).
        Close(v3 + 5 * a.CurrentA / 1000, v1, "line 1-3", 1e-12);
        Close(v3 + 5 * b.CurrentA / 1000, v2, "line 2-3", 1e-12);
        Close(-(a.CurrentA + b.CurrentA), v.CurrentA, "KCL at DC bus 3", 1e-12);
        Assert.True(a.Rectifier && b.Rectifier && !v.Rectifier);

        // The voltage converter at gamma min 18 degrees: its DC voltage is the converter equation at its tap, the tap
        // nearest the 400 kV schedule (no neighbour closer).
        double vi = pf.Vm[Ix(pf, 4)], iv = -v.CurrentA / 1000;
        Assert.Equal(18.0, v.AngleDeg);
        Close(Vd(vi, v.Tap, 18, iv), v3, "inverter DC voltage", 1e-12);
        double Err(double t) => Math.Abs(Vd(vi, t, 18, iv) - 400);
        Assert.True(Err(v.Tap) <= Err(v.Tap + 0.00625) && Err(v.Tap) <= Err(v.Tap - 0.00625));

        // Each rectifier's firing angle reproduces its DC bus voltage at its tap; within its limits.
        Close(v1, Vd(pf.Vm[Ix(pf, 2)], a.Tap, a.AngleDeg, a.CurrentA / 1000), "rectifier A voltage", 1e-12);
        Close(v2, Vd(pf.Vm[Ix(pf, 3)], b.Tap, b.AngleDeg, b.CurrentA / 1000), "rectifier B voltage", 1e-12);
        Assert.InRange(a.AngleDeg, 10.0, 35.0);

        // The AC buses carry the converters: rectifiers draw their orders, the inverter delivers its DC power.
        Close(-100.0, pf.PInjMw[Ix(pf, 2)], "bus 2 P", 1e-9);
        Close(-50.0, pf.PInjMw[Ix(pf, 3)], "bus 3 P", 1e-9);
        Close(-v.DrawnMw, pf.PInjMw[Ix(pf, 4)], "bus 4 P", 1e-9);
        Close(-a.AbsorbedMvar, pf.QInjMvar[Ix(pf, 2)], "bus 2 Q", 1e-9);
        // Power balance on the DC side: what the rectifiers send in less the line losses arrives at the inverter.
        double loss = 5 * Math.Pow(a.CurrentA / 1000, 2) + 5 * Math.Pow(b.CurrentA / 1000, 2);
        Close(150.0 - loss, -v.DrawnMw, "DC power balance", 1e-12);
    }

    [Fact]
    public void A_current_order_link_holds_its_currents()
    {
        (_, PowerFlowResult pf) = Solve(Case(mdc: 2, o2: 250, o3: 120));
        MtdcLinkResult d = Assert.Single(pf.MtdcLinks!);
        Assert.Equal(250.0, d.Converters[0].CurrentA);
        Assert.Equal(120.0, d.Converters[1].CurrentA);
        Close(-370.0, d.Converters[2].CurrentA, "KCL", 1e-12);
    }

    [Fact]
    public void A_state_no_angle_and_tap_can_reach_fails_the_solve_loud()
    {
        // The rectifiers' taps locked at 1.0 and their firing angles limited to 10..12 degrees: reaching about 401 kV
        // from a 466 kV no-load voltage needs about 30 degrees, so no angle and tap can give it.
        (_, PowerFlowResult pf) = Solve(Case(rectAngles: "12, 10", rectTaps: "1.0, 1.0, 1.0"));
        Assert.False(pf.Converged);
        Assert.Contains("multi-terminal DC link MT1", pf.MtdcNote, StringComparison.Ordinal);
        Assert.Contains("VCMOD, VCONVN, MARG and DCPF are not applied", pf.MtdcNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Dense_and_sparse_newton_give_the_same_bits_with_a_link()
    {
        GridModel m = PsseRawParser.ParseWithDiagnostics(Case(), "mtdc").Model;
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(m), b = new SparseNewtonRaphsonPowerFlow().Solve(m);
        Assert.Equal(a.Vm.Select(BitConverter.DoubleToInt64Bits), b.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.MtdcLinks![0].DcBusKv, b.MtdcLinks![0].DcBusKv);
    }

    [Fact]
    public void A_two_bus_converter_is_refused_unless_allowed()
    {
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Case(idc2: 1), "idc2"));
        Assert.Contains("multi-terminal DC converter connected to two DC buses (IDC2)", ex.Message, StringComparison.Ordinal);
        Assert.Empty(PsseRawParser.ParseWithDiagnostics(Case(idc2: 1), "idc2", new PsseReadOptions { AllowUnmodelled = true }).Model.MtdcLinks);
    }

    [Theory]
    [InlineData("vconv", "VCONV 9 is not one of the link's converters")]
    [InlineData("rdc", "a DC line needs a resistance above 0 ohms")]
    [InlineData("dcbus", "converter 4 is on 0 DC buses")]
    public void Malformed_multi_terminal_records_are_refused_naming_the_line(string what, string expected)
    {
        string raw = what switch
        {
            "vconv" => Case(vconv: 9),
            "rdc" => Case(rdc: 0),
            _ => Case(dcBusOf3: 3).Replace("3, 4, 1, 1, 'D3', 0, 0, 1", "3, 0, 1, 1, 'D3', 0, 0, 1"),
        };
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(raw, "bad"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pssE_30_sample_link_is_read_with_its_four_converters()
    {
        var r = PsseRawParser.ParseFile(Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "t_psse_case3.raw"),
            new PsseReadOptions { AllowUnmodelled = true });
        MtdcLink l = Assert.Single(r.Model.MtdcLinks);
        Assert.Equal(212, l.VoltageConverterBus);
        Assert.Equal(new[] { 402, 401, 212, 213 }, l.Converters.Select(c => c.Bus));
        Assert.Equal(new[] { 321.0, 321.0, 500.0, -303.8 }, l.Converters.Select(c => c.Setpoint));
        Assert.Equal(4, l.Lines.Count);
        Assert.All(l.Lines, x => Assert.Equal(29.0, x.ResistanceOhms));
    }
}
