using System.Numerics;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.Hvdc;
using GridSim.Core.Dynamics.Emt.Nodal;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-19: MMC arm-average and sub-module-level models. Oracles are the average-value equations of the MMC, derived in the test comments:
/// arm energy balance, the arm voltage law, the DC loop law and the second-harmonic circulating current. COMPARATOR-UNVERIFIED: no PSCAD, EMTP or
/// CIGRE TB 604 waveform is compared; the CIGRE parameter sets are public but the expected waveforms are tool outputs.
/// </summary>
public sealed class R419MmcTests(ITestOutputHelper output)
{
    private const double Vdc = 20000.0, Vm = 8000.0, F = 50.0, Dt = 5e-5;
    private static readonly double W = 2.0 * Math.PI * F;

    // ---- helpers ----
    internal static double[] Ser(NodalResult r, string name) => r.Series[r.ProbeNames.ToList().IndexOf(name)];

    /// <summary>Complex peak phasor (cosine convention) of harmonic <paramref name="h"/> over the samples with t0 &lt;= t &lt; t1 (whole cycles).</summary>
    internal static Complex Ph(NodalResult r, double[] x, double t0, double t1, int h = 1)
    {
        double w = 2.0 * Math.PI * F * h, re = 0, im = 0; int n = 0;
        for (int i = 0; i < r.Times.Length; i++)
        {
            double t = r.Times[i];
            if (t < t0 - 1e-12 || t >= t1 - 1e-12) continue;
            re += x[i] * Math.Cos(w * t); im -= x[i] * Math.Sin(w * t); n++;
        }
        return new Complex(2.0 * re / n, 2.0 * im / n);
    }

    internal static double Mean(NodalResult r, double[] x, double t0, double t1)
    {
        double s = 0; int n = 0;
        for (int i = 0; i < r.Times.Length; i++)
        {
            double t = r.Times[i];
            if (t < t0 - 1e-12 || t >= t1 - 1e-12) continue;
            s += x[i]; n++;
        }
        return s / n;
    }

    private sealed record Rig(NodalNetwork Net, VscHvdcStation Station, NodalResult Res);

    private static MmcParameters Arm(int n = 20) => new(n, 0.02, 0.1, 6e-3, Vdc);

    private static Rig RunStation(VscStationConverter kind, double suppressionAlpha, double duration, double dt = Dt, double pInject = 4e6)
    {
        var net = new NodalNetwork();
        var spec = new VscStationSpec
        {
            GridPeakVolts = Vm, FrequencyHz = F, GridAngleRad = 0.0, GridROhm = 0.05, GridLHenry = 1e-3, FilterROhm = 0.02, FilterLHenry = 5e-3,
            DcNominalVolts = Vdc, DcCapF = 1e-3, Converter = kind, Mmc = Arm(), Mode = VscStationMode.Power, SetPoint = pInject,
            CurrentBandwidthRadPerS = 1000.0, OuterBandwidthRadPerS = 60.0, ControlEvery = 2, SuppressionBandwidthRadPerS = suppressionAlpha,
        };
        VscHvdcStation st = VscHvdcStation.Add(net, "M", "", spec, dt);
        // a stiff DC supply (ideal +/- Vdc/2 about the grounded mid-point)
        net.Add(new NodalVoltageSource("dcp", st.DcPositive, 0.0, _ => 0.5 * Vdc));
        net.Add(new NodalVoltageSource("dcn", st.DcNegative, 0.0, _ => -0.5 * Vdc));
        var probes = new List<NodalProbe>();
        for (int k = 0; k < 3; k++)
        {
            probes.Add(new NodalProbe($"iu{k}", NodalProbeKind.ElementCurrent, $"M.mmc.u{k}"));
            probes.Add(new NodalProbe($"il{k}", NodalProbeKind.ElementCurrent, $"M.mmc.l{k}"));
        }
        probes.Add(new NodalProbe("vcu0", NodalProbeKind.ElementState, "M.mmc.u0", 0));
        probes.Add(new NodalProbe("vcl0", NodalProbeKind.ElementState, "M.mmc.l0", 0));
        probes.Add(new NodalProbe("wu0", NodalProbeKind.ElementState, "M.mmc.u0", 1));
        probes.Add(new NodalProbe("nu0", NodalProbeKind.ElementState, "M.mmc.u0", 3));
        probes.Add(new NodalProbe("nl0", NodalProbeKind.ElementState, "M.mmc.l0", 3));
        probes.Add(new NodalProbe("vm0", NodalProbeKind.NodeVoltage, "cv0"));
        probes.Add(new NodalProbe("vdcnode", NodalProbeKind.NodeVoltage, "dc.p"));
        NodalResult res = NodalSolver.Run(net, dt, duration, probes);
        Assert.Equal(NodalStatus.Completed, res.Status);
        return new Rig(net, st, res);
    }

    /// <summary>The operating-point quantities of leg 0 over the window, in the frame of the modulation reference <c>m = mhat sin(w t')</c>.</summary>
    private sealed record Op(double MHat, double Phi, double IHat, double VBar, double Idc, double ThetaM, Complex Iprime);

    private static Op Operating(NodalResult r, double t0, double t1)
    {
        double[] nu = Ser(r, "nu0"), iu = Ser(r, "iu0"), il = Ser(r, "il0"), vcu = Ser(r, "vcu0"), vcl = Ser(r, "vcl0");
        double[] m = nu.Select(n => 1.0 - 2.0 * n).ToArray();           // z = 0 (no suppression) so m = 1 - 2 n_u exactly
        double[] iac = iu.Zip(il, (a, b) => a - b).ToArray();
        Complex pm = Ph(r, m, t0, t1), pi = Ph(r, iac, t0, t1);
        double thetaM = pm.Phase;
        Complex ip = pi * Complex.Exp(new Complex(0, -(thetaM + Math.PI / 2)));      // primed frame: m = mhat sin(w t'), t' = t + (thetaM + pi/2)/w
        // i' = ihat sin(w t' - phi)  ->  phasor -j ihat e^(-j phi)  ->  phi = -arg(j i')
        double phi = -(Complex.ImaginaryOne * ip).Phase;
        double vbar = 0.5 * (Mean(r, vcu, t0, t1) + Mean(r, vcl, t0, t1));
        double idc = 3.0 * Mean(r, iu, t0, t1);
        return new Op(pm.Magnitude, phi, pi.Magnitude, vbar, idc, thetaM, ip);
    }

    /// <summary>The second-harmonic circulating current phasor of the primed frame from the average-value equations, with the coupling of the
    /// circulating current back into the arm-capacitor ripple (see the comment on the circulating-current test). Also returns the fundamental
    /// ripple integral P_t of the arm.</summary>
    private static Complex SolveCirculating(Op op, MmcParameters p, out Complex t0Phasor, out Complex pt)
    {
        double c = p.ArmCapacitanceF, r = p.ArmResistanceOhm, l = p.ArmInductanceH, m = op.MHat;
        double fs = 0.5 * op.IHat * Math.Cos(op.Phi) * (1.0 - m * m / 2.0), fc = -0.5 * op.IHat * Math.Sin(op.Phi);
        Complex pt0 = new Complex(-fs, -fc) / W;
        Complex q = new Complex(0, -1) * m * op.IHat * Complex.Exp(new Complex(0, -op.Phi)) / (16.0 * c * W);
        t0Phasor = q + m / (4.0 * c * W) * new Complex(fc, -fs);
        var den = new Complex(2.0 * r, 4.0 * W * l - 1.0 / (4.0 * c * W) - m * m / (8.0 * c * W));
        Complex ic = -t0Phasor / den;
        pt = pt0 - 0.5 * m * ic / W;
        return ic;
    }

    // ==================================================================================================================
    // Steady state from the average-value equations (arm model, no suppression)
    //
    // Leg: upper arm n_u = 1/2 - m s/2, lower n_l = 1/2 + m s/2 (s = sin w t'), arm currents i_u = idc/3 + i_ac/2 + i_c, i_l = idc/3 - i_ac/2 + i_c,
    // i_ac = I sin(w t' - phi), arm capacitor C dV/dt = n i, so dW/dt = V n i (W = C V^2/2).
    //  * mean(n_u i_u) = 0 (energy balance)  =>  idc/3 = m I cos(phi) / 4, and the DC power  Vdc idc = 3/4 m V I cos(phi).
    //  * pole voltage  v_M = (v_l - v_u)/2 - (R + sL) i_ac / 2,  v = n V:  fundamental  m Vbar s / 2 - Vripple/4 - (Z_arm/2) i_ac  where the arm
    //    capacitor ripple V~_u = P/(2C) (fundamental), P = (-Fs cos + Fc sin)/w, Fs = I cos(phi)(1 - m^2/2)/2, Fc = -I sin(phi)/2.
    //  * DC loop  Vdc = mean(v_u + v_l) + 2 R idc/3  =>  Vbar = Vdc - 2 R idc / 3 (ripple correlation < 1e-3 Vdc).
    //  * second harmonic: see the circulating-current test.
    // ==================================================================================================================
    [Fact]
    public void Arm_average_steady_state_satisfies_the_average_value_equations()
    {
        Rig rig = RunStation(VscStationConverter.MmcArmAverage, 0.0, 0.8);
        NodalResult r = rig.Res;
        const double t0 = 0.7, t1 = 0.8;                              // five whole cycles
        Op op = Operating(r, t0, t1);
        MmcParameters p = Arm();
        double C = p.ArmCapacitanceF, R = p.ArmResistanceOhm, L = p.ArmInductanceH;
        output.WriteLine($"mhat {op.MHat:F4}  phi {op.Phi:F4} rad  Ihat {op.IHat:F2} A  Vbar {op.VBar:F1} V  Idc {op.Idc:F3} A");

        // 1. energy balance: idc / 3 = m I cos(phi) / 4
        double idcClosed = 0.75 * op.MHat * op.IHat * Math.Cos(op.Phi);
        output.WriteLine($"Idc measured {op.Idc:F3} A, closed form 3/4 m I cos(phi) = {idcClosed:F3} A");
        Assert.InRange(Math.Abs(op.Idc - idcClosed) / idcClosed, 0.0, 0.01);

        // 2. DC loop law
        double vbarClosed = Vdc - 2.0 * R * op.Idc / 3.0;
        output.WriteLine($"Vbar measured {op.VBar:F2} V, closed form Vdc - 2 R idc/3 = {vbarClosed:F2} V");
        Assert.InRange(Math.Abs(op.VBar - vbarClosed) / Vdc, 0.0, 1e-3);

        // 3. pole-voltage fundamental from the arm voltage law (primed frame, then rotated back)
        SolveCirculating(op, p, out _, out Complex pt);                              // cosine phasor of the arm ripple integral P_t(t), primed frame
        Complex zArm = new(R, W * L);
        Complex vPrime = op.MHat * op.VBar * new Complex(0, -1) / 2.0 - pt / (4.0 * C) - zArm / 2.0 * op.Iprime;
        Complex vClosed = vPrime * Complex.Exp(new Complex(0, op.ThetaM + Math.PI / 2));
        Complex vMeas = Ph(r, Ser(r, "vm0"), t0, t1);
        double relV = (vClosed - vMeas).Magnitude / vMeas.Magnitude;
        output.WriteLine($"pole voltage fundamental: measured {vMeas.Magnitude:F2} V, closed form {vClosed.Magnitude:F2} V, rel. complex difference {relV:E2};"
            + $" simple m Vbar/2 = {op.MHat * op.VBar / 2.0:F2} V");
        Assert.InRange(relV, 0.0, 3e-3);

        // 4. power balance: Pdc - Pac(terminal) = arm losses 6 R (idc^2/9 + I^2/8 + Ic2^2/2)
        double[] iu = Ser(r, "iu0"), il = Ser(r, "il0"), vm = Ser(r, "vm0");
        double[] iac = iu.Zip(il, (a, b) => a - b).ToArray();
        double[] ic = iu.Zip(il, (a, b) => 0.5 * (a + b)).ToArray();
        double ic2 = Ph(r, ic, t0, t1, 2).Magnitude;
        double pM = 0; int n = 0;
        for (int i = 0; i < r.Times.Length; i++) if (r.Times[i] >= t0 - 1e-12 && r.Times[i] < t1 - 1e-12) { pM += vm[i] * iac[i]; n++; }
        pM = 3.0 * pM / n;
        double pDc = Vdc * op.Idc;
        double loss = 6.0 * R * (op.Idc * op.Idc / 9.0 + op.IHat * op.IHat / 8.0 + ic2 * ic2 / 2.0);
        output.WriteLine($"Pdc {pDc / 1e6:F4} MW, Pac(terminal) {pM / 1e6:F4} MW, difference {(pDc - pM) / 1e3:F3} kW, arm loss closed form {loss / 1e3:F3} kW");
        Assert.InRange(Math.Abs(pDc - pM - loss), 0.0, 0.15 * loss);

        // 5. arm energy ripple at the fundamental: dW/dt = V n i, amplitude Vbar |P_t| / 2
        double[] wu = Ser(r, "wu0");
        double w1 = Ph(r, wu, t0, t1).Magnitude;
        double w1Closed = op.VBar * pt.Magnitude / 2.0;
        output.WriteLine($"arm energy fundamental ripple: measured {w1:F2} J, closed form {w1Closed:F2} J, mean W {Mean(r, wu, t0, t1) / 1e3:F2} kJ (C Vbar^2/2 = {0.5 * C * op.VBar * op.VBar / 1e3:F2} kJ)");
        Assert.InRange(Math.Abs(w1 - w1Closed) / w1Closed, 0.0, 0.03);
        double wMean = Mean(r, wu, t0, t1);
        Assert.InRange(Math.Abs(wMean - 0.5 * C * op.VBar * op.VBar) / wMean, 0.0, 1e-3);
    }

    // ==================================================================================================================
    // Circulating current: second harmonic from the average-value equations, and its suppression.
    //
    // With i_c the circulating current, 2 (R + sL) (idc/3 + i_c) = Vdc - (v_u + v_l). The second harmonic of v_u + v_l is
    //   T0 + V~' + (the same coupling through the fundamental ripple),  T0 = Q + (m/(4 C w)) (Fc - j Fs)  (phasor, primed frame;
    //   Q = -j m I e^(-j phi) / (16 C w)) the part driven by the AC current, V~' = Ic / (j 4 C w) the part driven by the circulating current
    //   itself (n_u i_c = i_c/2 + ...), and the product -(m/2) s i_c of the insertion fundamental with the circulating current adds -(m/2) Ic/w to the
    //   fundamental ripple integral P_t, which feeds back as j m P_t/(4C) = ... - j m^2 Ic / (8 C w).
    // Hence  Ic = -T0 / (2 R + j (4 w L - 1/(4 C w) - m^2/(8 C w))).  The same -(m/2) Ic/w term makes the fundamental arm-energy ripple 7 % larger than
    // the value from the AC current alone.
    // ==================================================================================================================
    [Fact]
    public void Second_harmonic_circulating_current_matches_the_closed_form_and_vanishes_with_suppression()
    {
        Rig off = RunStation(VscStationConverter.MmcArmAverage, 0.0, 0.7);
        NodalResult r = off.Res;
        const double t0 = 0.6, t1 = 0.7;
        Op op = Operating(r, t0, t1);
        MmcParameters p = Arm();
        double C = p.ArmCapacitanceF, R = p.ArmResistanceOhm, L = p.ArmInductanceH;
        Complex icClosed = SolveCirculating(op, p, out Complex t0Phasor, out _);
        double naive = t0Phasor.Magnitude / (2.0 * new Complex(R, 2.0 * W * L).Magnitude);

        var ic2 = new Complex[3];
        for (int k = 0; k < 3; k++)
        {
            double[] ic = Ser(r, $"iu{k}").Zip(Ser(r, $"il{k}"), (a, b) => 0.5 * (a + b)).ToArray();
            ic2[k] = Ph(r, ic, t0, t1, 2);
        }
        double meas = ic2[0].Magnitude;
        output.WriteLine($"unsuppressed 2nd harmonic circulating current: leg a {ic2[0].Magnitude:F3} A, leg b {ic2[1].Magnitude:F3} A, leg c {ic2[2].Magnitude:F3} A; "
            + $"closed form {icClosed.Magnitude:F3} A (without the self term {naive:F3} A); Ihat {op.IHat:F1} A");
        Assert.InRange(Math.Abs(meas - icClosed.Magnitude) / icClosed.Magnitude, 0.0, 0.04);
        Assert.InRange(Math.Abs(ic2[1].Magnitude - meas) / meas, 0.0, 0.02);
        Assert.InRange(Math.Abs(ic2[2].Magnitude - meas) / meas, 0.0, 0.02);
        // negative sequence: the legs rotate 2 psi_j with psi = 0, -120, +120, so the three phasors sum to zero in the frame rotated by 2 psi
        Complex a = new(-0.5, Math.Sqrt(3.0) / 2.0);
        Complex posSeq = (ic2[0] + a * ic2[1] + a * a * ic2[2]) / 3.0;
        Complex negSeq = (ic2[0] + a * a * ic2[1] + a * ic2[2]) / 3.0;
        output.WriteLine($"2nd harmonic sequence content: positive {posSeq.Magnitude:F4} A, negative {negSeq.Magnitude:F4} A");
        Assert.True(posSeq.Magnitude < 0.03 * meas, "the second harmonic is not positive-sequence");
        Assert.True(negSeq.Magnitude > 0.97 * meas, "the second harmonic is negative-sequence");

        // With the suppression control on the second harmonic vanishes (integral action) and the power is unchanged.
        Rig on = RunStation(VscStationConverter.MmcArmAverage, 250.0, 0.7);
        NodalResult r2 = on.Res;
        // the suppression control makes z != 0, so use the measured circulating currents only
        double worst = 0, worstOff = 0;
        for (int k = 0; k < 3; k++)
        {
            double[] icOn = Ser(r2, $"iu{k}").Zip(Ser(r2, $"il{k}"), (x, y) => 0.5 * (x + y)).ToArray();
            worst = Math.Max(worst, Ph(r2, icOn, t0, t1, 2).Magnitude);
            worstOff = Math.Max(worstOff, ic2[k].Magnitude);
        }
        double idcOn = 3.0 * Mean(r2, Ser(r2, "iu0"), t0, t1), idcOff = op.Idc;
        output.WriteLine($"with suppression: worst 2nd harmonic {worst:E3} A (off: {worstOff:F3} A); Idc {idcOn:F3} A (off {idcOff:F3} A)");
        Assert.True(worst < 0.02 * worstOff, "second harmonic suppressed to under 2 % of the uncontrolled value");
        Assert.InRange(Math.Abs(idcOn - idcOff) / idcOff, 0.0, 0.01);
        // The suppression removes the second harmonic from the arm current: the mean square of an arm current falls by Ic2^2 / 2 (the loss falls with it).
        // (The PEAK current need not fall: the second harmonic can cancel the fundamental's peak.)
        double ms(NodalResult rr) { double[] x = Ser(rr, "iu0"); double acc = 0; int cnt = 0; for (int i = 0; i < rr.Times.Length; i++) if (rr.Times[i] >= t0 - 1e-12 && rr.Times[i] < t1 - 1e-12) { acc += x[i] * x[i]; cnt++; } return acc / cnt; }
        double msOff = ms(r), msOn = ms(r2), expected = ic2[0].Magnitude * ic2[0].Magnitude / 2.0;
        output.WriteLine($"arm current mean square: off {msOff:F1} A^2, on {msOn:F1} A^2, fall {msOff - msOn:F2} A^2; Ic2^2/2 = {expected:F2} A^2");
        Assert.InRange(Math.Abs((msOff - msOn) - expected) / expected, 0.0, 0.2);
    }

    [Fact]
    public void Sub_module_level_model_agrees_with_the_arm_average_model_and_balances_its_capacitors()
    {
        Rig avg = RunStation(VscStationConverter.MmcArmAverage, 0.0, 0.5);
        Rig det = RunStation(VscStationConverter.MmcDetailed, 0.0, 0.5);
        const double t0 = 0.4, t1 = 0.5;
        double[] iaA = Ser(avg.Res, "iu0").Zip(Ser(avg.Res, "il0"), (a, b) => a - b).ToArray();
        double[] iaD = Ser(det.Res, "iu0").Zip(Ser(det.Res, "il0"), (a, b) => a - b).ToArray();
        double iA = Ph(avg.Res, iaA, t0, t1).Magnitude, iD = Ph(det.Res, iaD, t0, t1).Magnitude;
        double vA = Ph(avg.Res, Ser(avg.Res, "vm0"), t0, t1).Magnitude, vD = Ph(det.Res, Ser(det.Res, "vm0"), t0, t1).Magnitude;
        double idcA = 3 * Mean(avg.Res, Ser(avg.Res, "iu0"), t0, t1), idcD = 3 * Mean(det.Res, Ser(det.Res, "iu0"), t0, t1);
        output.WriteLine($"AC current amplitude: average {iA:F2} A, detailed {iD:F2} A; pole voltage: {vA:F1} V, {vD:F1} V; Idc {idcA:F2} A, {idcD:F2} A");
        Assert.InRange(Math.Abs(iA - iD) / iA, 0.0, 0.015);
        Assert.InRange(Math.Abs(vA - vD) / vA, 0.0, 0.015);
        Assert.InRange(Math.Abs(idcA - idcD) / idcA, 0.0, 0.02);
        double wA = Mean(avg.Res, Ser(avg.Res, "wu0"), t0, t1), wD = Mean(det.Res, Ser(det.Res, "wu0"), t0, t1);
        Assert.InRange(Math.Abs(wA - wD) / wA, 0.0, 0.01);

        // capacitor voltage balancing: every sub-module of every arm within a few percent of the arm mean at the end
        double worst = 0.0;
        foreach (MmcArm arm in det.Station.Mmc!.Arms)
        {
            var st = (DetailedArmBank)arm.Bank;
            double mean = st.SubmoduleVoltages.Average();
            double spread = (st.SubmoduleVoltages.Max() - st.SubmoduleVoltages.Min()) / mean;
            worst = Math.Max(worst, spread);
            Assert.Equal(20, st.SubmoduleVoltages.Count);
        }
        output.WriteLine($"worst sub-module voltage spread (max - min)/mean over the six arms: {worst:P2}");
        Assert.True(worst < 0.05, "sorted balancing keeps the sub-module voltages together");
    }

    [Fact]
    public void Balancing_order_is_a_total_order_with_the_index_tie_break()
    {
        // All equal: the first k indices are inserted, so the result does not depend on the sort algorithm.
        var s = new DetailedArmBank(8, 1e-3, 1000.0);
        double e = s.Emf(0.5, 1.0, 1e-5);                     // k = 4, charging: lowest voltages first (all equal: indices 0..3)
        Assert.Equal(4000.0, e);
        s.Accept(0.5, 100.0, 1e-3);                            // sub-modules 0..3 gain 100 V
        var v = s.SubmoduleVoltages;
        Assert.Equal(1100.0, v[0]); Assert.Equal(1100.0, v[3]); Assert.Equal(1000.0, v[4]); Assert.Equal(1000.0, v[7]);
        // charging again with k = 4: the four LOWEST (4..7) go in; discharging: the four HIGHEST (0..3)
        Assert.Equal(4000.0, s.Emf(0.5, 1.0, 1e-5));
        Assert.Equal(4400.0, s.Emf(0.5, -1.0, 1e-5));
        // nearest level, midpoint away from zero: n = 0.3125 * 8 = 2.5 -> 3 sub-modules
        Assert.Equal(3, (int)Math.Round(0.3125 * 8, MidpointRounding.AwayFromZero));
        s.Emf(0.3125, -1.0, 1e-5);
        Assert.Equal(3, s.InsertedCount);
        // bit repeat of a full run
        Rig a = RunStation(VscStationConverter.MmcDetailed, 0.0, 0.06), b = RunStation(VscStationConverter.MmcDetailed, 0.0, 0.06);
        for (int k = 0; k < a.Res.Series.Length; k++)
            for (int i = 0; i < a.Res.Series[k].Length; i++)
                Assert.Equal(BitConverter.DoubleToInt64Bits(a.Res.Series[k][i]), BitConverter.DoubleToInt64Bits(b.Res.Series[k][i]));
    }

    // ==================================================================================================================
    // DC pole-to-pole fault: the initial rise of the fault current is V/L.
    //
    // MMC (N sub-modules, arms R, L, C_arm) with an inactive AC side (m = 0, n_u = n_l = 1/2) charged to Vdc, behind a DC reactor Ldc to a stiff
    // DC source Vdc. A bolted fault at the converter DC bus at t0. Source branch: Ldc di/dt = Vdc, so i_s = Vdc t / Ldc. Each of the three legs is a series RLC
    // loop: x = v_u + v_l = (V_u + V_l)/2 obeys x' = i/(2C) (so the loop capacitance is 2 C), loop L = 2L, R = 2R:
    // I'' + (R/L) I' + I / (4 L C) = 0, I(0) = 0, I'(0) = Vdc/(2L)  =>  I(t) = Vdc / (2 L wd) e^(-a t) sin(wd t), a = R/(2L).
    // ==================================================================================================================
    [Fact]
    public void Dc_fault_current_initial_rise_is_V_over_L_and_follows_the_series_RLC()
    {
        const double Ldc = 0.02, dt = 1e-5, tFault = 1e-3;
        MmcParameters p = Arm();
        var net = new NodalNetwork();
        int sp = net.Node("sp"), sn = net.Node("sn"), bp = net.Node("bp"), bn = net.Node("bn");
        net.Add(new NodalVoltageSource("Vp", sp, 0.0, _ => 0.5 * Vdc));
        net.Add(new NodalVoltageSource("Vn", sn, 0.0, _ => -0.5 * Vdc));
        net.Add(NodalBranch.Inductor("Ldp", sp, bp, 0.5 * Ldc));
        net.Add(NodalBranch.Inductor("Ldn", sn, bn, 0.5 * Ldc));
        var ac = new int[3];
        for (int k = 0; k < 3; k++) ac[k] = net.Node($"a{k}");
        MmcConverter.Add(net, "M", bp, bn, ac, p, MmcStackKind.ArmAverage, (t, k) => 0.0);
        net.Add(new NodalSwitch("F", bp, bn, 1e-4, 1e9, false));
        var res = NodalSolver.Run(net, dt, 4e-3, [new NodalProbe("i", NodalProbeKind.ElementCurrent, "F"), new NodalProbe("vb", NodalProbeKind.NodeVoltage, "bp")],
            [new NodalEvent(tFault, "F", true)]);
        Assert.Equal(NodalStatus.Completed, res.Status);
        double[] i = res.Series[0];
        int k0 = (int)Math.Round(tFault / dt);
        double slopeClosed = Vdc / Ldc + 3.0 * Vdc / (2.0 * p.ArmInductanceH);            // A/s: source + three legs
        double slope = (i[k0 + 9] - i[k0 + 4]) / (5 * dt);
        output.WriteLine($"fault current initial slope {slope:E4} A/s; V/L closed form: source {Vdc / Ldc:E3} + MMC {3 * Vdc / (2 * p.ArmInductanceH):E3} = {slopeClosed:E4} A/s");
        Assert.InRange(Math.Abs(slope - slopeClosed) / slopeClosed, 0.0, 5e-3);
        Assert.True(Math.Abs(i[k0 - 1]) < 1e-2, "no current before the fault");

        // The full series-RLC response at 1 and 2 ms after the fault (the trapezoidal rule places the step half a step late).
        double a = p.ArmResistanceOhm / (2.0 * p.ArmInductanceH), w0 = 1.0 / (2.0 * Math.Sqrt(p.ArmInductanceH * p.ArmCapacitanceF)), wd = Math.Sqrt(w0 * w0 - a * a);
        foreach (int steps in new[] { 100, 200, 300 })
        {
            double t = (steps + 0.5) * dt;
            double closed = 3.0 * Vdc / (2.0 * p.ArmInductanceH * wd) * Math.Exp(-a * t) * Math.Sin(wd * t) + Vdc * t / Ldc;
            double meas = i[k0 + steps];                  // the switch current from bp to bn is the fault current
            output.WriteLine($"t = {steps * dt * 1e3:F2} ms after the fault: simulated {meas:F1} A, series RLC + source {closed:F1} A");
            Assert.InRange(Math.Abs(meas - closed) / closed, 0.0, 0.02);
        }
    }
}
