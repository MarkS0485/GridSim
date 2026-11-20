using System.Numerics;
using GridSim.Core.Dynamics.CoSim;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Emt.Partition;
using GridSim.Core.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-19: RMS-EMT co-simulation. One network: an ideal source, a lossless line (Zc, tau) and an R-L load, with a shunt resistance switched in at the
/// load. Three representations: (a) everything in EMT (Bergeron line), (b) the source and line as an RMS phasor equivalent handed to the EMT load through
/// <see cref="RmsEmtInterface"/>, (c) the phasor closed form. COMPARATOR-UNVERIFIED: no commercial tool is run.
/// </summary>
public sealed class R419CoSimTests(ITestOutputHelper output)
{
    private const double Vm = 10000.0, F = 50.0, Dt = 5e-5, Zc = 300.0, Tau = 5e-4, RP = 300.0, LP = 0.477, Rx = 60.0, TLoadStep = 0.2, TSourceStep = 0.3105;
    private static readonly double W = 2.0 * Math.PI * F;
    private static readonly Complex Source = new(0.0, -Vm);                // Vm sin(w t) = Re{-j Vm e^(jwt)}

    private static double Amp(double t) => Vm * (t >= TSourceStep - 1e-9 ? 1.1 : 1.0);

    // The load is a resistor in parallel with an inductor: at high frequency it is the resistance RP = Zc, so the line sees a matched end and the
    // trapezoidal solution has no undamped Nyquist ringing (a series R-L load would reflect high frequencies totally and ring for ever).
    private static Complex ZLoad => 1.0 / (1.0 / RP + 1.0 / new Complex(0, W * LP));

    private static (Complex Eth, Complex Zth) Thevenin()
    {
        double th = W * Tau;
        return (Source / Math.Cos(th), new Complex(0, Zc * Math.Tan(th)));
    }

    private static void AddLoad(NodalNetwork net, int[] b)
    {
        for (int k = 0; k < 3; k++)
        {
            net.Add(NodalBranch.Resistor($"RP{k}", b[k], NodalNetwork.Ground, RP));
            net.Add(NodalBranch.Inductor($"LP{k}", b[k], NodalNetwork.Ground, LP));
            int x = net.Node($"x{k}");
            net.Add(new NodalSwitch($"X{k}", b[k], x, 1e-4, 1e9, false));
            net.Add(NodalBranch.Resistor($"Rx{k}", x, NodalNetwork.Ground, Rx));
        }
    }

    private static List<NodalEvent> LoadStep() => [new(TLoadStep, "X0", true), new(TLoadStep, "X1", true), new(TLoadStep, "X2", true)];

    private static List<NodalProbe> Probes() => [new("vb0", NodalProbeKind.NodeVoltage, "B0"), new("vb1", NodalProbeKind.NodeVoltage, "B1"), new("vb2", NodalProbeKind.NodeVoltage, "B2")];

    private static NodalResult AllEmt(double duration)
    {
        var net = new NodalNetwork();
        var b = new int[3];
        for (int k = 0; k < 3; k++)
        {
            int kk = k;
            b[k] = net.Node($"B{k}");
            int g = net.Node($"G{k}");
            net.Add(new NodalVoltageSource($"E{k}", g, 0.0, t => Amp(t) * DetMath.Sin(W * t - kk * 2.0 * Math.PI / 3.0)));
            NodalBergeronLine.Add(net, g, NodalNetwork.Ground, net, b[k], NodalNetwork.Ground, $"L{k}", Zc, Tau);
        }
        AddLoad(net, b);
        return NodalSolver.Run(net, Dt, duration, Probes(), LoadStep());
    }

    private static (NodalResult Res, RmsEmtInterface Itf) CoSim(CoSimForm form, double duration, int every = 20)
    {
        var net = new NodalNetwork();
        var b = new int[3];
        for (int k = 0; k < 3; k++) b[k] = net.Node($"B{k}");
        var rms = new PhasorSide(Source, F, Zc, Tau);
        rms.ScheduleSourceChange(TSourceStep, 1.1 * Source);
        RmsEmtInterface itf = RmsEmtInterface.Add(net, "I", "", b, rms, new CoSimOptions(form, every, F), Dt);
        AddLoad(net, b);
        NodalResult res = NodalSolver.Run(net, Dt, duration, Probes(), LoadStep());
        return (res, itf);
    }

    private static Complex Fund(NodalResult r, double[] x, double t0, double t1) => R419MmcTests.Ph(r, x, t0, t1);

    [Fact]
    public void Steady_state_agrees_at_the_interface_between_all_EMT_RMS_plus_EMT_and_the_phasor_closed_form()
    {
        var (eth, zth) = Thevenin();
        Complex zp = ZLoad * Rx / (ZLoad + Rx);
        Complex vBefore = eth * ZLoad / (zth + ZLoad), vAfter = 1.1 * eth * zp / (zth + zp);
        output.WriteLine($"Thevenin at 50 Hz: E = {eth.Magnitude:F2} V, Z = {zth}; phasor closed form |V_B| before the load step {vBefore.Magnitude:F2} V, after the load step and the source step {vAfter.Magnitude:F2} V");
        NodalResult all = AllEmt(0.45);
        foreach (CoSimForm form in new[] { CoSimForm.Thevenin, CoSimForm.Norton })
        {
            var (co, itf) = CoSim(form, 0.45);
            Assert.Equal(NodalStatus.Completed, co.Status);
            // windows of whole cycles: before (0.12 .. 0.20) and after (0.40 .. 0.44)
            foreach (var (t0, t1, vClosed, label) in new[] { (0.12, 0.20, vBefore, "before"), (0.40, 0.44, vAfter, "after") })
            {
                double a = Fund(all, all.Series[0], t0, t1).Magnitude, c = Fund(co, co.Series[0], t0, t1).Magnitude;
                output.WriteLine($"{form}, {label}: |V_B| all-EMT {a:F2} V, RMS+EMT {c:F2} V, phasor {vClosed.Magnitude:F2} V; RMS+EMT vs all-EMT {(c - a) / a:E2}, vs phasor {(c - vClosed.Magnitude) / vClosed.Magnitude:E2}");
                Assert.InRange(Math.Abs(c - a) / a, 0.0, 3e-3);
                Assert.InRange(Math.Abs(a - vClosed.Magnitude) / vClosed.Magnitude, 0.0, 3e-3);
                Assert.InRange(Math.Abs(c - vClosed.Magnitude) / vClosed.Magnitude, 0.0, 3e-3);
                // the phase agrees too
                Complex pa = Fund(all, all.Series[0], t0, t1), pc = Fund(co, co.Series[0], t0, t1);
                Assert.InRange(Math.Abs(Complex.Divide(pc, pa).Phase), 0.0, 3e-3);
            }
            // the interface's own consistency: the voltage the RMS side computes from the EMT current equals the EMT measurement (Thevenin form)
            ExchangeRecord last = itf.Exchanges.Last(e => e.TimeSeconds < 0.44);
            output.WriteLine($"{form}: last exchange {last.TimeSeconds:F4} s, EMT voltage {last.EmtVoltage.Magnitude:F2} V, RMS side {last.RmsVoltage.Magnitude:F2} V");
            Assert.InRange((last.EmtVoltage - last.RmsVoltage).Magnitude / last.EmtVoltage.Magnitude, 0.0, 3e-3);
            Assert.False(itf.Exchanges.Take(19).Any(e => e.MeasurementValid), "no measurement is valid before a full fundamental cycle exists");
            Assert.True(itf.Exchanges.Count(e => e.MeasurementValid) > 300);
        }
    }

    [Fact]
    public void The_exchange_schedule_is_fixed_and_an_RMS_event_reaches_the_EMT_side_within_one_interval()
    {
        const int Every = 20;                                          // 1 ms
        var (co, itf) = CoSim(CoSimForm.Thevenin, 0.35, Every);
        Assert.Equal((int)Math.Floor(0.35 / (Every * Dt) + 1e-9), itf.Exchanges.Count);
        for (int i = 0; i < itf.Exchanges.Count; i++)
            Assert.Equal((i + 1) * Every * Dt, itf.Exchanges[i].TimeSeconds, 9);
        ExchangeRecord first = itf.Exchanges.First(e => e.Equivalent.Emf.Magnitude > 1.05 * Thevenin().Eth.Magnitude);
        double latency = first.TimeSeconds - TSourceStep;
        output.WriteLine($"RMS event at {TSourceStep} s reaches the EMT source at the exchange at {first.TimeSeconds:F4} s: latency {latency * 1e3:F3} ms (interval {Every * Dt * 1e3} ms)");
        Assert.InRange(latency, 0.0, Every * Dt + 1e-9);
        Assert.Contains("exchange every 20 step(s)", itf.Description);
        Assert.Contains("not bit-comparable", itf.Description);

        // bit-repeatable with itself
        var (co2, _) = CoSim(CoSimForm.Thevenin, 0.35, Every);
        for (int k = 0; k < 3; k++)
            for (int i = 0; i < co.Series[k].Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(co.Series[k][i]), BitConverter.DoubleToInt64Bits(co2.Series[k][i]));
        // and NOT bit-comparable with the all-EMT run (the equivalent is an approximation by construction)
        NodalResult all = AllEmt(0.35);
        Assert.NotEqual(BitConverter.DoubleToInt64Bits(all.Series[0][3000]), BitConverter.DoubleToInt64Bits(co.Series[0][3000]));
    }

    // The difference between the representations lies only in what an RMS equivalent cannot represent: waves on the line. At a load step the line
    // is a surge impedance Zc for 2 tau, while the Thevenin series inductance keeps its current.
    [Fact]
    public void A_load_step_differs_between_all_EMT_and_RMS_plus_EMT_only_in_the_band_the_RMS_model_cannot_represent()
    {
        NodalResult all = AllEmt(0.30);
        var (co, _) = CoSim(CoSimForm.Thevenin, 0.30);
        int e = (int)Math.Round(TLoadStep / Dt);
        // Instant of the step (all inductor currents are continuous, the line looks like Zc for 2 tau): with G = 1/R the node balance gives
        //   line :  v = v0 (Gc + Gp) / (Gc + Gp + Gx),     RMS equivalent (series inductance, no wave) :  v = v0 Gp / (Gp + Gx).
        double gc = 1.0 / Zc, gp = 1.0 / RP, gx = 1.0 / Rx;
        double ratioLine = (gc + gp) / (gc + gp + gx), ratioCo = gp / (gp + gx);
        double worstAll = 0.0, worstCo = 0.0, v0max = 0.0;
        for (int k = 0; k < 3; k++)
        {
            double v0 = all.Series[k][e - 1];
            worstAll = Math.Max(worstAll, Math.Abs(all.Series[k][e] - ratioLine * v0));
            worstCo = Math.Max(worstCo, Math.Abs(co.Series[k][e] - ratioCo * co.Series[k][e - 1]));
            v0max = Math.Max(v0max, Math.Abs(v0));
            output.WriteLine($"phase {k}: v_B before {v0:F1} V; all-EMT after the step {all.Series[k][e]:F1} V (line closed form {ratioLine * v0:F1}); "
                + $"RMS+EMT {co.Series[k][e]:F1} V (equivalent closed form {ratioCo * co.Series[k][e - 1]:F1})");
        }
        Assert.InRange(worstAll, 0.0, 0.03 * Vm);                      // the Bergeron line follows the wave closed form
        Assert.InRange(worstCo, 0.0, 0.03 * Vm);                       // the Thevenin equivalent follows its own (no wave step)
        Assert.True(v0max > 0.4 * Vm, "the step is applied at a non-trivial voltage");

        // Sub-cycle: the instantaneous difference is large right after the step (on the phase with the largest voltage) ...
        int ph = Enumerable.Range(0, 3).OrderByDescending(k => Math.Abs(all.Series[k][e - 1])).First();
        double[] d = all.Series[ph].Zip(co.Series[ph], (x, y) => x - y).ToArray();
        double dmax = 0.0;
        for (int i = e; i < e + 40; i++) dmax = Math.Max(dmax, Math.Abs(d[i]));
        output.WriteLine($"max |v_all - v_cosim| in the 2 ms after the step: {dmax:F1} V ({dmax / Vm:P1} of the peak)");
        Assert.True(dmax > 0.05 * Vm);
        // ... it is concentrated in the first milliseconds (the travelling waves, whose frequencies are far above the fundamental) ...
        double early = Rms(d, e, e + 60), late = Rms(d, e + 400, e + 1200);
        output.WriteLine($"rms of the difference: first 3 ms {early:F1} V, from 20 to 60 ms after the step {late:F1} V");
        Assert.True(early > 10.0 * late);
        // ... and in the fundamental (a whole cycle, starting 2 cycles later) the two agree.
        double t0 = TLoadStep + 0.04, t1 = t0 + 0.04 - 1e-12;
        double fa = Fund(all, all.Series[0], t0, t1).Magnitude, fc = Fund(co, co.Series[0], t0, t1).Magnitude;
        output.WriteLine($"fundamental |V_B| after the step: all-EMT {fa:F2} V, RMS+EMT {fc:F2} V ({(fc - fa) / fa:E2})");
        Assert.InRange(Math.Abs(fc - fa) / fa, 0.0, 3e-3);
    }

    private static double Rms(double[] x, int from, int to)
    {
        double s = 0; int n = 0;
        for (int i = from; i < to && i < x.Length; i++) { s += x[i] * x[i]; n++; }
        return Math.Sqrt(s / n);
    }

    [Fact]
    public void An_interface_whose_cycle_is_not_a_whole_number_of_steps_is_refused()
    {
        var net = new NodalNetwork();
        var b = new[] { net.Node("B0"), net.Node("B1"), net.Node("B2") };
        var rms = new PhasorSide(Source, F, Zc, Tau);
        var ex = Assert.Throws<ArgumentException>(() => RmsEmtInterface.Add(net, "I", "", b, rms, new CoSimOptions(CoSimForm.Thevenin, 10, F), 3e-5));
        Assert.Contains("whole number", ex.Message);
        // a capacitive equivalent cannot be a series R-L
        var cap = new PhasorSide(Source, new Complex(1.0, -5.0));
        Assert.Throws<ArgumentException>(() => RmsEmtInterface.Add(net, "J", "", b, cap, new CoSimOptions(CoSimForm.Thevenin, 10, F), Dt));
    }
}
