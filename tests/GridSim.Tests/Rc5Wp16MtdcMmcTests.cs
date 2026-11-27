using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.IO.Psse;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP16: MTDC1 (an RMS multi-terminal DC grid on the power flow's multi-terminal links), the averaged MMC (MMC1), the
/// opt-in VSC1 feedback of the DC voltage converter's current limit, and the re-check of ten DYR records. Verification is
/// analytical (each expected value is derived in the test) and against GridSim's power-flow MTDC solution. Not compared
/// with PSS/E or PowerFactory. Existing outputs are pinned with hashes taken from the 2.1.110 build before any WP16 edit.
/// </summary>
public sealed class Rc5Wp16MtdcMmcTests
{
    // ------------------------------------------------------------------------------------------------ pins

    private static string Hash(Action<BinaryWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true)) write(w);
        return Convert.ToHexString(SHA256.HashData(ms.ToArray()));
    }

    private static void Bits(BinaryWriter w, double v) => w.Write(BitConverter.DoubleToInt64Bits(v));

    private static string Digest(MultiMachineSubtransient.Result r, bool devices) => Hash(w =>
    {
        foreach (MultiMachineSubtransient.MachineTrace m in r.Machines)
            foreach (IReadOnlyList<double> s in new[] { m.DeltaRad, m.SpeedPu, m.EqpPu, m.EdpPu, m.EqppPu, m.EddpPu })
                foreach (double v in s) Bits(w, v);
        foreach (RmsConverterTrace c in r.Converters ?? Array.Empty<RmsConverterTrace>())
            foreach (IReadOnlyList<double> s in new[] { c.TerminalVoltagePu, c.ActivePowerPu, c.ReactivePowerPu })
                foreach (double v in s) Bits(w, v);
        if (devices)
            foreach (RmsDeviceTrace d in r.Devices ?? Array.Empty<RmsDeviceTrace>())
                foreach (double[] s in d.Samples)
                    foreach (double v in s) Bits(w, v);
    });

    private static PowerFlowResult SinkPf(GridModel m)
    {
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-12 });
        Assert.True(pf.Converged);
        return pf;
    }

    private static List<SubtransientInit.MachineData> SinkData() => new() { Gencls.Data(5, 0, 0.2), Gencls.Data(5, 0, 0.2) };

    private static RmsNetworkDeviceData[] SinkDevices() => new RmsNetworkDeviceData[]
    {
        new LccLinkDynamicsData("L1", new Lcc1Data(5, 18, 0.01, 0.02, 0.5, 0.8, 0.1, 0, 0, 20, 100, 50, 300, 150, 450, 300)),
        new VscLinkDynamicsData("V1", new Vsc1Data(0.05, 0.02, 1.2, 100)),
        new SvcDeviceData("svc1", new Csvgn1Data(25, 0, 0, 0, 0, 0.05, 0, 1, 0, 50, 100)),
        new StatcomDeviceData("st1", new Statcom1Data(100, 0.03, 0, 50, 0.01, 1.0, 1.0)),
    };

    /// <summary>The WP6 kitchen-sink case with every device model bound (VSC1 among them, its new flag left at false),
    /// through a load step and a fault at the rectifier bus.</summary>
    internal static string SinkWithDevicesDigest()
    {
        GridModel m = Rc5Wp6FactsHvdcDynamicsTests.Sink();
        PowerFlowResult pf = SinkPf(m);
        var data = new RmsLoadedMachineData(SinkData(), Array.Empty<DynamicLoadData>(), SinkDevices());
        var ev = new RmsEvent[] { new RmsLoadStep(0.1, 6, 0, 10), new RmsBusFault(0.2, 2), new RmsFaultClear(0.25, 2) };
        return Digest(RmsTransientStability.StudyEvents(m, pf, data, ev, stepSeconds: 0.005, durationSeconds: 1.0), devices: true);
    }

    /// <summary>The same case with no device data (the WP6 digest, first computed with the 2.1.082 build).</summary>
    internal static string SinkWithoutDevicesDigest()
    {
        GridModel m = Rc5Wp6FactsHvdcDynamicsTests.Sink();
        PowerFlowResult pf = SinkPf(m);
        var ev = new RmsEvent[] { new RmsLoadStep(0.1, 6, 0, 10) };
        return Digest(RmsTransientStability.StudyEvents(m, pf, SinkData(), ev, stepSeconds: 0.005, durationSeconds: 1.0), devices: false);
    }

    /// <summary>The WP6 test 9 VSC link (converter 1 draws 80 MW, converter 2 holds 300 kV, Imax 1.1 pu) driven through
    /// both current limits: converter 1 at 0.5 pu from step 50 to 150 (its Id clipped, as in WP6 test 9) and converter 2
    /// at 0.5 pu from step 100 to 200 under AC voltage control (Kv 2, so its reactive current rises toward 1 pu).</summary>
    internal static string VscLimitTraceDigest(Func<Vsc1Data, Vsc1Data>? with = null)
    {
        var a = new VscConverter(1, VscDcControl.Power, VscAcControl.PowerFactor, 80, 1.0, 100, 0.5, 50, 0, 0, 1, 500, -500);
        var b = new VscConverter(2, VscDcControl.DcVoltage, VscAcControl.PowerFactor, 300, 1.0, 100, 0.5, 50, 0, 0, 1, 500, -500);
        var link = new VscDcLink("VV", 2, a, b);
        var dc = VscDcLinks.SolveDc(link);
        var pf = new VscLinkResult("VV", dc.CurrentKa * 1000, new VscConverterResult(1, dc.FirstMw, 0, dc.FirstDcKv, dc.FirstLossMw, 80, false),
            new VscConverterResult(2, dc.SecondMw, 0, dc.SecondDcKv, dc.SecondLossMw, double.NaN, false));
        Vsc1Data data = new Vsc1Data(0.05, 0.02, 1.1, 100) { SecondKv = 2.0 };
        if (with is not null) data = with(data);
        var s = new VscLinkState(new VscLinkPlan(link, pf, data, 100, Complex.One, Complex.One), null);
        const double dt = 1e-3;
        return Hash(w =>
        {
            for (int n = 0; n <= 300; n++)
            {
                double t = n * dt;
                Complex v1 = n >= 50 && n < 150 ? new Complex(0.5, 0) : Complex.One;
                Complex v2 = n >= 100 && n < 200 ? new Complex(0.5, 0) : Complex.One;
                Complex i1 = s.TrialCurrent(0, v1), i2 = s.TrialCurrent(1, v2);
                foreach (double v in new[] { s.PowerFiltered, s.DcTerminalPower, s.DcCurrentKa, i1.Real, i1.Imaginary, i2.Real, i2.Imaginary }) Bits(w, v);
                s.Advance(t, v1, v2, dt);
            }
        });
    }

    private static string Raw() => PsseLccTests.Case().Replace("0 / END OF GENERATOR DATA",
        "2, 'S', 0, 0, 50, -50, 1, 0, 100, 0, 1, 0, 0, 1, 1, 100, 0, 0, 1, 1\n0 / END OF GENERATOR DATA", StringComparison.Ordinal);

    /// <summary>The ten records WP16 re-checked, in the form the reader meets them (on unit S at bus 2, or led by a DC line
    /// name), with the full refusal each gives with the extended library, without and with unmodelled dynamics allowed.</summary>
    internal static string DyrRefusalsDigest() => Hash(w =>
    {
        const string gencls = "  1 'GENCLS' 1  5 0 /\n";
        foreach (string name in new[] { "SVSMO1", "SVSMO2", "SVSMO3", "CSVGN3", "CSVGN4", "CSVGN5", "CSVGN6", "CSTCON", "CSTATT", "VSCDCT" })
        {
            string rec = name == "VSCDCT" ? "'VDC 1' 'VSCDCT' 1 2 3 /\n" : $"  2 '{name}' S  1 2 3 4 5 6 7 8 9 10 11 /\n";
            foreach (bool allow in new[] { false, true })
            {
                string text;
                try
                {
                    PsseDyrResult r = PsseDyrReader.Read(gencls + rec, PsseRawParser.ParseWithDiagnostics(Raw(), "wp16"), "wp16",
                        new PsseDyrOptions { ExtendedLibrary = true, AllowUnmodelled = allow });
                    text = "ok|" + string.Join("|", r.Notes) + "|" + r.Facts.Count + "|" + r.NetworkDevices.Count;
                }
                catch (InvalidDataException ex) { text = "refused|" + Rc5LineEndings.ToCrLf(ex.Message); }
                w.Write(name + (allow ? "+allow" : "") + "=" + text);
            }
        }
    });

    // Pins taken with the 2.1.110 build (4056837d) before any WP16 edit: the test project alone was built against that
    // build's GridSim.Core.dll (version 2.1.110, built from the sources of commit 4056837d; dotnet build tests/GridSim.Tests
    // -p:BuildProjectReferences=false) with a probe that wrote these four digests to a file; the probe was then replaced
    // by these asserts. That dll's full InformationalVersion stamp and SHA-256 were not recorded, and the file no longer
    // exists; a Release build of commit 4056837d reproduces the pins. The no-device pin also equals the WP6 pin taken
    // with the 2.1.082 build (Rc5Wp6FactsHvdcDynamicsTests), which corroborates the method.
    private const string PinSinkWithDevices = "E3EE24262D45976D93D61206666C182CC57FD0BBD1ED6E0E0942A6FDD4BF2B76";
    private const string PinSinkWithoutDevices = "8E379F057FEA2B2C066E2ECFE48203510DB765F1449A8D4B2BA47CE8DF10B0D1";   // = the WP6 pin from 2.1.082
    private const string PinVscLimitTrace = "73C695CD61C5584D5C22D8078B40A4DF2E71810D612B52EEC24976EEA53FB60D";
    private const string PinDyrRefusals = "39B4640A0908C5AE7154DA3DBD873E794064A1444A577F20D265E1E8299A715E";

    private const double U = 1.1102230246251565e-16;   // 2^-53, the unit roundoff

    // ------------------------------------------------------------------------------------------------ cases

    private static readonly double K = 3.0 * Math.Sqrt(2.0) / Math.PI;

    /// <summary>A lossless converter for <see cref="MtdcLinks"/>: one bridge, no commutating impedance, tap 1 fixed, angles
    /// 0 to 90 degrees, its transformer base chosen so its ideal no-load DC voltage is <paramref name="vd0Kv"/> at 1 pu AC.</summary>
    private static MtdcConverter Conv(int bus, int dcBus, double setpoint, double vd0Kv) =>
        new(bus, 1, 90, 0, 0, 0, vd0Kv / K, 1, 1, 1, 1, 0, setpoint, dcBus);

    /// <summary>Test 2's link on a 500 kV DC base and a 100 MVA system base: DC bus 1 under the voltage converter (AC bus
    /// 2, 500 kV at 1 pu AC), DC bus 2 under a 50 MW power order (AC bus 3, injecting 0.5 pu into the DC grid), one line of
    /// 0.01 pu = 0.01 x 500^2 / 100 = 25 ohm.</summary>
    private static MtdcLink TwoTerminal() => new("M2", LccMode.Power, 2,
        new[] { Conv(2, 1, 500, 500), Conv(3, 2, 50, 600) },
        new[] { new MtdcDcBus(1, 2, 0, "D1"), new MtdcDcBus(2, 3, 0, "D2") },
        new[] { new MtdcDcLine(1, 2, 25) });

    /// <summary>Test 3's link: DC bus 1 under the voltage converter (AC bus 2), DC bus 2 drawing 20 MW (AC bus 3), DC bus 3
    /// sending 50 MW (AC bus 4); lines 3-1 and 3-2 of 1e-6 pu = 0.0025 ohm.</summary>
    private static MtdcLink ThreeTerminal() => new("M3", LccMode.Power, 2,
        new[] { Conv(2, 1, 500, 500), Conv(3, 2, -20, 600), Conv(4, 3, 50, 600) },
        new[] { new MtdcDcBus(1, 2, 0, "D1"), new MtdcDcBus(2, 3, 0, "D2"), new MtdcDcBus(3, 4, 0, "D3") },
        new[] { new MtdcDcLine(3, 1, 0.0025), new MtdcDcLine(3, 2, 0.0025) });

    private static Dictionary<int, double> Flat(MtdcLink l) => l.Converters.ToDictionary(c => c.Bus, _ => 1.0);

    private static MtdcGridState State(MtdcLink link, MtdcGridDynamicsData data, double baseMva = 100)
    {
        MtdcLinkResult pf = MtdcLinks.Solve(link, Flat(link));
        var plan = new MtdcGridPlan(link, pf, data, baseMva, link.Converters.Select(_ => Complex.One).ToArray());
        return new MtdcGridState(plan, null);
    }

    private static void Run(MtdcGridState s, double dt, int steps, Action<int>? each = null)
    {
        var v = Enumerable.Repeat(Complex.One, s.Plan.Controls.Length).ToArray();
        for (int n = 0; n < steps; n++)
        {
            each?.Invoke(n);
            s.Advance(n * dt, v, dt);
        }
    }

    // ------------------------------------------------------------------------------------------------ 1. energy ramp

    /// <summary>
    /// One DC bus, H = 0.01 s, W0 = 1, a constant 0.1 pu into it and no lines: H dW/dt = 0.1, so W(t) = 1 + 10 t and
    /// Vdc(0.01 s) = sqrt(1.1) = 1.0488088481701516. A constant right-hand side makes the step exact in exact arithmetic
    /// (backward and forward Euler alike), so the error is rounding only. Bound: each step adds dt P / H, whose inputs are
    /// three rounded decimals and two operations (at most 5u relative, on a total rise of 0.1), and rounds the sum (at most
    /// half an ulp of W below 2, u x 2 = 2.2e-16, so 100 x 1.1e-16 = 1.1e-14 absolute in W). sqrt halves a relative error and
    /// adds its own rounding u. The reference has two roundings of its own: 1.1 is stored with a relative error of at most
    /// u, which sqrt halves (u / 2), and Math.Sqrt rounds once more (u). So
    /// |dV| / V &lt;= (1.1e-14 + 0.1 x 5u) / (2 x 1.1) + u + u / 2 + u = 5.03e-15 + 2.5u = 5.30e-15. The spec's 1e-15 is not
    /// provable this way; 5.4e-15 is asserted (the observed error is printed on failure).
    /// </summary>
    [Fact]
    public void A_constant_power_imbalance_ramps_the_stored_energy_linearly()
    {
        var net = new MtdcDcNetwork(new[] { 0.01 }, new double[1, 1], new[] { false }, new[] { 1.0 }, "t1");
        for (int n = 0; n < 100; n++) net.Step(new[] { 0.1 }, 1e-4);
        double want = Math.Sqrt(1.1);
        Assert.Equal(1.0488088481701516, want);
        Assert.True(Math.Abs(net.V(0) - want) <= 5.4e-15 * want, $"V {net.V(0):R} vs {want:R}");

        // With powers of two everything is exact: H = 1/64 s, P = 1/8 pu, dt = 2^-10 s add exactly 2^-7 per step, so
        // W_n = 1 + n / 128 bit for bit and V_n is the correctly rounded sqrt of it (IEEE sqrt).
        var exact = new MtdcDcNetwork(new[] { 1.0 / 64 }, new double[1, 1], new[] { false }, new[] { 1.0 }, "t1b");
        for (int n = 1; n <= 128; n++)
        {
            exact.Step(new[] { 0.125 }, 1.0 / 1024);
            Assert.Equal(1 + n / 128.0, exact.W(0));
            Assert.Equal(Math.Sqrt(1 + n / 128.0), exact.V(0));
        }
        Assert.Equal(Math.Sqrt(2.0), exact.V(0));
    }

    // ------------------------------------------------------------------------------------------------ 2. two terminals

    /// <summary>
    /// DC bus 1 held at 1.0 pu, DC bus 2 sending 0.5 pu through R = 0.01 pu. With I the line current, V2 I = 0.5 and
    /// V2 = 1 + 0.01 I, so V2^2 - V2 - 0.005 = 0: V2 = (1 + sqrt(1.02)) / 2 = 1.004975246918104, I = 0.4975246918103898,
    /// bus 1 receives V1 I = 0.4975246918103898 pu and the line loses 0.5 - 0.4975246918103898 = 0.002475308189610226 pu.
    /// </summary>
    [Fact]
    public void Two_terminals_settle_on_the_analytical_dc_line_state()
    {
        double v2 = (1 + Math.Sqrt(1.02)) / 2, i = (v2 - 1) / 0.01;
        Assert.True(Math.Abs(v2 - 1.004975246918104) <= 2 * U);
        Assert.True(Math.Abs(i - 0.4975246918103898) <= 1e-13);
        Assert.True(Math.Abs(0.5 - i - 0.002475308189610226) <= 1e-13);
        Assert.True(Math.Abs(0.01 * i * i - 0.002475308189610226) <= 1e-15);

        // The power-flow MTDC solution of the same case (the link in kV, ohm and MW). Bound: its DC voltages stop moving by
        // 1e-12 (1 + 500) kV, 1e-12 pu, so 1e-10 leaves margin.
        MtdcLinkResult pf = MtdcLinks.Solve(TwoTerminal(), Flat(TwoTerminal()));
        Assert.True(Math.Abs(pf.DcBusKv[0] / 500 - 1) <= 1e-12, $"V1 {pf.DcBusKv[0]:R}");
        Assert.True(Math.Abs(pf.DcBusKv[1] / 500 - v2) <= 1e-10, $"V2 {pf.DcBusKv[1]:R}");
        Assert.True(Math.Abs(pf.Converters[0].DrawnMw / 100 + i) <= 1e-10, $"P1 {pf.Converters[0].DrawnMw:R}");
        Assert.True(Math.Abs(pf.Converters[1].DrawnMw / 100 - 0.5) <= 1e-10);

        // The RMS grid started on that solution stays on it for 2 s (converter 2's 100 uF: H = 0.125 s).
        var data = new MtdcGridDynamicsData("M2", new[]
        {
            new MtdcTerminalControl(2, MtdcControlMode.DcVoltage, 1.5, 100),
            new MtdcTerminalControl(3, MtdcControlMode.Power, 1.5, 100) { CapacitanceMicroFarads = 100, Tp = 0.02 },
        }) { DcBaseKv = 500 };
        MtdcGridState s = State(TwoTerminal(), data);
        Assert.Equal(0.125, s.Plan.H[1], 15);
        Run(s, 1e-3, 2000);
        Assert.True(Math.Abs(s.Dc.V(1) - v2) <= 1e-10, $"RMS V2 {s.Dc.V(1):R}");
        Assert.True(Math.Abs(-s.DcPower(0) - i) <= 1e-10, $"bus 1 receives {-s.DcPower(0):R}");
        Assert.True(Math.Abs(s.DcPower(1) - 0.5) <= 1e-10);

        // The same network started off its equilibrium (W2 = 1, H2 = 0.01 s) reaches it: the backward Euler step's fixed
        // point is the steady state P = P_dc exactly, reached to the Newton stop (1e-14 in W) and rounding.
        var g = new double[2, 2] { { 100, -100 }, { -100, 100 } };
        var net = new MtdcDcNetwork(new[] { 0.0, 0.01 }, g, new[] { true, false }, new[] { 1.0, 1.0 }, "t2");
        for (int n = 0; n < 2000; n++) net.Step(new[] { 0.0, 0.5 }, 1e-3);
        Assert.True(Math.Abs(net.V(1) - v2) <= 1e-10, $"V2 {net.V(1):R}");
        Assert.True(Math.Abs(-net.PowerOut(0) - i) <= 1e-10, $"bus 1 receives {-net.PowerOut(0):R}");
        Assert.Equal(1.0, net.V(0));
    }

    // ------------------------------------------------------------------------------------------------ 3 and 4. droop

    private static MtdcGridDynamicsData Droop(bool step) => new("M3", new[]
    {
        new MtdcTerminalControl(2, MtdcControlMode.Droop, 1.5, 100) { DroopGain = 20, Tp = 0.01, CapacitanceMicroFarads = 100 },
        new MtdcTerminalControl(3, MtdcControlMode.Droop, 1.5, 100) { DroopGain = 10, Tp = 0.01, CapacitanceMicroFarads = 100 },
        new MtdcTerminalControl(4, MtdcControlMode.Power, 1.5, 100)
        {
            CapacitanceMicroFarads = 100,
            OrderSteps = step ? new[] { new RmsOrderStep(0.1, 80) } : Array.Empty<RmsOrderStep>(),
        },
    }) { DcBaseKv = 500 };

    /// <summary>
    /// Two droop converters (Kd1 = 20, Kd2 = 10) and a power converter stepping by +0.3 pu (50 to 80 MW) on lines of
    /// R = 1e-6 pu. Each droop converter's power into the grid changes by -Kd_i dV_i and together they take the 0.3 pu:
    /// with the bus voltages equal, (Kd1 + Kd2) dV = 0.3, dV = +0.01, converter 1 takes 0.2 and converter 2 takes 0.1 (ratio
    /// Kd1 / Kd2 = 2). Bound: the extra line currents (0.2 and 0.1 pu from bus 3) part the bus voltages by at most
    /// 0.2 x 1e-6 = 2e-7 pu, which moves converter 1's share by at most Kd1 x 2e-7 = 4e-6 pu and dV by less; the extra line
    /// loss is R I^2 &lt; 1e-7 pu. So 1e-5 holds on every share and on dV.
    /// </summary>
    [Fact]
    public void Droop_converters_share_a_power_step_in_the_ratio_of_their_gains()
    {
        MtdcGridState s = State(ThreeTerminal(), Droop(step: true));
        double p1 = s.DcPower(0), p2 = s.DcPower(1), v0 = s.Dc.V(2);
        Assert.True(Math.Abs(p2 + 0.2) <= 1e-10 && Math.Abs(s.DcPower(2) - 0.5) <= 1e-10);
        Run(s, 1e-3, 3000);
        Assert.True(Math.Abs(s.DcPower(2) - 0.8) <= 1e-12, $"P3 {s.DcPower(2):R}");
        double d1 = s.DcPower(0) - p1, d2 = s.DcPower(1) - p2, dv = s.Dc.V(2) - v0;
        Assert.True(Math.Abs(d1 + 0.2) <= 1e-5, $"dP1 {d1:R}");
        Assert.True(Math.Abs(d2 + 0.1) <= 1e-5, $"dP2 {d2:R}");
        Assert.True(Math.Abs(dv - 0.01) <= 1e-5, $"dV {dv:R}");
        // The lines now carry 0.5 and 0.3 pu out of bus 3, so the buses sit within 0.5 x 1e-6 pu of it.
        for (int b = 0; b < 3; b++) Assert.True(Math.Abs(s.Dc.V(b) - s.Dc.V(2)) <= 5e-7 * 1.01, $"bus {b + 1}");
    }

    /// <summary>
    /// The unstepped case of test 3 holds every state for 1 s. What it starts off by is rounding, from two sources.
    /// (a) The power flow: the voltage converter's current there is a sum of conductance-times-voltage terms (400 kA/kV x
    /// 500 kV on a 0.0025 ohm line) that cancel, so it can be off by 2 x 2u x 400 x 500 x 2 kA = 1.78e-10 kA, which is
    /// 1.78e-10 x 500 / 100 = 8.9e-10 pu of power. (b) The per-unit DC voltages: each Vdc0 = kV / 500 is rounded by up to u,
    /// and a line of y = 1e6 pu turns a difference of 2u in its end voltages into 2.2e-10 pu of current; bus 3 has two
    /// lines, 4.4e-10 pu. Together at most 1.33e-9 pu. The droop converters take up that imbalance between them; the loop
    /// is underdamped (2 sum(H) Tp s^2 + 2 sum(H) s + sum(Kd) with sum(H) = 0.375 s, Tp = 0.01 s, sum(Kd) = 30: damping
    /// ratio 0.79, overshoot 1.7 %), so a power moves by at most 1.017 x 1.33e-9 = 1.36e-9 pu (1.5e-9 asserted) and the DC
    /// voltages by at most 1.5e-9 / (Kd1 + Kd2) = 5e-11 pu. The spec's 1e-12 for every state is not reachable on this
    /// stiff line; it is asserted on test 2's 25 ohm line, where (a) is 2 x 2u x 0.04 x 500 x 2 kA x 500 / 100 =
    /// 8.9e-14 pu and (b) is 100 pu x 2u = 2.2e-14 pu, 1.1e-13 together.
    /// </summary>
    [Fact]
    public void An_unstepped_dc_grid_starts_flat_and_stays_there()
    {
        MtdcGridState s = State(ThreeTerminal(), Droop(step: false));
        Run(s, 1e-3, 1000, _ => s.Record());
        RmsDeviceTrace t = s.Trace;
        Assert.Equal(1000, t.Samples.Count);
        Assert.Equal(new[] { "vdc_pu_1", "vdc_pu_2", "vdc_pu_3", "p_dc_pu_2", "q_state_2", "p_dc_pu_3", "q_state_3", "p_dc_pu_4", "q_state_4" }, t.StateNames);
        for (int k = 0; k < t.Samples.Count; k++)
            for (int j = 0; j < t.StateNames.Count; j++)
            {
                double bound = t.StateNames[j].StartsWith("vdc", StringComparison.Ordinal) ? 5e-11 : t.StateNames[j].StartsWith("p_dc", StringComparison.Ordinal) ? 1.5e-9 : 1e-12;
                Assert.True(Math.Abs(t.Samples[k][j] - t.Samples[0][j]) <= bound, $"{t.StateNames[j]} moved by {t.Samples[k][j] - t.Samples[0][j]:R} at sample {k}");
            }

        var data = new MtdcGridDynamicsData("M2", new[]
        {
            new MtdcTerminalControl(2, MtdcControlMode.Droop, 1.5, 100) { DroopGain = 20, Tp = 0.01, CapacitanceMicroFarads = 100 },
            new MtdcTerminalControl(3, MtdcControlMode.Power, 1.5, 100) { CapacitanceMicroFarads = 100, Tp = 0.02 },
        }) { DcBaseKv = 500 };
        MtdcGridState s2 = State(TwoTerminal(), data);
        Run(s2, 1e-3, 1000, _ => s2.Record());
        RmsDeviceTrace t2 = s2.Trace;
        for (int k = 0; k < t2.Samples.Count; k++)
            for (int j = 0; j < t2.StateNames.Count; j++)
                Assert.True(Math.Abs(t2.Samples[k][j] - t2.Samples[0][j]) <= 1e-12, $"{t2.StateNames[j]} moved by {t2.Samples[k][j] - t2.Samples[0][j]:R} at sample {k}");
    }

    // ------------------------------------------------------------------------------------------------ 5. MMC

    /// <summary>
    /// N = 200, C_SM = 10 mF: C_eq = 6 x 0.01 / 200 = 0.3 mF; on a 640 kV DC base and a 1000 MVA system base
    /// H = 0.5 x 0.3e-3 x (640e3)^2 / 1e9 = 0.06144 s. The ramp of test 1 with this H is W = 1 + 0.1 t / 0.06144; the bound
    /// is test 1's derivation with the rise 0.1 x 0.01 / 0.06144 = 0.0163 and H itself a few roundings off (5u relative on a
    /// rise of 0.0163), well inside 1e-14.
    /// </summary>
    [Fact]
    public void An_mmc_adds_its_equivalent_capacitance_to_its_dc_bus()
    {
        var mmc = new Mmc1Data(200, 0.01, 0.05, 0, 0, 0);
        Assert.True(Math.Abs(mmc.EquivalentCapacitanceFarads - 3e-4) <= 2 * U * 3e-4);
        Assert.False(mmc.EnergyControl);
        // The link of test 2 on a 640 kV base and 1000 MVA, the power converter an MMC with no other capacitance.
        var data = new MtdcGridDynamicsData("M2", new[]
        {
            new MtdcTerminalControl(2, MtdcControlMode.DcVoltage, 1.5, 1000),
            new MtdcTerminalControl(3, MtdcControlMode.Power, 1.5, 1000) { Mmc = mmc },
        }) { DcBaseKv = 640 };
        MtdcGridState s = State(TwoTerminal(), data, baseMva: 1000);
        double h = s.Plan.H[1];
        Assert.True(Math.Abs(h - 0.06144) <= 8 * U * 0.06144, $"H {h:R}");
        Assert.Equal(0.0, s.Plan.H[0]);
        // The arm reactance: w L_arm / 2 = 2 pi 50 x 0.025 = 7.854 ohm on the converter's AC base (600 / K kV, 1000 MVA).
        double kv = 600 / K;
        Assert.True(Math.Abs(s.Plan.Xarm[1] - 2 * Math.PI * 50 * 0.025 / (kv * kv / 1000)) <= 4 * U * s.Plan.Xarm[1]);

        var net = new MtdcDcNetwork(new[] { h }, new double[1, 1], new[] { false }, new[] { 1.0 }, "t5");
        for (int n = 0; n < 100; n++) net.Step(new[] { 0.1 }, 1e-4);
        double want = Math.Sqrt(1 + 0.1 * 0.01 / 0.06144);
        Assert.True(Math.Abs(net.V(0) - want) <= 1e-14 * want, $"V {net.V(0):R} vs {want:R}");
    }

    /// <summary>The MMC energy controller (Kp_W = 20 /s, Ki_W = 100 /s^2, Tw = 0.01 s) starts on E0 = C_eq Vbase^2 / (2 Sb) x
    /// W0 and returns to it after an order step: at steady state the integral holds E - E0 = 0 and P_dc = P_ac.</summary>
    [Fact]
    public void The_mmc_energy_controller_returns_its_stored_energy_to_the_start()
    {
        var data = new MtdcGridDynamicsData("M2", new[]
        {
            new MtdcTerminalControl(2, MtdcControlMode.DcVoltage, 1.5, 100),
            new MtdcTerminalControl(3, MtdcControlMode.Power, 1.5, 100)
            {
                CapacitanceMicroFarads = 100, Tp = 0.02, Mmc = new Mmc1Data(200, 0.01, 0, 20, 100, 0.01),
                OrderSteps = new[] { new RmsOrderStep(0.05, 60) },
            },
        }) { DcBaseKv = 500 };
        MtdcGridState s = State(TwoTerminal(), data);
        double e0 = s.Plan.E0[1];
        // C_eq = 0.3 mF on 500 kV, 100 MVA: E0 = C_eq Vbase^2 / (2 Sb) W0 = 0.3e-3 x 2.5e11 / 2e8 = 0.375 pu s at W0.
        Assert.True(Math.Abs(e0 - 0.375 * s.Dc.V(1) * s.Dc.V(1)) <= 1e-14);
        Assert.Equal(0.125, s.Plan.H[1], 15);   // the energy is kept apart from the DC bus
        double peak = 0;
        Run(s, 1e-3, 5000, _ => peak = Math.Max(peak, Math.Abs(s.StoredEnergy(1) - e0)));
        Assert.True(peak > 1e-4, "the step moved the stored energy");
        Assert.True(Math.Abs(s.StoredEnergy(1) - e0) <= 1e-9, $"E {s.StoredEnergy(1):R} vs {e0:R}");
        Assert.True(Math.Abs(s.DcPower(1) - 0.6) <= 1e-9, $"P_dc {s.DcPower(1):R}");
    }

    // ------------------------------------------------------------------------------------------------ 6. VSC1 feedback

    /// <summary>
    /// The WP6 test 9 limit (Imax = 1.1, Iq = 1.0 at |V| = 0.5, so |Id| &lt;= sqrt(1.21 - 1) = 0.45825756949558394), here on
    /// the DC voltage converter: the power converter draws 80 MW at 1 pu, the voltage converter sits at 0.5 pu with an
    /// instant reactive loop (Kv = 2, Tq = 0: Iq = 2 x (1 - 0.5) = 1.0). The power converter's own limit has been fed back
    /// since 2.1.099 (B17) by default, so the opt-in flag closes the other gap: with it the voltage converter delivers
    /// 0.5 x 0.45825756949558394 = 0.22912878474779197 pu, and the power converter draws that plus the losses: I from
    /// 300 I - max(0.05, 0.1 + 0.5 |I|) = 22.912878474779197 MW, then (300 + 2 I) I + max(0.05, 0.1 + 0.5 |I|).
    /// Without the flag the trace is the 2.1.110 one bit for bit.
    /// </summary>
    [Fact]
    public void Vsc1_can_feed_the_dc_voltage_converter_limit_back_to_the_dc_side()
    {
        Assert.Equal(PinVscLimitTrace, VscLimitTraceDigest());
        Assert.Equal(PinVscLimitTrace, VscLimitTraceDigest(d => d with { DcFollowsLimitedPower = false }));
        Assert.NotEqual(PinVscLimitTrace, VscLimitTraceDigest(d => d with { DcFollowsLimitedPower = true }));

        var a = new VscConverter(1, VscDcControl.Power, VscAcControl.PowerFactor, 80, 1.0, 100, 0.5, 50, 0, 0, 1, 500, -500);
        var b = new VscConverter(2, VscDcControl.DcVoltage, VscAcControl.PowerFactor, 300, 1.0, 100, 0.5, 50, 0, 0, 1, 500, -500);
        var link = new VscDcLink("VV", 2, a, b);
        var dc = VscDcLinks.SolveDc(link);
        var pf = new VscLinkResult("VV", dc.CurrentKa * 1000, new VscConverterResult(1, dc.FirstMw, 0, dc.FirstDcKv, dc.FirstLossMw, 80, false),
            new VscConverterResult(2, dc.SecondMw, 0, dc.SecondDcKv, dc.SecondLossMw, double.NaN, false));
        var s = new VscLinkState(new VscLinkPlan(link, pf, new Vsc1Data(0.05, 0, 1.1, 100) { SecondKv = 2.0, DcFollowsLimitedPower = true }, 100,
            Complex.One, Complex.One), null);
        var half = new Complex(0.5, 0);
        s.Advance(0, Complex.One, half, 1e-3);
        double pV = 0.5 * 0.45825756949558394;
        Assert.True(Math.Abs(s.DcTerminalPower - pV) <= 4 * U, $"voltage side {s.DcTerminalPower:R}");
        Complex iV = s.TrialCurrent(1, half);
        Assert.True(Math.Abs(iV.Magnitude - 1.1) <= 4 * U);
        Assert.True(Math.Abs(0.5 * iV.Real - pV) <= 4 * U);
        // The current: the linear loss branch, I > 0: (300 - 0.5) I = 22.912878474779197 + 0.1.
        double ika = (pV * 100 + 0.1) / 299.5;
        Assert.True(0.1 + 0.5 * ika >= 0.05);
        double drawn = (300 + 2 * ika) * ika + Math.Max(0.05, 0.1 + 0.5 * ika);
        Assert.True(Math.Abs(s.DcCurrentKa - ika) <= 1e-15, $"I {s.DcCurrentKa:R}");
        Complex iP = s.TrialCurrent(0, Complex.One);
        Assert.True(Math.Abs(iP.Real + drawn / 100) <= 1e-14, $"power converter {iP.Real:R} vs {-drawn / 100:R}");
        // The DC side balances: what the power converter draws less the line loss and both converter losses.
        double loss = 2 * ika * ika + Math.Max(0.05, 0.1 + 0.5 * ika) + Math.Max(0.05, 0.1 + 0.5 * ika);
        Assert.True(Math.Abs(drawn - loss - pV * 100) <= 1e-11);
        // Voltage restored: the cap is released and the order comes back through its lag.
        s.Advance(1e-3, Complex.One, Complex.One, 1e-3);
        Assert.True(s.TrialCurrent(0, Complex.One).Real < -0.79);
    }

    /// <summary>
    /// The voltage converter's limit held for 60 steps (V2 = 0.5 pu throughout, the setting of the test above, the power
    /// order 80 MW far above what the limit lets through). On every step the cap must stand: the voltage converter delivers
    /// pV = 0.5 x 0.45825756949558394 pu, the power converter draws pV plus the losses, and the DC side balances (drawn less
    /// line and converter losses equals pV, to 1e-11 MW as above). The 2.1.111 code released the cap on about two steps in
    /// three under a sustained limit, because the previous step's cap fed the next step's limit test.
    /// <para>The bounds, with u = 2^-53. (1) pV, 4u: the code forms Id = sqrt(fl(1.1 x 1.1) - 1) with ImaxSys =
    /// 1.1 x 100 / 100 = the double 1.1 and Iq = 2 x (1 - 0.5) = 1 exactly; the literal is sqrt(fl(1.21) - 1).
    /// fl(1.1 x 1.1) = 1.2100000000000002 is one ulp (2u) above fl(1.21), and both subtractions of 1 are exact, so the two
    /// square-root arguments differ by 2u. Near 0.21 that moves sqrt by 2u / (2 x 0.458) = 2.18u, and each sqrt rounds by at
    /// most half an ulp in [0.25, 0.5), u / 4: |dId| &lt;= 2.68u. Multiplying by 0.5 is exact: |dpV| &lt;= 1.34u (observed
    /// 1.25u), inside 4u. (2) I, 1e-15 kA: the code and the test both compute (100 pV + 0.1) / 299.5 (A = 100 kW / 1000
    /// rounds to the same double as 0.1; 300 - 0.5 is exact). The pV difference gives 100 x 1.34u / 299.5 = 5e-17 kA, and
    /// each side's three roundings (times, plus, divide) add at most 3u x 0.0768 = 2.6e-17: |dI| &lt;= 1.0e-16, a tenth of
    /// the bound. (3) The power converter's current, 1e-14 pu: with no clipping (0.232 &lt; 1.1), Iq = 0 and V = 1 it is
    /// -drawn / 100 exactly as stored. drawn = (300 + 2 I) I + max(0.05, 0.1 + 0.5 I) has slope 300.8 MW/kA, so dI gives
    /// 3.0e-14 MW; each side's five roundings on about 23.2 MW add at most 5u x 23.2 = 1.3e-14 MW, two sides 2.6e-14 MW;
    /// /100 and its rounding: |d| &lt;= 5.6e-16 + 2 x 0.232u = 6.1e-16 pu, a sixteenth of the bound. The DC balance's 1e-11 MW
    /// is looser still (the identity drawn - losses = 100 pV holds exactly for exact I, and the roundings are a few u on
    /// 300 x 0.0768 = 23 MW, below 1e-13 MW).</para>
    /// </summary>
    [Fact]
    public void Vsc1_holds_the_voltage_converter_limit_on_every_step_while_it_binds()
    {
        var a = new VscConverter(1, VscDcControl.Power, VscAcControl.PowerFactor, 80, 1.0, 100, 0.5, 50, 0, 0, 1, 500, -500);
        var b = new VscConverter(2, VscDcControl.DcVoltage, VscAcControl.PowerFactor, 300, 1.0, 100, 0.5, 50, 0, 0, 1, 500, -500);
        var link = new VscDcLink("VV", 2, a, b);
        var dc = VscDcLinks.SolveDc(link);
        var pf = new VscLinkResult("VV", dc.CurrentKa * 1000, new VscConverterResult(1, dc.FirstMw, 0, dc.FirstDcKv, dc.FirstLossMw, 80, false),
            new VscConverterResult(2, dc.SecondMw, 0, dc.SecondDcKv, dc.SecondLossMw, double.NaN, false));
        var s = new VscLinkState(new VscLinkPlan(link, pf, new Vsc1Data(0.05, 0, 1.1, 100) { SecondKv = 2.0, DcFollowsLimitedPower = true }, 100,
            Complex.One, Complex.One), null);
        var half = new Complex(0.5, 0);
        double pV = 0.5 * 0.45825756949558394;
        double ika = (pV * 100 + 0.1) / 299.5;
        double drawn = (300 + 2 * ika) * ika + Math.Max(0.05, 0.1 + 0.5 * ika);
        double loss = 2 * ika * ika + 2 * Math.Max(0.05, 0.1 + 0.5 * ika);
        for (int n = 0; n < 60; n++)
        {
            s.Advance(n * 1e-3, Complex.One, half, 1e-3);
            Assert.True(Math.Abs(s.DcTerminalPower - pV) <= 4 * U, $"step {n}: voltage side {s.DcTerminalPower:R}");
            Assert.True(Math.Abs(s.DcCurrentKa - ika) <= 1e-15, $"step {n}: I {s.DcCurrentKa:R}");
            Complex iP = s.TrialCurrent(0, Complex.One);
            Assert.True(Math.Abs(iP.Real + drawn / 100) <= 1e-14, $"step {n}: power converter {iP.Real:R} vs {-drawn / 100:R}");
            Assert.True(Math.Abs(-iP.Real * 100 - loss - s.DcTerminalPower * 100) <= 1e-11, $"step {n}: DC balance");
        }
    }

    /// <summary>
    /// The droop loop is explicit (the order uses the start-of-step DC voltage, the DC power follows a step later), so on a
    /// DC bus on its own it is stable exactly for dt &lt; 2 H V0 / Kd (Jury test on z^2 - (1 + a) z + a + k (1 - a),
    /// k = dt Kd / (2 H V0), a = exp(-dt / Tp): the product of the roots a + k (1 - a) is below 1 exactly when k &lt; 1).
    /// Test 3's case: DC bus 1 carries Kd = 20 and 100 uF on a 500 kV, 100 MVA base, H = 0.125 s, so the limit is
    /// 2 x 0.125 x V0 / 20 = 0.0125 V0 s; DC bus 2's (Kd = 10) is twice that. A step at the limit is refused by name; a
    /// step just below runs. The reviewer's case (H = 0.3 s, Kd = 200, dt = 5 ms, k = 1.67) has a spectral radius of 1.12
    /// at Tp = 0.01 s and 1.29 at Tp = 0, both refused by this rule.
    /// </summary>
    [Fact]
    public void A_step_too_long_for_the_explicit_droop_loop_is_refused()
    {
        MtdcGridState s = State(ThreeTerminal(), Droop(step: false));
        double limit = 2 * s.Plan.H[0] * s.Plan.Vdc0[0] / 20;
        Assert.Equal(0.125, s.Plan.H[0], 15);
        Assert.Equal(limit, s.Plan.DroopStepLimitSeconds);
        Assert.Equal(1, s.Plan.DroopLimitDcBus);
        var v = Enumerable.Repeat(Complex.One, 3).ToArray();
        var ex = Assert.Throws<InvalidOperationException>(() => s.Advance(0, v, limit));
        Assert.Contains("too long for the droop at DC bus 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("2 H V0 / Kd", ex.Message, StringComparison.Ordinal);
        MtdcGridState ok = State(ThreeTerminal(), Droop(step: false));
        ok.Advance(0, v, limit * (1 - 1e-12));
        // No droop converter: no limit.
        MtdcGridState none = State(TwoTerminal(), new MtdcGridDynamicsData("M2", new[]
        {
            new MtdcTerminalControl(2, MtdcControlMode.DcVoltage, 1.5, 100),
            new MtdcTerminalControl(3, MtdcControlMode.Power, 1.5, 100) { CapacitanceMicroFarads = 100 },
        }) { DcBaseKv = 500 });
        Assert.Equal(double.PositiveInfinity, none.Plan.DroopStepLimitSeconds);
    }

    /// <summary>
    /// The droop stability boundary itself, run for 210 steps either side of it. One droop converter (Kd = 20, Tp = 0,
    /// 100 uF, so H = 0.125 s) on DC bus 2, joined to the voltage-held DC bus 1 by a 2.5e7 ohm line (1e4 pu, g = 1e-4 pu),
    /// so the bus is all but on its own; the power flow starts it at zero power and V0 = 1. A +0.1 MW order step at t = 0
    /// starts the motion.
    /// <para>The step map. With Tp = 0 (a = 0) the order of step n is P0 - Kd (V_(n-1) - V0), and the backward Euler step
    /// gives (1 + c) e_(n+1) = e_n - k e_(n-1) for the deviation e from the new equilibrium, k = dt Kd / (2 H V) and
    /// c = dt g / (2 H) (the implicit line). The roots of (1 + c) z^2 - z + k are complex, of modulus r = sqrt(k / (1 + c)),
    /// at angle theta with cos(theta) = 1 / (2 sqrt(k (1 + c))), about 60 degrees. So e_n = A r^n cos(theta n + phi), and the
    /// step differences d_n = V_(n+1) - V_n follow the same recurrence once the step has passed (n &gt;= 1).</para>
    /// <para>The bound. The phases theta n mod 180 degrees are three points about 60 degrees apart (gaps of at most 60.34
    /// degrees for these k), so among 12 consecutive samples one lies within 30.17 degrees of 0 and max |d| over the window
    /// is at least cos(30.17 deg) = 0.864 times A r^n at that sample, and at most A r^n at the window's largest r^n. The
    /// windows are d_5..d_16 and d_194..d_205. Growing: late &gt;= 0.864 A r^194, early &lt;= A r^16, so late/early &gt;=
    /// 0.864 r^178. Decaying: late &lt;= A r^194, early &gt;= 0.864 A r^16, so late/early &lt;= r^178 / 0.864.
    /// The constants: c = 0.0125 x 1e-4 / 0.25 = 5e-6. V moves by the order step over Kd, 0.001 / 20 = 5e-5 pu, and the
    /// growing oscillation stays within 3e-4 pu of V0 for 210 steps (1.01^105 = 2.8 times its first swing), so k lies within
    /// 3e-4 of its value at V0. k = 1.01: r^2 &gt;= 1.01 (1 - 3e-4) / (1 + 5e-6) = 1.00969, r^178 &gt;= 1.00969^89 = 2.359,
    /// bound 0.864 x 2.359 = 2.04: asserted &gt; 2.0. k = 0.99: r^2 &lt;= 0.99 (1 + 3e-4) = 0.9903, r^178 &lt;= 0.9903^89 = 0.420,
    /// bound 0.420 / 0.864 = 0.486: asserted &lt; 0.49. The k = 1.01 run bypasses the refusal by lifting the plan's limit (reflection); without that the
    /// step is refused, as it must be.</para>
    /// </summary>
    [Fact]
    public void The_droop_loop_decays_just_below_the_step_limit_and_grows_just_above_it()
    {
        static MtdcGridState Lone()
        {
            var link = new MtdcLink("M1", LccMode.Power, 2,
                new[] { Conv(2, 1, 500, 500), Conv(3, 2, 0, 600) },
                new[] { new MtdcDcBus(1, 2, 0, "D1"), new MtdcDcBus(2, 3, 0, "D2") },
                new[] { new MtdcDcLine(1, 2, 2.5e7) });
            return State(link, new MtdcGridDynamicsData("M1", new[]
            {
                new MtdcTerminalControl(2, MtdcControlMode.DcVoltage, 1.5, 100),
                new MtdcTerminalControl(3, MtdcControlMode.Droop, 1.5, 100)
                {
                    DroopGain = 20, Tp = 0, CapacitanceMicroFarads = 100, OrderSteps = new[] { new RmsOrderStep(0, 0.1) },
                },
            }) { DcBaseKv = 500 });
        }

        static double Ratio(MtdcGridState s, double dt)
        {
            var x = new List<double> { s.Dc.V(1) };
            var v = Enumerable.Repeat(Complex.One, 2).ToArray();
            for (int n = 0; n < 210; n++)
            {
                s.Advance(n * dt, v, dt);
                x.Add(s.Dc.V(1));
            }
            double Max(int from)
            {
                double m = 0;
                for (int n = from; n < from + 12; n++) m = Math.Max(m, Math.Abs(x[n + 1] - x[n]));
                return m;
            }
            return Max(194) / Max(5);
        }

        MtdcGridState below = Lone();
        Assert.Equal(0.125, below.Plan.H[1], 15);
        Assert.Equal(1.0, below.Plan.Vdc0[1], 12);
        double limit = below.Plan.DroopStepLimitSeconds;
        Assert.Equal(2 * below.Plan.H[1] * below.Plan.Vdc0[1] / 20, limit);
        double decay = Ratio(below, 0.99 * limit);
        Assert.True(decay < 0.49, $"k = 0.99: late/early {decay:R}");

        MtdcGridState above = Lone();
        var v = Enumerable.Repeat(Complex.One, 2).ToArray();
        Assert.Throws<InvalidOperationException>(() => above.Advance(0, v, 1.01 * limit));
        typeof(MtdcGridPlan).GetField("<DroopStepLimitSeconds>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(above.Plan, double.PositiveInfinity);
        double growth = Ratio(above, 1.01 * limit);
        Assert.True(growth > 2.0, $"k = 1.01: late/early {growth:R}");
    }

    // ------------------------------------------------------------------------------------------------ 7. DYR

    /// <summary>
    /// None of the ten records gains a second public source that agrees on every position, so all stay refused by name
    /// with their 2.1.110 reasons, byte for byte. Checked (WP16): PowerWorld's transient model pages give orders for
    /// CSVGN3 (CSVGN1's ten values and VOV, an override voltage, not a second voltage input) and SVSMO3 (45 values with
    /// deadbands, I2t and MSS switching); NREL PowerSystems.jl's psse_dynamic_mapping.yaml maps none of the ten; OpenIPSL's
    /// PSSE banks hold CSVGN1 only; the PSS/E model library is not public text.
    /// </summary>
    [Fact]
    public void The_svc_statcom_and_vscdct_records_stay_refused_with_the_old_reasons()
    {
        Assert.Equal(PinDyrRefusals, DyrRefusalsDigest());
        foreach (string name in new[] { "SVSMO1", "SVSMO2", "SVSMO3", "CSVGN3", "CSVGN4", "CSVGN5", "CSVGN6", "CSTCON", "CSTATT" })
            Assert.NotNull(PsseDyrReader.FactsModels[name]);
        Assert.NotNull(PsseDyrReader.DcModels["VSCDCT"]);
    }

    // ------------------------------------------------------------------------------------------------ 8, 9. bits

    [Fact]
    public void Existing_rms_cases_give_the_old_bits()
    {
        Assert.Equal(PinSinkWithoutDevices, SinkWithoutDevicesDigest());
        Assert.Equal(PinSinkWithDevices, SinkWithDevicesDigest());
    }

    /// <summary>An AC system with test 2's link: bus 1 a stiff source (GENCLS, H = 1000 s, X'd = 0.1), lines of 0.05 pu
    /// to buses 2 and 3 where the converters sit.</summary>
    private static (GridModel Model, PowerFlowResult Pf) AcCase()
    {
        var m = new GridModel
        {
            Name = "wp16", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, BaseKv: 345), new Bus(2, BusType.Pq, BaseKv: 345), new Bus(3, BusType.Pq, BaseKv: 345) },
            Generators = new[] { new Generator(1, 0, Vg: 1.0, MBase: 1000, H: 1000, Name: "A") },
            Branches = new[] { new Branch(1, 2, 0, 0.05), new Branch(1, 3, 0, 0.05) },
            MtdcLinks = new[] { TwoTerminal() },
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-12 });
        Assert.True(pf.Converged);
        Assert.Null(pf.MtdcNote);
        return (m, pf);
    }

    private static RmsLoadedMachineData AcData() => new(new List<SubtransientInit.MachineData> { Gencls.Data(1000, 0, 0.1) },
        Array.Empty<DynamicLoadData>(), new RmsNetworkDeviceData[]
        {
            new MtdcGridDynamicsData("M2", new[]
            {
                new MtdcTerminalControl(2, MtdcControlMode.DcVoltage, 1.5, 100) { Kv = 1.0, Tq = 0.02 },
                new MtdcTerminalControl(3, MtdcControlMode.Power, 1.5, 100) { CapacitanceMicroFarads = 100, Tp = 0.02, OrderSteps = new[] { new RmsOrderStep(0.2, 40) } },
            }),
        });

    [Fact]
    public void An_mtdc_link_in_the_rms_study_starts_on_its_power_flow_and_runs_the_same_bits_every_time()
    {
        (GridModel m, PowerFlowResult pf) = AcCase();
        MtdcLinkResult r = pf.MtdcLinks![0];
        string Go(out MultiMachineSubtransient.Result res)
        {
            res = RmsTransientStability.StudyEvents(m, pf, AcData(), new RmsEvent[] { new RmsLoadStep(0.5, 2, 0, 5) }, stepSeconds: 0.005, durationSeconds: 1.0);
            return Digest(res, devices: true);
        }
        string want = Go(out MultiMachineSubtransient.Result run);
        Assert.True(run.ConverterIterationsConverged);
        Assert.Equal(2, run.Converters!.Count);
        // Each converter starts on the injection the power flow's AC model carried (bound as WP6's sink test).
        for (int k = 0; k < 2; k++)
        {
            Assert.True(Math.Abs(run.Converters[k].ActivePowerPu[0] + r.Converters[k].DrawnMw / 100) <= 1e-11, $"P0 {k}");
            Assert.True(Math.Abs(run.Converters[k].ReactivePowerPu[0] + r.Converters[k].AbsorbedMvar / 100) <= 1e-11, $"Q0 {k}");
        }
        RmsDeviceTrace d = Assert.Single(run.Devices!);
        Assert.Equal("MTDC1", d.Model);
        // Flat until the order step at 0.2 s, then converter 3 sends 0.4 pu.
        for (int k = 0; k < 40; k++) Assert.True(Math.Abs(d.Samples[k][1] - d.Samples[0][1]) <= 1e-10, $"V2 moved at {k}");
        Assert.True(Math.Abs(d.Samples[^1][4] - 0.4) <= 1e-6, $"P3 {d.Samples[^1][4]:R}");
        // Determinism: again, and in parallel.
        Assert.Equal(want, Go(out _));
        var par = new string[4];
        Parallel.For(0, par.Length, j => par[j] = Go(out _));
        Assert.All(par, x => Assert.Equal(want, x));
        // Without device data the link is left out of the RMS network, as before.
        MultiMachineSubtransient.Result plain = RmsTransientStability.StudyEvents(m, pf, new List<SubtransientInit.MachineData> { Gencls.Data(1000, 0, 0.1) }, new RmsEvent[] { new RmsLoadStep(0.5, 2, 0, 5) },
            stepSeconds: 0.005, durationSeconds: 1.0);
        Assert.Null(plain.Devices);
    }

    [Fact]
    public void Mtdc_binding_refuses_what_it_cannot_honour()
    {
        (GridModel m, PowerFlowResult pf) = AcCase();
        void Refused(string contains, params MtdcTerminalControl[] controls)
        {
            var data = new RmsLoadedMachineData(new List<SubtransientInit.MachineData> { Gencls.Data(1000, 0, 0.1) }, Array.Empty<DynamicLoadData>(),
                new RmsNetworkDeviceData[] { new MtdcGridDynamicsData("M2", controls) });
            var ex = Assert.ThrowsAny<ArgumentException>(() => SubtransientInit.FromSolvedCase(m, pf, data));
            Assert.Contains(contains, ex.Message, StringComparison.Ordinal);
        }
        var v = new MtdcTerminalControl(2, MtdcControlMode.DcVoltage, 1.5, 100);
        var p = new MtdcTerminalControl(3, MtdcControlMode.Power, 1.5, 100) { CapacitanceMicroFarads = 100 };
        Refused("has no control", v);
        Refused("given two controls", v, p, p);
        Refused("no converter on AC bus 9", v, p, new MtdcTerminalControl(9, MtdcControlMode.Power, 1.5, 100));
        Refused("no capacitance", v, p with { CapacitanceMicroFarads = 0 });
        Refused("above Imax", v, p with { ImaxPu = 0.1 });
        Refused("energy controller needs a Power or Droop", v with { Mmc = new Mmc1Data(10, 0.01, 0, 1, 0, 0) }, p);
        Refused("droop gain needs the Droop mode", v, p with { DroopGain = 5 });
        var ex = Assert.ThrowsAny<ArgumentException>(() => SubtransientInit.FromSolvedCase(m, pf, new RmsLoadedMachineData(
            new List<SubtransientInit.MachineData> { Gencls.Data(1000, 0, 0.1) }, Array.Empty<DynamicLoadData>(),
            new RmsNetworkDeviceData[] { new MtdcGridDynamicsData("M9", new[] { v, p }) })));
        Assert.Contains("has no multi-terminal DC link named 'M9'", ex.Message, StringComparison.Ordinal);
    }
}
