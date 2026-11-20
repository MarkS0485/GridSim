using System.Numerics;
using GridSim.Core.Dynamics.Emt;
using GridSim.Core.Dynamics.Emt.FrequencyDependent;
using GridSim.Core.Dynamics.Emt.Nodal;
using GridSim.Core.Dynamics.Emt.Nonlinear;
using GridSim.Core.Lines;
using Xunit;
using Xunit.Abstractions;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-16, EMT depth A on the new nodal solver. Every oracle is derived in this file: closed-form RLC responses, a
/// reflection-coefficient lattice, the distortionless-line attenuation, an independent bisection for the arrester operating
/// point, the closed-form peak flux linkage, and the analytic open-end impedance Z0 coth(gamma l) of the R4-04 line constants.
/// Comparison with a commercial EMT tool is COMPARATOR-UNVERIFIED (none available).
/// </summary>
public sealed class R416EmtDepthATests(ITestOutputHelper output)
{
    private const double Pi = Math.PI;

    private static List<NodalProbe> V(params string[] nodes) => nodes.Select(n => new NodalProbe("v:" + n, NodalProbeKind.NodeVoltage, n)).ToList();

    // ---- closed-form series RLC driven by V sin(w t) from rest ---------------------------------------------------

    private static double RlcSineCurrent(double r, double l, double c, double vpk, double w, double t)
    {
        Complex z = new(r, w * l - 1.0 / (w * c));
        Complex ph = vpk / z;
        double ipP(double tt) => (ph * Complex.Exp(new Complex(0, w * tt))).Imaginary;
        double dipP(double tt) => (new Complex(0, w) * ph * Complex.Exp(new Complex(0, w * tt))).Imaginary;
        double alpha = r / (2 * l), wd = Math.Sqrt(1.0 / (l * c) - alpha * alpha);
        double a = -ipP(0), b = (alpha * a - dipP(0)) / wd;
        return ipP(t) + Math.Exp(-alpha * t) * (a * Math.Cos(wd * t) + b * Math.Sin(wd * t));
    }

    private static NodalResult RunRlc(double dt, double duration, double r, double l, double c, double vpk, double f, bool withSwitch,
        NodalEventRule rule = NodalEventRule.Trapezoidal, double tClose = 0.0)
    {
        var net = new NodalNetwork();
        int s = net.Node("S"), b = net.Node("B");
        int a = s;
        double w = 2 * Pi * f;
        net.Add(new NodalVoltageSource("E", s, 0.0, t => vpk * Math.Sin(w * t)));
        if (withSwitch) { a = net.Node("A"); net.Add(new NodalSwitch("SW", s, a, 1e-6, 1e9, false)); }
        net.Add(NodalBranch.SeriesRl("RL", a, b, r, l));
        net.Add(NodalBranch.Capacitor("C", b, NodalNetwork.Ground, c));
        var events = withSwitch ? new[] { new NodalEvent(tClose, "SW", true) } : null;
        return NodalSolver.Run(net, dt, duration, [new NodalProbe("i", NodalProbeKind.ElementCurrent, "RL")], events,
            new NodalOptions { EventRule = rule });
    }

    [Fact]
    public void Series_rlc_sine_response_matches_the_closed_form_and_is_second_order()
    {
        const double r = 10, l = 0.1, c = 100e-6, vpk = 1000, f = 50;
        double Err(double dt)
        {
            NodalResult res = RunRlc(dt, 0.1, r, l, c, vpk, f, false);
            Assert.Equal(NodalStatus.Completed, res.Status);
            double m = 0, peak = 0;
            for (int k = 0; k < res.Times.Length; k++)
            {
                double exact = RlcSineCurrent(r, l, c, vpk, 2 * Pi * f, res.Times[k]);
                m = Math.Max(m, Math.Abs(res.Series[0][k] - exact));
                peak = Math.Max(peak, Math.Abs(exact));
            }
            return m / peak;
        }
        double e1 = Err(4e-5), e2 = Err(2e-5), e3 = Err(1e-5);
        double p12 = Math.Log(e1 / e2) / Math.Log(2), p23 = Math.Log(e2 / e3) / Math.Log(2);
        output.WriteLine($"RLC sine: relative error {e1:E3} {e2:E3} {e3:E3}; observed order {p12:F3} {p23:F3}");
        Assert.True(e3 < 1e-4, $"error {e3}");
        Assert.InRange(p12, 1.8, 2.2);
        Assert.InRange(p23, 1.8, 2.2);
    }

    [Theory]
    [InlineData(NodalEventRule.Trapezoidal, 1e-4)]
    [InlineData(NodalEventRule.BackwardEulerStep, 2e-3)]
    public void Switching_an_rlc_on_at_a_step_boundary_follows_the_closed_form(NodalEventRule rule, double tol)
    {
        const double r = 10, l = 0.1, c = 100e-6, vpk = 1000, f = 50, dt = 1e-5;
        double tClose = 0.01;   // half a period: the source is at zero, the response is minus the from-rest sine response
        NodalResult res = RunRlc(dt, 0.1, r, l, c, vpk, f, true, rule, tClose);
        Assert.Equal(NodalStatus.Completed, res.Status);
        double m = 0, peak = 0;
        for (int k = 0; k < res.Times.Length; k++)
        {
            double t = res.Times[k];
            double exact = t < tClose - 1e-12 ? 0.0 : -RlcSineCurrent(r + 1e-6, l, c, vpk, 2 * Pi * f, t - tClose);
            m = Math.Max(m, Math.Abs(res.Series[0][k] - exact));
            peak = Math.Max(peak, Math.Abs(exact));
        }
        output.WriteLine($"switched RLC ({rule}): max error / peak = {m / peak:E3}; refactorisations {res.Stats.Refactorisations}");
        Assert.True(m / peak < tol, $"error {m / peak}");
        Assert.Equal(rule == NodalEventRule.Trapezoidal ? 2 : 3, res.Stats.Refactorisations);
    }

    // ---- Bergeron / distortionless line --------------------------------------------------------------------------

    [Fact]
    public void Lossless_line_reflection_lattice_matches_the_travel_wave_closed_form()
    {
        const double zc = 400, rs = 100, rl = 1200, e = 1000, tau = 100e-6, dt = 10e-6;
        double gs = (rs - zc) / (rs + zc), gr = (rl - zc) / (rl + zc);
        var net = new NodalNetwork();
        int s = net.Node("S"), r = net.Node("R");
        net.Add(new NodalVoltageSource("E", s, rs, _ => e));
        net.Add(new FdLine("L", [s], [r], [FdMode.Bergeron("m", zc, tau)]));
        net.Add(NodalBranch.Resistor("RL", r, NodalNetwork.Ground, rl));
        NodalResult res = NodalSolver.Run(net, dt, 12 * tau, V("S", "R"));
        Assert.Equal(NodalStatus.Completed, res.Status);
        double v1 = e * zc / (rs + zc);
        double Fwd(int k) => v1 * Math.Pow(gr * gs, k);
        double Vs(int k) { double a = 0; for (int m = 0; m <= k; m++) a += Fwd(m); for (int m = 0; m < k; m++) a += gr * Fwd(m); return a; }
        double Vr(int k) { double a = 0; for (int m = 0; m <= k; m++) a += Fwd(m); return (1 + gr) * a; }
        for (int k = 0; k <= 4; k++)
        {
            int n = (int)Math.Round((2 * k + 1) * tau / dt);          // middle of sending-end plateau k
            Assert.Equal(Vs(k), res.Series[0][n], 6);
            int n2 = (int)Math.Round((2 * k + 2) * tau / dt);         // middle of receiving-end plateau k
            Assert.Equal(Vr(k), res.Series[1][n2], 6);
        }
        // travel time: the receiving end is still zero just before tau and has moved by tau + one step
        Assert.Equal(0.0, res.Series[1][(int)Math.Round(tau / dt)], 9);
        Assert.NotEqual(0.0, res.Series[1][(int)Math.Round(tau / dt) + 2], 3);
    }

    [Fact]
    public void Open_ended_line_doubles_the_voltage_and_a_matched_end_absorbs()
    {
        const double zc = 300, e = 600, tau = 200e-6, dt = 20e-6;
        NodalResult Run(bool matched)
        {
            var net = new NodalNetwork();
            int s = net.Node("S"), r = net.Node("R");
            net.Add(new NodalVoltageSource("E", s, zc, _ => e));
            net.Add(new FdLine("L", [s], [r], [FdMode.Bergeron("m", zc, tau)]));
            if (matched) net.Add(NodalBranch.Resistor("RL", r, NodalNetwork.Ground, zc));
            return NodalSolver.Run(net, dt, 6 * tau, V("S", "R"));
        }
        NodalResult open = Run(false), match = Run(true);
        int n = (int)Math.Round(3 * tau / dt);
        Assert.Equal(e, open.Series[1][n], 8);        // incident e/2, doubled at the open end
        Assert.Equal(e / 2, match.Series[1][n], 8);   // absorbed: no reflection
        Assert.Equal(e / 2, match.Series[0][n], 8);   // the source end never sees a reflection
    }

    [Fact]
    public void Distortionless_line_attenuates_a_step_by_exp_of_minus_alpha_x_and_the_fit_reduces_to_it()
    {
        // per-metre R, L, G, C with R/L = G/C
        const double rP = 5e-4, lP = 1e-6, cP = 1e-11;
        double gP = rP * cP / lP;
        double zc = Math.Sqrt(lP / cP), v = 1.0 / Math.Sqrt(lP * cP), alpha = rP * Math.Sqrt(cP / lP);
        const double dt = 20e-6;
        double len = v * 20 * dt;                       // tau = 20 steps
        double tau = len / v, h = Math.Exp(-alpha * len);
        const double e = 1000;

        double Plateau(FdMode mode)
        {
            var net = new NodalNetwork();
            int s = net.Node("S"), r = net.Node("R");
            net.Add(new NodalVoltageSource("E", s, zc, _ => e));
            net.Add(new FdLine("L", [s], [r], [mode]));
            net.Add(NodalBranch.Resistor("RL", r, NodalNetwork.Ground, zc));
            NodalResult res = NodalSolver.Run(net, dt, 4 * tau, V("R"));
            return res.Series[0][(int)Math.Round(2.5 * tau / dt)];
        }
        double exact = e / 2 * h;
        Assert.Equal(exact, Plateau(FdMode.Bergeron("b", zc, tau, h)), 8);

        // the vector fit of exact distortionless data is exact, and the FD model then reproduces the closed form
        int m = 60;
        var f = Enumerable.Range(0, m).Select(i => 10.0 * Math.Pow(1000.0, i / (m - 1.0))).ToArray();
        var z = f.Select(x => new Complex(rP, 2 * Pi * x * lP)).ToArray();
        var y = f.Select(x => new Complex(gP, 2 * Pi * x * cP)).ToArray();
        (FdMode fitted, FdModeFitReport rep) = FdLineFitter.FitMode("d", f, z, y, len, dt, new FdFitOptions { BandHzLow = 10, BandHzHigh = 10000, Samples = m, Order = 4 });
        output.WriteLine($"distortionless fit: Yc err {rep.YcMaxRelError:E2}, A err {rep.AMaxRelError:E2}, tau {rep.DelaySeconds:E4} (exact {tau:E4})");
        Assert.True(rep.YcMaxRelError < 1e-6 && rep.AMaxRelError < 1e-6);
        Assert.Equal(tau, rep.DelaySeconds, 12);
        Assert.Equal(exact, Plateau(fitted), 5);
    }

    [Fact]
    public void A_line_shorter_than_one_step_is_refused_and_an_unstable_pole_is_refused()
    {
        var net = new NodalNetwork();
        int s = net.Node("S"), r = net.Node("R");
        net.Add(new NodalVoltageSource("E", s, 100, _ => 1.0));
        net.Add(new FdLine("L", [s], [r], [FdMode.Bergeron("m", 300, 5e-6)]));
        net.Add(NodalBranch.Resistor("RL", r, NodalNetwork.Ground, 300));
        var ex = Assert.Throws<ArgumentException>(() => NodalSolver.Run(net, 10e-6, 1e-3, V("R")));
        Assert.Contains("shorter than the step", ex.Message);
        var bad = new FdMode("bad", new RationalFunction(1e-3, [10.0], [1.0]), new RationalFunction(1.0, [], []), 1e-4);
        Assert.Throws<ArgumentException>(() => new FdLine("x", [0], [1], [bad]));
    }

    // ---- frequency-dependent line against the R4-04 line constants -------------------------------------------------

    private static LineGeometry Overhead(double f = 50) => new(
        [new ConductorData("A", ConductorRole.Phase, -4, 15, 0.07, 0.0116, 0.0149),
         new ConductorData("B", ConductorRole.Phase, 0, 15, 0.07, 0.0116, 0.0149),
         new ConductorData("C", ConductorRole.Phase, 4, 15, 0.07, 0.0116, 0.0149)],
        f, 100.0, EarthReturnMethod.EarthIntegral, true);

    private static Complex Phasor(double[] x, int start, int count, double dt, double f)
    {
        Complex acc = 0;
        for (int k = 0; k < count; k++) acc += x[start + k] * Complex.Exp(new Complex(0, -2 * Pi * f * (start + k) * dt));
        return acc * 2.0 / count;
    }

    [Fact]
    public void Fd_line_open_end_impedance_matches_z0_coth_gamma_l_from_the_line_constants()
    {
        const double lenKm = 100, dt = 5e-6;
        var opt = new FdFitOptions { BandHzLow = 10, BandHzHigh = 10_000, Samples = 80, Order = 8 };
        FdLineModel model = FdLineFitter.FromLineConstants(Overhead(), lenKm, opt, dt);
        foreach (FdModeFitReport m in model.Report.Modes.Take(2))
            output.WriteLine($"mode {m.Name}: delay {m.DelaySeconds:E4} s, Yc fit err max {m.YcMaxRelError:E2} rms {m.YcRmsRelError:E2}, A fit err max {m.AMaxRelError:E2} rms {m.ARmsRelError:E2}, Yc shift {m.YcPassivityShiftSiemens:E2}, A scale {m.APassivityScale:R}");
        Assert.All(model.Report.Modes, m => { Assert.True(m.YcMaxRelError < 0.05); Assert.True(m.AMaxRelError < 0.05); });

        foreach (double f in new[] { 50.0, 400.0, 2000.0 })
        {
            // analytic positive-sequence open-end impedance from the R4-04 constants at exactly this frequency
            LineConstantsResult lc = LineConstantsSolver.Solve(Overhead(f));
            double w = 2 * Pi * f;
            Complex z1 = lc.Sequence!.Z1 / 1000.0, y1 = new Complex(0, w * lc.Sequence.C1FaradPerKm / 1000.0);
            Complex exact = FdLineFitter.OpenEndImpedance(z1, y1, lenKm * 1000.0);
            Complex fitted = FdLineFitter.OpenEndImpedance(model.Modes[1], w);
            double zcAbs = Complex.Abs(Complex.Sqrt(z1 / y1));

            var study = new Emt3Study(dt, 1.0, 1, f, IntegrationRule.Trapezoidal, NodalEventRule.Trapezoidal, new NewtonPolicy(),
                ["S", "R"],
                [new Emt3Source("G", "S", 1000.0 * Math.Sqrt(3.0), f, 0, zcAbs, 0)], [], [],
                [new Emt3Line("L1", "S", "R", lenKm, "fd", Overhead(), opt)], [], [], [], [], [],
                [new Emt3Output("v", "voltage", "S", "a", ""), new Emt3Output("i", "current", "G", "a", "")]);
            Emt3Result res = Emt3Runner.Run(study);
            Assert.Equal(NodalStatus.Completed, res.Run.Status);
            int perCycle = (int)Math.Round(1.0 / (f * dt));
            int cycles = 5;
            int start = res.Run.Series[0].Length - 1 - cycles * perCycle + 1;
            // window must hold a whole number of cycles of samples ending at the last step
            Complex vv = Phasor(res.Run.Series[0], start, cycles * perCycle, dt, f);
            Complex ii = Phasor(res.Run.Series[1], start, cycles * perCycle, dt, f);
            Complex sim = vv / ii;
            double errFit = Complex.Abs(fitted - exact) / Complex.Abs(exact);
            double errSimVsFitted = Complex.Abs(sim - fitted) / Complex.Abs(fitted);
            double errSimVsExact = Complex.Abs(sim - exact) / Complex.Abs(exact);
            output.WriteLine($"f={f} Hz: exact Zin {exact.Magnitude:F3} ohm at {exact.Phase * 180 / Pi:F3} deg; fit-only error {errFit:E2}; simulation vs fitted {errSimVsFitted:E2}; simulation vs exact {errSimVsExact:E2}");
            Assert.True(errSimVsFitted < 2e-3, $"simulation vs fitted analytic {errSimVsFitted}");
            Assert.True(errFit < 0.05, $"fit error {errFit}");
            Assert.True(errSimVsExact < 0.05, $"simulation vs exact {errSimVsExact}");
        }
    }

    [Fact]
    public void Fd_fit_with_poor_data_reports_refusals_by_name()
    {
        var f = Enumerable.Range(0, 20).Select(i => 10.0 + i * 100).ToArray();
        var zBad = f.Select(x => new Complex(-1.0, 2 * Pi * x * 1e-6)).ToArray();
        var y = f.Select(x => new Complex(0, 2 * Pi * x * 1e-11)).ToArray();
        var ex = Assert.Throws<ArgumentException>(() => FdLineFitter.FitMode("neg", f, zBad, y, 1e5, 1e-5, new FdFitOptions { Order = 4 }));
        Assert.Contains("not positive", ex.Message);
        // too few band samples for the order
        Assert.Throws<ArgumentException>(() => RationalFit.Fit([1, 2, 3], [1, 1, 1], 4));
    }

    [Fact]
    public void A_non_passive_fit_that_cannot_be_corrected_is_refused_by_name()
    {
        const double rP = 1e-9, lP = 1e-6, cP = 1e-11, dt = 20e-6;
        double v = 1.0 / Math.Sqrt(lP * cP), len = v * 20.4 * dt;
        int m = 60;
        var f = Enumerable.Range(0, m).Select(i => 10.0 * Math.Pow(1000.0, i / (m - 1.0))).ToArray();
        var z = f.Select(x => new Complex(rP, 2 * Pi * x * lP)).ToArray();
        var y = f.Select(x => new Complex(0, 2 * Pi * x * cP)).ToArray();
        var opt = new FdFitOptions { BandHzLow = 10, BandHzHigh = 10000, Samples = m, Order = 4 };
        // A lossless line has |A| = 1 everywhere; a four-pole real fit of it overshoots 1 by far more than the 5 per cent correction, so it is REFUSED by name.
        var ex = Assert.Throws<ArgumentException>(() => FdLineFitter.FitMode("ll", f, z, y, len, dt, opt));
        output.WriteLine("lossless refusal: " + ex.Message);
        Assert.Contains("exceeds 1 in magnitude", ex.Message);
        // with enforcement off, any violation is refused too
        Assert.Throws<ArgumentException>(() => FdLineFitter.FitMode("ll", f, z, y, len, dt, opt with { EnforcePassivity = false }));
    }

    // ---- nonlinear elements -----------------------------------------------------------------------------------------

    private static double Bisect(Func<double, double> g, double lo, double hi)
    {
        for (int i = 0; i < 300; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (g(lo) * g(mid) <= 0) hi = mid; else lo = mid;
        }
        return 0.5 * (lo + hi);
    }

    [Fact]
    public void Power_law_arrester_dc_operating_point_equals_the_independent_bisection()
    {
        const double e = 20_000, rs = 50, vref = 10_000, iref = 1.0, alpha = 30;
        double root = Bisect(v => e - rs * iref * Math.Pow(v / vref, alpha) - v, 0, e);
        var net = new NodalNetwork();
        int n = net.Node("N");
        net.Add(new NodalVoltageSource("E", n, rs, _ => e));
        net.Add(SurgeArrester.PowerLaw("ZnO", n, NodalNetwork.Ground, vref, iref, alpha));
        NodalResult res = NodalSolver.Run(net, 1e-4, 1e-3, V("N"));
        Assert.Equal(NodalStatus.Completed, res.Status);
        double got = res.Series[0][^1];
        output.WriteLine($"arrester: bisection {root:R} V, simulation {got:R} V, Newton iterations {res.Stats.NewtonIterations} (max per step {res.Stats.MaxNewtonIterations})");
        Assert.Equal(root, got, 6);
        Assert.True(Math.Abs(got - root) / root < 1e-10);
        Assert.True(res.Stats.NewtonIterations > 0);
    }

    [Fact]
    public void Piecewise_arrester_operating_point_equals_the_segment_intersection()
    {
        (double V, double I)[] pts = [(5_000, 0.001), (8_000, 1.0), (9_000, 100.0), (10_000, 5_000.0)];
        const double e = 30_000, rs = 40;
        // independent: solve e - rs*i(v) = v segment by segment on the table written here
        double IofV(double v)
        {
            double pv = 0, pi2 = 0;
            for (int k = 0; k < pts.Length; k++)
            {
                if (v <= pts[k].V) return pi2 + (pts[k].I - pi2) * (v - pv) / (pts[k].V - pv);
                pv = pts[k].V; pi2 = pts[k].I;
            }
            double sl = (pts[^1].I - pts[^2].I) / (pts[^1].V - pts[^2].V);
            return pts[^1].I + sl * (v - pts[^1].V);
        }
        double root = Bisect(v => e - rs * IofV(v) - v, 0, e);
        var net = new NodalNetwork();
        int n = net.Node("N");
        net.Add(new NodalVoltageSource("E", n, rs, _ => e));
        net.Add(SurgeArrester.Piecewise("ZnO", n, NodalNetwork.Ground, pts));
        NodalResult res = NodalSolver.Run(net, 1e-4, 1e-3, V("N"));
        Assert.Equal(NodalStatus.Completed, res.Status);
        Assert.True(Math.Abs(res.Series[0][^1] - root) / root < 1e-10, $"{res.Series[0][^1]} vs {root}");
    }

    [Fact]
    public void Arrester_vi_characteristic_traced_by_a_ramp_equals_the_defined_curves()
    {
        (double V, double I)[] pts = [(5_000, 0.001), (8_000, 1.0), (9_000, 100.0)];
        double IofV(double v)
        {
            double a = Math.Abs(v), pv = 0, pi2 = 0, y;
            for (int k = 0; ; k++)
            {
                if (k == pts.Length)
                {
                    double sl = (pts[^1].I - pts[^2].I) / (pts[^1].V - pts[^2].V);
                    y = pts[^1].I + sl * (a - pts[^1].V);
                    break;
                }
                if (a <= pts[k].V) { y = pi2 + (pts[k].I - pi2) * (a - pv) / (pts[k].V - pv); break; }
                pv = pts[k].V; pi2 = pts[k].I;
            }
            return v < 0 ? -y : y;
        }
        foreach (bool power in new[] { false, true })
        {
            var net = new NodalNetwork();
            int n = net.Node("N");
            net.Add(new NodalVoltageSource("E", n, 0.0, t => 12_000.0 * Math.Sin(2 * Pi * 25 * t)));   // ideal: the arrester sees the ramp
            net.Add(power ? SurgeArrester.PowerLaw("ZnO", n, NodalNetwork.Ground, 8_000, 1.0, 25)
                          : SurgeArrester.Piecewise("ZnO", n, NodalNetwork.Ground, pts));
            NodalResult res = NodalSolver.Run(net, 1e-4, 0.04, [new NodalProbe("v", NodalProbeKind.NodeVoltage, "N"), new NodalProbe("i", NodalProbeKind.ElementCurrent, "ZnO")]);
            Assert.Equal(NodalStatus.Completed, res.Status);
            double worst = 0;
            for (int k = 1; k < res.Times.Length; k++)
            {
                double v = res.Series[0][k];
                double expect = power ? Math.Sign(v) * Math.Pow(Math.Abs(v) / 8_000, 25) : IofV(v);
                worst = Math.Max(worst, Math.Abs(res.Series[1][k] - expect) / Math.Max(1.0, Math.Abs(expect)));
            }
            output.WriteLine($"arrester V-I ({(power ? "power law" : "piecewise")}): worst relative deviation from the defined curve {worst:E2}");
            Assert.True(worst < 1e-10);
        }
    }

    [Fact]
    public void Newton_nonconvergence_is_a_named_status_not_a_silent_last_value()
    {
        var net = new NodalNetwork();
        int n = net.Node("N");
        net.Add(new NodalVoltageSource("E", n, 50, _ => 20_000));
        net.Add(SurgeArrester.PowerLaw("ZnO", n, NodalNetwork.Ground, 10_000, 1.0, 30));
        NodalResult res = NodalSolver.Run(net, 1e-4, 1e-3, V("N"), null, new NodalOptions { Newton = new NewtonPolicy { MaxIterations = 2 } });
        Assert.Equal(NodalStatus.NewtonNotConverged, res.Status);
        Assert.Contains("did not converge", res.StatusMessage);
        Assert.True(res.Times.Length < 11, "the series must stop at the failure, not be padded");
        Assert.Equal(1e-4, res.FailedAtSeconds, 12);
    }

    [Fact]
    public void A_floating_node_gives_a_singular_status()
    {
        var net = new NodalNetwork();
        int a = net.Node("A"), b = net.Node("B");
        net.Add(new NodalVoltageSource("E", a, 10, _ => 1.0));
        net.Add(new NodalCurrentSource("I", b, _ => 1.0));
        NodalResult res = NodalSolver.Run(net, 1e-4, 1e-3, V("A"));
        Assert.Equal(NodalStatus.SingularMatrix, res.Status);
        Assert.Contains("singular", res.StatusMessage);
    }

    private static readonly (double X, double Y)[] Curve = [(4.0, 1.0), (4.5, 51.0)];   // (flux Wb, current A): unsaturated 4 H, then 0.01 H

    private static double CurveI(double lambda)
    {
        double a = Math.Abs(lambda), y;
        if (a <= 4.0) y = a * 0.25;
        else if (a <= 4.5) y = 1.0 + (a - 4.0) * 100.0;
        else y = 51.0 + (a - 4.5) * 100.0;
        return lambda < 0 ? -y : y;
    }

    [Fact]
    public void Saturable_inductor_energisation_peak_flux_is_the_residual_plus_two_vpk_over_w_and_current_follows_the_curve()
    {
        const double vpk = 1000, f = 50, res0 = 0.8;
        double w = 2 * Pi * f, expectPeak = res0 + 2 * vpk / w;
        double Run(double dt, bool check)
        {
            var net = new NodalNetwork();
            int n = net.Node("N");
            net.Add(new NodalVoltageSource("E", n, 0.0, t => vpk * Math.Sin(w * t)));
            net.Add(new SaturableInductor("L", n, NodalNetwork.Ground, new PiecewiseCurve(Curve), res0));
            NodalResult r = NodalSolver.Run(net, dt, 0.04, [new NodalProbe("lambda", NodalProbeKind.ElementState, "L"), new NodalProbe("i", NodalProbeKind.ElementCurrent, "L")]);
            Assert.Equal(NodalStatus.Completed, r.Status);
            if (check)
            {
                double worst = 0, worstI = 0;
                for (int k = 0; k < r.Times.Length; k++)
                {
                    double t = r.Times[k];
                    worst = Math.Max(worst, Math.Abs(r.Series[0][k] - (res0 + vpk / w * (1 - Math.Cos(w * t)))));
                    worstI = Math.Max(worstI, Math.Abs(r.Series[1][k] - CurveI(r.Series[0][k])));
                }
                output.WriteLine($"saturable inductor: flux error vs closed form {worst:E2} Wb; current vs defined curve {worstI:E2} A; peak {r.Series[0].Max():R} (closed form {expectPeak:R}); peak current {r.Series[1].Max():F2} A");
                Assert.True(worstI < 1e-9, "the current must be on the piecewise curve");
                Assert.True(r.Series[1].Max() > 100.0, "the core must have saturated");
            }
            return Math.Abs(r.Series[0].Max() - expectPeak);
        }
        double e1 = Run(2e-4, false), e2 = Run(1e-4, true), e3 = Run(5e-5, false);
        Assert.True(e2 < 2e-3, $"peak flux error {e2}");
        double order = Math.Log(e1 / e2) / Math.Log(2);
        output.WriteLine($"peak-flux error {e1:E3} {e2:E3} {e3:E3}; observed order {order:F3}");
        Assert.InRange(order, 1.7, 2.3);
        Assert.InRange(Math.Log(e2 / e3) / Math.Log(2), 1.7, 2.3);
    }

    [Fact]
    public void Saturable_transformer_energisation_flux_balance_and_the_qualitative_inrush_shape()
    {
        const double vpk = 1000, f = 50, res0 = 0.8, ll1 = 0.02, dt = 5e-5;
        double w = 2 * Pi * f;
        var net = new NodalNetwork();
        int p = net.Node("P"), s = net.Node("S");
        net.Add(new NodalVoltageSource("E", p, 0.0, t => vpk * Math.Sin(w * t)));
        SaturableTransformerParts parts = SaturableTransformer.AddTo(net, "T", "P", "S",
            new SaturableTransformerData(0.0, ll1, 0.0, 0.02, 0.0, new PiecewiseCurve(Curve), res0));
        net.Add(NodalBranch.Resistor("Rload", s, NodalNetwork.Ground, 1e6));
        NodalResult r = NodalSolver.Run(net, dt, 0.12,
            [new NodalProbe("i1", NodalProbeKind.ElementCurrent, "T.primary"), new NodalProbe("lm", NodalProbeKind.ElementState, "T.core")]);
        Assert.Equal(NodalStatus.Completed, r.Status);
        // terminal flux linkage = leakage + magnetising; with no resistance it is the integral of v (closed form)
        double i0 = CurveI(res0), lt0 = res0 + ll1 * i0;
        double worst = 0, peakT = 0;
        for (int k = 0; k < r.Times.Length; k++)
        {
            double t = r.Times[k];
            double lt = r.Series[1][k] + ll1 * r.Series[0][k];
            double exact = lt0 + vpk / w * (1 - Math.Cos(w * t));
            worst = Math.Max(worst, Math.Abs(lt - exact));
            peakT = Math.Max(peakT, lt);
        }
        output.WriteLine($"transformer: terminal flux balance error {worst:E2} Wb; peak terminal flux {peakT:R} vs {lt0 + 2 * vpk / w:R}");
        Assert.True(worst < 5e-3);
        Assert.True(Math.Abs(peakT - (lt0 + 2 * vpk / w)) < 5e-3);
        // qualitative inrush shape only (published shape, not a numeric oracle): unipolar, peaky, once per cycle, decaying only through R
        double[] i = r.Series[0];
        double imax = i.Max(), imin = i.Min();
        Assert.True(imax > 100.0 && Math.Abs(imin) < 0.2 * imax, $"inrush must be strongly one-sided: max {imax}, min {imin}");
        var peaks = new List<double>();
        for (int k = 1; k < i.Length - 1; k++)
            if (i[k] > 0.5 * imax && i[k] >= i[k - 1] && i[k] > i[k + 1]) peaks.Add(r.Times[k]);
        Assert.True(peaks.Count >= 2, "an inrush pulse is expected every cycle");
        Assert.InRange(peaks[1] - peaks[0], 0.9 / f, 1.1 / f);
        Assert.NotNull(parts.Core);
    }

    // ---- three-phase symmetrical fault: positive-sequence closed form -----------------------------------------------

    [Fact]
    public void Three_phase_bolted_fault_current_follows_the_positive_sequence_rl_closed_form()
    {
        const double vll = 11_000, f = 50, rs = 0.5, ls = 0.01, r1 = 1.0, l1 = 0.02, rf = 0.01, dt = 5e-6, tf = 0.0205;
        double w = 2 * Pi * f, vpk = vll * Math.Sqrt(2.0 / 3.0);
        double rt = rs + r1 + rf, lt = ls + l1, zmag = Math.Sqrt(rt * rt + w * w * lt * lt), phi = Math.Atan2(w * lt, rt), tc = lt / rt;
        var study = new Emt3Study(dt, 0.1, 1, f, IntegrationRule.Trapezoidal, NodalEventRule.Trapezoidal, new NewtonPolicy(),
            ["S", "F"], [new Emt3Source("G", "S", vll, f, 0, rs, ls)],
            [new Emt3Branch("L", "S", "F", r1, l1, 3.0, 0.06)], [], [], [], [], [], [],
            [new Emt3Fault("FLT", "F", "abc", rf, true, tf, null)],
            [new Emt3Output("ia", "current", "L", "a", ""), new Emt3Output("ib", "current", "L", "b", ""), new Emt3Output("ic", "current", "L", "c", "")]);
        Emt3Result res = Emt3Runner.Run(study);
        Assert.Equal(NodalStatus.Completed, res.Run.Status);
        double peak = vpk / zmag, worst = 0, zeroSeq = 0;
        for (int k = 0; k < res.Run.Times.Length; k++)
        {
            double t = res.Run.Times[k];
            zeroSeq = Math.Max(zeroSeq, Math.Abs(res.Run.Series[0][k] + res.Run.Series[1][k] + res.Run.Series[2][k]));
            for (int ph = 0; ph < 3; ph++)
            {
                double phase = -ph * 2 * Pi / 3;
                double exact = t < tf - 1e-9 ? 0.0
                    : peak * (Math.Sin(w * t + phase - phi) - Math.Sin(w * tf + phase - phi) * Math.Exp(-(t - tf) / tc));
                worst = Math.Max(worst, Math.Abs(res.Run.Series[ph][k] - exact));
            }
        }
        output.WriteLine($"fault: peak steady current {peak:F2} A; worst deviation from the closed form {worst / peak:E2} of peak; zero-sequence leakage {zeroSeq:E2} A");
        Assert.True(worst / peak < 3e-3, $"deviation {worst / peak}");
        Assert.True(zeroSeq < 1e-6 * peak);
    }

    // ---- determinism -----------------------------------------------------------------------------------------------

    private static Emt3Study Mixed(double dt) => new(dt, 0.05, 2, 50, IntegrationRule.Trapezoidal, NodalEventRule.BackwardEulerStep, new NewtonPolicy(),
        ["S", "M", "R"], [new Emt3Source("G", "S", 11_000, 50, 0, 0.5, 0.0)],
        [new Emt3Branch("B", "S", "M", 0.5, 0.01, 1.5, 0.03)], [new Emt3Shunt("C", "M", 0, 1e-6, 0)],
        [new Emt3Line("L1", "M", "R", 30, "fd", Overhead(), new FdFitOptions { BandHzLow = 10, BandHzHigh = 5000, Samples = 60, Order = 6 })],
        [new Emt3SaturableTransformer("T", "S", "M", 0.1, 0.02, 0.1, 0.02, 0, Curve, [0.4, -0.2, 0.0])], [],
        [new Emt3Arrester("ZnO", "R", "abc", 12_000, 1.0, 20, null)], [],
        [new Emt3Fault("F", "R", "a", 5.0, true, 0.02, 0.03)],
        [new Emt3Output("vr", "voltage", "R", "a", ""), new Emt3Output("ib", "current", "B", "b", ""), new Emt3Output("lam", "flux", "T", "a", "")]);

    [Fact]
    public void A_mixed_study_is_bit_identical_across_repeats_and_thread_counts()
    {
        NodalResult Run() => Emt3Runner.Run(Mixed(10e-6)).Run;
        NodalResult a = Run();
        Assert.Equal(NodalStatus.Completed, a.Status);
        Assert.Contains("backward-Euler", a.Method);
        static bool Same(NodalResult x, NodalResult y) =>
            x.Series.Length == y.Series.Length && x.Series.Zip(y.Series).All(p => p.First.Length == p.Second.Length
                && p.First.Zip(p.Second).All(q => BitConverter.DoubleToInt64Bits(q.First) == BitConverter.DoubleToInt64Bits(q.Second)))
            && x.Stats == y.Stats;
        Assert.True(Same(a, Run()));
        foreach (int threads in new[] { 1, 4 })
        {
            var results = new NodalResult[4];
            Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = threads }, i => results[i] = Run());
            Assert.All(results, r => Assert.True(Same(a, r)));
        }
    }

    [Fact]
    public void The_incumbent_single_phase_engine_still_has_no_nonlinear_or_fd_element()
    {
        // documents the seam: the existing sealed element set is what R4-16 could not extend without editing core files
        var names = typeof(EmtElement).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(EmtElement)) && t.Namespace == "GridSim.Core.Dynamics.Emt").Select(t => t.Name).OrderBy(n => n).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("Arrester") || n.Contains("Saturable") || n.Contains("Fd"));
    }
}
