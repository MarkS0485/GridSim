using System.Globalization;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// FACTS devices (PSS/E FACTS device records, modes 1 to 3) and static var compensators (PSS/E switched shunts with
/// continuous voltage control, MODSW 2, read with shunt control) solved with the AC power flow (<see cref="FactsDevices"/>,
/// <see cref="HvdcCoupling"/>). Every solved device quantity is recomputed here from the solved bus voltages by the
/// equations written out in each test; the AC side of a UPFC is cross-checked against pandapower; the refusals are
/// pinned. Also the fix for device powers at a bus with a voltage-dependent (ZIP) load.
/// </summary>
public class PsseFactsTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public PsseFactsTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static string I(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>A revision 33 case: slack bus 1 (345 kV, 1.0 pu) feeding bus 2 (line 1-2, X 0.02 pu) and bus 4 (line 1-4,
    /// X 0.04 pu); bus 3 joined to bus 4 (line 3-4, X 0.02 pu); a load of 300 MW + 100 Mvar at bus 4; and the FACTS
    /// record <paramref name="facts"/> (line 28), by default a UPFC from bus 2 to bus 3 delivering 150 MW + 20 Mvar to bus
    /// 3 with its shunt element holding bus 2 at 1.01 pu.</summary>
    internal static string Case(string? facts = null, string extraGen = "") => $"""
        0, 100, 33, 0, 0, 50     / PSS/E-33 RAW, FACTS check
        GridSim FACTS check
        Slack, a UPFC from bus 2 to bus 3, a load at bus 4
        1, 'SRC', 345, 3, 1, 1, 1, 1.0, 0
        2, 'SEND', 345, 1, 1, 1, 1, 1.0, 0
        3, 'TERM', 345, 1, 1, 1, 1, 1.0, 0
        4, 'LOAD', 345, 1, 1, 1, 1, 1.0, 0
        0 / END OF BUS DATA, BEGIN LOAD DATA
        4, '1', 1, 1, 1, 300, 100, 0, 0, 0, 0, 1, 1, 0
        0 / END OF LOAD DATA, BEGIN FIXED SHUNT DATA
        0 / END OF FIXED SHUNT DATA, BEGIN GENERATOR DATA
        1, '1', 0, 0, 9999, -9999, 1, 0, 1000, 0, 0.01, 0, 0, 1, 1, 100, 9999, -9999, 1, 1
        {extraGen}0 / END OF GENERATOR DATA, BEGIN BRANCH DATA
        1, 2, '1', 0, 0.02, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
        1, 4, '1', 0, 0.04, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
        3, 4, '1', 0, 0.02, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 0, 1, 1
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
        {facts ?? Upfc()}
        0 / END OF FACTS DEVICE DATA, BEGIN SWITCHED SHUNT DATA
        0 / END OF SWITCHED SHUNT DATA, BEGIN GNE DEVICE DATA
        0 / END OF GNE DEVICE DATA, BEGIN INDUCTION MACHINE DATA
        0 / END OF INDUCTION MACHINE DATA
        Q
        """;

    /// <summary>A FACTS record: NAME, I, J, MODE, PDES, QDES, VSET, SHMX, TRMX, VTMN, VTMX, VSMX, IMX, LINX, RMPCT, OWNER,
    /// SET1, SET2, VSREF, REMOT, MNAME.</summary>
    internal static string Upfc(string name = "UPFC A", int i = 2, int j = 3, int mode = 1, double pdes = 150, double qdes = 20, double vset = 1.01,
        double shmx = 200, double trmx = 9999, double vtmn = 0.9, double vtmx = 1.1, double vsmx = 1.0, double imx = 0, double set1 = 0, double set2 = 0, int remot = 0)
        => $"'{name}', {i}, {j}, {mode}, {I(pdes)}, {I(qdes)}, {I(vset)}, {I(shmx)}, {I(trmx)}, {I(vtmn)}, {I(vtmx)}, {I(vsmx)}, {I(imx)}, 0.05, 100, 1, {I(set1)}, {I(set2)}, 0, {remot}, ''";

    private static GridModel Read(string raw) => PsseRawParser.ParseWithDiagnostics(raw, "facts").Model;

    private static int Ix(PowerFlowResult pf, int bus) => pf.BusIds.ToList().IndexOf(bus);

    private static void Close(double expected, double actual, string what, double rel = 1e-12)
        => Assert.True(Math.Abs(expected - actual) <= rel * (1 + Math.Abs(expected)), $"{what}: expected {expected:R}, got {actual:R}");

    private static (double Re, double Im) V(PowerFlowResult pf, int bus)
    {
        double a = pf.VaDeg[Ix(pf, bus)] * Math.PI / 180, m = pf.Vm[Ix(pf, bus)];
        return (m * DetMath.Cos(a), m * DetMath.Sin(a));
    }

    [Fact]
    public void A_upfc_delivers_its_set_points_and_its_series_and_shunt_quantities_follow_from_the_solved_voltages()
    {
        GridModel m = Read(Case());
        FactsDevice d = Assert.Single(m.FactsDevices);
        Assert.Equal(new FactsDevice("UPFC A", FactsMode.Upfc, 2, 3, 150, 20, 1.01, 200, 9999, 0.9, 1.1, 1.0, 0), d);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged, pf.FactsNote);
        FactsResult f = Assert.Single(pf.FactsDevices!);

        // The network sees PDES + j QDES arriving at bus 3 (which has nothing else) and PDES leaving bus 2, held at VSET.
        Close(150.0, pf.PInjMw[Ix(pf, 3)], "bus 3 P", 1e-9);
        Close(20.0, pf.QInjMvar[Ix(pf, 3)], "bus 3 Q", 1e-9);
        Close(-150.0, pf.PInjMw[Ix(pf, 2)], "bus 2 P", 1e-9);
        Assert.Equal(1.01, pf.Vm[Ix(pf, 2)]);

        // By hand: conj(I) = S_J / V_J; S_I = V_I conj(I); V_se = V_J - V_I; bridge = Re(S_J - S_I); the shunt output is
        // the bus 2 injection (-PDES + j Q2) plus S_I.
        (double vir, double vii) = V(pf, 2);
        (double vjr, double vji) = V(pf, 3);
        double sjr = 1.5, sji = 0.2, den = vjr * vjr + vji * vji;
        double cr = (sjr * vjr + sji * vji) / den, ci = (sji * vjr - sjr * vji) / den;
        double sir = vir * cr - vii * ci, sii = vir * ci + vii * cr;
        Close(Math.Sqrt(cr * cr + ci * ci) * 100, f.SeriesCurrentMva, "series current");
        Close(Math.Sqrt((vjr - vir) * (vjr - vir) + (vji - vii) * (vji - vii)), f.SeriesVoltagePu, "series voltage");
        Close((sjr - sir) * 100, f.BridgeMw, "bridge power");
        double q2 = pf.QInjMvar[Ix(pf, 2)];
        double pSh = -150 + sir * 100, qSh = q2 + sii * 100;
        Close(qSh, f.ShuntMvar, "shunt Mvar", 1e-9);
        Close(Math.Sqrt(pSh * pSh + qSh * qSh) / 1.01, f.ShuntCurrentMva, "shunt current", 1e-9);
        Assert.False(f.ShuntLimited);
        Assert.Equal((150.0, 20.0), (f.SeriesMw, f.SeriesMvar));
        // Energy: what leaves bus 2 through the device (PDES) is what arrives at bus 3 (lossless).
        _out.WriteLine($"Vse {f.SeriesVoltagePu:R} pu, Ise {f.SeriesCurrentMva:R} MVA, bridge {f.BridgeMw:R} MW, shunt {f.ShuntMvar:R} Mvar");
    }

    [Fact]
    public void The_upfc_ac_side_matches_pandapower_with_the_device_as_its_injections()
    {
        // Cross-check (pandapower 3.5.6, scipy 1.18.1, numpy 2.4.6, pandas 2.3.3, isolated virtual environment): the same
        // network with the UPFC as an sgen of 150 MW + 20 Mvar at bus 3 and a gen of -150 MW holding 1.01 pu at bus 2, the
        // lines as 1 km of X ohm (0.02 pu = 23.805 ohm, 0.04 pu = 47.61 ohm on 100 MVA, 345 kV), tolerance_mva 1e-10.
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(Read(Case()));
        Assert.True(pf.Converged, pf.FactsNote);
        double[] want = { PpVa2, PpVa3, PpVa4, PpVm3, PpVm4, PpQ2 };
        double[] got = { pf.VaDeg[Ix(pf, 2)], pf.VaDeg[Ix(pf, 3)], pf.VaDeg[Ix(pf, 4)], pf.Vm[Ix(pf, 3)], pf.Vm[Ix(pf, 4)], pf.QInjMvar[Ix(pf, 2)] };
        string[] what = { "Va2", "Va3", "Va4", "Vm3", "Vm4", "Q2" };
        for (int k = 0; k < want.Length; k++)
        {
            _out.WriteLine($"{what[k]}: GridSim {got[k]:R}, pandapower {want[k]:R}, delta {got[k] - want[k]:R}");
            Close(want[k], got[k], what[k], 1e-8);
        }
    }

    // From the pandapower run (see the test above).
    private const double PpVa2 = -1.702105184663994, PpVa3 = -1.725281094545523, PpVa4 = -3.5729544897440033, PpVm3 = 0.9664203033765483,
        PpVm4 = 0.962781888185993, PpQ2 = 52.728214502334595;

    [Fact]
    public void A_shunt_element_over_its_current_rating_is_held_on_it_and_its_bus_released()
    {
        // SHMX 20 MVA: holding bus 2 at 1.01 pu needs more, so the shunt element sits on its rating and bus 2 moves.
        PowerFlowResult free = new NewtonRaphsonPowerFlow().Solve(Read(Case()));
        Assert.True(Math.Abs(Assert.Single(free.FactsDevices!).ShuntCurrentMva) > 20, "control: the free shunt needs more than 20 MVA");
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(Read(Case(Upfc(shmx: 20))));
        Assert.True(pf.Converged, pf.FactsNote);
        FactsResult f = Assert.Single(pf.FactsDevices!);
        Assert.True(f.ShuntLimited);
        Close(20.0, f.ShuntCurrentMva, "shunt current on its rating", 1e-8);
        Assert.NotEqual(1.01, pf.Vm[Ix(pf, 2)]);
        Close(150.0, pf.PInjMw[Ix(pf, 3)], "bus 3 still receives PDES", 1e-9);
    }

    [Fact]
    public void A_statcom_holds_its_bus_and_a_current_limited_one_gives_its_rating_times_the_voltage()
    {
        string statcom = Upfc("STATCOM B", i: 4, j: 0, pdes: 0, qdes: 0, vset: 1.0, shmx: 500);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(Read(Case(statcom)));
        Assert.True(pf.Converged, pf.FactsNote);
        FactsResult f = Assert.Single(pf.FactsDevices!);
        Assert.Equal(FactsMode.Statcom, f.Mode);
        Assert.Equal(1.0, pf.Vm[Ix(pf, 4)]);
        // The bus's net reactive injection is the STATCOM's output less the 100 Mvar load.
        Close(pf.QInjMvar[Ix(pf, 4)] + 100, f.ShuntMvar, "STATCOM Q", 1e-12);
        Close(Math.Abs(f.ShuntMvar), f.ShuntCurrentMva, "current at 1 pu", 1e-12);

        PowerFlowResult lim = new NewtonRaphsonPowerFlow().Solve(Read(Case(Upfc("STATCOM B", i: 4, j: 0, pdes: 0, qdes: 0, vset: 1.0, shmx: 10))));
        Assert.True(lim.Converged, lim.FactsNote);
        FactsResult g = Assert.Single(lim.FactsDevices!);
        Assert.True(g.ShuntLimited);
        double v4 = lim.Vm[Ix(lim, 4)];
        Assert.True(v4 < 1.0, $"bus 4 at {v4:R}");
        Close(10 * v4, g.ShuntMvar, "Q = SHMX x V", 1e-8);
        Close(g.ShuntMvar - 100, lim.QInjMvar[Ix(lim, 4)], "bus 4 Q", 1e-9);
    }

    [Fact]
    public void Mode_3_and_a_bypassed_mode_2_are_series_branches_with_the_shunt_element()
    {
        GridModel m3 = Read(Case(Upfc("TCSC C", mode: 3, set1: 0.001, set2: -0.005)));
        Branch b3 = m3.Branches.Single(b => b.Name == "FACTS TCSC C series");
        Assert.Equal((2, 3, 0.001, -0.005), (b3.From, b3.To, b3.R, b3.X));
        Assert.Equal(FactsMode.SeriesImpedance, Assert.Single(m3.FactsDevices).Mode);
        PowerFlowResult pf3 = new NewtonRaphsonPowerFlow().Solve(m3);
        Assert.True(pf3.Converged, pf3.FactsNote);
        Assert.Equal(1.01, pf3.Vm[Ix(pf3, 2)]);

        GridModel m2 = Read(Case(Upfc("BYPASS D", mode: 2)));
        Branch b2 = m2.Branches.Single(b => b.Name == "FACTS BYPASS D series");
        Assert.Equal((0.0, PsseRawParser.ZeroImpedanceXPu), (b2.R, b2.X));
        Assert.Equal(FactsMode.Statcom, Assert.Single(m2.FactsDevices).Mode);
        Assert.True(new NewtonRaphsonPowerFlow().Solve(m2).Converged);
    }

    [Theory]
    [InlineData("vsmx", "above VSMX")]
    [InlineData("imx", "above IMX")]
    [InlineData("trmx", "above TRMX")]
    [InlineData("vt", "outside VTMN..VTMX")]
    public void A_upfc_outside_a_rating_or_its_terminal_band_is_not_converged_naming_it(string which, string expected)
    {
        string facts = which switch
        {
            "vsmx" => Upfc(vsmx: 0.0001),
            "imx" => Upfc(imx: 10),
            "trmx" => Upfc(trmx: 0),
            _ => Upfc(vtmn: 1.2, vtmx: 1.3),
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(Read(Case(facts)));
        Assert.False(pf.Converged);
        Assert.Contains("FACTS device UPFC A", pf.FactsNote, StringComparison.Ordinal);
        Assert.Contains(expected, pf.FactsNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Dense_and_sparse_newton_give_the_same_bits_with_a_upfc()
    {
        GridModel m = Read(Case(Upfc(shmx: 20)));
        PowerFlowResult a = new NewtonRaphsonPowerFlow().Solve(m), b = new SparseNewtonRaphsonPowerFlow().Solve(m);
        Assert.Equal(a.Vm.Select(BitConverter.DoubleToInt64Bits), b.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.VaDeg.Select(BitConverter.DoubleToInt64Bits), b.VaDeg.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(a.FactsDevices![0], b.FactsDevices![0]);
    }

    [Fact]
    public void A_model_with_a_facts_device_goes_to_newton_and_the_writers_and_sweep_refuse_it()
    {
        GridModel m = Read(Case());
        Assert.True(m.HasCoupledDevices);
        Assert.False(SolverSelector.LooksRadial(m));
        Assert.Contains("does not model FACTS devices", Assert.Throws<InvalidOperationException>(() => new BackwardForwardSweep().Solve(m)).Message);
        Assert.Contains("1 FACTS device(s) (UPFC A)", Assert.Throws<InvalidDataException>(() => GridSim.Core.IO.Matpower.MatpowerWriter.Write(m)).Message);
        // Test changed 2026-10-25 (export pack): the PSS/E RAW writer now writes this device (it used to refuse it), so the
        // refusal check became a check that the written file reads back to the same device records (strengthened).
        Assert.Equal(m.FactsDevices, PsseRawParser.ParseWithDiagnostics(PsseRawWriter.Write(m).Text, "facts").Model.FactsDevices);
    }

    [Fact]
    public void A_shunt_element_at_a_generator_bus_or_the_slack_bus_is_refused_at_the_solve()
    {
        string gen2 = "2, '1', 50, 0, 100, -100, 1.01, 0, 100, 0, 0.01, 0, 0, 1, 1, 100, 999, -999, 1, 1\n";
        var ex = Assert.Throws<InvalidOperationException>(() => new NewtonRaphsonPowerFlow().Solve(Read(Case(extraGen: gen2))));
        Assert.Contains("FACTS device UPFC A's shunt element controls the voltage of bus 2, which also has generators", ex.Message, StringComparison.Ordinal);
        var slack = Assert.Throws<InvalidOperationException>(() => new NewtonRaphsonPowerFlow().Solve(Read(Case(Upfc("S", i: 1, j: 0)))));
        Assert.Contains("the slack bus", slack.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5, 0, "1 FACTS device in constant series voltage or IPFC mode (MODE 4 to 8)(s)")]
    [InlineData(4, 0, "1 FACTS device in constant series voltage or IPFC mode (MODE 4 to 8)(s)")]
    [InlineData(1, 4, "1 FACTS device regulating a remote bus(s)")]
    public void Unmodelled_facts_modes_and_remote_regulation_are_refused_unless_allowed(int mode, int remot, string expected)
    {
        string raw = Case(Upfc(mode: mode, remot: remot));
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(raw, "f"));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Empty(PsseRawParser.ParseWithDiagnostics(raw, "f", new PsseReadOptions { AllowUnmodelled = true }).Model.FactsDevices);
    }

    [Theory]
    [InlineData("shmx0", "needs its shunt element (SHMX above 0)")]
    [InlineData("mode3", "MODE 3 (constant series impedance) needs a terminal end bus J")]
    [InlineData("same", "the terminal end bus J is the sending end bus 2")]
    [InlineData("vt", "VTMN 1.1 and VTMX 0.9 must make a band above 0 pu")]
    public void Malformed_facts_records_are_refused_naming_the_line(string what, string expected)
    {
        string facts = what switch
        {
            "shmx0" => Upfc(shmx: 0),
            "mode3" => Upfc(j: 0, mode: 3),
            "same" => Upfc(j: 2),
            _ => Upfc(vtmn: 1.1, vtmx: 0.9),
        };
        var ex = Assert.Throws<InvalidDataException>(() => PsseRawParser.ParseWithDiagnostics(Case(facts), "bad"));
        Assert.Contains("line 28 (FACTS DEVICE data)", ex.Message, StringComparison.Ordinal);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PSSE_30_sample_reads_its_statcom_and_upfc_and_shares_bus_153_between_their_shunt_elements()
    {
        // Tests: until 2.0.180 this test pinned the refusal of two shunt elements holding bus 153 ("which FACTS device
        // FACTS 1's shunt element also controls"). Several FACTS shunt elements and compensators on one bus are now
        // modelled (RMPCT sharing, PsseSharedVoltageControlTests), so the shared-bus refusal is gone: the strengthened
        // test asserts that both devices read with their RMPCT (100 each, an equal split at one VSET of 1.015 pu), that the
        // solve gets past the shared bus, and that it still fails loud for the sample's remaining gap: its multi-terminal
        // DC link needs a control (VCMOD / MARG) GridSim does not apply, and its two capacitor-commutated links are read
        // past (allowed here), so the sample cannot report a converged state.
        string path = Path.Combine(CliProcess.Root(), "tests", "GridSim.Tests", "fixtures", "psse", "t_psse_case3.raw");
        PsseParseResult r = PsseRawParser.ParseFile(path, new PsseReadOptions { AllowUnmodelled = true });
        Assert.Equal(2, r.Model.FactsDevices.Count);
        Assert.Equal(new FactsDevice("FACTS 1", FactsMode.Statcom, 153, 0, 0, 0, 1.015, 50, 100, 0.9263, 1.134, 1.0, 0), r.Model.FactsDevices[0]);
        Assert.Equal(new FactsDevice("FACTS 2", FactsMode.Upfc, 153, 155, 350, 40, 1.015, 25, 9999, 0.9, 1.1, 1.0, 0), r.Model.FactsDevices[1]);
        Assert.Equal(new[] { 100.0, 100.0 }, r.Model.FactsDevices.Select(d => d.RmpctPercent));
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(r.Model);
        Assert.False(pf.Converged);
        Assert.Contains("multi-terminal DC link MTDC 1: converter 402 needs a firing angle", pf.MtdcNote, StringComparison.Ordinal);
    }

    // ---- Static var compensators (MODSW 2, with shunt control) ---------------------------------------------------------

    private static readonly PsseReadOptions WithShunts = new() { FrequencyHz = 50, ApplyShuntControl = true };

    private static GridModel Svc(string shunt, PsseReadOptions? o = null) => PsseRawParser.ParseWithDiagnostics(PsseShuntControlTests.TwoBus(shunt), "svc", o ?? WithShunts).Model;

    private static PowerFlowResult FixedB(GridModel m, double b) =>
        new NewtonRaphsonPowerFlow().Solve(m.CopyWith(buses: m.Buses.Select(x => x.Id == 2 ? x with { Bs = x.Bs + b } : x).ToArray(),
            staticVarCompensators: Array.Empty<StaticVarCompensator>()));

    [Fact]
    public void An_svc_below_its_band_holds_the_band_edge_with_the_susceptance_a_fixed_shunt_would_need()
    {
        GridModel m = Svc("2, 0, 1, 1.03, 0.97, 0, 100, '', 0, 4, 10, 2, 20");   // mixed block sizes: 0..80 Mvar
        StaticVarCompensator c = Assert.Single(m.StaticVarCompensators);
        Assert.Equal(new StaticVarCompensator(2, 0, 80, 0, 0.97, 1.03, "SVC 2"), c);
        Assert.True(FixedB(m, 0).Vm[1] < 0.97, "control: at BINIT bus 2 is below the band");
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged, pf.FactsNote);
        SvcResult s = Assert.Single(pf.StaticVarCompensators!);
        Assert.Equal(SvcState.Regulating, s.State);
        Assert.Equal(0.97, s.SetpointPu);
        Assert.Equal(0.97, pf.Vm[1]);
        Assert.InRange(s.SusceptanceMvar, 0.0, 80.0);
        Close(s.SusceptanceMvar * 0.97 * 0.97, s.Mvar, "Q = B V^2");
        // The same network with that susceptance as a fixed shunt solves to the same voltage.
        PowerFlowResult fixedPf = FixedB(m, s.SusceptanceMvar);
        Close(0.97, fixedPf.Vm[1], "fixed shunt at the solved B", 1e-9);
    }

    [Fact]
    public void An_svc_short_of_range_is_fixed_at_its_maximum_giving_exactly_the_fixed_shunt_solve()
    {
        GridModel m = Svc("2, 0, 1, 1.03, 0.97, 0, 100, '', 0, 1, 1");   // one 1 Mvar block: too small to reach 0.97
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged, pf.FactsNote);
        SvcResult s = Assert.Single(pf.StaticVarCompensators!);
        Assert.Equal(SvcState.AtMaximum, s.State);
        Assert.Equal(1.0, s.SusceptanceMvar);
        PowerFlowResult want = FixedB(m, 1.0);
        Assert.Equal(want.Vm.Select(BitConverter.DoubleToInt64Bits), pf.Vm.Select(BitConverter.DoubleToInt64Bits));
        Assert.Equal(want.VaDeg.Select(BitConverter.DoubleToInt64Bits), pf.VaDeg.Select(BitConverter.DoubleToInt64Bits));
    }

    [Fact]
    public void An_svc_inside_its_band_stays_at_binit_and_a_fixed_set_point_is_held()
    {
        GridModel wide = Svc("2, 0, 1, 1.5, 0.5, 0, 100, '', 10, 8, 10");
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(wide);
        SvcResult s = Assert.Single(pf.StaticVarCompensators!);
        Assert.Equal((SvcState.Initial, 10.0), (s.State, s.SusceptanceMvar));
        Assert.True(double.IsNaN(s.SetpointPu));
        PowerFlowResult want = FixedB(wide, 0);   // BINIT is already in the bus shunt
        Assert.Equal(want.Vm.Select(BitConverter.DoubleToInt64Bits), pf.Vm.Select(BitConverter.DoubleToInt64Bits));

        PowerFlowResult held = new NewtonRaphsonPowerFlow().Solve(Svc("2, 0, 1, 0.98, 0.98, 0, 100, '', 0, 8, 10"));
        Assert.True(held.Converged, held.FactsNote);
        Assert.Equal(0.98, held.Vm[1]);
        Assert.Equal(SvcState.Regulating, Assert.Single(held.StaticVarCompensators!).State);
    }

    [Fact]
    public void Without_shunt_control_a_modsw_2_shunt_stays_fixed_at_binit_as_before()
    {
        PsseParseResult plain = PsseRawParser.ParseWithDiagnostics(PsseShuntControlTests.TwoBus("2, 0, 1, 1.03, 0.97, 0, 100, '', 20, 8, 10"), "svc", new PsseReadOptions { FrequencyHz = 50 });
        Assert.Empty(plain.Model.StaticVarCompensators);
        Assert.False(plain.Model.HasCoupledDevices);
        Assert.Equal(20.0, plain.Model.Buses[1].Bs);
        PsseParseResult with = PsseRawParser.ParseWithDiagnostics(PsseShuntControlTests.TwoBus("2, 0, 1, 1.03, 0.97, 0, 100, '', 20, 8, 10"), "svc", WithShunts);
        Assert.Equal(plain.Model.Buses, with.Model.Buses);   // BINIT in the bus shunt either way
        Assert.Contains(with.Notes, n => n.StartsWith("1 switched shunt(s) with continuous voltage control (MODSW 2) are static var compensators", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2, 0, 1, 1.03, 0.97, 1, 100, '', 0, 8, 10", "with continuous voltage control (MODSW 2) regulate a remote bus (SWREM)")]
    [InlineData("2, 0, 1, 1.03, 0.97, 0, 100, '', 0", "with continuous voltage control (MODSW 2) have no blocks")]
    [InlineData("2, 0, 1, 1.03, 0.97, 0, 100, '', 90, 8, 10", "with continuous voltage control (MODSW 2) have a BINIT outside the range their blocks reach")]
    public void An_svc_the_model_cannot_represent_stays_fixed_and_is_noted(string shunt, string reason)
    {
        PsseParseResult r = PsseRawParser.ParseWithDiagnostics(PsseShuntControlTests.TwoBus(shunt), "svc", WithShunts);
        Assert.Empty(r.Model.StaticVarCompensators);
        Assert.Contains(r.Notes, n => n == "1 switched shunt(s) " + reason + ": they stay fixed at BINIT.");
    }

    [Fact]
    public void An_svc_band_below_zero_or_inverted_is_refused_with_its_line()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Svc("2, 0, 1, 0.96, 0.97, 0, 100, '', 0, 8, 10"));
        Assert.Contains("line 28 (SWITCHED SHUNT data)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("VSWHI must be at or above VSWLO", ex.Message, StringComparison.Ordinal);
    }

    // ---- Device powers at a ZIP-load bus (fixed with this item) --------------------------------------------------------

    [Fact]
    public void A_converter_at_a_bus_with_a_constant_current_load_keeps_its_power_constant()
    {
        // VSC converter 1 (MODE 2, 200 MW drawn at pf 0.95) at bus 2, which also has a load of 10 MW + 5 Mvar constant
        // power and 20 MW + 10 Mvar constant current. The bus's net injection must be the converter's constant power less
        // the load at the solved voltage: P = -200 - (10 + 20 V), Q = q1 - (5 + 10 V). Before the fix the converter's power
        // was added to the bus load and scaled with it by the ZIP coefficients.
        string raw = PsseVscTests.Case().Replace("0 / END OF LOAD DATA", "2, '1', 1, 1, 1, 10, 5, 20, 10, 0, 0, 1, 1, 0\n0 / END OF LOAD DATA", StringComparison.Ordinal);
        GridModel m = PsseRawParser.ParseWithDiagnostics(raw, "zip").Model;
        Assert.NotNull(m.Buses.Single(b => b.Id == 2).Zip);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged, pf.VscNote);
        double v = pf.Vm[Ix(pf, 2)];
        double q1 = -200 * Math.Sqrt(1 - 0.95 * 0.95) / 0.95;
        Close(-200 - (10 + 20 * v), pf.PInjMw[Ix(pf, 2)], "bus 2 P", 1e-9);
        Close(q1 - (5 + 10 * v), pf.QInjMvar[Ix(pf, 2)], "bus 2 Q", 1e-9);
    }
}
