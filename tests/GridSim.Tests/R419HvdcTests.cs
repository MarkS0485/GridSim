using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.Hvdc;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Emt.Partition;
using GridSim.Core.Dynamics.Emt.PowerElectronics;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-19: an HVDC link (two VSC stations joined by a DC line cut into two partitions) against closed forms and the load-flow VscDcLink solution, and
/// the LCC bridge group against the six-pulse closed forms. COMPARATOR-UNVERIFIED: no CIGRE HVDC benchmark waveform or PSCAD/EMTP result is compared
/// (the benchmark parameter sets are public, its waveforms are tool output).
/// </summary>
public sealed class R419HvdcTests(ITestOutputHelper output)
{
    private const double Dt = 5e-5, RdcOhm = 2.0, V2kv = 20.0, OrderMw = 8.0, Zc = 300.0, Tau = 5e-4, Rs = 0.01, Rf = 0.05;

    private static (VscDcLink Link, double I, double Vd1, double Vd2) LoadFlow()
    {
        // Power converter (first): DCSET = MW drawn from its AC bus; voltage converter (second): DCSET kV. No converter losses in the load-flow record, so the
        // DC power is exactly the order and the closed form P = Vd1 (Vd1 - Vd2) / R must hold.
        var first = new VscConverter(1, VscDcControl.Power, VscAcControl.Voltage, OrderMw, 1.0, 0, 0, 0, 0, 0, 1.0, 1000, -1000);
        var second = new VscConverter(2, VscDcControl.DcVoltage, VscAcControl.Voltage, V2kv, 1.0, 0, 0, 0, 0, 0, 1.0, 1000, -1000);
        var link = new VscDcLink("L", RdcOhm, first, second);
        var s = VscDcLinks.SolveDc(link);
        return (link, s.CurrentKa * 1000.0, s.FirstDcKv * 1000.0, s.SecondDcKv * 1000.0);
    }

    private static PartitionedModel BuildLink(bool single, double dt, double i, double vd1, double vd2, double pPccDrawnW)
    {
        PartitionedModel m = PartitionedModel.Create(["S1", "S2"], single);
        VscStationSpec Spec(VscStationMode mode, double setPoint, double dcNominal, double initial) => new()
        {
            GridPeakVolts = 8000.0, FrequencyHz = 50.0, GridAngleRad = 0.0, GridROhm = 0.05, GridLHenry = 1e-3, FilterROhm = Rf, FilterLHenry = 5e-3,
            ConverterRsOhm = Rs, DcNominalVolts = dcNominal, DcCapF = 5e-3, Converter = VscStationConverter.TwoLevelAverage, Mode = mode, SetPoint = setPoint,
            CurrentBandwidthRadPerS = 1000.0, OuterBandwidthRadPerS = 60.0, ControlEvery = 2, InitialDcVolts = initial,
        };
        VscHvdcStation.Add(m.Net("S1"), "S1", "S1/", Spec(VscStationMode.Power, -pPccDrawnW, vd1, vd1), dt);
        VscHvdcStation.Add(m.Net("S2"), "S2", "S2/", Spec(VscStationMode.DcVoltage, vd2, vd2, vd2), dt);
        double rPole = 0.5 * RdcOhm;
        m.Add("S1", NodalBranch.Resistor("S1.rp", m.Node("S1", "dc.p"), m.Node("S1", "lp"), rPole));
        m.Add("S1", NodalBranch.Resistor("S1.rn", m.Node("S1", "dc.n"), m.Node("S1", "ln"), rPole));
        NodalBergeronLine lp = m.AddLine("DCP", "S1", "lp", "S2", "dc.p", Zc, Tau);
        NodalBergeronLine ln = m.AddLine("DCN", "S1", "ln", "S2", "dc.n", Zc, Tau);
        // steady state at t = 0: the load-flow current I flows from station 1 to station 2 in the positive pole and returns in the negative pole
        lp.A.SetInitialState(0.5 * vd1 - i * rPole, +i);
        lp.B.SetInitialState(0.5 * vd2, -i);
        ln.A.SetInitialState(-0.5 * vd1 + i * rPole, -i);
        ln.B.SetInitialState(-0.5 * vd2, +i);
        return m;
    }

    private static List<NodalProbe> LinkProbes() =>
    [
        new("v1p", NodalProbeKind.NodeVoltage, "S1/dc.p"), new("v1n", NodalProbeKind.NodeVoltage, "S1/dc.n"),
        new("v2p", NodalProbeKind.NodeVoltage, "S2/dc.p"), new("v2n", NodalProbeKind.NodeVoltage, "S2/dc.n"),
        new("iline", NodalProbeKind.ElementCurrent, "DCP.a"), new("iline2", NodalProbeKind.ElementCurrent, "DCP.b"),
        new("ia1", NodalProbeKind.ElementCurrent, "S1.f0"), new("va1", NodalProbeKind.NodeVoltage, "S1/pcc0"),
    ];

    [Fact]
    public void Load_flow_vsc_link_solution_obeys_the_closed_form_P_equals_Vd1_times_Vd1_minus_Vd2_over_R()
    {
        var (link, i, vd1, vd2) = LoadFlow();
        double pClosed = vd1 * (vd1 - vd2) / RdcOhm / 1e6;          // MW
        output.WriteLine($"load flow: I = {i:F3} A, Vd1 = {vd1:F3} V, Vd2 = {vd2:F3} V; closed form P = {pClosed:F6} MW (order {OrderMw})");
        Assert.Equal(OrderMw, pClosed, 9);
        Assert.Equal((vd1 - vd2) / RdcOhm, i, 6);
        Assert.Equal(V2kv * 1000.0, vd2, 9);
        Assert.NotNull(link);
    }

    [Fact]
    public void Vsc_link_dc_steady_state_matches_the_closed_form_and_starts_from_the_load_flow_state()
    {
        var (_, iLf, vd1Lf, vd2Lf) = LoadFlow();
        // The order is the DC power of the load flow; the EMT controller works at the PCC, so add the converter and reactor losses 3/2 (Rf + Rs) I^2
        // (two fixed-point passes; the AC current is 2 P / (3 Vpcc)).
        double pDc = OrderMw * 1e6, pPcc = pDc;
        for (int k = 0; k < 3; k++) { double ihat = 2.0 * pPcc / (3.0 * 8000.0); pPcc = pDc + 1.5 * (Rf + Rs) * ihat * ihat; }
        PartitionedModel m = BuildLink(false, Dt, iLf, vd1Lf, vd2Lf, pPcc);
        PartitionedResult res = PartitionedEngine.Run(m, Dt, 1.2, LinkProbes(), null, new PartitionedOptions { Threads = 2 });
        output.WriteLine(res.Plan.Description);
        NodalResult r = res.Run;
        Assert.Equal(NodalStatus.Completed, r.Status);
        double[] v1 = r.Series[0].Zip(r.Series[1], (a, b) => a - b).ToArray(), v2 = r.Series[2].Zip(r.Series[3], (a, b) => a - b).ToArray(), il = r.Series[4];

        // t = dt: the state starts from the load-flow solution
        output.WriteLine($"t = dt: Vd1 = {v1[1]:F1} V (load flow {vd1Lf:F1}), Vd2 = {v2[1]:F1} V ({vd2Lf:F1}), line current {il[1]:F2} A ({iLf:F2})");
        Assert.InRange(Math.Abs(v1[1] - vd1Lf) / vd1Lf, 0.0, 2e-3);
        Assert.InRange(Math.Abs(v2[1] - vd2Lf) / vd2Lf, 0.0, 2e-3);
        Assert.InRange(Math.Abs(il[1] - iLf) / iLf, 0.0, 2e-3);

        // steady state over the last 10 cycles
        double t0 = 1.0, t1 = 1.2;
        double mv1 = R419MmcTests.Mean(r, v1, t0, t1), mv2 = R419MmcTests.Mean(r, v2, t0, t1), mi = R419MmcTests.Mean(r, il, t0, t1), mi2 = R419MmcTests.Mean(r, r.Series[5], t0, t1);
        double pDc1 = mv1 * mi;
        double pClosed = mv1 * (mv1 - mv2) / RdcOhm;
        output.WriteLine($"steady state: Vd1 = {mv1:F1} V (LF {vd1Lf:F1}), Vd2 = {mv2:F1} V (set {vd2Lf:F1}), I = {mi:F2} A (LF {iLf:F2}, far end {-mi2:F2});"
            + $" P = Vd1 I = {pDc1 / 1e6:F4} MW, closed form Vd1 (Vd1 - Vd2)/R = {pClosed / 1e6:F4} MW, order {OrderMw} MW");
        Assert.InRange(Math.Abs(mv2 - vd2Lf) / vd2Lf, 0.0, 2e-3);                 // controlled voltage
        Assert.InRange(Math.Abs(mi - iLf) / iLf, 0.0, 5e-3);
        Assert.InRange(Math.Abs(mv1 - vd1Lf) / vd1Lf, 0.0, 3e-3);
        Assert.InRange(Math.Abs(pDc1 - pClosed) / pClosed, 0.0, 3e-3);            // Ohm's law on the DC link, measured independently
        Assert.InRange(Math.Abs(pDc1 / 1e6 - OrderMw) / OrderMw, 0.0, 6e-3);      // equals the load-flow order
        Assert.InRange(Math.Abs(mi + mi2) / mi, 0.0, 1e-3);                       // lossless line: the two ends carry the same current
    }

    [Fact]
    public void Hvdc_link_partitioned_run_is_bit_identical_to_the_single_partition_run_and_across_threads()
    {
        var (_, iLf, vd1Lf, vd2Lf) = LoadFlow();
        double pPcc = OrderMw * 1.0e6 * 1.005;
        List<NodalProbe> probes = LinkProbes();
        PartitionedModel mono = BuildLink(true, Dt, iLf, vd1Lf, vd2Lf, pPcc);
        NodalResult inc = NodalSolver.Run(mono.Net("S1"), Dt, 0.15, probes);
        Assert.Equal(NodalStatus.Completed, inc.Status);
        foreach (int threads in new[] { 1, 2, 16 })
        {
            PartitionedResult part = PartitionedEngine.Run(BuildLink(false, Dt, iLf, vd1Lf, vd2Lf, pPcc), Dt, 0.15, probes, null, new PartitionedOptions { Threads = threads });
            for (int k = 0; k < probes.Count; k++)
                for (int i = 0; i < inc.Series[k].Length; i++)
                    Assert.Equal(BitConverter.DoubleToInt64Bits(inc.Series[k][i]), BitConverter.DoubleToInt64Bits(part.Run.Series[k][i]));
            output.WriteLine($"HVDC link, 2 partitions, {threads} thread(s): bit-identical to the single-partition solver over {inc.Times.Length} samples x {probes.Count} probes");
        }
        Assert.True(inc.Series[4].Max() > 0.9 * iLf, "the DC line carries current");
    }

    // ==================================================================================================================
    // LCC bridge group (six or twelve pulse) with a DC R-L load: closed forms.
    //   Vd = N [ Vd0 cos(alpha) - (3 w Lc / pi) Id - 2 (Ron + Rs) Id ],  Vd0 = 3 sqrt(2) V_LL / pi per six-pulse bridge (N = 1 or 2),
    //   steady state Vd = R Id  =>  Id = N Vd0 cos(alpha) / (R + N (3 w Lc / pi + 2 (Ron + Rs))).
    // ==================================================================================================================
    [Theory]
    [InlineData(6, 20.0)]
    [InlineData(12, 20.0)]
    [InlineData(12, 35.0)]
    public void Lcc_bridge_group_dc_voltage_and_current_match_the_closed_forms(int pulses, double alphaDeg)
    {
        var spec = new LccBridgeSpec { LineToLineRmsVolts = 10000.0, FrequencyHz = 50.0, CommutationLHenry = 5e-3, SourceROhm = 1e-3, Pulses = pulses };
        double alpha = alphaDeg * Math.PI / 180.0, rLoad = 30.0, lLoad = 0.2;
        var net = new NodalNetwork();
        int p = net.Node("dcp"), n = net.Node("dcn");
        LccBridgeEmt g = LccBridgeEmt.Add(net, "G", "", p, n, spec, _ => alpha);
        net.Add(NodalBranch.SeriesRl("Load", p, n, rLoad, lLoad));
        const double dt = 1e-5;
        NodalResult r = NodalSolver.Run(net, dt, 0.2, [new NodalProbe("id", NodalProbeKind.ElementCurrent, "Load"), new NodalProbe("vp", NodalProbeKind.NodeVoltage, "dcp"),
            new NodalProbe("vn", NodalProbeKind.NodeVoltage, "dcn")]);
        Assert.Equal(NodalStatus.Completed, r.Status);
        double vd0 = LccBridgeEmt.OpenCircuitDcVolts(spec), rc = LccBridgeEmt.CommutationResistanceOhm(spec), rcond = LccBridgeEmt.ConductionResistanceOhm(spec);
        double idClosed = vd0 * Math.Cos(alpha) / (rLoad + rc + rcond);
        double vdClosed = rLoad * idClosed;
        double[] vd = r.Series[1].Zip(r.Series[2], (a, b) => a - b).ToArray();
        double idMeas = R419MmcTests.Mean(r, r.Series[0], 0.1, 0.2), vdMeas = R419MmcTests.Mean(r, vd, 0.1, 0.2);
        // The six-pulse Vd0 is 13.5 kV; commutation resistance is 3 w Lc / pi per bridge.
        output.WriteLine($"{pulses}-pulse, alpha {alphaDeg} deg: Id = {idMeas:F2} A (closed form {idClosed:F2}), Vd = {vdMeas:F1} V ({vdClosed:F1}); Vd0 {vd0:F1} V, Rc {rc:F3} ohm, conduction {rcond:F4} ohm");
        Assert.InRange(Math.Abs(idMeas - idClosed) / idClosed, 0.0, 6e-3);
        Assert.InRange(Math.Abs(vdMeas - vdClosed) / vdClosed, 0.0, 6e-3);
        Assert.Equal(ConverterModelClass.Switched, g.ModelClass);
        Assert.Equal(pulses / 6, g.Bridges.Count);
    }
}
