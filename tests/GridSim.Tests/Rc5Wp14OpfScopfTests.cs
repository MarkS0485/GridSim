using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Analysis;
using GridSim.Core.Analysis.Opf;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP14: phase-shifter angle as an AC-OPF control, neighbours rounding, and generator and N-2 outages in the
/// AC-SCOPF (<see cref="AcOpfExtended"/>, <see cref="OpfRounding"/>, <see cref="AcScopf"/>, <see cref="ScopfOutageFile"/>).
///
/// Sources, per test: SciPy 1.18 and mpmath 1.3 (tools/crossval/opf/pst_two_line.py) for the PST loss minimum; central
/// finite differences for the derivatives; a Newton power flow in NumPy/SciPy (fsolve, the MATPOWER branch model) for the
/// two-tap rounding case; hand derivations restated in each test for the generator outages (governor-droop sharing since
/// 2.1.110, with the tolerances derived there); equivalence between two formulations for N-2. The bit-identity pins were
/// taken from the 2.1.107 build (582956e1) before any WP14 change.
/// </summary>
public sealed class Rc5Wp14OpfScopfTests
{
    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);
    private static GridModel Case(string rel) => S10TestSupport.LoadMatpower(rel).Model;

    // ---- 1. PST loss minimum ---------------------------------------------------------------------------------------

    private static GridModel PstTwoLine() => new()
    {
        Name = "pst-two-line", BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, VminPu: 1.0, VmaxPu: 1.0),
            new Bus(2, BusType.Pq, Pd: 100, Qd: 0, VminPu: 1.0, VmaxPu: 1.0),
        },
        Generators = new[]
        {
            new Generator(1, 0, Pmin: 0, Pmax: 1000, Qmin: -1000, Qmax: 1000, Name: "slack"),
            new Generator(2, 0, Qmin: -100, Qmax: 100, Pmin: 0, Pmax: 0, Name: "q"),
        },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1, Name: "A"), new Branch(1, 2, 0.02, 0.1, Name: "B") },
    };

    [Fact]
    public void T1_phase_shifter_loss_minimum_matches_the_scipy_reference()
    {
        // Reference: tools/crossval/opf/pst_two_line.py (sha256 of its LF-ended text 740dc7b76f3d084bb8b8cec8e57b8c7db6346e1e0c94af76ca5e78cccc4bb016):
        // the exact two-line AC losses, minimised over phi by SciPy's bounded scalar minimiser and polished with mpmath
        // at 40 digits (the Lagrange condition). phi* = 1.9524805291234986 deg (MATPOWER sign), losses
        // 0.6851386175019902 MW; at phi = 0 the losses are 0.77125686559531 MW; the DC current-divider bound
        // R_A R_B / (R_A + R_B) x 100 = 0.6666666666666667 MW lies below the AC optimum. The loss curvature there is
        // 148.15 MW/rad^2.
        // Tolerances. The loss objective stops at 1e-12, which bounds the objective error by 1e-8 (1 + |x|inf) MW
        // (AcOpfExtended.DefaultIpm); |x|inf is the slack output 1.0069 pu (every other variable is smaller), so
        // 1e-8 x 2.0069 = 2.0069e-8 MW. The optimum is flat: an angle error d moves the losses by 148.15 d^2 / 2, so
        // |d| <= sqrt(2 x 2.0069e-8 / 148.15) = 1.646e-5 rad = 9.43e-4 deg, inside 1e-3 deg.
        GridModel m = PstTwoLine();
        OpfControlSet c = OpfControlSet.Parse("{\"format\":\"gridsim-opf-controls/1\",\"pst\":[{\"kind\":\"pst\",\"branch\":\"B\",\"minDeg\":-30,\"maxDeg\":30}]}", m);
        Assert.Equal(new OpfPhaseShiftControl(1, -30, 30, 0), Assert.Single(c.PhaseShifts));
        Assert.Equal(1, c.Count);
        AcOpfExtendedResult r = AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses, Controls = c });
        Assert.True(r.Converged, r.Base.Message);
        double lossTol = 1e-8 * (1 + 1.0069);
        Assert.True(Math.Abs(r.LossesMw - 0.6851386175019902) <= lossTol, $"losses {r.LossesMw:R}");
        OpfControlValue pst = Assert.Single(r.Controls);
        Assert.Equal("pst", pst.Kind);
        Assert.Equal("B", pst.Element);
        Assert.Equal(0.0, pst.CaseValue);
        Assert.Equal(-30.0, pst.Min);
        Assert.Equal(30.0, pst.Max);
        Assert.True(Math.Abs(pst.Value - 1.9524805291234986) <= 1e-3, $"phi {pst.Value:R} deg");
        Assert.True(r.LossesMw > 0.6666666666666667 && r.LossesMw < 0.77125686559531);

        // The same case with the angle held at 0 (a fixed control) gives the phi = 0 losses, and the report carries the
        // fixed value back in degrees.
        AcOpfExtendedResult r0 = AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses, Controls = c, FixedControlValues = new[] { 0.0 } });
        Assert.True(r0.Converged, r0.Base.Message);
        Assert.True(Math.Abs(r0.LossesMw - 0.77125686559531) <= 1e-8 * (1 + 1.0078), $"losses at 0: {r0.LossesMw:R}");
        Assert.True(Math.Abs(r0.Controls[0].Value) <= 1e-12, $"fixed angle {r0.Controls[0].Value:R}");

        // Rounding to a 1 degree step (flip mode): 1.95 rounds to 2 = -30 + 32 x 1, and the re-solve is feasible.
        var stepped = new OpfControlSet(Array.Empty<OpfTapControl>(), Array.Empty<OpfShuntControl>()) { PhaseShifts = new[] { new OpfPhaseShiftControl(1, -30, 30, 1) } };
        OpfRoundingResult rr = OpfRounding.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses, Controls = stepped });
        Assert.Equal(OpfRoundingStatus.Rounded, rr.Status);
        Assert.Equal(32, rr.Controls[0].Step);
        Assert.Equal(2.0, rr.FinalValues[0]);
        Assert.True(rr.Final!.LossesMw >= r.LossesMw - lossTol);

        // A continuous angle (step 0) is held at its continuous optimum by the rounding, never flipped.
        OpfRoundingResult rc = OpfRounding.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses, Controls = c });
        Assert.Equal(OpfRoundingStatus.Rounded, rc.Status);
        Assert.Equal(Bits(r.Controls[0].Value), Bits(rc.FinalValues[0]));
        Assert.True(double.IsNaN(rc.Controls[0].OtherNeighbour));
    }

    [Fact]
    public void Phase_shifter_controls_file_and_refusals()
    {
        GridModel m = PstTwoLine().CopyWith(
            transformerFlowControls: new[] { new TransformerFlowControl("B", 1, TransformerFlowKind.ActivePower, 0, 10, -20, 25, 0.5) });
        // Range and step from the branch's COD 3 control when the file gives none.
        OpfControlSet c = OpfControlSet.Parse("{\"pst\":[{\"branch\":\"B\"}]}", m);
        Assert.Equal(new OpfPhaseShiftControl(1, -20, 25, 0.5), Assert.Single(c.PhaseShifts));
        // Without a COD 3 control the range is needed.
        Assert.Contains("needs minDeg and maxDeg", Assert.Throws<InvalidDataException>(() => OpfControlSet.Parse("{\"pst\":[{\"branch\":\"A\"}]}", m)).Message, StringComparison.Ordinal);
        Assert.Contains("unknown pst member", Assert.Throws<InvalidDataException>(() => OpfControlSet.Parse("{\"pst\":[{\"branch\":\"B\",\"deg\":1}]}", m)).Message, StringComparison.Ordinal);
        Assert.Contains("names no branch", Assert.Throws<InvalidDataException>(() => OpfControlSet.Parse("{\"pst\":[{\"branch\":\"C\",\"minDeg\":0,\"maxDeg\":1}]}", m)).Message, StringComparison.Ordinal);
        // A tap and an angle on one branch, crossed ranges and a negative step are refused by the OPF.
        GridModel t = m.CopyWith(branches: new[] { m.Branches[0], m.Branches[1] with { IsTransformer = true } });
        var both = new OpfControlSet(new[] { new OpfTapControl(1, 0.9, 1.1, 0.0125) }, Array.Empty<OpfShuntControl>()) { PhaseShifts = new[] { new OpfPhaseShiftControl(1, -30, 30, 0) } };
        Assert.Contains("a tap control and a phase-shifter control", Assert.Throws<InvalidDataException>(() => AcOpfExtended.Solve(t, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses, Controls = both })).Message, StringComparison.Ordinal);
        foreach (OpfPhaseShiftControl bad in new[] { new OpfPhaseShiftControl(1, 10, -10, 0), new OpfPhaseShiftControl(1, -10, 10, -1), new OpfPhaseShiftControl(5, -10, 10, 0) })
        {
            var set = new OpfControlSet(Array.Empty<OpfTapControl>(), Array.Empty<OpfShuntControl>()) { PhaseShifts = new[] { bad } };
            Assert.Throws<InvalidDataException>(() => AcOpfExtended.Solve(m, new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses, Controls = set }));
        }
        // The AC-SCOPF does not take controls.
        Assert.Throws<InvalidDataException>(() => AcOpfExtended.SolveBlocks(m, new AcOpfExtendedOptions { Controls = c },
            new AcOpfBlocks(Array.Empty<ScopfOutage>(), Array.Empty<int>(), AcOpfCoupling.Preventive, null, PostFaultRating.RateA)));
    }

    // ---- 2. Derivatives --------------------------------------------------------------------------------------------

    [Fact]
    public void T2_phase_shifter_derivatives_match_central_finite_differences_on_case9()
    {
        // case9 with a phase shifter on branch 5 (7-8, rated, so its flow rows carry phi at both ends), a tap on branch 0
        // and a shunt at bus 5 as well. Central differences with h = 1e-6: the truncation error is h^2/6 |f'''| (about
        // 1e-12 x 1e4 here) and the rounding error eps |L| / h (about 2e-16 x 1e3 / 1e-6 = 2e-7 on the gradient of the
        // Hessian columns), so the phi row and column are held to 1e-6 (1 + |value|) and the rest, as in WP7, to 1e-5.
        GridModel m = Case("lib/case9.m");
        var opt = new AcOpfExtendedOptions
        {
            Objective = AcOpfObjective.Losses,
            Controls = new OpfControlSet(new[] { new OpfTapControl(0, 0.9, 1.1, 0.0125) }, new[] { new OpfShuntControl(5, -20, 40, 5, 0) })
            { PhaseShifts = new[] { new OpfPhaseShiftControl(5, -30, 30, 0) } },
        };
        (int nx, int neq, int niq) = AcOpfExtended.Dimensions(m, opt);
        int phiVar = nx - 1;
        var rnd = new Random(14);
        double[] x = AcOpfExtended.StartingPoint(m, opt);
        for (int j = 0; j < nx; j++) x[j] += 0.05 * (rnd.NextDouble() - 0.5);
        x[phiVar] = 0.07;   // a shift of about 4 degrees
        var lam = Enumerable.Range(0, neq).Select(_ => rnd.NextDouble() - 0.5).ToArray();
        var mu = Enumerable.Range(0, niq).Select(_ => rnd.NextDouble()).ToArray();
        double[] g = AcOpfExtended.LagrangianGradient(m, opt, x, lam, mu);
        double[,] h = AcOpfExtended.LagrangianHessian(m, opt, x, lam, mu);
        const double step = 1e-6;
        for (int j = 0; j < nx; j++)
        {
            double[] xp = (double[])x.Clone(), xm = (double[])x.Clone();
            xp[j] += step; xm[j] -= step;
            double fd = (AcOpfExtended.Lagrangian(m, opt, xp, lam, mu) - AcOpfExtended.Lagrangian(m, opt, xm, lam, mu)) / (2 * step);
            Assert.True(Math.Abs(fd - g[j]) <= 1e-6 * (1 + Math.Abs(g[j])), $"gradient {j}: analytic {g[j]:R}, finite difference {fd:R}");
            double[] gp = AcOpfExtended.LagrangianGradient(m, opt, xp, lam, mu), gm = AcOpfExtended.LagrangianGradient(m, opt, xm, lam, mu);
            for (int i = 0; i < nx; i++)
            {
                double fh = (gp[i] - gm[i]) / (2 * step);
                double tol = i == phiVar || j == phiVar ? 1e-6 : 1e-5;
                Assert.True(Math.Abs(fh - h[i, j]) <= tol * (1 + Math.Abs(h[i, j])), $"Hessian ({i},{j}): analytic {h[i, j]:R}, finite difference {fh:R}");
            }
        }
        // The phi column is not empty: the angle reaches the balance and the flow rows.
        Assert.Contains(Enumerable.Range(0, nx - 1), i => h[i, phiVar] != 0);
    }

    // ---- 3. Bit identity without the new options (pins from the 2.1.107 build, before WP14) -------------------------
    // Method: before any WP14 edit the 2.1.107 Release CLI (582956e1) ran each command below; the --out JSON file was
    // hashed as written, and the console output (stdout only, line ends normalised to LF, the "result written to" line
    // dropped) was hashed as UTF-8. The same commands must give the same hashes with WP14 in place.

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static string ConsoleSha(string stdout) =>
        Sha(Encoding.UTF8.GetBytes(string.Join("\n", stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => !l.Contains("result written to", StringComparison.Ordinal)))));

    [Theory]
    [InlineData("acopf-opt case9 --objective cost", "9a0e0a73c811bcc501d00f69adc2c6220af46df9d64268e9c483b4bd30b8eaee", "3f38afc98b71e598edab08d4c7eefc911e8e207391c410df5bfd603fea52c619")]
    [InlineData("acopf-opt case9 --objective losses", "e1636335c750b224d290a5190cedd7d618960c443d645b82498d3d316ab1c7e6", "e9c6e8d931a695a83e2e470347abcbb39af2a2cc815bd85e93c9b2886d807e2f")]
    [InlineData("acopf-opt case9 --objective rpo", "937da210fb2512936d25f0f464be53caf235f275189ee20d1d0bd4931f04cab1", "1d7ebbe6ed165e3621c1be0d652b385dfca98e48d2761cd0afbf28083701a06d")]
    [InlineData("acopf-opt case30 --objective cost", "14a50a580f1d71175376688a7823d027e4c6332dfdd07a14cab4258761e4718a", "bdaec3137f8e5f10300896e93729f2c93259db591b56cd11d404e3e2ecdb7a1e")]
    [InlineData("acopf-opt case30 --objective losses", "ef82a162fb68c121f3e0d79d274c570eac95f6cf8c442c1c45af12a7895251c5", "838cfa9c5f450e84910ffe15cb82a64295f3036e509e33e1bca13fea3b2ada39")]
    // case30 rpo ends in a numerical failure (exit 3, no result file) in 2.1.107 as well; its console is pinned.
    [InlineData("acopf-opt case30 --objective rpo", null, "7f57fbe3c0a488862d599e6d78ea41b60180ea624223596c3947ecedde922e6c")]
    [InlineData("acopf-opt case9 --objective losses --controls SHUNT", "25c903cebd1d74dbebe325d494378cec51dd1e84392c9d328c928f7b0bf4f1d3", "9569a4296cf4dbcd55d43f3d1d77dfea904f7a0ac5b9ab8fa6d6d41030e90baa")]
    [InlineData("acscopf case9 --contingencies 1,2", "8df510f9dbf20f05da8c96fd98119aadf47d82645457a20b8472eb261799d157", "f9ba0459b082f48dcb208c9bd6b640015906eb35f5cc22af58d04a01066c2a87")]
    [InlineData("acscopf case9 --mode corrective --ramp-mw 10 --contingencies 1,2,4", "2f4f5c783ff16faea9ab99bbb7dba36c8a7fe16d74e11cac10af39ead2dacec1", "ae121fb7139da3f087a08a3649374d7ba2efa2cd40905469dcdf13d6bea043aa")]
    public void T3_existing_commands_give_the_same_bytes_as_before_wp14(string command, string? jsonSha, string consoleSha)
    {
        string dir = TestTemp.Under("gridsim-wp14-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string shunt = Path.Combine(dir, "shunt9.json");
            File.WriteAllText(shunt, "{\"format\":\"gridsim-opf-controls/1\",\"shunts\":[{\"bus\":5,\"bMinMvar\":0,\"bMaxMvar\":50,\"stepMvar\":10}]}\n");
            string outFile = Path.Combine(dir, "out.json");
            string[] args = command.Split(' ').Select(t => t == "SHUNT" ? shunt : t).Concat(new[] { "--out", outFile }).ToArray();
            CliProcess.Result r = CliProcess.RunBuilt(args);
            Assert.Equal(consoleSha, ConsoleSha(r.StdOut));
            if (jsonSha is null) { Assert.Equal(3, r.ExitCode); Assert.False(File.Exists(outFile)); }
            else { Assert.Equal(0, r.ExitCode); Assert.Equal(jsonSha, Sha(File.ReadAllBytes(outFile))); }
        }
        finally { Directory.Delete(dir, true); }
    }

    // ---- 4. Neighbours rounding --------------------------------------------------------------------------------------
    // Radial 1 -(T1)- 2 -(T2)- 3, both transformers 0.005 + j0.05 pu, loads 40 + j20 at bus 2 and 60 + j30 at bus 3, V1
    // held at 1.0, V2 and V3 within 0.9 .. 1.05, losses objective. The continuous optimum raises both voltages to 1.05
    // (losses fall with voltage): tau1* = 0.92549712, tau2* = 0.98361762, losses 0.782272 MW (a NumPy/SciPy Newton power
    // flow on the MATPOWER branch model). Grids: T1 0.89 + n 0.05 (tau1* at n = 0.710), T2 0.975 + n 0.0125 (tau2* at
    // n = 0.689). With the taps fixed there is one unit and V1 is held, so each fixed-tap OPF is the power flow:
    //   (0.89, 0.975):  V2 1.09450, V3 1.10599  infeasible
    //   (0.89, 0.9875): V2 1.09448, V3 1.09153  infeasible
    //   (0.94, 0.975):  V2 1.03277, V3 1.04162  losses 0.805210652417145 MW  <- best
    //   (0.94, 0.9875): V2 1.03275, V3 1.02794  losses 0.8111234903294076 MW <- nearest (flip mode keeps it)

    private static GridModel TwoTap() => new()
    {
        Name = "two-tap", BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, VminPu: 1.0, VmaxPu: 1.0),
            new Bus(2, BusType.Pq, Pd: 40, Qd: 20, VminPu: 0.9, VmaxPu: 1.05),
            new Bus(3, BusType.Pq, Pd: 60, Qd: 30, VminPu: 0.9, VmaxPu: 1.05),
        },
        Generators = new[] { new Generator(1, 0, Pmin: 0, Pmax: 1000, Qmin: -1000, Qmax: 1000, Name: "slack") },
        Branches = new[]
        {
            new Branch(1, 2, 0.005, 0.05, Name: "T1", IsTransformer: true),
            new Branch(2, 3, 0.005, 0.05, Name: "T2", IsTransformer: true),
        },
    };

    private static readonly OpfControlSet TwoTaps = new(new[] { new OpfTapControl(0, 0.89, 1.09, 0.05), new OpfTapControl(1, 0.975, 1.1, 0.0125) }, Array.Empty<OpfShuntControl>());

    [Fact]
    public void T4_neighbours_rounding_finds_the_best_combination_that_flip_misses()
    {
        GridModel m = TwoTap();
        var opt = new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses, Controls = TwoTaps };
        OpfRoundingResult flip = OpfRounding.Solve(m, opt);
        OpfRoundingResult nb = OpfRounding.Solve(m, opt, OpfRoundingMode.Neighbours);
        Assert.True(nb.Continuous.Converged, nb.Continuous.Base.Message);
        Assert.Equal(0.92549712, nb.Continuous.Controls[0].Value, 1e-6);
        Assert.Equal(0.98361762, nb.Continuous.Controls[1].Value, 1e-6);

        // Enumerate the four combinations directly (cold-started fixed-control solves, grid points min + n step).
        double[] t1 = { 0.89 + 0 * 0.05, 0.89 + 1 * 0.05 }, t2 = { 0.975 + 0 * 0.0125, 0.975 + 1 * 0.0125 };
        AcOpfExtendedResult? best = null;
        double[]? bestValues = null;
        foreach (double a in t1)
            foreach (double b in t2)
            {
                AcOpfExtendedResult r = AcOpfExtended.Solve(m, opt with { FixedControlValues = new[] { a, b } });
                bool feasible = OpfRounding.Feasible(m, r);
                Assert.Equal(a > 0.9, feasible);   // tau1 = 0.89 puts V2 near 1.0945, above 1.05
                if (feasible && (best is null || r.Base.ObjectivePerHour < best.Base.ObjectivePerHour)) { best = r; bestValues = new[] { a, b }; }
            }
        Assert.NotNull(best);
        Assert.Equal(new[] { 0.94, 0.975 }, bestValues!.Select(v => Math.Round(v, 12)));

        // Neighbours mode is that enumerated best, bit for bit (each of its solves is the same cold-started solve).
        Assert.Equal(OpfRoundingStatus.BestNeighbour, nb.Status);
        Assert.Equal(OpfRoundingMode.Neighbours, nb.Mode);
        Assert.Equal(4, nb.Combinations);
        Assert.Equal(1, nb.Combination);   // bit 0 (T1) at its ceil, bit 1 (T2) at its floor
        Assert.Equal(bestValues!.Select(Bits), nb.FinalValues.Select(Bits));
        Assert.Equal(Bits(best!.Base.ObjectivePerHour), Bits(nb.Final!.Base.ObjectivePerHour));
        Assert.Contains("not a global mixed-integer optimum", nb.Message, StringComparison.Ordinal);
        // The losses agree with the power-flow reference (the fixed-tap solve meets the balance to 1e-12 relative, about
        // 1e-9 MW per bus; 1e-8 MW covers three buses).
        Assert.Equal(0.805210652417145, nb.Final.LossesMw, 1e-8);

        // Flip mode keeps the nearest point (both taps up), which is feasible but worse.
        Assert.Equal(OpfRoundingStatus.Rounded, flip.Status);
        Assert.Equal(new[] { 0.94, 0.9875 }, flip.FinalValues.Select(v => Math.Round(v, 12)));
        Assert.Equal(0.8111234903294076, flip.Final!.LossesMw, 1e-8);
        Assert.True(flip.Final.Base.ObjectivePerHour > nb.Final.Base.ObjectivePerHour);

        // The report names the mode only in neighbours mode.
        Assert.Contains(OptimisationReport.Rows(nb), row => row.Field == "rounding_mode");
        Assert.DoesNotContain(OptimisationReport.Rows(flip), row => row.Field == "rounding_mode");
    }

    [Fact]
    public void T4b_neighbours_rounding_refuses_thirteen_discrete_controls()
    {
        GridModel m = Case("lib/case14.m");
        var shunts = Enumerable.Range(2, 13).Select(b => new OpfShuntControl(b, 0, 20, 5, 0)).ToArray();
        var opt = new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses, Controls = new OpfControlSet(Array.Empty<OpfTapControl>(), shunts) };
        InvalidDataException ex = Assert.Throws<InvalidDataException>(() => OpfRounding.Solve(m, opt, OpfRoundingMode.Neighbours));
        Assert.Contains("13 controls", ex.Message, StringComparison.Ordinal);
        Assert.Contains("limit of 12", ex.Message, StringComparison.Ordinal);
        // A continuous angle is not a discrete control, so it does not count.
        Assert.Equal(0, OpfRounding.DiscreteCount(new OpfControlSet(Array.Empty<OpfTapControl>(), Array.Empty<OpfShuntControl>())
        { PhaseShifts = new[] { new OpfPhaseShiftControl(0, -1, 1, 0) } }));
    }

    // ---- 5-7. Generator outages ------------------------------------------------------------------------------------
    // Bus 1 (slack) G1 cost 10; bus 2 load 100 MW, G2 cost 5 Pmax 60, G3 cost 30; line 1-2 X = 0.05 (lossless), rating
    // 200 MVA. Both voltages are held at 1.0 pu and G2's Q at 0, so no direction of the problem is left without an
    // objective or a constraint to fix it (a free direction is held by the barrier alone, and then the feasibility measure
    // falls only by about 0.8 per iteration and cannot reach 1e-12). The outage is G2. The units carry no MBase, so their
    // governor-droop weights are equal (the system base) and G1 and G3 each take half of G2's output: alpha = 1/2.
    //
    // Tolerances (2.1.110). The runs use the 1e-12 settings (AcOpfExtended.DefaultIpm(Losses)). The P balance of the
    // lossless line sums to sum Pg = 100 MW in each block, so in the units' outputs the problem is a linear programme; its
    // optimum is a vertex fixed by the active rows. At the computed point:
    //   - an equality row is met to 1e-12 (1 + max(|x|inf, |z|inf)) = 5e-12 pu, since |x|inf = 1 (the voltages) and
    //     |z|inf <= 4 (the flow rows: Smax^2 = 4 pu^2); the two bus balances sum to 1e-11 pu;
    //   - an active inequality i has slack z_i <= z'mu / mu_i <= 1e-12 (1 + |x|inf) / mu_i = 2e-12 / mu_i, where mu_i is
    //     its multiplier in the scaled problem: the reduced cost in $/MWh x 100 MW/pu x 1e-4 = reduced cost x 1e-2.
    // Each output then follows from the active rows; its error is the sum of their slacks and residuals through those rows
    // (worked per test below, in pu; x 100 for MW). The objective error is at most sum c_g |error_g|. Observed errors are
    // about 1e-10 $/h, three orders inside.

    private static GridModel GenOutageCase(double g1Max, double g3Max) => new()
    {
        Name = "gen-outage", BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, VminPu: 1.0, VmaxPu: 1.0), new Bus(2, BusType.Pv, Pd: 100, Qd: 0, VminPu: 1.0, VmaxPu: 1.0) },
        Generators = new[]
        {
            new Generator(1, 0, Qmin: -100, Qmax: 100, Pmin: 0, Pmax: g1Max, Name: "G1") { Cost = new GeneratorCost(GeneratorCostModel.Polynomial, 0, 0, new[] { 10.0, 0.0 }) },
            new Generator(2, 0, Qmin: 0, Qmax: 0, Pmin: 0, Pmax: 60, Name: "G2") { Cost = new GeneratorCost(GeneratorCostModel.Polynomial, 0, 0, new[] { 5.0, 0.0 }) },
            new Generator(2, 0, Qmin: -100, Qmax: 100, Pmin: 0, Pmax: g3Max, Name: "G3") { Cost = new GeneratorCost(GeneratorCostModel.Polynomial, 0, 0, new[] { 30.0, 0.0 }) },
        },
        Branches = new[] { new Branch(1, 2, 0, 0.05, RateAMva: 200, Name: "L") },
    };

    private static readonly AcOpfIpmOptions Tight = AcOpfExtended.DefaultIpm(AcOpfObjective.Losses);

    private static void Near(double expected, double actual, double bound, string what) =>
        Assert.True(Math.Abs(actual - expected) <= bound, $"{what} {actual:R}, expected {expected:R} within {bound:R}");

    [Fact]
    public void T5_generator_outage_corrective()
    {
        // G1 Pmax 60, G3 Pmax 50, ramp 5 MW. After the outage G1c + G3c = 100, G1c <= 60, and
        // |G3c - (G3 + G2/2)| <= 5. So G3c >= 40 and G3 >= 35 - G2/2. Cost = 10 G1 + 5 G2 + 30 G3 = 1000 - 5 G2 + 20 G3
        // = 1700 - 15 G2 along G3 = 35 - G2/2, least at G2 = 60. Optimum G1 = 35, G2 = 60, G3 = 5, cost 800; after the
        // outage G1c = 60, G3c = 40 (G3's droop share of 30 MW plus its 5 MW ramp).
        // Duals (L = c'x + lambda'g + mu'h): lambda0 = 10 (G1 free), ramp 20 (G3), G1c upper 20, G2 upper 15. Slacks:
        // G2 2e-12/0.15 = 1.33e-11, G1c 2e-12/0.2 = 1e-11, ramp 1e-11. Errors (pu): G2 1.33e-11, G1c 1e-11,
        // G3c = 1 - G1c + r: 2e-11, G3 = G3c - G2/2 - 0.05 + z: 2e-11 + 0.67e-11 + 1e-11 = 3.67e-11,
        // G1 = 1 - G2 - G3 + r: 1.33e-11 + 3.67e-11 + 1e-11 = 6e-11. Cost: 100 (10 x 6e-11 + 5 x 1.33e-11 + 30 x 3.67e-11)
        // = 1.77e-7 $/h.
        AcScopfResult r = AcScopf.Solve(GenOutageCase(60, 50), new AcScopfOptions
        {
            Mode = AcScopfMode.Corrective, RampMw = 5, Outages = new[] { ScopfOutage.OfGenerator(1) }, Ipm = Tight,
        });
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(new[] { 0 }, r.Secured);
        Assert.NotNull(r.OutageList);
        AcOpfIpmResult b = r.Last!.Base;
        Near(800, b.ObjectivePerHour, 1.77e-7, "cost");
        Near(35, b.Generators[0].PgMw, 6e-9, "G1");
        Near(60, b.Generators[1].PgMw, 1.33e-9, "G2");
        Near(5, b.Generators[2].PgMw, 3.67e-9, "G3");
        AcOpfBlockResult c = Assert.Single(r.Last.Outages);
        Assert.Equal(0, c.Outage);
        Assert.Equal("generator 1", c.OutageLabel);
        Assert.Equal(0.0, c.Generators[1].PgMw);    // held at 0 by its own bounds
        Assert.Equal(0.0, c.Generators[1].QgMvar);
        Near(60, c.Generators[0].PgMw, 1e-9, "G1c");
        Near(40, c.Generators[2].PgMw, 2e-9, "G3c");
        // Round 1 is the plain optimum (G1 40, G2 60, G3 0). Its screen shares G2's 60 MW by droop: 30 to G3 and 30 to G1,
        // which puts G1 at 70 MW, above its 60 MW, so the outage is secured.
        Assert.Equal(new[] { 0 }, r.Rounds[0].Added);
        Assert.Contains(r.Last.CouplingBinding, x => x.Kind == "RampUp" && x.Element == "generator 2 after outage generator 1");
    }

    [Fact]
    public void T6_generator_outage_preventive()
    {
        // G1 Pmax 80, G3 Pmax 25. After the outage G3c = G3 + G2/2 <= 25 (G3's reserve for its droop share) and
        // G1c = G1 + G2/2 <= 80. Cost = 1000 - 5 G2 + 20 G3: G3 = 0 and G2 = 50 (G3's reserve binds), G1 = 50; cost 750;
        // after the outage G1c = 75, G3c = 25. The plain optimum (40, 60, 0) fails the screen: G3's share of 30 MW takes it
        // to 30 MW, above 25.
        // Duals: lambda0 = 10, coupling 10, G3c upper 10, G3 lower 30. Slacks: G3 2e-12/0.3 = 6.7e-12, G3c 2e-12/0.1 =
        // 2e-11. Errors (pu): G3 6.7e-12, G3c 2e-11, G2 = 2 (G3c - G3 - r): 2 (2e-11 + 6.7e-12 + 5e-12) = 6.34e-11,
        // G1 = 1 - G2 - G3 + r: 6.34e-11 + 6.7e-12 + 1e-11 = 8.0e-11, G1c = 1 - G3c + r: 3e-11. Cost: 100 (10 x 8e-11 +
        // 5 x 6.34e-11 + 30 x 6.7e-12) = 1.32e-7 $/h.
        AcScopfResult r = AcScopf.Solve(GenOutageCase(80, 25), new AcScopfOptions { Outages = new[] { ScopfOutage.OfGenerator(1) }, Ipm = Tight });
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(new[] { 0 }, r.Rounds[0].Added);
        AcOpfIpmResult b = r.Last!.Base;
        Near(750, b.ObjectivePerHour, 1.32e-7, "cost");
        Near(50, b.Generators[0].PgMw, 8e-9, "G1");
        Near(50, b.Generators[1].PgMw, 6.34e-9, "G2");
        Near(0, b.Generators[2].PgMw, 6.7e-10, "G3");
        AcOpfBlockResult c = Assert.Single(r.Last.Outages);
        Assert.Equal(0.0, c.Generators[1].PgMw);
        Assert.Equal(0.0, c.Generators[1].QgMvar);
        Near(75, c.Generators[0].PgMw, 3e-9, "G1c");
        Near(25, c.Generators[2].PgMw, 2e-9, "G3c");
        // The preventive coupling row G3c - G3 - G2/2 = 0 is an equality row, met to 5e-12 pu = 5e-10 MW; the MW values
        // are the pu values times 100, which adds a few ulp of 50 (1e-14). So 1e-9 MW holds with a factor of two to spare.
        Assert.True(Math.Abs(c.Generators[2].PgMw - (b.Generators[2].PgMw + 0.5 * b.Generators[1].PgMw)) <= 1e-9);
    }

    [Fact]
    public void T7_an_outage_of_the_slack_unit_moves_the_reference()
    {
        // G1 is the only unit at the slack bus. Its outage moves the reference to the bus of the largest remaining unit by
        // case |Pg| (all are 0 here, so the first: G2 at bus 2), as the contingency screen does; G2 and G3 then balance the
        // block between them. The plain optimum (40, 60, 0) passes: bus 2 must then carry 100 MW within its units' range
        // 0 .. 110 MW. Voltages free (0.9 .. 1.1) so bus 1 can float without a unit; default settings.
        GridModel m = GenOutageCase(80, 50);
        m = m.CopyWith(buses: m.Buses.Select(x => x with { VminPu = 0.9, VmaxPu = 1.1 }).ToArray());
        AcScopfResult r = AcScopf.Solve(m, new AcScopfOptions { Outages = new[] { ScopfOutage.OfGenerator(0) } });
        Assert.True(r.Optimal, r.Message);
        Assert.Empty(r.Secured);
        Assert.Equal(new[] { 0 }, r.ScreenedOk);
        AcScopf.OutageResponse resp = AcScopf.Response(m, ScopfOutage.OfGenerator(0));
        Assert.Equal(new[] { false, true, true }, resp.Balancing);
        Assert.Equal(new[] { BusType.Pq, BusType.Slack }, resp.Types);
        Assert.Equal("reference moved from bus 1 to bus 2", resp.Note);
        Assert.Equal(new[] { 0.0, 0.5, 0.5 }, resp.Alpha);
        // With G3 Pmax 30 the reference bus cannot carry the 100 MW: the screen says so, by the moved reference.
        GridModel small = m.CopyWith(generators: m.Generators.Select((g, i) => i == 2 ? g with { Pmax = 30 } : g).ToArray());
        AcScopfScreen sc = AcScopf.ScreenCore(small, r.Last!.Base, r.Last.Base.Generators, 0, ScopfOutage.OfGenerator(0), PostFaultRating.RateA);
        Assert.False(sc.Passes);
        Assert.StartsWith("slack bus 2 at ", sc.Reason, StringComparison.Ordinal);
        Assert.EndsWith(" MW against its units' range 0 .. 90 MW", sc.Reason, StringComparison.Ordinal);
        // Giving both lists is refused, and so is a duplicate and a unit the case does not have.
        Assert.Throws<ArgumentException>(() => AcScopf.Solve(m, new AcScopfOptions { Outages = new[] { ScopfOutage.OfGenerator(1) }, Contingencies = new[] { 0 } }));
        Assert.Throws<ArgumentException>(() => AcScopf.Solve(m, new AcScopfOptions { Outages = new[] { ScopfOutage.OfGenerator(1), ScopfOutage.OfGenerator(1) } }));
        Assert.Throws<ArgumentException>(() => AcScopf.Solve(m, new AcScopfOptions { Outages = new[] { ScopfOutage.OfGenerator(3) } }));
        // Taking out every unit leaves nothing to hold the reference: refused by name.
        ArgumentException none = Assert.Throws<ArgumentException>(() =>
            AcScopf.Solve(m, new AcScopfOptions { Outages = new[] { new ScopfOutage(Array.Empty<int>(), new[] { 0, 1, 2 }) } }));
        Assert.Contains("leaves no in-service unit to take over the reference of bus 1", none.Message, StringComparison.Ordinal);
    }

    // ---- 8. Branch-only outages through the new list give the old bits ---------------------------------------------

    private static void SameBits(AcOpfIpmResult a, AcOpfIpmResult b)
    {
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(Bits(a.ObjectivePerHour), Bits(b.ObjectivePerHour));
        Assert.Equal(a.Buses.SelectMany(x => new[] { x.VmPu, x.VaDeg, x.LmpP, x.LmpQ }).Select(Bits), b.Buses.SelectMany(x => new[] { x.VmPu, x.VaDeg, x.LmpP, x.LmpQ }).Select(Bits));
        Assert.Equal(a.Generators.SelectMany(x => new[] { x.PgMw, x.QgMvar }).Select(Bits), b.Generators.SelectMany(x => new[] { x.PgMw, x.QgMvar }).Select(Bits));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void T8_one_branch_outages_give_the_same_bits_as_contingencies(bool corrective)
    {
        // case9 with every RateA x 0.75 (the Rc5Wp7 T11 case), every in-service branch listed. Preventive and corrective
        // (ramp 30 MW). The ids are branch indices on the old path and list positions on the new one; with every branch
        // listed in order the two coincide.
        GridModel src = Case("lib/case9.m");
        GridModel m = src.CopyWith(branches: src.Branches.Select(b => b with { RateAMva = b.RateAMva * 0.75 }).ToArray());
        var legacyOpt = new AcScopfOptions { Mode = corrective ? AcScopfMode.Corrective : AcScopfMode.Preventive, RampMw = corrective ? 30 : double.NaN };
        AcScopfResult a = AcScopf.Solve(m, legacyOpt);
        AcScopfResult b = AcScopf.Solve(m, legacyOpt with { Outages = Enumerable.Range(0, m.Branches.Count).Select(ScopfOutage.OfBranch).ToArray() });
        Assert.Equal(a.Status, b.Status);
        Assert.Equal(a.Secured, b.Secured);
        Assert.Equal(a.ScreenedOk, b.ScreenedOk);
        Assert.Equal(a.Islanding, b.Islanding);
        Assert.Equal(a.UnresolvedSecuredOutages, b.UnresolvedSecuredOutages);
        Assert.Equal(a.Message, b.Message);
        Assert.Equal(a.Rounds.Select(x => (x.Round, string.Join(",", x.Secured), x.Iterations, Bits(x.Objective), string.Join(",", x.Added))),
            b.Rounds.Select(x => (x.Round, string.Join(",", x.Secured), x.Iterations, Bits(x.Objective), string.Join(",", x.Added))));
        SameBits(a.Last!.Base, b.Last!.Base);
        Assert.Equal(a.Last.Outages.Count, b.Last.Outages.Count);
        for (int i = 0; i < a.Last.Outages.Count; i++)
        {
            Assert.Equal(a.Last.Outages[i].Outage, b.Last.Outages[i].Outage);
            Assert.Equal(a.Last.Outages[i].Buses.SelectMany(x => new[] { x.VmPu, x.VaDeg }).Select(Bits), b.Last.Outages[i].Buses.SelectMany(x => new[] { x.VmPu, x.VaDeg }).Select(Bits));
            Assert.Equal(a.Last.Outages[i].Generators.SelectMany(x => new[] { x.PgMw, x.QgMvar }).Select(Bits), b.Last.Outages[i].Generators.SelectMany(x => new[] { x.PgMw, x.QgMvar }).Select(Bits));
        }
        Assert.Equal(a.Last.CouplingBinding.Select(x => Bits(x.ShadowPrice)), b.Last.CouplingBinding.Select(x => Bits(x.ShadowPrice)));
        Assert.Null(a.OutageList);
        Assert.NotNull(b.OutageList);
        if (!corrective) Assert.Equal(new[] { 7, 8 }, a.Secured);   // T11's pinned set
    }

    // ---- 9. N-2 ------------------------------------------------------------------------------------------------------
    // Bus 1 slack at 1.0 pu; bus 2 load 50 + j10 fed by three parallel lines 1-2 (branches 0-2, 0.01 + j0.1); bus 3 load
    // 20 + j5 fed by two parallel lines 2-3 (branches 3-4, 0.02 + j0.2); one unit, unrated lines, losses objective.

    private static GridModel ThreeParallel() => new()
    {
        Name = "n2", BaseMva = 100,
        Buses = new[]
        {
            new Bus(1, BusType.Slack, VminPu: 1.0, VmaxPu: 1.0),
            new Bus(2, BusType.Pq, Pd: 50, Qd: 10, VminPu: 0.9, VmaxPu: 1.1),
            new Bus(3, BusType.Pq, Pd: 20, Qd: 5, VminPu: 0.9, VmaxPu: 1.1),
        },
        Generators = new[] { new Generator(1, 0, Pmin: 0, Pmax: 1000, Qmin: -1000, Qmax: 1000, Name: "slack") },
        Branches = new[]
        {
            new Branch(1, 2, 0.01, 0.1), new Branch(1, 2, 0.01, 0.1), new Branch(1, 2, 0.01, 0.1),
            new Branch(2, 3, 0.02, 0.2), new Branch(2, 3, 0.02, 0.2),
        },
    };

    [Fact]
    public void T9_an_n2_block_equals_the_n1_block_of_the_case_with_one_branch_already_out()
    {
        // With one unit there are no coupling rows, and a block's state (theta2, theta3, V2, V3, Pg, Qg) is the unique
        // root of its six balance equations, which each solve meets to 1e-12 (1 + max(|x|inf, |z|inf)), about 1e-11 pu
        // (|z|inf <= 10, the unit's P range in pu). The Jacobian of this network maps that to a state error of the order
        // of |X| x 1e-11, far inside the 1e-9 asserted.
        GridModel m = ThreeParallel();
        var opt = new AcOpfExtendedOptions { Objective = AcOpfObjective.Losses };
        AcOpfExtendedResult n2 = AcOpfExtended.SolveBlocks(m, opt,
            new AcOpfBlocks(new[] { new ScopfOutage(new[] { 0, 1 }, Array.Empty<int>()) }, new[] { 0 }, AcOpfCoupling.Preventive, null, PostFaultRating.RateA));
        GridModel less = m.CopyWith(branches: m.Branches.Select((b, k) => k == 0 ? b with { InService = false } : b).ToArray());
        AcOpfExtendedResult n1 = AcOpfExtended.SolveBlocks(less, opt,
            new AcOpfBlocks(new[] { ScopfOutage.OfBranch(1) }, new[] { 1 }, AcOpfCoupling.Preventive, null, PostFaultRating.RateA));
        Assert.True(n2.Converged, n2.Base.Message);
        Assert.True(n1.Converged, n1.Base.Message);
        AcOpfBlockResult a = Assert.Single(n2.Outages), b = Assert.Single(n1.Outages);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(b.Buses[i].VmPu, a.Buses[i].VmPu, 1e-9);
            Assert.Equal(b.Buses[i].VaDeg * Math.PI / 180, a.Buses[i].VaDeg * Math.PI / 180, 1e-9);
        }
        Assert.Equal(b.Generators[0].PgMw / 100, a.Generators[0].PgMw / 100, 1e-9);
        Assert.Equal(b.Generators[0].QgMvar / 100, a.Generators[0].QgMvar / 100, 1e-9);
        // The block reports the flows of the branches left in it: 2 and 3, 4 (branch 0 and 1 are out).
        Assert.Equal(new[] { 2, 3, 4 }, a.Branches.Select(x => x.Index));

        // Every pair of the five branches: the pair 3-4 islands bus 3, so it is listed and not secured. The lines are
        // unrated, so every other pair passes its screen.
        AcScopfResult r = AcScopf.Solve(m, new AcScopfOptions { Objective = AcOpfObjective.Losses, Outages = ScopfOutageFile.Pairs(Enumerable.Range(0, 5).ToArray()) });
        Assert.True(r.Optimal, r.Message);
        Assert.Equal(new[] { 9 }, r.Islanding);
        Assert.Equal("branches 3+4", r.OutageList![9].Label);
        Assert.Equal(Enumerable.Range(0, 9), r.ScreenedOk);
        Assert.Empty(r.Secured);
        // The N-2 screen takes out both branches: the pair 1-2 leaves bus 2 on branch 0 alone.
        Assert.True(AcScopf.ScreenCore(m, r.Last!.Base, r.Last.Base.Generators, 4, r.OutageList[4], PostFaultRating.RateA).Passes);
        Assert.Equal("branches 1+2", r.OutageList[4].Label);
    }

    // ---- 10. Pair enumeration and the cap ---------------------------------------------------------------------------

    [Fact]
    public void T10_pairs_come_in_lexicographic_order_and_the_cap_is_enforced()
    {
        IReadOnlyList<ScopfOutage> p = ScopfOutageFile.Pairs(new[] { 3, 1, 0, 2 });
        Assert.Equal(new[] { (0, 1), (0, 2), (0, 3), (1, 2), (1, 3), (2, 3) }, p.Select(o => (o.Branches[0], o.Branches[1])));
        Assert.All(p, o => Assert.Empty(o.Generators));
        int[] many = Enumerable.Range(0, 101).ToArray();   // 101 x 100 / 2 = 5050 pairs
        Assert.Contains("5050 pairs, above the cap of 5000", Assert.Throws<InvalidDataException>(() => ScopfOutageFile.Pairs(many)).Message, StringComparison.Ordinal);
        Assert.Equal(5050, ScopfOutageFile.Pairs(many, 5050).Count);
        Assert.Throws<InvalidDataException>(() => ScopfOutageFile.Pairs(new[] { 1, 1 }));
    }

    [Fact]
    public void Outage_file_reads_and_refuses()
    {
        IReadOnlyList<ScopfOutage> o = ScopfOutageFile.Parse(
            "{\"format\":\"gridsim-scopf-outages/1\",\"outages\":[{\"branches\":[0,1],\"name\":\"double circuit\"},{\"generators\":[2]},{\"branches\":[3],\"generators\":[1]}]}");
        Assert.Equal(3, o.Count);
        Assert.Equal("double circuit", o[0].Label);
        Assert.Equal("generator 2", o[1].Label);
        Assert.Equal("branch 3, generator 1", o[2].Label);
        Assert.Throws<InvalidDataException>(() => ScopfOutageFile.Parse("{\"outages\":[{\"branches\":[0]}]}"));                                       // no format
        Assert.Throws<InvalidDataException>(() => ScopfOutageFile.Parse("{\"format\":\"gridsim-scopf-outages/1\",\"outages\":[{}]}"));                   // removes nothing
        Assert.Throws<InvalidDataException>(() => ScopfOutageFile.Parse("{\"format\":\"gridsim-scopf-outages/1\",\"outages\":[{\"branches\":[-1]}]}"));  // not an index
        Assert.Throws<InvalidDataException>(() => ScopfOutageFile.Parse("{\"format\":\"gridsim-scopf-outages/1\",\"outages\":[{\"bus\":[1]}]}"));         // unknown member
    }

    // ---- CLI ---------------------------------------------------------------------------------------------------------

    private const string GenOutageM = """
        function mpc = genout
        mpc.version = '2';
        mpc.baseMVA = 100;
        mpc.bus = [ 1 3 0 0 0 0 1 1 0 230 1 1.1 0.9; 2 2 100 0 0 0 1 1 0 230 1 1.1 0.9; ];
        mpc.gen = [ 1 0 0 100 -100 1 100 1 60 0; 2 0 0 100 -100 1 100 1 60 0; 2 0 0 100 -100 1 100 1 50 0; ];
        mpc.branch = [ 1 2 0 0.05 0 200 200 200 0 0 1 -360 360; ];
        mpc.gencost = [ 2 0 0 2 10 0; 2 0 0 2 5 0; 2 0 0 2 30 0; ];
        """;

    [Fact]
    public void Cli_acscopf_takes_an_outage_file_and_n2_pairs()
    {
        string dir = TestTemp.Under("gridsim-wp14-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string mfile = Path.Combine(dir, "genout.m");
            File.WriteAllText(mfile, GenOutageM);
            string ofile = Path.Combine(dir, "outages.json");
            File.WriteAllText(ofile, "{\"format\":\"gridsim-scopf-outages/1\",\"outages\":[{\"generators\":[1]}]}");
            string res = Path.Combine(dir, "r.json");
            CliProcess.Result r = CliProcess.RunBuilt("acscopf", mfile, "--mode", "corrective", "--ramp-mw", "5", "--outages", ofile, "--out", res);
            Assert.True(r.ExitCode == 0, $"exit {r.ExitCode}: {r.StdErr}");
            Assert.Contains("after outage 0 (generator 1)", r.StdOut, StringComparison.Ordinal);
            using (JsonDocument d = JsonDocument.Parse(File.ReadAllText(res)))
            {
                JsonElement[] rows = d.RootElement.GetProperty("rows").EnumerateArray().ToArray();
                JsonElement obj = rows.First(x => x[0].GetString() == "base" && x[2].GetString() == "objective")[3];
                // T5's case with the voltages free (0.9 .. 1.1) and the CLI's settings (tolerances tau = 1e-6). T5's working
                // with |x|inf <= 1.1 gives slacks 2.1 tau / mu_i and an objective error of at most
                // 100 (10 x 62 + 5 x 14 + 30 x 38) tau = 0.183 $/h (G1, G2, G3 off by 62, 14 and 38 tau pu).
                Assert.True(Math.Abs(obj.GetDouble() - 800) <= 0.183, $"objective {obj.GetDouble():R}");
                Assert.Contains(rows, x => x[0].GetString() == "outage" && x[1].GetString() == "0" && x[2].GetString() == "label" && x[3].GetString() == "generator 1");
            }
            // The slack unit can be taken out (the reference moves); --outages with --n2 or --contingencies, and
            // --max-outages without --n2, are usage errors (exit 2); a pair count above the cap is refused (exit 1).
            File.WriteAllText(ofile, "{\"format\":\"gridsim-scopf-outages/1\",\"outages\":[{\"generators\":[0]}]}");
            CliProcess.Result slack = CliProcess.RunBuilt("acscopf", mfile, "--outages", ofile);
            Assert.True(slack.ExitCode == 0, $"exit {slack.ExitCode}: {slack.StdErr}");
            Assert.Equal(2, CliProcess.RunBuilt("acscopf", mfile, "--outages", ofile, "--n2", "pairs").ExitCode);
            Assert.Equal(2, CliProcess.RunBuilt("acscopf", mfile, "--outages", ofile, "--contingencies", "0").ExitCode);
            Assert.Equal(2, CliProcess.RunBuilt("acscopf", "case9", "--max-outages", "3").ExitCode);
            CliProcess.Result cap = CliProcess.RunBuilt("acscopf", "case9", "--n2", "pairs", "--max-outages", "3");
            Assert.Equal(1, cap.ExitCode);
            Assert.Contains("36 pairs, above the cap of 3", cap.StdErr, StringComparison.Ordinal);
            Assert.Equal(2, CliProcess.RunBuilt("acopf-opt", "case9", "--rounding", "best").ExitCode);
        }
        finally { Directory.Delete(dir, true); }
    }
}
