using System;
using System.Globalization;
using System.Linq;
using GridSim.Core.Protection.ShortCircuit.Dc;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// R4-20c: the IEC 61660-style DC short-circuit study. The standard is paid and was not read; every oracle is a closed form derived here from
/// the circuit equations: RL charging i = (E/R)(1 - e^(-Rt/L)) for a battery, the second-order discharge of a charged capacitor
/// (L C s^2 + R C s + 1 = 0) and of a DC motor's back EMF through its inertia (L J s^2 + R J s + k^2 = 0), and a uniform-time-constant
/// parallel network (every element L = tau R, so the whole network responds as 1 - e^(-t/tau)). No table value of the standard is used.
/// </summary>
public class Iec61660DcShortCircuitTests
{
    private static void Near(double expected, double actual, double tol) => Assert.InRange(actual, expected - tol, expected + tol);

    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static DcNetwork Net(string sources, string branches, string fault, string node = "1") =>
        DcNetwork.Parse($"{{\"schema\":\"gridsim-dc-network/1\",\"branches\":[{branches}],\"sources\":[{sources}],\"fault\":{{\"node\":\"{node}\",\"rOhm\":{fault}}}}}");

    [Fact]
    public void Battery_through_cable_follows_rl_charging()
    {
        const double e = 110.0, rs = 0.02, ls = 0.001, rc = 0.03, lc = 0.002;
        DcNetwork n = Net($"{{\"name\":\"B1\",\"kind\":\"battery\",\"node\":\"1\",\"eV\":{F(e)},\"rOhm\":{F(rs)},\"lH\":{F(ls)}}}",
            $"{{\"name\":\"cab\",\"from\":\"1\",\"to\":\"2\",\"rOhm\":{F(rc)},\"lH\":{F(lc)}}}", "0", node: "2");
        double r = rs + rc, l = ls + lc, tau = l / r;
        double[] ts = { 0.0, 0.01, 0.03, 0.06, 0.2 };
        DcResult res = Iec61660.Compute(n, new DcOptions(ts, tau / 1000.0, 8.0 * tau));
        Assert.Equal(DcStatus.Ok, res.Status);
        for (int k = 0; k < ts.Length; k++)
        {
            double expected = e / r * (1.0 - Math.Exp(-r * ts[k] / l));
            Near(expected, res.AtTimesA[k], 0.005);
        }
        Near(e / r * (1.0 - Math.Exp(-8.0)), res.PeakA, 0.005);   // monotonic rise: the peak is the last sample
        Assert.Equal(res.EndS, res.PeakTimeS, 9);
        Near(e / r * (1.0 - Math.Exp(-8.0)), res.Sources[0].EndA, 0.005);   // the one source carries it all
    }

    [Fact]
    public void Capacitor_underdamped_discharge_matches_closed_form()
    {
        const double u = 100.0, r = 0.5, l = 0.01, c = 0.01;
        DcNetwork n = Net($"{{\"name\":\"C1\",\"kind\":\"capacitor\",\"node\":\"1\",\"uV\":{F(u)},\"rOhm\":{F(r)},\"lH\":{F(l)},\"cF\":{F(c)}}}", "", "0");
        double alpha = r / (2 * l), w0sq = 1.0 / (l * c), wd = Math.Sqrt(w0sq - alpha * alpha);
        double[] ts = { 0.0, 0.005, 0.01, 0.02, 0.05, 0.1 };
        DcResult res = Iec61660.Compute(n, new DcOptions(ts, 2e-6, 0.2));
        Assert.Equal(DcStatus.Ok, res.Status);
        for (int k = 0; k < ts.Length; k++)
            Near(u / (l * wd) * Math.Exp(-alpha * ts[k]) * Math.Sin(wd * ts[k]), res.AtTimesA[k], 0.005);
        double peak = 0.0;
        for (double t = 0; t < 0.05; t += 1e-6) peak = Math.Max(peak, u / (l * wd) * Math.Exp(-alpha * t) * Math.Sin(wd * t));
        Near(peak, res.PeakA, 0.005);
    }

    [Fact]
    public void Capacitor_overdamped_discharge_matches_two_exponentials()
    {
        const double u = 100.0, r = 5.0, l = 0.01, c = 0.01;
        DcNetwork n = Net($"{{\"name\":\"C1\",\"kind\":\"capacitor\",\"node\":\"1\",\"uV\":{F(u)},\"rOhm\":{F(r)},\"lH\":{F(l)},\"cF\":{F(c)}}}", "", "0");
        double a = r / (2 * l), root = Math.Sqrt(a * a - 1.0 / (l * c)), s1 = -a + root, s2 = -a - root;
        double[] ts = { 0.001, 0.005, 0.02, 0.1 };
        DcResult res = Iec61660.Compute(n, new DcOptions(ts, 2e-6, 0.4));
        for (int k = 0; k < ts.Length; k++)
            Near(u / l * (Math.Exp(s1 * ts[k]) - Math.Exp(s2 * ts[k])) / (s1 - s2), res.AtTimesA[k], 0.005);
    }

    [Fact]
    public void Dc_motor_back_emf_through_inertia_matches_closed_form()
    {
        // L J s^2 + R J s + k^2 = 0 ; i(0) = 0, i'(0) = k w0 / L.
        const double k = 2.0, j = 0.5, r = 0.1, l = 0.02, w0 = 100.0;
        DcNetwork n = Net($"{{\"name\":\"M1\",\"kind\":\"motor\",\"node\":\"1\",\"eV\":{F(k * w0)},\"rOhm\":{F(r)},\"lH\":{F(l)},\"jKgM2\":{F(j)},\"kVsPerRad\":{F(k)}}}", "", "0");
        double alpha = r / (2 * l), wd = Math.Sqrt(k * k / (l * j) - alpha * alpha), di0 = k * w0 / l;
        double[] ts = { 0.0, 0.01, 0.03, 0.1, 0.5 };
        DcResult res = Iec61660.Compute(n, new DcOptions(ts, 1e-5, 1.0));
        Assert.Equal(DcStatus.Ok, res.Status);
        for (int i = 0; i < ts.Length; i++)
            Near(di0 / wd * Math.Exp(-alpha * ts[i]) * Math.Sin(wd * ts[i]), res.AtTimesA[i], 0.05);
        Assert.Equal(res.Sources[0].PeakA, res.PeakA, 6);
    }

    [Fact]
    public void Uniform_time_constant_parallel_network_responds_as_one_minus_exp()
    {
        // Two batteries (different EMF, so a prefault circulating current) at node A, one shared cable A-F, every element L = tau R.
        const double tau = 0.01, e1 = 120.0, e2 = 110.0, r1 = 0.04, r2 = 0.06, r3 = 0.02;
        DcNetwork n = Net(
            $"{{\"name\":\"B1\",\"kind\":\"battery\",\"node\":\"A\",\"eV\":{F(e1)},\"rOhm\":{F(r1)},\"lH\":{F(tau * r1)}}},"
            + $"{{\"name\":\"B2\",\"kind\":\"battery\",\"node\":\"A\",\"eV\":{F(e2)},\"rOhm\":{F(r2)},\"lH\":{F(tau * r2)}}}",
            $"{{\"name\":\"cab\",\"from\":\"A\",\"to\":\"F\",\"rOhm\":{F(r3)},\"lH\":{F(tau * r3)}}}", "0", node: "F");
        double eth = (e1 / r1 + e2 / r2) / (1 / r1 + 1 / r2), rth = 1.0 / (1 / r1 + 1 / r2);
        double iss = eth / (rth + r3);
        double[] ts = { 0.0, 0.005, 0.01, 0.02, 0.05 };
        DcResult res = Iec61660.Compute(n, new DcOptions(ts, tau / 1000.0, 10.0 * tau));
        Assert.Equal(DcStatus.Ok, res.Status);
        for (int k = 0; k < ts.Length; k++) Near(iss * (1.0 - Math.Exp(-ts[k] / tau)), res.AtTimesA[k], 0.005);
        // Prefault: node A sits at the open-circuit voltage of the two batteries in parallel.
        Assert.Equal(eth, res.PrefaultFaultNodeV, 6);
        // Each battery's own current starts at its prefault circulating value (E1 - E2) / (R1 + R2).
        Assert.Equal((e1 - e2) / (r1 + r2), res.Sources.First(s => s.Name == "B1").AtTimesA[0], 6);
        Assert.Equal(-(e1 - e2) / (r1 + r2), res.Sources.First(s => s.Name == "B2").AtTimesA[0], 6);
    }

    [Fact]
    public void Fault_resistance_lowers_the_steady_current_and_runs_repeat_bit_identically()
    {
        string src = "{\"name\":\"B1\",\"kind\":\"rectifier\",\"node\":\"1\",\"eV\":220,\"rOhm\":0.05,\"lH\":0.005}";
        DcResult a = Iec61660.Compute(Net(src, "", "0.15"), new DcOptions(new[] { 0.01 }, 1e-4, 0.2, BreakerMakingA: 1000.0));
        DcResult b = Iec61660.Compute(Net(src, "", "0.15"), new DcOptions(new[] { 0.01 }, 1e-4, 0.2, BreakerMakingA: 1000.0));
        Near(220.0 / 0.20, a.EndA, 0.5);
        Assert.False(a.MakingOk);                                   // 1100 A > 1000 A
        Assert.Equal(1000.0 - a.PeakA, a.MakingMarginA, 9);
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.PeakA), BitConverter.DoubleToInt64Bits(b.PeakA));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.AtTimesA[0]), BitConverter.DoubleToInt64Bits(b.AtTimesA[0]));
        Assert.NotEmpty(a.Awaiting);
    }

    [Fact]
    public void Unchosen_step_or_singular_network_is_incomplete_not_guessed()
    {
        // No inductance anywhere: no time constant to choose a step from.
        DcResult r = Iec61660.Compute(Net("{\"name\":\"B\",\"kind\":\"battery\",\"node\":\"1\",\"eV\":24,\"rOhm\":0.1,\"lH\":0}", "", "0"), new DcOptions(new[] { 0.1 }));
        Assert.Equal(DcStatus.Incomplete, r.Status);
        Assert.Contains(r.Reasons, x => x.Contains("give a step"));
        // A floating node pair with no source path: the prefault matrix is singular.
        DcResult s = Iec61660.Compute(Net("{\"name\":\"B\",\"kind\":\"battery\",\"node\":\"1\",\"eV\":24,\"rOhm\":0.1,\"lH\":0.01}",
            "{\"name\":\"x\",\"from\":\"8\",\"to\":\"9\",\"rOhm\":1,\"lH\":0.1}", "0"), new DcOptions(new[] { 0.1 }, 1e-4, 0.1));
        Assert.Equal(DcStatus.Incomplete, s.Status);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"schema\":\"gridsim-dc-network/1\",\"sources\":[],\"fault\":{\"node\":\"1\",\"rOhm\":0}}")]
    [InlineData("{\"schema\":\"gridsim-dc-network/1\",\"bogus\":1}")]
    [InlineData("{\"schema\":\"gridsim-dc-network/1\",\"sources\":[{\"name\":\"C\",\"kind\":\"capacitor\",\"node\":\"1\",\"uV\":10,\"rOhm\":1,\"lH\":0.1}],\"fault\":{\"node\":\"1\",\"rOhm\":0}}")]
    [InlineData("{\"schema\":\"gridsim-dc-network/1\",\"sources\":[{\"name\":\"B\",\"kind\":\"flywheel\",\"node\":\"1\",\"eV\":10,\"rOhm\":1,\"lH\":0.1}],\"fault\":{\"node\":\"1\",\"rOhm\":0}}")]
    [InlineData("{\"schema\":\"gridsim-dc-network/1\",\"sources\":[{\"name\":\"B\",\"kind\":\"battery\",\"node\":\"1\",\"eV\":10,\"rOhm\":1,\"lH\":0.1}],\"fault\":{\"node\":\"7\",\"rOhm\":0}}")]
    public void Strict_parser_refuses_incomplete_networks(string json) => Assert.Throws<FormatException>(() => DcNetwork.Parse(json));
}
