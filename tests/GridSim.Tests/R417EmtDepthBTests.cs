using System.Numerics;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.Machines;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Emt.PowerElectronics;
using GridSim.Core.Dynamics.Models;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-17, EMT depth B (power electronics and machines) on the R4-16 nodal solver. Every oracle is derived in this file:
/// the six-pulse bridge average voltage and commutation overlap from the closed forms of the commutation circuit, the PWM
/// pole-voltage spectrum from the double Fourier (Bessel) series of naturally sampled PWM, the synchronous machine's steady state
/// from the phasor equations and its sudden short circuit from the classical X''d / X'd / Xd time-constant formula, the induction
/// machine's end state from the equivalent-circuit torque balance. Comparison with a commercial EMT tool is COMPARATOR-UNVERIFIED
/// (none available).
/// </summary>
public sealed class R417EmtDepthBTests(ITestOutputHelper output)
{
    private const double Pi = Math.PI;

    private static NodalProbe Pv(string node) => new("v:" + node, NodalProbeKind.NodeVoltage, node);
    private static NodalProbe Ip(string el, int ch = 0) => new($"i:{el}:{ch}", NodalProbeKind.ElementCurrent, el, ch);
    private static NodalProbe Sp(string el, int ch = 0) => new($"s:{el}:{ch}", NodalProbeKind.ElementState, el, ch);
    private static double[] Ser(NodalResult r, string name) => r.Series[r.ProbeNames.ToList().IndexOf(name)];

    /// <summary>Complex amplitude c with x(t) = Re(c e^(j 2 pi f t)) over <paramref name="count"/> samples from <paramref name="start"/>.</summary>
    private static Complex Coef(double[] t, double[] x, int start, int count, double f)
    {
        double w = 2 * Pi * f, re = 0, im = 0;
        for (int i = start; i < start + count; i++) { re += x[i] * Math.Cos(w * t[i]); im -= x[i] * Math.Sin(w * t[i]); }
        return new Complex(re, im) * (2.0 / count);
    }

    private static double Mean(double[] x, int start, int count)
    {
        double s = 0; for (int i = start; i < start + count; i++) s += x[i]; return s / count;
    }

    // ================================================================================================================
    // 1. Six-pulse bridge: average DC voltage and commutation overlap from closed forms
    // ================================================================================================================

    private const double Ls = 1e-3, Id = 200.0, VllRms = 1000.0, Fgrid = 50.0, RonValve = 1e-4;
    private static readonly double VllPk = VllRms * Math.Sqrt(2.0), Vph = VllPk / Math.Sqrt(3.0), Wg = 2 * Pi * Fgrid;

    private sealed record BridgeRun(NodalResult Res, TwoLevelBridge Bridge, double Dt, int StepsPerCycle);

    private static BridgeRun RunSixPulse(ValveKind kind, double alphaRad, double dt, int cycles, NodalEventRule rule)
    {
        var net = new NodalNetwork();
        var ac = new int[3];
        for (int k = 0; k < 3; k++)
        {
            int kk = k;
            int s = net.Node($"S{k}");
            ac[k] = net.Node($"A{k}");
            net.Add(new NodalVoltageSource($"E{k}", s, 0.0, t => Vph * Math.Cos(Wg * t - kk * 2 * Pi / 3)));
            net.Add(NodalBranch.Inductor($"L{k}", s, ac[k], Ls));
        }
        int p = net.Node("P"), n = net.Node("N");
        Func<double, int, bool, bool>? gate = kind == ValveKind.Diode ? null : ThyristorFiring.Bridge(Fgrid, 0.0, alphaRad);
        TwoLevelBridge br = TwoLevelBridge.Add(net, "B", p, n, ac, kind, new ValveParameters(RonValve, 1e7), gate, $"six-pulse {kind} bridge");
        double ramp(double t) => Math.Min(1.0, t / 20e-3);
        net.Add(new NodalCurrentSource("Ip", p, t => -ramp(t) * Id));
        net.Add(new NodalCurrentSource("In", n, t => ramp(t) * Id));
        var probes = new List<NodalProbe> { Pv("P"), Pv("N") };
        for (int k = 0; k < 3; k++) { probes.Add(Sp(br.Upper(k))); probes.Add(Sp(br.Lower(k))); }
        double duration = cycles / Fgrid;
        NodalResult res = NodalSolver.Run(net, dt, duration, probes, null, new NodalOptions { EventRule = rule });
        return new BridgeRun(res, br, dt, (int)Math.Round(1.0 / (Fgrid * dt)));
    }

    private static (double Vd, double MuUpper, double MuLower) MeasureBridge(BridgeRun r)
    {
        int spc = r.StepsPerCycle;
        int start = r.Res.Times.Length - 1 - 2 * spc;                  // last two whole cycles
        double[] vp = Ser(r.Res, "v:P"), vn = Ser(r.Res, "v:N");
        var vd = new double[vp.Length];
        for (int i = 0; i < vd.Length; i++) vd[i] = vp[i] - vn[i];
        double mean = Mean(vd, start + 1, 2 * spc);
        double[][] up = Enumerable.Range(0, 3).Select(k => Ser(r.Res, $"s:{r.Bridge.Upper(k)}:0")).ToArray();
        double[][] lo = Enumerable.Range(0, 3).Select(k => Ser(r.Res, $"s:{r.Bridge.Lower(k)}:0")).ToArray();
        int overU = 0, overL = 0;
        for (int i = start + 1; i <= start + 2 * spc; i++)
        {
            if (up[0][i] + up[1][i] + up[2][i] >= 2) overU++;
            if (lo[0][i] + lo[1][i] + lo[2][i] >= 2) overL++;
        }
        // 3 commutations per group per cycle, two cycles measured
        return (mean, Wg * overU * r.Dt / 6.0, Wg * overL * r.Dt / 6.0);
    }

    private static (double Vd, double Mu) ClosedFormBridge(double alpha)
    {
        double vd0 = 3.0 * VllPk / Pi;
        double cosMu = Math.Cos(alpha) - 2.0 * Wg * Ls * Id / VllPk;           // cos(alpha) - cos(alpha + mu) = 2 w Ls Id / V_LL,peak
        double mu = Math.Acos(cosMu) - alpha;
        double vdOverlap = vd0 * (Math.Cos(alpha) + cosMu) / 2.0;              // mean of the commutating and conducting segments
        double vdDrop = vd0 * Math.Cos(alpha) - 3.0 * Wg * Ls * Id / Pi;       // the same by the commutation-drop form
        Assert.True(Math.Abs(vdOverlap - vdDrop) < 1e-9 * vd0, "the two closed forms of Vd must agree");
        return (vdDrop - 2.0 * RonValve * Id, mu);
    }

    [Theory]
    [InlineData(ValveKind.Diode, 0.0)]
    [InlineData(ValveKind.Thyristor, 30.0)]
    [InlineData(ValveKind.Thyristor, 60.0)]
    public void Six_pulse_bridge_average_voltage_and_overlap_match_the_closed_forms(ValveKind kind, double alphaDeg)
    {
        double alpha = alphaDeg * Pi / 180.0;
        BridgeRun r = RunSixPulse(kind, alpha, 5e-6, 8, NodalEventRule.BackwardEulerStep);
        Assert.Equal(NodalStatus.Completed, r.Res.Status);
        (double vd, double muU, double muL) = MeasureBridge(r);
        (double vdExp, double muExp) = ClosedFormBridge(alpha);
        output.WriteLine($"{kind} alpha {alphaDeg} deg: Vd measured {vd:F3} V, closed form {vdExp:F3} V (rel {(vd - vdExp) / vdExp:E2}); overlap upper {muU:F4} lower {muL:F4}, closed form {muExp:F4} rad");
        Assert.True(Math.Abs(vd - vdExp) / vdExp < 2e-3, $"Vd {vd} vs {vdExp}");
        // Thyristor cases agree to about 0.002 rad. The diode case (alpha = 0) is the ill-conditioned point of the closed form (mu ~ sqrt(2x), so a
        // small error in the start of the overlap is magnified) and the natural turn-on is detected one step late: observed 0.040 rad at dt 5 us and
        // 0.030 rad at 2.5 us above the closed form, so it gets the wider bound.
        double tol = kind == ValveKind.Diode ? 0.05 : 0.012;
        Assert.True(Math.Abs(muU - muExp) < tol, $"overlap (upper) {muU} vs {muExp}");
        Assert.True(Math.Abs(muL - muExp) < tol, $"overlap (lower) {muL} vs {muExp}");
    }


    [Fact]
    public void Diode_bridge_without_commutation_reactance_loss_gives_the_ideal_average_and_no_overlap_in_the_limit()
    {
        // Ls -> small: the overlap shrinks in proportion to Ls (closed form mu ~ 2 w Ls Id / (V_LL,pk sin alpha) with alpha = 0 giving sqrt) and Vd -> Vd0 = 3 V_LL,pk / pi.
        double vd0 = 3.0 * VllPk / Pi;
        (double vd, double mu) = ClosedFormBridge(0.0);
        Assert.True(vd < vd0 && vd > 0.9 * vd0);
        Assert.True(mu > 0.0 && mu < 0.5);
        output.WriteLine($"Vd0 = {vd0:F3} V; with Ls {Ls * 1e3} mH at {Id} A the closed-form Vd = {vd:F3} V, overlap {mu:F4} rad");
    }

    // ================================================================================================================
    // 2. Two-level VSC with sine PWM: fundamental and characteristic harmonics (Bessel double Fourier series)
    // ================================================================================================================

    private const double Vdc = 1000.0, Mi = 0.8, F1 = 50.0, Mf = 39.0, RLoad = 5.0, LLoad = 10e-3, RonVsc = 1e-3;

    private static double BesselJ(int n, double x)
    {
        // J_n(x) = (1/pi) int_0^pi cos(n tau - x sin tau) dtau, composite Simpson with 4000 intervals (an independent evaluation)
        int m = 4000; double h = Pi / m, s = 0;
        for (int i = 0; i <= m; i++)
        {
            double tau = i * h, f = Math.Cos(n * tau - x * Math.Sin(tau));
            s += (i == 0 || i == m) ? f : (i % 2 == 1 ? 4 * f : 2 * f);
        }
        return s * h / 3.0 / Pi;
    }

    /// <summary>Amplitude of pole-voltage harmonic (m, n) at m Mf + n times F1: 2 Vdc / (m pi) |J_n(m pi M / 2)|, for m + n odd.</summary>
    private static double PoleHarmonic(int m, int n) => ((m + n) % 2 == 0) ? 0.0 : 2.0 * Vdc / (m * Pi) * Math.Abs(BesselJ(Math.Abs(n), m * Pi * Mi / 2.0));

    private sealed record VscRun(NodalResult Res, int StepsPerCycle, double Dt, PowerElectronicsResult Pe);

    private static VscRun RunVsc(bool average, double dt, int cycles, double carrierPhase = 0.0)
    {
        var net = new NodalNetwork();
        int p = net.Node("P"), n = net.Node("N");
        net.Add(new NodalVoltageSource("Ep", p, 0.0, _ => Vdc / 2));
        net.Add(new NodalVoltageSource("En", n, 0.0, _ => -Vdc / 2));
        var ac = new[] { net.Node("A0"), net.Node("A1"), net.Node("A2") };
        int nn = net.Node("NN");
        var pwm = new SinePwm(Mf * F1, carrierPhase, SinePwm.SineReference(F1, Mi, 0.0));
        IConverterModel conv;
        var probes = new List<NodalProbe> { Pv("A0"), Pv("A1"), Pv("A2"), Pv("NN") };
        if (average)
        {
            var av = net.Add(new AverageValueConverter("C", p, n, ac, RonVsc, Vdc, pwm.Reference));
            conv = av;
            probes.Add(Ip("C", 3));
            for (int k = 0; k < 3; k++) probes.Add(Ip("C", k));
        }
        else
        {
            TwoLevelBridge br = TwoLevelBridge.AddVsc(net, "C", p, n, ac, new ValveParameters(RonVsc, 1e7), pwm);
            conv = br;
            for (int k = 0; k < 3; k++) { probes.Add(Ip(br.Upper(k))); probes.Add(Ip(br.Lower(k))); probes.Add(Sp(br.Upper(k))); probes.Add(Sp(br.Lower(k))); }
        }
        for (int k = 0; k < 3; k++)
        {
            net.Add(NodalBranch.SeriesRl($"RL{k}", ac[k], nn, RLoad, LLoad));
            probes.Add(Ip($"RL{k}"));
        }
        PowerElectronicsResult pe = PowerElectronicsRunner.Run(net, dt, cycles / F1, probes, [conv], null, new NodalOptions { EventRule = NodalEventRule.Trapezoidal });
        return new VscRun(pe.Run, (int)Math.Round(1.0 / (F1 * dt)), dt, pe);
    }

    [Fact]
    public void Vsc_sine_pwm_pole_voltage_has_the_closed_form_fundamental_and_characteristic_harmonics()
    {
        VscRun r = RunVsc(false, 2.5e-6, 6);
        Assert.Equal(NodalStatus.Completed, r.Res.Status);
        Assert.Equal(ConverterModelClass.Switched, r.Pe.Converters[0].Class);
        Assert.Contains("sine PWM", r.Pe.Converters[0].Description);
        Assert.Contains("quantised UP", r.Pe.SwitchingTiming);
        int spc = r.StepsPerCycle, start = r.Res.Times.Length - 1 - 2 * spc + 1, count = 2 * spc;
        double[] t = r.Res.Times, va = Ser(r.Res, "v:A0");

        double fund = Coef(t, va, start, count, F1).Magnitude;
        double fundExp = Mi * Vdc / 2;
        output.WriteLine($"pole voltage fundamental {fund:F3} V, closed form m Vdc/2 = {fundExp:F3} V ({(fund - fundExp) / fundExp:E2})");
        Assert.True(Math.Abs(fund - fundExp) / fundExp < 0.01);

        // Oracle check: the Bessel series against the IDEAL natural-sampled waveform (comparator on a 40x finer grid), harmonics (1,0), (1,+-2), (2,+-1), (2,+-3)
        var cases = new List<(int m, int n)> { (1, 0), (1, 2), (1, -2), (1, 4), (1, -4), (1, 6), (1, -6), (2, 1), (2, -1), (2, 3), (2, -3), (2, 5), (2, -5) };
        double dtf = 1.0 / (F1 * 80000);
        int nf = (int)Math.Round(0.02 / dtf);
        var pwm = new SinePwm(Mf * F1, 0.0, SinePwm.SineReference(F1, Mi, 0.0));
        var tf = new double[nf]; var vf = new double[nf];
        for (int i = 0; i < nf; i++) { tf[i] = (i + 1) * dtf; vf[i] = pwm.UpperOn(tf[i], 0) ? Vdc / 2 : -Vdc / 2; }
        double worstIdeal = 0, worstSim = 0;
        foreach ((int m, int n) in cases)
        {
            int h = (int)(m * Mf + n);
            double exp = PoleHarmonic(m, n);
            double ideal = Coef(tf, vf, 0, nf, h * F1).Magnitude;
            double sim = Coef(t, va, start, count, h * F1).Magnitude;
            worstIdeal = Math.Max(worstIdeal, Math.Abs(ideal - exp) / Vdc);
            worstSim = Math.Max(worstSim, Math.Abs(sim - exp) / Vdc);
            output.WriteLine($"harmonic ({m},{n}) at {h} x f1: closed form {exp:F3} V, ideal PWM {ideal:F3} V, simulated {sim:F3} V");
        }
        output.WriteLine($"worst |ideal - Bessel| = {worstIdeal:E2} Vdc, worst |simulated - Bessel| = {worstSim:E2} Vdc");
        Assert.True(worstIdeal < 2e-3, "the Bessel oracle must match the ideal natural-sampled waveform");
        Assert.True(worstSim < 8e-3, $"simulated harmonics off the closed form by {worstSim} Vdc");

        // First sidebands: carrier multiples (1: m_f +- 2, 2: 2 m_f +- 1); the (m=1, n=+-1) and baseband harmonics are absent (m + n even).
        double absent = 0;
        foreach (int h in new[] { 2, 3, 5, 7, 9, 11, 13, 19, 38, 40, 76, 78, 80 }) absent = Math.Max(absent, Coef(t, va, start, count, h * F1).Magnitude);
        output.WriteLine($"largest non-characteristic harmonic amplitude {absent:F3} V");
        Assert.True(absent < 8e-3 * Vdc);
    }

    [Fact]
    public void Average_value_converter_agrees_with_the_switched_model_fundamental_and_with_the_closed_form_current()
    {
        VscRun sw = RunVsc(false, 2.5e-6, 8), av = RunVsc(true, 2.5e-6, 8);
        Assert.Equal(ConverterModelClass.AverageValue, av.Pe.Converters[0].Class);
        Assert.Contains("average-value", av.Pe.Converters[0].Description);
        int spc = sw.StepsPerCycle, start = sw.Res.Times.Length - 1 - 2 * spc + 1, count = 2 * spc;
        // closed form: phase current = (m Vdc/2) / (R + Ron + j w L), phase a reference sin(wt) -> peak amplitude and phase
        Complex z = new(RLoad + RonVsc, 2 * Pi * F1 * LLoad);
        Complex iExp = (Mi * Vdc / 2) / z * new Complex(0, -1);                  // sin(wt) = Re(-j e^{jwt})
        Complex cs = Coef(sw.Res.Times, Ser(sw.Res, "i:RL0:0"), start, count, F1);
        Complex ca = Coef(av.Res.Times, Ser(av.Res, "i:RL0:0"), start, count, F1);
        output.WriteLine($"phase-a current: closed form {iExp.Magnitude:F4} A at {iExp.Phase:F4} rad; switched {cs.Magnitude:F4} A at {cs.Phase:F4}; average {ca.Magnitude:F4} A at {ca.Phase:F4}");
        Assert.True((cs - iExp).Magnitude / iExp.Magnitude < 0.01, "switched fundamental current vs closed form");
        Assert.True((ca - iExp).Magnitude / iExp.Magnitude < 1e-3, "average-value current vs closed form");
        Assert.True((ca - cs).Magnitude / ca.Magnitude < 0.01, "average-value and switched fundamentals agree");
        // the average model has no switching ripple: no energy at the carrier frequency, while the switched model has
        double ripAv = Coef(av.Res.Times, Ser(av.Res, "v:A0"), start, count, Mf * F1 * 1).Magnitude;
        double ripSw = Coef(sw.Res.Times, Ser(sw.Res, "v:A0"), start, count, Mf * F1).Magnitude;
        output.WriteLine($"carrier-frequency pole voltage: switched {ripSw:F2} V, average {ripAv:E2} V");
        Assert.True(ripAv < 1e-6 * ripSw);
    }

    [Fact]
    public void Switched_converter_balances_dc_and_ac_power_at_every_step_and_over_whole_cycles()
    {
        VscRun r = RunVsc(false, 2.5e-6, 6);
        NodalResult res = r.Res;
        int n = res.Times.Length;
        var pdc = new double[n]; var pac = new double[n]; var loss = new double[n];
        for (int i = 0; i < n; i++)
        {
            double iu = 0, il = 0, pl = 0;
            for (int k = 0; k < 3; k++)
            {
                double u = Ser(res, $"i:C.up{k}:0")[i], l = Ser(res, $"i:C.lo{k}:0")[i];
                iu += u; il += l;
                pl += u * u * (Ser(res, $"s:C.up{k}:0")[i] > 0.5 ? RonVsc : 1e7) + l * l * (Ser(res, $"s:C.lo{k}:0")[i] > 0.5 ? RonVsc : 1e7);
            }
            pdc[i] = (Vdc / 2) * (iu + il);                  // P leaves the + source as iu, N takes il back into the - source
            double pa = 0;
            for (int k = 0; k < 3; k++) pa += Ser(res, $"v:A{k}")[i] * Ser(res, $"i:RL{k}:0")[i];
            pac[i] = pa; loss[i] = pl;
        }
        double worst = 0, scale = 0;
        for (int i = 1; i < n; i++) { worst = Math.Max(worst, Math.Abs(pdc[i] - pac[i] - loss[i])); scale = Math.Max(scale, Math.Abs(pdc[i])); }
        int spc = r.StepsPerCycle, start = n - 1 - 2 * spc + 1, count = 2 * spc;
        double mDc = Mean(pdc, start, count), mAc = Mean(pac, start, count), mLoss = Mean(loss, start, count);
        // closed form load power: 3 (Ipk^2 / 2) R (inductor stores nothing over whole cycles)
        Complex z = new(RLoad + RonVsc, 2 * Pi * F1 * LLoad);
        double ipk = (Mi * Vdc / 2) / z.Magnitude;
        double pLoadExp = 3 * ipk * ipk / 2 * RLoad;
        output.WriteLine($"instantaneous |Pdc - Pac - Ploss| worst {worst:E2} W of {scale:F0} W peak; mean Pdc {mDc:F1} W, Pac {mAc:F1} W, losses {mLoss:F2} W; closed-form load power (fundamental) {pLoadExp:F1} W");
        Assert.True(worst < 1e-8 * scale, "KCL power balance at every step");
        Assert.True(Math.Abs(mDc - mAc - mLoss) < 1e-8 * mDc);
        Assert.True(Math.Abs(mDc - mLoss - pLoadExp) / pLoadExp < 0.02, "mean power equals the fundamental load power plus harmonic dissipation to 2 per cent");

        // average-value model: DC power from its DC current equals the EMF power exactly
        VscRun a = RunVsc(true, 2.5e-6, 6);
        double worstA = 0;
        for (int i = 1; i < a.Res.Times.Length; i++)
        {
            double idc = Ser(a.Res, "i:C:3")[i];
            double pdcA = Vdc * idc, pAc = 0, pl = 0;
            for (int k = 0; k < 3; k++) { double ik = Ser(a.Res, $"i:C:{k}")[i]; pAc += Ser(a.Res, $"v:A{k}")[i] * ik; pl += ik * ik * RonVsc; }
            worstA = Math.Max(worstA, Math.Abs(pdcA - pAc - pl));
        }
        output.WriteLine($"average-value model: worst |Pdc - Pac - Ploss| {worstA:E2} W");
        Assert.True(worstA < 1e-6 * scale);
    }


    // ================================================================================================================
    // 2b. Grid-connected VSC with dq current control and a DC link: first-order current response, power balance, link voltage
    // ================================================================================================================

    [Fact]
    public void Current_controlled_vsc_follows_the_first_order_response_and_the_dc_link_settles_at_the_power_balance_voltage()
    {
        const double vg = 400.0, rF = 0.05, lF = 2e-3, vSup = 1100.0, rSup = 0.5, cDc = 2e-3, iRef = 100.0, tStep = 0.02, fc = 2000.0, dt = 5e-6;
        double w = 2 * Pi * 50.0, alpha = 2 * Pi * 100.0;
        var net = new NodalNetwork();
        DcLink link = DcLink.Add(net, "D", cDc, 1070.0, vSup, rSup);
        var ac = new int[3]; var gr = new int[3]; var filt = new NodalBranch[3];
        for (int k = 0; k < 3; k++)
        {
            int kk = k;
            ac[k] = net.Node($"A{k}"); gr[k] = net.Node($"G{k}");
            net.Add(new NodalVoltageSource($"E{k}", gr[k], 0.0, t => vg * Math.Cos(w * t - kk * 2 * Pi / 3)));
            filt[k] = net.Add(NodalBranch.SeriesRl($"F{k}", ac[k], gr[k], rF, lF));
        }
        int every = (int)Math.Round(0.5 / fc / dt);
        var ctrl = net.Add(new VscCurrentController("CTL", filt, gr, link.Positive, link.Negative, 50.0, 0.0, rF, lF, alpha, 1070.0, every, t => (t < tStep ? 0.0 : iRef, 0.0)));
        TwoLevelBridge br = TwoLevelBridge.AddVsc(net, "C", link.Positive, link.Negative, ac, new ValveParameters(1e-3, 1e7), new SinePwm(fc, 0.0, ctrl.Reference));
        var probes = new List<NodalProbe> { Pv("D.p"), Pv("D.n") };
        for (int k = 0; k < 3; k++) probes.Add(Ip($"F{k}"));
        PowerElectronicsResult pe = PowerElectronicsRunner.Run(net, dt, 0.3, probes, [br], null, new NodalOptions { EventRule = NodalEventRule.Trapezoidal });
        NodalResult res = pe.Run;
        Assert.Equal(NodalStatus.Completed, res.Status);
        int n = res.Times.Length;
        var id = new double[n]; var iq = new double[n];
        for (int i = 0; i < n; i++)
        {
            double th = w * res.Times[i], a = res.Series[2][i], b = res.Series[3][i], c = res.Series[4][i];
            id[i] = 2.0 / 3.0 * (a * Math.Cos(th) + b * Math.Cos(th - 2 * Pi / 3) + c * Math.Cos(th + 2 * Pi / 3));
            iq[i] = -2.0 / 3.0 * (a * Math.Sin(th) + b * Math.Sin(th - 2 * Pi / 3) + c * Math.Sin(th + 2 * Pi / 3));
        }
        int win = (int)Math.Round(1.0 / fc / dt);   // one carrier period moving average removes the switching ripple
        double Avg(double[] x, double t) { int c0 = (int)Math.Round(t / dt) - win / 2; return Mean(x, c0, win); }
        // oracle: the sampled loop written out. Plant L di/dt + R i = u (after decoupling), u held over each control period Ts; the integrator
        // updates before the output (I[k] = I[k-1] + Ki Ts e[k]; u[k] = Kp e[k] + I[k]), Kp = alpha L, Ki = alpha R.
        double ts = every * dt, kp = alpha * lF, ki = alpha * rF, aD = Math.Exp(-rF * ts / lF);
        double IAt(double tau)     // current at time tau after the first sample (continuous, exact ZOH plant)
        {
            double iK = 0, integ = 0; int k = 0;
            while ((k + 1) * ts <= tau) { double e = iRef - iK; integ += ki * ts * e; double u = kp * e + integ; iK = aD * iK + (u / rF) * (1 - aD); k++; }
            double e2 = iRef - iK; double u2 = kp * e2 + integ + ki * ts * e2;
            double x = tau - k * ts;
            return iK * Math.Exp(-rF * x / lF) + (u2 / rF) * (1 - Math.Exp(-rF * x / lF));
        }
        double worst = 0;
        foreach (double dtau in new[] { 0.5e-3, 1e-3, 2e-3, 3e-3, 5e-3, 8e-3, 15e-3 })
        {
            double t = tStep + dtau;
            double exp = 0; for (int q = 0; q < 20; q++) exp += IAt(dtau + (q - 9.5) * (1.0 / fc / 20)) / 20;   // same one-carrier-period window
            double got = Avg(id, t);
            worst = Math.Max(worst, Math.Abs(got - exp) / iRef);
            output.WriteLine($"t0 + {dtau * 1e3:F1} ms: i_d {got:F2} A, sampled-loop closed form {exp:F2} A");
        }
        Assert.True(worst < 0.06, $"step response deviates by {worst} of the reference");
        double idEnd = Avg(id, 0.28), iqEnd = Avg(iq, 0.28);
        int s0 = n - 1 - 2 * 4000;
        double vdc = Mean(res.Series[0], s0, 8000) - Mean(res.Series[1], s0, 8000);
        double pAc = 1.5 * vg * idEnd, pLoss = 1.5 * rF * idEnd * idEnd + 1.5 * 1e-3 * idEnd * idEnd * 2;
        double vExp = (vSup + Math.Sqrt(vSup * vSup - 4 * rSup * (pAc + pLoss))) / 2;
        output.WriteLine($"steady: i_d {idEnd:F3} A, i_q {iqEnd:F3} A; DC link {vdc:F2} V, power-balance closed form {vExp:F2} V (P_ac {pAc:F0} W + losses {pLoss:F0} W)");
        Assert.True(Math.Abs(idEnd - iRef) / iRef < 0.01);
        Assert.True(Math.Abs(iqEnd) < 0.02 * iRef);
        Assert.True(Math.Abs(vdc - vExp) / vExp < 0.01);
    }

    // ================================================================================================================
    // 3. Synchronous machine: phasor steady state and the sudden three-phase short circuit
    // ================================================================================================================

    private static SubtransientMachine.Parameters Genr() => new(
        InertiaSeconds: 3.5, DampingPu: 0.0, Xd: 1.8, Xdp: 0.3, Xdpp: 0.2, Xq: 1.7, Xqp: 0.5, Xqpp: 0.2,
        Tdop: 6.0, Tdopp: 0.04, Tqop: 0.5, Tqopp: 0.05, ExternalReactance: 0.1, InfiniteBusVoltagePu: 1.0, Saturation: null, LeakageReactance: 0.15);

    private const double Ra = 0.003;
    private static SynchronousMachineEmtData GenData(double ra = Ra) => new(Genr(), 100.0, 13.8, 50.0, ra);
    private static readonly double Zb = 13.8 * 13.8 / 100.0, VbPk = 13800.0 * Math.Sqrt(2.0 / 3.0), IbPk = (2.0 / 3.0) * 100e6 / (13800.0 * Math.Sqrt(2.0 / 3.0));

    [Fact]
    public void Synchronous_machine_on_an_infinite_bus_holds_the_phasor_steady_state()
    {
        const double reLine = 0.01, xeLine = 0.1;
        SubtransientMachine.Parameters rp = Genr();
        double w0 = 2 * Pi * 50.0;
        Complex iOut = Complex.FromPolarCoordinates(0.85, -0.35);
        Complex vBus = Complex.One, zLine = new(reLine, xeLine);
        Complex vT = vBus + zLine * iOut;
        // ---- independent phasor solution (salient, with Ra) ----
        double xd = rp.Xd, xq = rp.Xq;
        Complex e = vT + new Complex(Ra, xq) * iOut;
        double theta0 = e.Phase - Pi / 2, delta = e.Phase - vT.Phase;
        Complex idq = iOut * Complex.Exp(new Complex(0, -theta0)), vdq = vT * Complex.Exp(new Complex(0, -theta0));
        double ef = e.Magnitude + (xd - xq) * idq.Real;
        double ifdExp = ef / (xd - 0.15);
        double pOut = (vT * Complex.Conjugate(iOut)).Real, teExp = pOut + Ra * iOut.Magnitude * iOut.Magnitude;

        var net = new NodalNetwork();
        var mach = new SynchronousMachineEmt("G", net.Node("T0"), net.Node("T1"), net.Node("T2"), NodalNetwork.Ground, GenData());
        SynchronousInitialState init = mach.InitialiseFromPhasors(vT, iOut);
        output.WriteLine($"phasor solution: delta {delta:F4} rad, Ef {ef:F4} pu, id {idq.Real:F4} iq {idq.Imaginary:F4}, Te {teExp:F4}; element init: theta0 {init.ThetaRad:F4}, Ef {init.FieldVoltagePu:F5} (Rfd ifd), ifd {init.FieldCurrentPu:F4}, Tm {init.MechanicalTorquePu:F4}");
        Assert.Equal(theta0, init.ThetaRad, 9);
        Assert.Equal(delta, init.LoadAngleRad, 9);
        Assert.Equal(ifdExp, init.FieldCurrentPu, 9);
        Assert.Equal(teExp, init.MechanicalTorquePu, 9);
        Assert.Equal(idq.Real, init.IdPu, 9);
        Assert.Equal(idq.Imaginary, init.IqPu, 9);
        Assert.Equal(vdq.Real, -(Ra * idq.Real) + (xq * idq.Imaginary), 9);        // the phasor equation vd = -Ra id + Xq iq

        net.Add(mach);
        for (int k = 0; k < 3; k++)
        {
            int kk = k;
            int b = net.Node($"B{k}");
            net.Add(new NodalVoltageSource($"E{k}", b, 0.0, t => VbPk * Math.Cos(w0 * t - kk * 2 * Pi / 3)));
            Complex ik = iOut * Complex.Exp(new Complex(0, -k * 2 * Pi / 3));
            net.Add(NodalBranch.SeriesRl($"LN{k}", net.Node($"T{k}"), b, reLine * Zb, xeLine * Zb / w0, IbPk * ik.Real));
        }
        double dt = 50e-6;
        var probes = new List<NodalProbe> { Ip("G", 0), Ip("G", 1), Ip("G", 2), Sp("G", 0), Sp("G", 1), Sp("G", 2), Sp("G", 3) };
        NodalResult res = NodalSolver.Run(net, dt, 1.0, probes, null, new NodalOptions { SampleEvery = 1 });
        Assert.Equal(NodalStatus.Completed, res.Status);
        int spc = 400, n = res.Times.Length, start = n - spc * 2, count = spc * 2;
        Complex ia = Coef(res.Times, res.Series[0], start, count, 50.0);
        Complex iaExp = IbPk * iOut * Complex.Exp(new Complex(0, 0));
        double speed = res.Series[3][n - 1], dDelta = res.Series[4][n - 1] - res.Series[4][0], te = Mean(res.Series[5], start, count), ifd = Mean(res.Series[6], start, count);
        output.WriteLine($"after 1 s: phase-a current {ia.Magnitude:F1} A at {ia.Phase:F4} rad (phasor {iaExp.Magnitude:F1} A at {iaExp.Phase:F4}); speed {speed:F7} pu; load-angle drift {dDelta:E2} rad; Te {te:F4} (phasor {teExp:F4}); field current {ifd:F4} (phasor {ifdExp:F4})");
        Assert.True((ia - iaExp).Magnitude / iaExp.Magnitude < 0.01);
        Assert.True(Math.Abs(speed - 1.0) < 2e-4);
        Assert.True(Math.Abs(dDelta) < 0.01);
        Assert.True(Math.Abs(te - teExp) / teExp < 0.01);
        Assert.True(Math.Abs(ifd - ifdExp) / ifdExp < 0.005);
    }

    private sealed record ShortCircuit(NodalResult Res, double Dt, double Tf);

    private static ShortCircuit RunShortCircuit(SubtransientMachine.Parameters rp, double ra, double dt, double duration)
    {
        var net = new NodalNetwork();
        var data = new SynchronousMachineEmtData(rp, 100.0, 13.8, 50.0, ra);
        var mach = new SynchronousMachineEmt("G", net.Node("T0"), net.Node("T1"), net.Node("T2"), NodalNetwork.Ground, data);
        mach.InitialiseNoLoad(1.0);
        net.Add(mach);
        for (int k = 0; k < 3; k++) net.Add(new NodalSwitch($"F{k}", net.Node($"T{k}"), NodalNetwork.Ground, 1e-5, 1e9, false));
        double tf = 0.005;                    // omega tf = pi/2: phase a voltage at its zero crossing, maximum DC offset
        var events = Enumerable.Range(0, 3).Select(k => new NodalEvent(tf, $"F{k}", true)).ToList();
        var probes = new List<NodalProbe> { Ip("G", 0), Ip("G", 1), Ip("G", 2), Pv("T0") };
        NodalResult res = NodalSolver.Run(net, dt, duration, probes, events, new NodalOptions { EventRule = NodalEventRule.Trapezoidal });
        return new ShortCircuit(res, dt, tf);
    }

    [Fact]
    public void Sudden_three_phase_short_circuit_follows_the_classical_envelope_and_dc_time_constant()
    {
        SubtransientMachine.Parameters rp = Genr();       // X''d = X''q so there is no second-harmonic term in the closed form
        double dt = 50e-6, w0 = 2 * Pi * 50.0;
        ShortCircuit sc = RunShortCircuit(rp, Ra, dt, 2.2);
        Assert.Equal(NodalStatus.Completed, sc.Res.Status);
        // classical formula, computed here from the data: T'd = T'do X'd / Xd, T''d = T''do X''d / X'd, Ta = X''/(w Ra)
        double td1 = rp.Tdop * rp.Xdp / rp.Xd, td2 = rp.Tdopp * rp.Xdpp / rp.Xdp;
        double ta = rp.Xdpp / (w0 * Ra);
        double Env(double tau) => (1 / rp.Xdpp - 1 / rp.Xdp) * Math.Exp(-tau / td2) + (1 / rp.Xdp - 1 / rp.Xd) * Math.Exp(-tau / td1) + 1 / rp.Xd;
        output.WriteLine($"classical: T'd {td1:F4} s, T''d {td2:F5} s, Ta {ta:F4} s; I''(0) = {Env(0):F3} pu, I'(0) = {1 / rp.Xdp:F3} pu, I(inf) = {1 / rp.Xd:F3} pu");

        // positive-sequence space vector s = 2/3 (ia + a ib + a^2 ic): rotating-frame whole-cycle mean = AC amplitude, plain mean = DC vector
        double[] ia = sc.Res.Series[0], ib = sc.Res.Series[1], ic = sc.Res.Series[2], t = sc.Res.Times;
        Complex a = Complex.FromPolarCoordinates(1, 2 * Pi / 3);
        int spc = 400;
        (double Ac, double Dc) Window(double tauCentre)
        {
            int c0 = (int)Math.Round((sc.Tf + tauCentre) / dt) - spc / 2;
            Complex acc = 0, dcc = 0;
            for (int i = c0; i < c0 + spc; i++)
            {
                Complex s = 2.0 / 3.0 * (ia[i] + a * ib[i] + a * a * ic[i]);
                acc += s * Complex.Exp(new Complex(0, -w0 * t[i]));
                dcc += s;
            }
            return (acc.Magnitude / spc / IbPk, dcc.Magnitude / spc / IbPk);
        }
        double worstEnv = 0;
        foreach (double tau in new[] { 0.05, 0.1, 0.2, 0.4, 0.8, 1.5 })
        {
            (double ac, _) = Window(tau);
            double exp = Env(tau);
            worstEnv = Math.Max(worstEnv, Math.Abs(ac - exp) / exp);
            output.WriteLine($"tau {tau:F2} s: AC envelope {ac:F4} pu, classical {exp:F4} pu ({(ac - exp) / exp:P2})");
        }
        Assert.True(worstEnv < 0.06, $"envelope deviates by {worstEnv}");

        (double d1, double dc1) = (0.0, 0.0);
        (_, d1) = Window(0.05); (_, dc1) = Window(0.3);
        double taSim = 0.25 / Math.Log(d1 / dc1);
        double d0 = d1 * Math.Exp(0.05 / taSim);
        output.WriteLine($"DC vector: {d1:F4} pu at 0.05 s, {dc1:F4} pu at 0.3 s -> Ta {taSim:F4} s (classical {ta:F4}); extrapolated initial {d0:F3} pu (1/X''d = {1 / rp.Xdpp:F3})");
        Assert.True(Math.Abs(taSim - ta) / ta < 0.08);
        Assert.True(Math.Abs(d0 - 1 / rp.Xdpp) / (1 / rp.Xdpp) < 0.06);

        // full phase-a waveform over the first three cycles against the closed form with its DC offset
        double worst = 0, i0 = Env(0);
        for (int i = (int)Math.Round(sc.Tf / dt) + 1; i < (int)Math.Round((sc.Tf + 0.06) / dt); i++)
        {
            double tau = t[i] - sc.Tf;
            double exp = Env(tau) * Math.Sin(w0 * t[i]) - i0 * Math.Exp(-tau / ta) * Math.Sin(w0 * sc.Tf);
            worst = Math.Max(worst, Math.Abs(ia[i] / IbPk - exp));
        }
        output.WriteLine($"first three cycles: worst |i_a - closed form| = {worst:F4} pu = {worst / i0:P2} of I''(0)");
        Assert.True(worst / i0 < 0.10);
    }

    // ================================================================================================================
    // 4. Induction machine direct start: steady slip from the equivalent-circuit torque balance
    // ================================================================================================================

    private static InductionMachine Motor() => new(1, "M1", InductionPowerCode.Mechanical, 8.0, 10.0, 6.6, 0.02, 0.10, 3.5, 0.02, 0.12);

    private static double AirGapTorque(InductionMachine m, double s)
    {
        Complex zr = new(m.R1Pu / s, m.X1Pu), zm = new(0, m.XmPu);
        Complex zp = zm * zr / (zm + zr);
        Complex zin = new Complex(m.RaPu, m.XaPu) + zp;
        double i2 = 1.0 / (zin.Magnitude * zin.Magnitude);
        return i2 * zp.Real;
    }

    [Fact]
    public void Induction_machine_direct_start_ends_at_the_equivalent_circuit_torque_balance_slip()
    {
        InductionMachine m = Motor();
        const double kLoad = 0.8, h = 0.4;
        // oracle: bisection of Te(s) = kLoad (1 - s)^2 on the stable branch (0, 0.1)
        double lo = 1e-6, hi = 0.1;
        for (int it = 0; it < 200; it++)
        {
            double mid = 0.5 * (lo + hi);
            if (AirGapTorque(m, mid) < kLoad * (1 - mid) * (1 - mid)) lo = mid; else hi = mid;
        }
        double sExp = 0.5 * (lo + hi);
        // the incumbent steady-state circuit (InductionMachines.AtSlip) must give the same torque
        Assert.Equal(AirGapTorque(m, sExp), InductionMachines.AtSlip(m, 1.0, sExp).TorquePu, 9);
        Complex zin = new Complex(m.RaPu, m.XaPu) + new Complex(0, m.XmPu) * new Complex(m.R1Pu / sExp, m.X1Pu) / (new Complex(0, m.XmPu) + new Complex(m.R1Pu / sExp, m.X1Pu));
        double iPkExp = 1.0 / zin.Magnitude;

        var net = new NodalNetwork();
        double vb = 6600.0 * Math.Sqrt(2.0 / 3.0), ib = (2.0 / 3.0) * 10e6 / vb, w0 = 2 * Pi * 50.0;
        var nodes = new int[3];
        for (int k = 0; k < 3; k++)
        {
            int kk = k;
            nodes[k] = net.Node($"A{k}");
            net.Add(new NodalVoltageSource($"E{k}", nodes[k], 0.0, t => vb * Math.Sin(w0 * t - kk * 2 * Pi / 3)));
        }
        var mach = net.Add(new InductionMachineEmt("M", nodes[0], nodes[1], nodes[2], new InductionMachineEmtData(m, 50.0, h)));
        mach.LoadTorquePu = w => kLoad * w * w;
        double dt = 100e-6;
        var probes = new List<NodalProbe> { Ip("M", 0), Sp("M", 0), Sp("M", 1) };
        NodalResult res = NodalSolver.Run(net, dt, 3.0, probes, null, new NodalOptions { EventRule = NodalEventRule.Trapezoidal });
        Assert.Equal(NodalStatus.Completed, res.Status);
        int n = res.Times.Length, spc = 200, start = n - 4 * spc, count = 4 * spc;
        double speed = Mean(res.Series[1], start, count), te = Mean(res.Series[2], start, count);
        double sSim = 1 - speed;
        double accel = (res.Series[1][n - 1] - res.Series[1][n - 1 - spc]) / (spc * dt);
        Complex iph = Coef(res.Times, res.Series[0], start, count, 50.0);
        output.WriteLine($"steady slip: simulated {sSim:F6}, equivalent-circuit torque balance {sExp:F6}; Te {te:F4} vs load {kLoad * speed * speed:F4} pu; residual acceleration {accel:E2} pu/s; stator current {iph.Magnitude / ib:F4} pu vs {iPkExp:F4} pu");
        Assert.True(Math.Abs(sSim - sExp) < 3e-4, $"slip {sSim} vs {sExp}");
        Assert.True(Math.Abs(te - kLoad * speed * speed) < 5e-3);
        Assert.True(Math.Abs(accel) < 5e-3);
        Assert.True(Math.Abs(iph.Magnitude / ib - iPkExp) / iPkExp < 0.01);
        // the start: standstill current exceeds the running current several times (locked-rotor circuit)
        Assert.True(res.Series[0].Take(400).Max(Math.Abs) / ib > 3.0);
    }

    // ================================================================================================================
    // 5. Cross-check with the RMS study model (same parameter set), determinism and failure paths
    // ================================================================================================================

    [Fact]
    public void Rms_and_emt_agree_on_the_electromechanical_swing_of_the_same_machine()
    {
        SubtransientMachine.Parameters rp = Genr();
        const double delta0 = 0.6, eqp0 = 1.1, dP = 0.15; double xe = rp.ExternalReactance;
        (double pm, double efd, double eqp, double edp, double eqpp, double eddp) = SubtransientMachine.Equilibrium(rp, delta0, eqp0);
        SubtransientMachine.Trace tr = SubtransientMachine.Simulate(rp, delta0, 0.0, eqp, edp, eqpp, eddp, pm + dP, efd, 50.0, 0.001, 4.0);

        // EMT: the same machine on the same infinite bus through the same reactance; initial phasors from the RMS equilibrium
        (double id, double iq) = SubtransientMachine.Currents(rp, delta0, eqpp, eddp);
        double th0 = delta0 - Pi / 2;
        Complex iOut = new Complex(id, iq) * Complex.Exp(new Complex(0, th0));
        Complex vT = Complex.One + new Complex(0, xe) * iOut;
        double w0 = 2 * Pi * 50.0;
        var net = new NodalNetwork();
        var mach = new SynchronousMachineEmt("G", net.Node("T0"), net.Node("T1"), net.Node("T2"), NodalNetwork.Ground, GenData(0.0));
        SynchronousInitialState init = mach.InitialiseFromPhasors(vT, iOut);
        output.WriteLine($"RMS equilibrium Pm {pm:F5}; EMT initial electromagnetic torque {init.MechanicalTorquePu:F5}; load angle {init.LoadAngleRad:F5} vs delta0 - angle(Vt)");
        Assert.True(Math.Abs(init.MechanicalTorquePu - pm) < 2e-3, "same equilibrium in both models");
        mach.MechanicalTorquePu = _ => init.MechanicalTorquePu + dP;
        net.Add(mach);
        for (int k = 0; k < 3; k++)
        {
            int kk = k;
            int b = net.Node($"B{k}");
            net.Add(new NodalVoltageSource($"E{k}", b, 0.0, t => VbPk * Math.Cos(w0 * t - kk * 2 * Pi / 3)));
            Complex ik = iOut * Complex.Exp(new Complex(0, -k * 2 * Pi / 3));
            net.Add(NodalBranch.SeriesRl($"LN{k}", net.Node($"T{k}"), b, 0.0, xe * Zb / w0, IbPk * ik.Real));
        }
        NodalResult res = NodalSolver.Run(net, 50e-6, 4.0, [Sp("G", 1), Sp("G", 0)], null, new NodalOptions { SampleEvery = 20 });
        Assert.Equal(NodalStatus.Completed, res.Status);

        // swing of the load angle about its start, both models, sampled every 1 ms
        double Rms(double t) { int i = (int)Math.Round(t / 0.001); return tr.DeltaRad[i] - tr.DeltaRad[0]; }
        double Emt(double t) { int i = (int)Math.Round(t / 1e-3); return res.Series[0][i] - res.Series[0][0]; }
        double peakR = 0, peakE = 0, tpR = 0, tpE = 0, worst = 0;
        for (int i = 1; i <= 3990; i++)
        {
            double t = i * 1e-3;
            if (Rms(t) > peakR) { peakR = Rms(t); tpR = t; }
            if (Emt(t) > peakE) { peakE = Emt(t); tpE = t; }
            worst = Math.Max(worst, Math.Abs(Rms(t) - Emt(t)));
        }
        output.WriteLine($"first swing peak: RMS {peakR:F4} rad at {tpR:F3} s; EMT {peakE:F4} rad at {tpE:F3} s; worst |difference| over 2.5 s = {worst:F4} rad ({worst / peakR:P1} of the peak)");
        Assert.True(Math.Abs(peakE - peakR) / peakR < 0.08, "first-swing peak");
        Assert.True(Math.Abs(tpE - tpR) / tpR < 0.04, "swing period");
        Assert.True(worst / peakR < 0.12);
    }

    [Fact]
    public void Switched_converter_machine_and_bridge_runs_are_bit_identical_across_repeats_and_thread_counts()
    {
        NodalResult Run()
        {
            var net = new NodalNetwork();
            int p = net.Node("P"), n = net.Node("N");
            net.Add(new NodalVoltageSource("Ep", p, 0.0, _ => Vdc / 2));
            net.Add(new NodalVoltageSource("En", n, 0.0, _ => -Vdc / 2));
            var ac = new[] { net.Node("A0"), net.Node("A1"), net.Node("A2") };
            int nn = net.Node("NN");
            var pwm = new SinePwm(Mf * F1, 0.125, SinePwm.SineReference(F1, Mi, 0.2));
            TwoLevelBridge.AddVsc(net, "C", p, n, ac, new ValveParameters(RonVsc, 1e7, 50.0, 1e-7), pwm);
            for (int k = 0; k < 3; k++) net.Add(NodalBranch.SeriesRl($"RL{k}", ac[k], nn, RLoad, LLoad));
            var mach = new SynchronousMachineEmt("G", net.Node("T0"), net.Node("T1"), net.Node("T2"), NodalNetwork.Ground, GenData());
            mach.InitialiseNoLoad(1.0);
            net.Add(mach);
            for (int k = 0; k < 3; k++) net.Add(new NodalSwitch($"F{k}", net.Node($"T{k}"), NodalNetwork.Ground, 1e-5, 1e9, false));
            return NodalSolver.Run(net, 5e-6, 0.04, [Pv("A0"), Pv("NN"), Ip("G", 0), Sp("G", 2), Sp("C.up0", 0)],
                [new NodalEvent(0.01, "F0", true), new NodalEvent(0.01, "F1", true), new NodalEvent(0.01, "F2", true)], new NodalOptions { EventRule = NodalEventRule.BackwardEulerStep });
        }
        static bool Same(NodalResult x, NodalResult y) => x.Series.Zip(y.Series).All(p => p.First.Length == p.Second.Length
            && p.First.Zip(p.Second).All(q => BitConverter.DoubleToInt64Bits(q.First) == BitConverter.DoubleToInt64Bits(q.Second))) && x.Stats == y.Stats;
        NodalResult a = Run();
        Assert.Equal(NodalStatus.Completed, a.Status);
        Assert.True(Same(a, Run()));
        foreach (int threads in new[] { 1, 4 })
        {
            var results = new NodalResult[4];
            Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = threads }, i => results[i] = Run());
            Assert.All(results, r => Assert.True(Same(a, r)));
        }
    }

    [Fact]
    public void Unusable_converters_machines_and_modulators_are_refused_by_name()
    {
        Assert.Throws<ArgumentException>(() => new NodalValve("V", 0, 0, ValveKind.Diode, 1e-3, 1e6));
        Assert.Throws<ArgumentException>(() => new NodalValve("V", 0, 1, ValveKind.Thyristor, 1e-3, 1e6));
        Assert.Throws<ArgumentException>(() => new NodalValve("V", 0, 1, ValveKind.Diode, 1e-3, 1e-4));
        Assert.Throws<ArgumentException>(() => new SinePwm(0.0, 0.0, (t, k) => 0.0));
        var net = new NodalNetwork();
        Assert.Throws<ArgumentException>(() => TwoLevelBridge.Add(net, "B", 0, 1, [2, 3], ValveKind.Diode, new ValveParameters(), null, ""));
        Assert.Throws<ArgumentException>(() => new AverageValueConverter("C", 0, 1, [2, 3, 4], 0.0, 1000, (t, k) => 0.0));

        // a machine that was never initialised is refused when the run starts
        var n2 = new NodalNetwork();
        var m = n2.Add(new SynchronousMachineEmt("G", n2.Node("a"), n2.Node("b"), n2.Node("c"), NodalNetwork.Ground, GenData()));
        ArgumentException ex = Assert.Throws<ArgumentException>(() => NodalSolver.Run(n2, 1e-4, 0.01, [Pv("a")]));
        Assert.Contains("G", ex.Message);
        // a leakage reactance that does not fit the subtransient reactance is refused with the numbers
        ArgumentException ex2 = Assert.Throws<ArgumentException>(() => SynchronousMachineCircuit.From(new SynchronousMachineEmtData(Genr(), 100, 13.8, 50, 0.003, -0.1)));
        Assert.Contains("zero-sequence", ex2.Message);
        // an induction machine without a positive rotor resistance is refused
        var badIm = new InductionMachine(1, "M", InductionPowerCode.Mechanical, 1, 10, 6.6, 0.02, 0.1, 3.5, 0.0, 0.12);
        Assert.Throws<ArgumentException>(() => new InductionMachineEmt("M", 0, 1, 2, new InductionMachineEmtData(badIm, 50, 0.4)));
    }
}
