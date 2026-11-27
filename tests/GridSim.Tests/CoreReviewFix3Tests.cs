using System.Globalization;
using System.Numerics;
using GridSim.Core.Assets;
using GridSim.Core.Dynamics.Models.Wecc;
using GridSim.Core.Dynamics.SmallSignal;
using GridSim.Core.IO.Dgs;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// Regression tests for the 2.1.100 core fixes (the last items of the core review). Each expected value comes from outside
/// the code under test: a closed form derived in the test, the PSS/E data-format definition of DCSET, the EPRI REEC_C
/// model description, or the exact COI state-space algebra.
/// </summary>
public sealed class CoreReviewFix3Tests
{
    private static void Near(double want, double got, double tol, string what)
        => Assert.True(Math.Abs(want - got) <= tol, $"{what}: want {want:R}, got {got:R}");

    // ---- DGS: a capacitor's qcapn is a positive Bs --------------------------------------------------------------------

    [Fact]
    public void A_dgs_capacitor_injects_its_rating_and_raises_the_far_end_voltage()
    {
        // Slack T1 at 1.02 pu, a lossless 110 kV line of 0.4 ohm/km x 50 km = 20 ohm to T2, and a 12 MVAr capacitor at T2
        // with nothing else there. Base 100 MVA, Zbase = 110^2 / 100 = 121 ohm, so X = 20/121 pu and B = 0.12 pu.
        // Closed form (bus 2 KCL, I = jB V2 into the shunt, I = (V1 - V2) / jX): V2 = V1 / (1 - X B), in phase with V1.
        // The slack then absorbs the shunt's MVAr: Q1 = -B V1 V2 x 100, and the shunt injects B V2^2 x 100.
        const string text = "$$General;ID(a:40);Descr(a:40);Val(a:40)\ng1;Version;5.0\ng2;Origin;SYNTHETIC hand-written test fixture (not a PowerFactory export)\n"
            + "$$ElmTerm;ID(a:40);loc_name(a:40);uknom(r:20)\nT1;Bus one;110\nT2;Bus two;110\n"
            + "$$TypLne;ID(a:40);loc_name(a:40);rline(r:20);xline(r:20)\nTL;Line type;0;0.4\n"
            + "$$ElmLne;ID(a:40);loc_name(a:40);typ_id(p:40);dline(r:20)\nL1;Line one;TL;50\n"
            + "$$ElmXnet;ID(a:40);loc_name(a:40);bustp(a:2);usetp(r:20)\nX1;Grid;SL;1.02\n"
            + "$$ElmShnt;ID(a:40);qcapn(r:20);shtype(a:2)\nSH;12;2\n"
            + "$$StaCubic;ID(a:40);fold_id(p:40);obj_id(p:40);obj_bus(i:2)\nC1;T1;X1;0\nC2;T1;L1;0\nC3;T2;L1;1\nC4;T2;SH;0\n";
        DgsImportResult r = DgsImporter.Import(DgsTableReader.Read(text), new DgsImportOptions(true));
        Assert.Equal(12.0, r.Model.Shunts.Single().Bs);
        GridModel m = r.Project().Model;
        Bus far = m.Buses.Single(b => b.Type != BusType.Slack);
        Assert.Equal(12.0, far.Bs);
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged);
        int i1 = pf.BusIds.ToList().IndexOf(m.Buses.Single(b => b.Type == BusType.Slack).Id), i2 = pf.BusIds.ToList().IndexOf(far.Id);
        double x = 20.0 / 121.0, b = 0.12, v1 = 1.02, v2 = v1 / (1.0 - x * b);
        Near(v1, pf.Vm[i1], 1e-12, "slack voltage");
        Near(v2, pf.Vm[i2], 1e-9, "capacitor bus voltage (above the slack's)");
        Assert.True(pf.Vm[i2] > pf.Vm[i1]);
        Near(-b * v1 * v2 * 100.0, pf.QInjMvar[i1], 1e-6, "slack absorbs the capacitor's MVAr");
    }

    // ---- PSS/E VSC TYPE 2: a positive DCSET feeds the AC network --------------------------------------------------------

    [Fact]
    public void A_positive_psse_dcset_feeds_its_ac_bus()
    {
        // PSS/E data format, TYPE 2: "a positive value specifies that the converter is feeding active power into the AC
        // network at bus ibus". The case file below carries DCSET = +100 at converter bus 2 (TYPE 2, MODE 2), with 300 kV
        // held at bus 3, R = 2 ohm, losses 500 kW + 2 kW/A at each end.
        string raw = PsseVscTests.Case(order: -100);
        Assert.Contains("2, 2, 2, 100, 0.95,", raw, StringComparison.Ordinal);
        GridModel m = PsseRawParser.ParseWithDiagnostics(raw, "vsc").Model;
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m);
        Assert.True(pf.Converged, pf.VscNote);
        VscLinkResult d = Assert.Single(pf.VscLinks!);
        // Converter 1 sends 100 MW into bus 2, so the DC current runs from converter 2. With J = -I (kA) and P = -100 MW
        // drawn: R J^2 - (V - B) J + (A - P) = 0, the root nearest 0.
        double rdc = 2, v = 300, a = 0.5, bl = 2, p = -100;
        double j = ((v - bl) - Math.Sqrt((v - bl) * (v - bl) - 4 * rdc * (a - p))) / (2 * rdc);
        Near(100.0, d.First.Mw, 1e-12 * 101, "converter 1 injects DCSET");
        Near(-j * 1000, d.CurrentA, 1e-12 * (1 + j * 1000), "current from converter 2");
        double loss = a + bl * j;
        Near(loss, d.First.LossMw, 1e-12, "converter 1 loss");
        Near(loss, d.Second.LossMw, 1e-12, "converter 2 loss");
        // The voltage converter draws the delivered power plus both losses and R J^2 from bus 3.
        Near(-(100.0 + 2 * loss + rdc * j * j), d.Second.Mw, 1e-9, "converter 2 draws the balance");
        Near(100.0, pf.PInjMw[pf.BusIds.ToList().IndexOf(2)], 1e-9, "bus 2 P injection");
    }

    [Fact]
    public void The_psse_writer_writes_dcset_in_the_psse_sign()
    {
        // A model whose power converter sends 100 MW into its AC bus (DcSetpoint = -100, the MW drawn) writes DCSET = +100.
        GridModel m = PsseRawParser.ParseWithDiagnostics(PsseVscTests.Case(order: -100), "vsc").Model;
        Assert.Equal(-100.0, m.VscLinks.Single().First.DcSetpoint);
        string text = PsseRawWriter.Write(m, 33).Text;
        string[] line = text.Split('\n').Select(l => l.Trim()).Single(l => l.StartsWith("2, 2, 2, ", StringComparison.Ordinal)).Split(',');
        Assert.Equal(100.0, double.Parse(line[3], NumberStyles.Float, CultureInfo.InvariantCulture));
        Assert.Equal(m.VscLinks, PsseRawParser.ParseWithDiagnostics(text, "vsc").Model.VscLinks);
    }

    // ---- REEC_C at SOCmin: charging is still allowed --------------------------------------------------------------------

    [Fact]
    public void Reec_c_at_soc_min_charges_at_its_order()
    {
        // EPRI 3002027129 Fig. 4-7: Ipmin = -Ipmax from the current limit, then "if SOC >= SOCmax, Ipmin = 0; elseif
        // SOC <= SOCmin, Ipmax = 0". Start at SOC = SOCmin = 0.2 with P0 = 0 and order Pref = -0.5 (charge). Expected:
        // Ipmax = 0 while SOC <= SOCmin, Ipmin = -Imax = -1.2 throughout, Ipcmd -> Pord / V = -0.5 once the Tpord = 0.02 s lag has settled, and the SOC
        // rises at -Pe / T = 0.5 / 10 = 0.05 per second.
        ReecParameters rp = new()
        {
            Variant = ReecVariant.C, PfFlag = 0, VFlag = 1, QFlag = 1, PFlag = 0, PqFlag = 0,
            Vdip = 0.9, Vup = 1.1, Trv = 0.02, Dbd1 = -0.05, Dbd2 = 0.05, Kqv = 2.0, Iqh1 = 1.05, Iql1 = -1.05, Vref0 = 0.0,
            Tp = 0.05, Qmax = 0.5, Qmin = -0.5, Vmax = 1.1, Vmin = 0.9, Kqp = 0.0, Kqi = 0.1, Kvp = 0.0, Kvi = 40.0, Tiq = 0.02,
            DPmax = 99.0, DPmin = -99.0, Pmax = 1.0, Pmin = -1.0, Imax = 1.2, Tpord = 0.02,
            Vq = new[] { 0.0, 0.2, 0.5, 1.0 }, Iq = new[] { 1.2, 1.2, 1.2, 1.2 }, Vp = new[] { 0.0, 0.2, 0.5, 1.0 }, Ip = new[] { 1.2, 1.2, 1.2, 1.2 },
            T = 10.0, SocIni = 0.2, SocMin = 0.2, SocMax = 0.8,
        };
        const double dt = 0.001;
        var reec = new Reec(rp, 1.0, 0.0, 0.0, 0.0, 0.0);
        double pe = 0.0, t = 0.0, socAtHalf = double.NaN;
        ReecOutputs o = default;
        for (int k = 0; k < 1000; k++, t += dt)
        {
            if (k == 500) socAtHalf = reec.StateOfCharge;
            var u = new ReecInputs(1.0, pe, 0.0, -0.5, reec.QextInitial, 1.0);
            reec.UpdateDipState(t, 1.0);
            o = reec.Evaluate(u, t, dt);
            // At SOCmin only discharge stops; once charging lifts the SOC above SOCmin, Ipmax is the current limit again.
            if (reec.StateOfCharge <= 0.2) Assert.Equal(0.0, o.Ipmax); else Near(1.2, o.Ipmax, 1e-12, "Ipmax above SOCmin");
            Near(-1.2, o.Ipmin, 1e-12, "Ipmin at SOCmin");
            reec.Advance(u, o, dt);
            pe = o.Ipcmd * 1.0;
        }
        Near(-0.5, o.Ipcmd, 1e-12, "charging current");
        Assert.True(reec.StateOfCharge > 0.2);
        // From t = 0.5 s the lag error is below 0.5 x 0.95^500 (about 3e-12), so dSOC = 0.05 / s x 0.5 s.
        Near(0.025, reec.StateOfCharge - socAtHalf, 1e-10, "SOC rise over the last 0.5 s");
    }

    // ---- Small-signal RoCoF row: the inverter's own power ---------------------------------------------------------------

    [Fact]
    public void The_gfm_rocof_leg_includes_the_inverter_power()
    {
        // One machine (H = 5 s on 100 MVA, D = 0) at the slack and one grid-forming unit with no droop, tau = 0.05 s and
        // synthetic inertia S = x Base M_T / f0 with x = 1 (M_T = 2H = 10 s, Base 100 MVA, f0 50 Hz: S = 20 MW.s/Hz).
        // Exact state space: omega' = p / (Base M_T), rocof = f0 omega' = f0 p / (Base M_T), and
        // p' = (-p - S rocof) / tau = -(1 + x) p / tau. So the actuator pole is -(1 + 1) / 0.05 = -40 (it was -20 when the
        // rate row left out the inverter power), and the other two eigenvalues are the rigid-body zeros.
        var pars = new Dictionary<string, double>
        {
            [AssetParams.SynInertiaMwsPerHz] = 20.0,
            [AssetParams.DampingMwPerHz] = 0.0,
            [AssetParams.ActuateTauS] = 0.05,
        };
        var cls = new AssetClass("gfm-x1", AssetKind.GridFormingInverter, "gfm", pars, new Dictionary<string, AssetClassProvenance>());
        var model = new GridModel
        {
            BaseMva = 100,
            FrequencyHz = 50,
            Name = "smib-gfm",
            Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pv) },
            Generators = new[]
            {
                new Generator(1, 0.0, MBase: 100, H: 5.0),
                new Generator(2, 0.0, MBase: 100, DynamicsClass: "gfm-x1"),
            },
            Branches = new[] { new Branch(1, 2, 0.0, 0.1) },
            AssetDynamics = new AssetDynamicsModel(new AssetClassLibrary(new[] { cls }), new AssetBindingSet(Array.Empty<AssetBinding>())),
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);
        SmallSignalModel ssm = SmallSignalAssembler.FromSolvedCaseWithInverters(model, pf, new[] { 0.3, 0.3 });
        Assert.Equal(3, ssm.Order);
        Near(-40.0, ssm.A[2, 2], 1e-12, "actuator diagonal -(1 + x) / tau");
        Complex[] eig = GeneralEigen.Eigenvalues(ssm.A);
        Complex pole = eig.Single(s => s.Real < -1.0);
        Near(-40.0, pole.Real, 1e-9, "actuator pole");
        Near(0.0, pole.Imaginary, 1e-9, "actuator pole is real");
    }
}
