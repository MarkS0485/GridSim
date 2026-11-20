using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GridSim.Core.Distribution;
using GridSim.Core.Distribution.Meshed;
using GridSim.Core.Lines;
using GridSim.Core.Model;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC4 R4-15: the meshed unbalanced load flow in the explicit phase frame (Core Distribution/Meshed). Every oracle is derived in
/// the test: Kirchhoff loop and nodal equations solved with a test-local Gaussian elimination, Kron reductions computed here, the
/// split-phase and SWER closed forms, a hand-worked ring, the incumbent radial sweep and positive-sequence solver for the
/// agreement cases, and (for the IEEE 4-node feeder) the published node voltages carried in the repo
/// (tests/GridSim.Tests/MultiPhaseJsonLoaderTests.cs).
/// </summary>
public class R415MeshedLoadFlowTests
{
    private static Complex C(double r, double i) => new(r, i);
    private static double Abs(Complex c) => Math.Sqrt(c.Real * c.Real + c.Imaginary * c.Imaginary);
    private static Complex Polar(double mag, double deg) => GridSim.Core.Numerics.DetMath.FromPolar(mag, deg * Math.PI / 180.0);

    private static void Near(Complex expected, Complex actual, double tol, string what)
        => Assert.True(Abs(expected - actual) <= tol, $"{what}: expected {expected} but got {actual} (|diff| {Abs(expected - actual):E3} > {tol:E1})");

    /// <summary>A test-local complex Gaussian elimination with partial pivoting (not the solver's sparse real LU).</summary>
    private static Complex[] Solve(Complex[,] a0, Complex[] b0)
    {
        int n = b0.Length;
        var a = (Complex[,])a0.Clone();
        var b = (Complex[])b0.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (Abs(a[r, c]) > Abs(a[p, c])) p = r;
            for (int j = 0; j < n; j++) (a[c, j], a[p, j]) = (a[p, j], a[c, j]);
            (b[c], b[p]) = (b[p], b[c]);
            for (int r = c + 1; r < n; r++)
            {
                Complex f = a[r, c] / a[c, c];
                for (int j = c; j < n; j++) a[r, j] -= f * a[c, j];
                b[r] -= f * b[c];
            }
        }
        var x = new Complex[n];
        for (int r = n - 1; r >= 0; r--)
        {
            Complex s = b[r];
            for (int j = r + 1; j < n; j++) s -= a[r, j] * x[j];
            x[r] = s / a[r, r];
        }
        return x;
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GridSim.slnx"))) dir = dir.Parent;
        Assert.True(dir is not null, "GridSim.slnx not found");
        return dir!.FullName;
    }

    private static Complex[] Abc(double mag) => [Polar(mag, 0), Polar(mag, -120), Polar(mag, 120)];

    /// <summary>S that makes a constant-impedance leg of impedance z at nominal voltage v0: z = v0^2 / conj(S).</summary>
    private static Complex SFor(Complex z, double v0) => v0 * v0 / Complex.Conjugate(z);

    // A hand-written, symmetric 4x4 primitive impedance (ohm): phases a, b, c and neutral n, with unequal selfs and mutuals.
    private static Complex[,] Z4() => new[,]
    {
        { C(0.50, 1.20), C(0.10, 0.50), C(0.09, 0.42), C(0.10, 0.46) },
        { C(0.10, 0.50), C(0.52, 1.15), C(0.11, 0.48), C(0.10, 0.44) },
        { C(0.09, 0.42), C(0.11, 0.48), C(0.49, 1.18), C(0.09, 0.40) },
        { C(0.10, 0.46), C(0.10, 0.44), C(0.09, 0.40), C(0.80, 1.30) },
    };

    private static PhaseFrameModel TwoBusFourWire(Complex[,] z4, Complex[] zLoad, Zip zip, double[] pScale, bool earthLoadNeutral, double rLoadEarth = 5.0)
    {
        const double vln = 7200.0;
        var nodes = new[] { new FrameNode("S", ["a", "b", "c", "n"]), new FrameNode("L", ["a", "b", "c", "n"]) };
        var src = new FrameSource("S", ["a", "b", "c"], Abc(vln));
        var line = new FrameBranch("line", "S", "L", ["a", "b", "c", "n"], z4);
        var s = new Complex[3];
        for (int p = 0; p < 3; p++) s[p] = SFor(zLoad[p], vln) * pScale[p];
        var load = FrameLoad.Wye("load", "L", s, vln, zip);
        var earth = new List<EarthingElement> { new("S", "n", Complex.Zero) };
        if (earthLoadNeutral) earth.Add(new EarthingElement("L", "n", C(rLoadEarth, 0)));
        return new PhaseFrameModel("two-bus", nodes, [src], [line], [load], earth);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // (1) Three-phase line with neutral and an unbalanced load, source fixed: Kirchhoff with the full 4x4 primitive matrix.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Four_wire_line_neutral_grounded_at_source_matches_the_hand_solved_loop_equations()
    {
        Complex[,] z = Z4();
        Complex[] zl = [C(20, 8), C(15, 6), C(30, 10)];
        MeshedResult r = Newton3Ph.Solve(TwoBusFourWire(z, zl, Zip.ConstantImpedance, [1, 1, 1], false));
        Assert.Equal(MeshedStatus.Converged, r.Status);
        Assert.True(r.MismatchAmps < 1e-6);

        // Loop equation with the neutral as the return (I_n = -sum I_j):  E_p = Zl_p I_p + sum_j (Z_pj - Z_pn - Z_nj + Z_nn) I_j.
        Complex[] e = Abc(7200.0);
        var m = new Complex[3, 3];
        for (int p = 0; p < 3; p++)
            for (int j = 0; j < 3; j++)
                m[p, j] = z[p, j] - z[p, 3] - z[3, j] + z[3, 3] + (p == j ? zl[p] : Complex.Zero);
        Complex[] i = Solve(m, e);
        Complex iN = -(i[0] + i[1] + i[2]);

        LoadState load = r.Load("load");
        for (int p = 0; p < 3; p++) Near(i[p], load.Current[p], 1e-7, "load current " + "abc"[p]);
        BranchCurrents br = r.Branch("line");
        Near(iN, br.IFrom[3], 1e-7, "neutral current (From end)");
        Near(Complex.Zero, br.Residual, 1e-7, "no earth return on a lossless 4-wire line with the neutral earthed only at the source and no shunt");

        // Load-end voltages by Kirchhoff: V_n(L) = -(sum_j Z_nj I_j + Z_nn I_n); V_p(L) = Zl_p I_p + V_n(L).
        Complex vnL = -(z[3, 0] * i[0] + z[3, 1] * i[1] + z[3, 2] * i[2] + z[3, 3] * iN);
        Near(vnL, r.V("L", "n"), 1e-6, "neutral potential rise at the load");
        for (int p = 0; p < 3; p++) Near(zl[p] * i[p] + vnL, r.V("L", "abc"[p].ToString()), 1e-6, "load node voltage " + "abc"[p]);
        // The neutral shift is real (non-zero) and the three currents are unequal: the case really is unbalanced.
        Assert.True(Abs(vnL) > 1.0);
        Assert.True(Abs(i[0]) - Abs(i[2]) > 1.0);
    }

    [Fact]
    public void Neutral_grounded_at_both_ends_is_a_parallel_path_and_is_solved_without_a_kron_reduction()
    {
        Complex[,] z = Z4();
        Complex[] zl = [C(20, 8), C(15, 6), C(30, 10)];
        double rg = 5.0;
        MeshedResult r = Newton3Ph.Solve(TwoBusFourWire(z, zl, Zip.ConstantImpedance, [1, 1, 1], true, rg));
        Assert.Equal(MeshedStatus.Converged, r.Status);
        Assert.DoesNotContain(r.Notes, n => n.Contains("Kron", StringComparison.Ordinal));

        // Hand solution. Every conductor current is positive toward the load: I_a, I_b, I_c and the neutral wire's Jn. KCL at the load-end
        // neutral node: Ie = Jn + sum(I_p) leaves through the electrode, so V_N = rg Ie. Phase p: V_Sp - V_Lp = sum_j Z_pj I_j + Z_pn Jn with
        // V_Lp = Zl_p I_p + V_N. Neutral wire: 0 - V_N = sum_j Z_nj I_j + Z_nn Jn. A 4x4 system in (I_a, I_b, I_c, Jn).
        Complex[] e = Abc(7200.0);
        var m = new Complex[4, 4];
        var b = new Complex[4];
        for (int p = 0; p < 3; p++)
        {
            for (int j = 0; j < 3; j++) m[p, j] = z[p, j] + rg + (p == j ? zl[p] : Complex.Zero);
            m[p, 3] = z[p, 3] + rg;
            b[p] = e[p];
        }
        for (int j = 0; j < 3; j++) m[3, j] = z[3, j] + rg;
        m[3, 3] = z[3, 3] + rg;
        b[3] = Complex.Zero;
        Complex[] x = Solve(m, b);
        LoadState load = r.Load("load");
        for (int p = 0; p < 3; p++) Near(x[p], load.Current[p], 1e-7, "line/load current " + "abc"[p]);
        Complex ie = x[0] + x[1] + x[2] + x[3];
        Near(rg * ie, r.V("L", "n"), 1e-6, "load-end neutral potential");
        Near(x[3], r.Branch("line").IFrom[3], 1e-7, "neutral wire current (toward the load)");
        Near(ie, r.Earthing.Single(q => q.Node == "L").Current, 1e-7, "current through the load earth electrode");
        // The branch's earth return (sum of its conductor currents at the From end) is what leaves at the load electrode.
        Near(ie, r.Branch("line").Residual, 1e-7, "branch earth-return residual");
        // The source-end solid earthing takes the rest of the neutral current: neutral wire current arriving there (-Jn) is the
        // current the electrode at S passes to earth.
        Near(-x[3], r.Earthing.Single(q => q.Node == "S").Current, 1e-7, "current into the source-end earth");
    }

    [Fact]
    public void A_declared_multi_grounded_neutral_is_kron_reduced_and_the_reduction_is_recorded()
    {
        Complex[,] z = Z4();
        Complex[] zl = [C(20, 8), C(15, 6), C(30, 10)];
        double vln = 7200.0;
        var nodes = new[] { new FrameNode("S", ["a", "b", "c"]), new FrameNode("L", ["a", "b", "c"]) };
        var line = new FrameBranch("line", "S", "L", ["a", "b", "c", "n"], z, null, "n");
        var s = zl.Select(q => SFor(q, vln)).ToArray();
        var model = new PhaseFrameModel("kron", nodes, [new FrameSource("S", ["a", "b", "c"], Abc(vln))], [line],
            [FrameLoad.WyeEarthed("load", "L", s, vln, Zip.ConstantImpedance)]);
        MeshedResult r = Newton3Ph.Solve(model);
        Assert.Equal(MeshedStatus.Converged, r.Status);
        Assert.Contains(r.Notes, n => n.StartsWith("Kron reduction: wire 'n' of branch 'line'", StringComparison.Ordinal));

        // Kron reduction computed here: Zred_pj = Z_pj - Z_pn Z_nj / Z_nn.
        var zr = new Complex[3, 3];
        for (int p = 0; p < 3; p++)
            for (int j = 0; j < 3; j++) zr[p, j] = z[p, j] - z[p, 3] * z[3, j] / z[3, 3] + (p == j ? zl[p] : Complex.Zero);
        Complex[] i = Solve(zr, Abc(vln));
        for (int p = 0; p < 3; p++) Near(i[p], r.Load("load").Current[p], 1e-7, "current " + "abc"[p]);
        // The neutral through-current implied by V_n = 0 along the line: Z_np I_p + Z_nn I_n = 0.
        Complex iN = -(z[3, 0] * i[0] + z[3, 1] * i[1] + z[3, 2] * i[2]) / z[3, 3];
        BranchCurrents br = r.Branch("line");
        Assert.Equal(["a", "b", "c", "n"], br.Wires);
        Near(iN, br.IFrom[3], 1e-7, "implied neutral current");
        for (int p = 0; p < 3; p++) Near(zl[p] * i[p], r.V("L", "abc"[p].ToString()), 1e-6, "load voltage " + "abc"[p]);
    }

    [Fact]
    public void Constant_power_unbalanced_load_matches_an_independent_fixed_point_on_the_hand_loop_equations()
    {
        Complex[,] z = Z4();
        double vln = 7200.0;
        Complex[] s = [C(60e3, 25e3), C(40e3, 12e3), C(90e3, 30e3)];
        var nodes = new[] { new FrameNode("S", ["a", "b", "c", "n"]), new FrameNode("L", ["a", "b", "c", "n"]) };
        var model = new PhaseFrameModel("cp", nodes, [new FrameSource("S", ["a", "b", "c"], Abc(vln))],
            [new FrameBranch("line", "S", "L", ["a", "b", "c", "n"], z)], [FrameLoad.Wye("load", "L", s, vln)],
            [new EarthingElement("S", "n", Complex.Zero)]);
        MeshedResult r = Newton3Ph.Solve(model);
        Assert.Equal(MeshedStatus.Converged, r.Status);
        Assert.True(r.Iterations <= 8, $"Newton took {r.Iterations} iterations");

        // Independent fixed point: u = E - Zeff I (leg voltage), I = conj(S/u).
        var zeff = new Complex[3, 3];
        for (int p = 0; p < 3; p++) for (int j = 0; j < 3; j++) zeff[p, j] = z[p, j] - z[p, 3] - z[3, j] + z[3, 3];
        Complex[] e = Abc(vln);
        Complex[] i = new Complex[3];
        for (int p = 0; p < 3; p++) i[p] = Complex.Conjugate(s[p] / e[p]);
        for (int it = 0; it < 500; it++)
        {
            var next = new Complex[3];
            for (int p = 0; p < 3; p++)
            {
                Complex u = e[p];
                for (int j = 0; j < 3; j++) u -= zeff[p, j] * i[j];
                next[p] = Complex.Conjugate(s[p] / u);
            }
            i = next;
        }
        LoadState load = r.Load("load");
        for (int p = 0; p < 3; p++)
        {
            Complex u = e[p];
            for (int j = 0; j < 3; j++) u -= zeff[p, j] * i[j];
            Near(u, load.Voltage[p], 1e-6, "leg voltage " + "abc"[p]);
            Near(i[p], load.Current[p], 1e-6, "current " + "abc"[p]);
            Near(s[p], load.Power[p], 1e-3, "power delivered " + "abc"[p]);
        }
    }

    [Fact]
    public void Line_constants_from_R4_04_feed_the_phase_frame_and_agree_with_the_solvers_own_kron_reduction()
    {
        // Geometry in metres; resistances in ohm/km (typical ACSR numbers used as plain inputs, not as published data).
        ConductorData Ph(string n, double x) => new(n, ConductorRole.Phase, x, 8.5344, 0.1902, 0.00744, 0.009157);
        var neutral = new ConductorData("N", ConductorRole.GroundWire, 0.0, 7.3152, 0.3679, 0.002481, 0.00715);
        var geo = new LineGeometry([Ph("A", -1.2192), Ph("B", -0.4572), Ph("C", 0.9144), neutral], 50.0, 100.0, EarthReturnMethod.EarthIntegral, false);
        LineConstantsResult lc = LineConstantsSolver.Solve(geo);
        double km = 3.0;
        int Ix(string n) => lc.AllConductors.ToList().IndexOf(n);
        string[] names = ["A", "B", "C", "N"];
        var z = new Complex[4, 4];
        for (int p = 0; p < 4; p++) for (int j = 0; j < 4; j++) z[p, j] = lc.PrimitiveZ[Ix(names[p]), Ix(names[j])] * km;

        double vln = 11000.0 / Math.Sqrt(3.0);
        Complex[] zl = [C(60, 20), C(45, 15), C(80, 30)];
        var nodes = new[] { new FrameNode("S", ["a", "b", "c"]), new FrameNode("L", ["a", "b", "c"]) };
        var model = new PhaseFrameModel("lc", nodes, [new FrameSource("S", ["a", "b", "c"], Abc(vln))],
            [new FrameBranch("line", "S", "L", ["a", "b", "c", "n"], z, null, "n")],
            [FrameLoad.WyeEarthed("load", "L", zl.Select(q => SFor(q, vln)).ToArray(), vln, Zip.ConstantImpedance)]);
        MeshedResult r = Newton3Ph.Solve(model);
        Assert.Equal(MeshedStatus.Converged, r.Status);

        // R4-04's own Kron-reduced phase matrix (ohm/km) must equal the reduction computed here from its primitive matrix.
        var zr = new Complex[3, 3];
        for (int p = 0; p < 3; p++)
            for (int j = 0; j < 3; j++)
            {
                Complex own = lc.PhaseZ[Ix(names[p]), Ix(names[j])] * km;
                Complex mine = z[p, j] - z[p, 3] * z[3, j] / z[3, 3];
                Near(own, mine, 1e-9, $"R4-04 Kron-reduced Z[{p},{j}]");
                zr[p, j] = mine + (p == j ? zl[p] : Complex.Zero);
            }
        Complex[] i = Solve(zr, Abc(vln));
        for (int p = 0; p < 3; p++) Near(i[p], r.Load("load").Current[p], 1e-8, "current " + "abc"[p]);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // (2) Balanced case: the unbalanced solver reproduces the positive-sequence solver.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_balanced_feeder_reproduces_the_positive_sequence_newton_raphson_result()
    {
        // Positive-sequence radial feeder, 10 MVA base: slack 1.0 pu, PQ loads at buses 2 and 3.
        var model = new GridModel
        {
            BaseMva = 10.0,
            Buses =
            [
                new Bus(1, BusType.Slack, BaseKv: 11.0),
                new Bus(2, BusType.Pq, Pd: 2.0, Qd: 1.0, BaseKv: 11.0),
                new Bus(3, BusType.Pq, Pd: 1.5, Qd: 0.6, BaseKv: 11.0),
            ],
            Generators = [new Generator(1, 0.0, 0.0, 1.0)],
            Branches = [new Branch(1, 2, 0.02, 0.06), new Branch(2, 3, 0.03, 0.08)],
        };
        PowerFlowResult pf = new NewtonRaphsonPowerFlow().Solve(model);
        Assert.True(pf.Converged);

        // The same feeder in the frame: per-phase base from the 3-phase base; mutual coupling Zm = 0.3 Z1 added (it cannot
        // matter to a balanced current), neutral earthed at the loads.
        double vBase = 11000.0 / Math.Sqrt(3.0);
        double sBase = 10e6 / 3.0;
        double zBase = vBase * vBase / sBase;
        Complex[,] Line(double rpu, double xpu)
        {
            Complex z1 = C(rpu, xpu) * zBase, zm = z1 * 0.3, zs = z1 + zm;
            var m = new Complex[3, 3];
            for (int p = 0; p < 3; p++) for (int j = 0; j < 3; j++) m[p, j] = p == j ? zs : zm;
            return m;
        }
        Complex[] Sph(double pMw, double qMvar) => [C(pMw * 1e6 / 3, qMvar * 1e6 / 3), C(pMw * 1e6 / 3, qMvar * 1e6 / 3), C(pMw * 1e6 / 3, qMvar * 1e6 / 3)];
        var nodes = new[] { new FrameNode("n1", ["a", "b", "c"]), new FrameNode("n2", ["a", "b", "c"]), new FrameNode("n3", ["a", "b", "c"]) };
        var frame = new PhaseFrameModel("balanced", nodes, [new FrameSource("n1", ["a", "b", "c"], Abc(vBase))],
            [new FrameBranch("l12", "n1", "n2", ["a", "b", "c"], Line(0.02, 0.06)), new FrameBranch("l23", "n2", "n3", ["a", "b", "c"], Line(0.03, 0.08))],
            [FrameLoad.WyeEarthed("ld2", "n2", Sph(2.0, 1.0), vBase), FrameLoad.WyeEarthed("ld3", "n3", Sph(1.5, 0.6), vBase)]);
        MeshedResult r = Newton3Ph.Solve(frame);
        Assert.Equal(MeshedStatus.Converged, r.Status);

        for (int k = 1; k <= 2; k++)
        {
            string node = "n" + (k + 1);
            for (int p = 0; p < 3; p++)
            {
                Complex v = r.V(node, "abc"[p].ToString());
                Assert.True(Math.Abs(Abs(v) / vBase - pf.Vm[k]) < 1e-9, $"{node} {"abc"[p]} magnitude {Abs(v) / vBase} vs {pf.Vm[k]}");
                double deg = Math.Atan2(v.Imaginary, v.Real) * 180.0 / Math.PI - (p == 0 ? 0 : p == 1 ? -120 : 120);
                Assert.True(Math.Abs(deg - pf.VaDeg[k]) < 1e-7, $"{node} {"abc"[p]} angle {deg} vs {pf.VaDeg[k]}");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // (3) Radial agreement with the incumbent three-phase sweep (different algorithm: stated tolerance, not bit identity).
    // ---------------------------------------------------------------------------------------------------------------

    private static PhaseImpedance Z3(double[,] r, double[,] x)
    {
        var z = new Complex[3, 3];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) z[i, j] = C(r[i, j], x[i, j]);
        return new PhaseImpedance(z);
    }

    private static MultiPhaseModel IncumbentFeeder()
    {
        double[,] r = { { 0.020, 0.006, 0.005 }, { 0.006, 0.021, 0.006 }, { 0.005, 0.006, 0.019 } };
        double[,] x = { { 0.050, 0.020, 0.016 }, { 0.020, 0.048, 0.018 }, { 0.016, 0.018, 0.052 } };
        PhaseImpedance full = Z3(r, x);
        var za = new Complex[3, 3]; za[0, 0] = C(0.04, 0.09);
        var zbc = new Complex[3, 3]; zbc[1, 1] = C(0.05, 0.10); zbc[2, 2] = C(0.052, 0.11); zbc[1, 2] = zbc[2, 1] = C(0.012, 0.03);
        var sh = new Complex[3, 3]; for (int i = 0; i < 3; i++) sh[i, i] = C(0, 0.01);
        var buses = new[] { new PhaseBus(1, true, MultiPhaseModel.BalancedNominal()), new PhaseBus(2, false), new PhaseBus(3, false), new PhaseBus(4, false), new PhaseBus(5, false) };
        var branches = new[]
        {
            new PhaseBranch(1, 2, full),
            new PhaseBranch(2, 3, full, Phase.Abc, new PhaseImpedance(sh)),
            new PhaseBranch(3, 4, new PhaseImpedance(za), Phase.A),
            new PhaseBranch(2, 5, new PhaseImpedance(zbc), Phase.B | Phase.C),
        };
        var loads = new[]
        {
            new PhaseLoad(3, [C(0.060, 0.025), C(0.040, 0.012), C(0.090, 0.030)]),
            new PhaseLoad(2, [C(0.030, 0.010), C(0.020, 0.008), C(0.025, 0.006)], PhaseLoadModel.ConstantCurrent, PhaseLoadConnection.Delta),
            new PhaseLoad(4, [C(0.020, 0.005), Complex.Zero, Complex.Zero], PhaseLoadModel.ConstantImpedance),
            new PhaseLoad(5, [Complex.Zero, C(0.030, 0.010), Complex.Zero], PhaseLoadModel.ConstantPower, PhaseLoadConnection.Delta),
        };
        return new MultiPhaseModel(buses, branches, loads, 10.0, [new PhaseCapacitor(3, [0.01, 0.02, 0.005])]);
    }

    [Fact]
    public void Radial_feeder_with_partial_phases_shunts_delta_and_zip_loads_agrees_with_the_incumbent_sweep()
    {
        MultiPhaseModel inc = IncumbentFeeder();
        MultiPhaseResult sweep = new ThreePhaseSweep().Solve(inc);
        Assert.True(sweep.Converged);

        double vBase = 7200.0, sBase = 10e6 / 3.0;
        FrameAdapterResult lifted = MultiPhaseFrameAdapter.FromMultiPhase(inc, vBase, sBase);
        Assert.NotNull(lifted.Model);
        MeshedResult r = Newton3Ph.Solve(lifted.Model!);
        Assert.Equal(MeshedStatus.Converged, r.Status);

        double worst = 0;
        for (int b = 0; b < sweep.BusIds.Count; b++)
            for (int p = 0; p < 3; p++)
            {
                if (!PhaseIndex.Has(sweep.BusPhases![b], p)) continue;
                Complex got = r.V(MultiPhaseFrameAdapter.Name(sweep.BusIds[b]), "abc"[p].ToString()) / vBase;
                worst = Math.Max(worst, Abs(got - sweep.V[b][p]));
            }
        // Stated tolerance: 1e-8 per unit (the sweep stops at a 1e-10 step; both are far inside this).
        Assert.True(worst < 1e-8, $"worst voltage difference {worst:E3} pu");

        // Branch currents too: the sweep publishes per-unit current toward the To bus; compare the first branch (no shunt).
        double iBase = sBase / vBase;
        BranchCurrents b12 = r.Branches.Single(q => q.Name.StartsWith("br0:", StringComparison.Ordinal));
        for (int p = 0; p < 3; p++) Assert.True(Abs(b12.IFrom[p] / iBase - sweep.BranchCurrents![0][p]) < 1e-8);
    }

    [Theory]
    [InlineData("ieee4-gyGy-stepdown-balanced.json", true)]
    [InlineData("ieee4-gyGy-stepdown-unbalanced.json", false)]
    public void Ieee_4_node_gyGy_case_matches_the_published_voltages_and_the_incumbent_sweep(string file, bool balanced)
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(Path.Combine(Root(), "data", "dist3ph", file));
        MultiPhaseResult sweep = new ThreePhaseSweep().Solve(f.Model);
        double vBase = f.VoltBaseLN(1);
        double sBase = f.Model.BaseMva * 1e6 / 3.0;
        FrameAdapterResult lifted = MultiPhaseFrameAdapter.FromMultiPhase(f.Model, vBase, sBase);
        Assert.NotNull(lifted.Model);
        MeshedResult r = Newton3Ph.Solve(lifted.Model!);
        Assert.Equal(MeshedStatus.Converged, r.Status);

        // Published values as carried in tests/GridSim.Tests/MultiPhaseJsonLoaderTests.cs (copied from there; the IEEE PES
        // source document was NOT re-fetched in this work order, so the printed values are UNCONFIRMED here against the source;
        // the comparison tolerance is half the last printed digit: 0.5 V and 0.05 degree).
        (int Bus, double[] V, double[] Deg)[] published = balanced
            ?
            [
                (2, [7107.0, 7140.0, 7121.0], [-0.3, -120.3, 119.6]),
                (3, [2247.6, 2269.0, 2256.0], [-3.7, -123.5, 116.4]),
                (4, [1918.0, 2061.0, 1981.0], [-9.1, -128.3, 110.9]),
            ]
            :
            [
                (2, [7164.0, 7110.0, 7082.0], [-0.1, -120.2, 119.3]),
                (3, [2305.0, 2255.0, 2203.0], [-2.3, -123.6, 114.8]),
                (4, [2175.0, 1930.0, 1833.0], [-4.1, -126.8, 102.8]),
            ];
        var failures = new List<string>();
        foreach ((int bus, double[] vPub, double[] degPub) in published)
            for (int p = 0; p < 3; p++)
            {
                Complex v = r.V(MultiPhaseFrameAdapter.Name(bus), "abc"[p].ToString());
                double volts = Abs(v) / vBase * f.VoltBaseLN(bus);
                double deg = Math.Atan2(v.Imaginary, v.Real) * 180.0 / Math.PI;
                if (Math.Abs(volts - vPub[p]) > 0.5 || Math.Abs(deg - degPub[p]) > 0.05)
                    failures.Add($"bus {bus} {"abc"[p]}: {volts:F3} V {deg:F4} deg vs {vPub[p]} V {degPub[p]} deg");
            }
        // The balanced node-3 phase a value is the one the repo's own test records as 0.136 V outside the half digit; it is inside
        // the 0.5 V tolerance used in both places.
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        // And against the incumbent sweep, tightly.
        for (int b = 0; b < sweep.BusIds.Count; b++)
            for (int p = 0; p < 3; p++)
                Assert.True(Abs(r.V(MultiPhaseFrameAdapter.Name(sweep.BusIds[b]), "abc"[p].ToString()) / vBase - sweep.V[b][p]) < 1e-8);
    }

    [Theory]
    [InlineData("ieee123.json")]
    [InlineData("ieee4-dGy-stepdown-unbalanced.json")]
    [InlineData("ieee4-yD-stepup-balanced.json")]
    public void Transformer_and_regulator_cases_are_reported_as_out_of_scope_by_name_not_dropped(string file)
    {
        MultiPhaseFeeder f = MultiPhaseJsonLoader.Load(Path.Combine(Root(), "data", "dist3ph", file));
        FrameAdapterResult lifted = MultiPhaseFrameAdapter.FromMultiPhase(f.Model, 7200.0, f.Model.BaseMva * 1e6 / 3.0, 1e-6);
        Assert.Null(lifted.Model);
        Assert.NotEmpty(lifted.Unsupported);
        Assert.All(lifted.Unsupported, u => Assert.Contains("branch", u, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // (4) Meshed loop worked by hand: current split by impedance.
    // ---------------------------------------------------------------------------------------------------------------

    private static PhaseFrameModel Ring(Complex zSA, Complex zSB, Complex zAB, Complex[] zlA, Complex[] zlB, double vln, Zip zip)
    {
        Complex[,] Diag(Complex z) => new[,] { { z, Complex.Zero, Complex.Zero }, { Complex.Zero, z, Complex.Zero }, { Complex.Zero, Complex.Zero, z } };
        string[] w = ["a", "b", "c"];
        var nodes = new[] { new FrameNode("S", w), new FrameNode("A", w), new FrameNode("B", w) };
        return new PhaseFrameModel("ring", nodes, [new FrameSource("S", w, Abc(vln))],
            [new FrameBranch("SA", "S", "A", w, Diag(zSA)), new FrameBranch("SB", "S", "B", w, Diag(zSB)), new FrameBranch("AB", "A", "B", w, Diag(zAB))],
            [FrameLoad.WyeEarthed("la", "A", zlA.Select(q => SFor(q, vln)).ToArray(), vln, zip), FrameLoad.WyeEarthed("lb", "B", zlB.Select(q => SFor(q, vln)).ToArray(), vln, zip)]);
    }

    [Fact]
    public void Meshed_ring_with_unbalanced_loads_matches_the_hand_nodal_solution_and_the_impedance_current_split()
    {
        Complex zSA = C(1.0, 2.0), zSB = C(1.5, 2.5), zAB = C(0.8, 1.6);
        Complex[] zlA = [C(40, 10), C(25, 8), C(60, 20)];
        Complex[] zlB = [C(30, 12), C(50, 5), C(20, 10)];
        double vln = 7200.0;
        MeshedResult r = Newton3Ph.Solve(Ring(zSA, zSB, zAB, zlA, zlB, vln, Zip.ConstantImpedance));
        Assert.Equal(MeshedStatus.Converged, r.Status);
        Complex[] e = Abc(vln);
        for (int p = 0; p < 3; p++)
        {
            // Nodal equations by hand: [y1+y3+yA, -y3; -y3, y2+y3+yB] [VA; VB] = E [y1; y2]; Cramer's rule.
            Complex y1 = 1 / zSA, y2 = 1 / zSB, y3 = 1 / zAB, ya = 1 / zlA[p], yb = 1 / zlB[p];
            Complex a11 = y1 + y3 + ya, a12 = -y3, a21 = -y3, a22 = y2 + y3 + yb;
            Complex det = a11 * a22 - a12 * a21;
            Complex va = (e[p] * y1 * a22 - a12 * e[p] * y2) / det;
            Complex vb = (a11 * e[p] * y2 - a21 * e[p] * y1) / det;
            Near(va, r.V("A", "abc"[p].ToString()), 1e-7, "VA " + "abc"[p]);
            Near(vb, r.V("B", "abc"[p].ToString()), 1e-7, "VB " + "abc"[p]);
            // Branch currents (toward the To node) and the loop circulation.
            Complex isa = (e[p] - va) * y1, isb = (e[p] - vb) * y2, iab = (va - vb) * y3;
            Near(isa, r.Branch("SA").IFrom[p], 1e-7, "I(SA) " + "abc"[p]);
            Near(isb, r.Branch("SB").IFrom[p], 1e-7, "I(SB) " + "abc"[p]);
            Near(iab, r.Branch("AB").IFrom[p], 1e-7, "I(AB) " + "abc"[p]);
            // KCL at A: what comes in through SA less what leaves through AB is the load current.
            Near(isa - iab, va * ya, 1e-7, "KCL at A " + "abc"[p]);
        }
    }

    [Fact]
    public void Single_load_on_a_ring_splits_by_the_inverse_path_impedances_whatever_the_load_model()
    {
        // Load only at A (a negligible load at B). The current entering A splits between the direct feeder (zSA) and the path
        // S-B-A (zSB + zAB) inversely to those impedances: I_direct = I_load (zSB + zAB) / (zSA + zSB + zAB).
        Complex zSA = C(1.0, 2.0), zSB = C(1.5, 2.5), zAB = C(0.8, 1.6);
        Complex[] zlA = [C(40, 10), C(25, 8), C(60, 20)];
        Complex[] zlB = [C(1e9, 0), C(1e9, 0), C(1e9, 0)];   // no load at B
        double vln = 7200.0;
        foreach (Zip zip in new[] { Zip.ConstantImpedance, Zip.ConstantPower, new Zip(0.5, 0.3, 0.2) })
        {
            MeshedResult r = Newton3Ph.Solve(Ring(zSA, zSB, zAB, zlA, zlB, vln, zip));
            Assert.Equal(MeshedStatus.Converged, r.Status);
            for (int p = 0; p < 3; p++)
            {
                Complex iLoad = r.Load("la").Current[p];
                Complex direct = r.Branch("SA").IFrom[p];
                Complex expected = iLoad * (zSB + zAB) / (zSA + zSB + zAB);
                // The tiny load at B (1e9 ohm) perturbs the split by about 1e-9 relative.
                Near(expected, direct, 1e-6 * Abs(iLoad), "direct feeder share " + "abc"[p]);
                // The rest of the load current reaches A through B: S -> B -> A, so SB carries it and AB (written A to B) carries it backwards.
                Near(iLoad - direct, r.Branch("SB").IFrom[p], 1e-6 * Abs(iLoad), "path S-B share " + "abc"[p]);
                Near(-(iLoad - direct), r.Branch("AB").IFrom[p], 1e-6 * Abs(iLoad), "AB carries the path current toward A " + "abc"[p]);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // (5) Split phase.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Split_phase_service_unbalanced_halves_neutral_current_is_the_sum_of_the_two_line_currents()
    {
        // 240 V ideal split-phase source (l1 = +120, l2 = -120, n earthed), service lines Zl, neutral conductor Zn, loads l1-n, l2-n
        // and l1-l2 (constant impedance, so the answer is an exact linear one).
        Complex zl = C(0.05, 0.02), zn = C(0.08, 0.03), r1 = C(6.0, 1.0), r2 = C(12.0, 2.0), r3 = C(20.0, 5.0);
        var nodes = new[] { new FrameNode("S", ["l1", "l2", "n"]), new FrameNode("L", ["l1", "l2", "n"]) };
        Complex[,] Z3x3() => new[,] { { zl, 0, 0 }, { 0, zl, 0 }, { 0, 0, zn } };
        var model = new PhaseFrameModel("split", nodes, [SplitPhase.IdealSource("S", 240.0)],
            [new FrameBranch("service", "S", "L", ["l1", "l2", "n"], Z3x3())],
            [FrameLoad.LineLine("h1", "L", "l1", "n", SFor(r1, 120.0), 120.0, Zip.ConstantImpedance),
             FrameLoad.LineLine("h2", "L", "l2", "n", SFor(r2, 120.0), 120.0, Zip.ConstantImpedance),
             FrameLoad.LineLine("h12", "L", "l1", "l2", SFor(r3, 240.0), 240.0, Zip.ConstantImpedance)]);
        MeshedResult r = Newton3Ph.Solve(model);
        Assert.Equal(MeshedStatus.Converged, r.Status);

        // By hand: unknowns V1, V2 at the load node, neutral wire current Jn (toward the source) = I1 + I2, V_N = zn Jn.
        // Node l1: (V1 - VN)/r1 + (V1 - V2)/r3 = I1 = (E1 - V1)/zl.    Node l2: (V2 - VN)/r2 - (V1 - V2)/r3 = I2 = (E2 - V2)/zl.
        Complex e1 = 120, e2 = -120;
        // Substitute VN = zn (I1 + I2) with I_k = (E_k - V_k)/zl and solve the 2x2 for V1, V2.
        var a = new Complex[2, 2];
        var b = new Complex[2];
        // l1 equation:  V1/r1 + (V1-V2)/r3 + (V1 - VN)(0) ... written with VN = zn/zl (E1 + E2 - V1 - V2):
        Complex k = zn / zl;
        // (V1 - VN)/r1 + (V1 - V2)/r3 - (E1 - V1)/zl = 0
        a[0, 0] = 1 / r1 + k / r1 + 1 / r3 + 1 / zl; a[0, 1] = k / r1 - 1 / r3; b[0] = k / r1 * (e1 + e2) + e1 / zl;
        // (V2 - VN)/r2 - (V1 - V2)/r3 - (E2 - V2)/zl = 0
        a[1, 0] = k / r2 - 1 / r3; a[1, 1] = 1 / r2 + k / r2 + 1 / r3 + 1 / zl; b[1] = k / r2 * (e1 + e2) + e2 / zl;
        // (V - VN)/r form: V/r + k V1/r + k V2/r - k(E1+E2)/r  -> moved: coefficients above (VN = k (E1+E2-V1-V2)).
        Complex[] v = Solve(a, b);
        Complex i1 = (e1 - v[0]) / zl, i2 = (e2 - v[1]) / zl;
        Complex jn = i1 + i2;
        Near(i1, r.Branch("service").IFrom[0], 1e-9, "line l1 current");
        Near(i2, r.Branch("service").IFrom[1], 1e-9, "line l2 current");
        // The neutral wire current toward the source is the sum of the two line currents (KCL at the load node) ...
        Near(-jn, r.Branch("service").IFrom[2], 1e-9, "neutral wire current, magnitude and sign from KCL");
        Near(SplitPhase_Sum(r.Branch("service").IFrom[0], r.Branch("service").IFrom[1]), -r.Branch("service").IFrom[2], 1e-12, "neutral = I_l1 + I_l2");
        // ... so with l1 and l2 in antiphase the neutral carries less than the larger line current (here the 240 V load cancels in the sum).
        Assert.True(Abs(jn) > 1.0 && Abs(jn) < Abs(i1));
    }

    private static Complex SplitPhase_Sum(Complex a, Complex b) => a + b;

    [Fact]
    public void Split_phase_balanced_halves_carry_no_neutral_current()
    {
        Complex r1 = C(6.0, 1.0);
        var nodes = new[] { new FrameNode("S", ["l1", "l2", "n"]), new FrameNode("L", ["l1", "l2", "n"]) };
        Complex zl = C(0.05, 0.02), zn = C(0.08, 0.03);
        var model = new PhaseFrameModel("split-bal", nodes, [SplitPhase.IdealSource("S", 240.0)],
            [new FrameBranch("service", "S", "L", ["l1", "l2", "n"], new[,] { { zl, 0, 0 }, { 0, zl, 0 }, { 0, 0, zn } })],
            [FrameLoad.LineLine("h1", "L", "l1", "n", SFor(r1, 120.0), 120.0, Zip.ConstantImpedance),
             FrameLoad.LineLine("h2", "L", "l2", "n", SFor(r1, 120.0), 120.0, Zip.ConstantImpedance)]);
        MeshedResult r = Newton3Ph.Solve(model);
        Assert.Equal(MeshedStatus.Converged, r.Status);
        Near(Complex.Zero, r.Branch("service").IFrom[2], 1e-9, "neutral current of balanced halves");
    }

    [Fact]
    public void Centre_tap_transformer_feeding_unbalanced_halves_matches_the_hand_solution_and_the_neutral_is_the_sum_of_the_line_currents()
    {
        // 11 kV / 240 V centre-tap unit (primary a-n, secondary halves l1-n and n-l2, ct earthed), service lines with a neutral
        // conductor, loads l1-n, l2-n and l1-l2 at the far end.
        double vp0 = 11000.0, vs = 240.0, t2 = vs / 2.0;
        Complex zp = C(20.0, 60.0), zh = C(0.01, 0.03), zl = C(0.05, 0.02), zn = C(0.08, 0.03), r1 = C(6.0, 1.0), r2 = C(12.0, 2.0), r3 = C(20.0, 5.0);
        var nodes = new[] { new FrameNode("P", ["a", "n"]), new FrameNode("T", ["l1", "l2", "n"]), new FrameNode("L", ["l1", "l2", "n"]) };
        var model = new PhaseFrameModel("ct", nodes, [new FrameSource("P", ["a"], [C(11000.0, 0)])],
            [new FrameBranch("svc", "T", "L", ["l1", "l2", "n"], new Complex[,] { { zl, 0, 0 }, { 0, zl, 0 }, { 0, 0, zn } })],
            [FrameLoad.LineLine("h1", "L", "l1", "n", SFor(r1, 120.0), 120.0, Zip.ConstantImpedance),
             FrameLoad.LineLine("h2", "L", "l2", "n", SFor(r2, 120.0), 120.0, Zip.ConstantImpedance),
             FrameLoad.LineLine("h12", "L", "l1", "l2", SFor(r3, 240.0), 240.0, Zip.ConstantImpedance)],
            [new EarthingElement("P", "n", Complex.Zero), new EarthingElement("T", "n", Complex.Zero)],
            null, [SplitPhase.CentreTapUnit("tx", "P", "a", "n", vp0, "T", vs, zp, zh)]);
        MeshedResult r = Newton3Ph.Solve(model);
        Assert.Equal(MeshedStatus.Converged, r.Status);

        // By hand. Windings v = t E + z i with sum(t i) = 0; with J1, J2 the currents leaving T on l1 and l2: i1 = -J1, i2 = +J2 (into the
        // positive terminals), ip = (t2/vp0)(J1 - J2), E = (Vp - zp ip)/vp0. With kappa = zp t2^2/vp0^2 and E0 = t2 Vp/vp0:
        //   V_l1T = E0 - kappa (J1 - J2) - zh J1,   V_l2T = -E0 + kappa (J1 - J2) - zh J2.
        // Far end: V_l1L = V_l1T - zl J1, V_l2L = V_l2T - zl J2, V_NL = zn (J1 + J2) (the neutral wire returns J1 + J2 to the earthed ct).
        // Unknowns [V1L, V2L, J1, J2]:
        Complex kappa = zp * t2 * t2 / (vp0 * vp0), e0 = t2 * 11000.0 / vp0;
        var a = new Complex[4, 4];
        var b = new Complex[4];
        a[0, 0] = 1; a[0, 2] = zl + kappa + zh; a[0, 3] = -kappa; b[0] = e0;
        a[1, 1] = 1; a[1, 3] = zl + kappa + zh; a[1, 2] = -kappa; b[1] = -e0;
        a[2, 0] = 1 / r1 + 1 / r3; a[2, 1] = -1 / r3; a[2, 2] = -1 - zn / r1; a[2, 3] = -zn / r1;
        a[3, 1] = 1 / r2 + 1 / r3; a[3, 0] = -1 / r3; a[3, 3] = -1 - zn / r2; a[3, 2] = -zn / r2;
        Complex[] x = Solve(a, b);
        Complex v1 = x[0], v2 = x[1], j1 = x[2], j2 = x[3];
        Near(v1, r.V("L", "l1"), 1e-7, "V(l1) at the load");
        Near(v2, r.V("L", "l2"), 1e-7, "V(l2) at the load");
        BranchCurrents svc = r.Branch("svc");
        Near(j1, svc.IFrom[0], 1e-8, "line l1 current");
        Near(j2, svc.IFrom[1], 1e-8, "line l2 current");
        // The neutral wire returns the sum of the two line currents (algebraic sum; the halves are in antiphase, so its magnitude is
        // about the difference of the line current magnitudes), and the ct earthing passes nothing to earth: the neutral current returns
        // through the windings.
        Near(-(j1 + j2), svc.IFrom[2], 1e-8, "neutral wire current = -(I_l1 + I_l2) toward the load");
        Assert.True(Abs(j1 + j2) > 1.0 && Abs(j1 + j2) < Abs(j1), "unbalanced halves: a real neutral current smaller than the larger line current");
        Near(Complex.Zero, r.Earthing.Single(q => q.Node == "T").Current, 1e-8, "no earth current at the ct earth with no second electrode");
        // Primary current and ampere-turn balance from the reported winding quantities (independent of the stamp).
        Near((t2 / vp0) * (j1 - j2), r.Sources.Single().Current, 1e-8, "primary current");
        UnitCurrents u = r.Units.Single();
        Near(Complex.Zero, vp0 * u.WindingCurrent[0] + t2 * u.WindingCurrent[1] + t2 * u.WindingCurrent[2], 1e-7, "ampere-turn balance");
        Complex eFlux = (u.WindingVoltage[0] - zp * u.WindingCurrent[0]) / vp0;
        Near(eFlux, (u.WindingVoltage[1] - zh * u.WindingCurrent[1]) / t2, 1e-9, "volts per turn, winding 2");
        Near(eFlux, (u.WindingVoltage[2] - zh * u.WindingCurrent[2]) / t2, 1e-9, "volts per turn, winding 3");
        Assert.True((r.V("L", "l1") * r.V("L", "l2")).Real < 0, "l1 and l2 are in antiphase about the neutral");
    }

    [Fact]
    public void A_delta_grounded_wye_bank_of_three_units_has_the_delta_line_to_line_ratio_and_the_root_three_line_current()
    {
        // Bank of three single-phase units: primary windings a-b, b-c, c-a (delta), secondary windings a-n, b-n, c-n (grounded wye).
        double tp = 12470.0, ts = 416.0;
        Complex zp = C(0.4, 1.6), zs = C(0.0004, 0.0016);
        var nodes = new[] { new FrameNode("P", ["a", "b", "c"]), new FrameNode("Q", ["a", "b", "c", "n"]) };
        string[] w = ["a", "b", "c"];
        var units = new List<FrameTransformerUnit>();
        for (int k = 0; k < 3; k++)
            units.Add(new FrameTransformerUnit("u" + w[k], [new FrameWinding("P", w[k], w[(k + 1) % 3], tp, zp), new FrameWinding("Q", w[k], "n", ts, zs)]));
        Complex[] e = Abc(7200.0);
        PhaseFrameModel Build(bool loaded) => new("dyn", nodes, [new FrameSource("P", w, e)], [],
            loaded ? [FrameLoad.Wye("ld", "Q", [C(20e3, 8e3), C(20e3, 8e3), C(20e3, 8e3)], 240.0, Zip.ConstantImpedance)] : null,
            [new EarthingElement("Q", "n", Complex.Zero)], null, units);

        // No load: every secondary phase voltage is the primary line-to-line voltage times ts/tp (a 30 degree shift), with no magnetising current.
        MeshedResult nl = Newton3Ph.Solve(Build(false));
        Assert.Equal(MeshedStatus.Converged, nl.Status);
        for (int k = 0; k < 3; k++) Near((e[k] - e[(k + 1) % 3]) * (ts / tp), nl.V("Q", w[k]), 1e-9, "no-load secondary voltage " + w[k]);
        Assert.True(Math.Abs(Math.Atan2(nl.V("Q", "a").Imaginary, nl.V("Q", "a").Real) * 180.0 / Math.PI - 30.0) < 1e-9, "the wye side leads the delta side by 30 degrees (V_ab leads V_a)");

        // Loaded and balanced: the primary line current into the delta is root three times the winding current.
        MeshedResult ld = Newton3Ph.Solve(Build(true));
        Assert.Equal(MeshedStatus.Converged, ld.Status);
        UnitCurrents ua = ld.Units[0], uc = ld.Units[2];
        Near(ua.WindingCurrent[0] - uc.WindingCurrent[0], ld.Sources.Single(q => q.Wire == "a").Current, 1e-8, "line current = I_ab - I_ca");
        double ratio = Abs(ld.Sources.Single(q => q.Wire == "a").Current) / Abs(ua.WindingCurrent[0]);
        Assert.True(Math.Abs(ratio - Math.Sqrt(3.0)) < 1e-6, $"line/winding current ratio {ratio}");
        foreach (UnitCurrents u in ld.Units)
            Near(Complex.Zero, tp * u.WindingCurrent[0] + ts * u.WindingCurrent[1], 1e-7, "ampere-turn balance " + u.Name);
        // The balanced load leaves no current in the neutral earth.
        Near(Complex.Zero, ld.Earthing.Single().Current, 1e-6, "neutral earth current of a balanced load");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // (6) SWER and earthing.
    // ---------------------------------------------------------------------------------------------------------------

    private static (Complex z, double c) SwerLine(double km)
    {
        var w = new ConductorData("W", ConductorRole.Phase, 0.0, 10.0, 1.0, 0.0015, 0.002);
        (Complex zKm, double cKm) = Swer.LineConstants(w, 50.0, 100.0);
        return (zKm * km, cKm * km);
    }

    [Fact]
    public void Swer_without_shunt_obeys_the_series_loop_equation_with_the_earth_return_impedance_from_the_line_constants()
    {
        (Complex zLine, _) = SwerLine(40.0);
        Assert.True(zLine.Real > 40.0 * 1.0, "the earth-return resistance adds to the conductor resistance");
        Complex emf = 12700.0, rs = 5.0, rl = 10.0, zLoad = C(600, 200);
        PhaseFrameModel model = Swer.Feeder(emf, rs, zLine, Complex.Zero, rl, SFor(zLoad, 12700.0), 12700.0);
        MeshedResult r = Newton3Ph.Solve(model);
        Assert.Equal(MeshedStatus.Converged, r.Status);

        // Loop equation:  E = I (Z_line + R_source_earth + R_load_earth + Z_load).
        Complex i = emf / (zLine + rs + rl + zLoad);
        Near(i, r.Branch("line").IFrom[0], 1e-9, "line current");
        Near(i, r.Branch("line").Residual, 1e-9, "the earth return carries the whole line current");
        Near((zLoad + rl) * i, r.V("L", "a"), 1e-7, "load-end conductor voltage");
        Near(rl * i, r.V("L", "e"), 1e-8, "load electrode potential rise");
        Near(i, r.Earthing.Single().Current, 1e-9, "current into the load electrode");
        Near(emf - rs * i, r.V("S", "a"), 1e-7, "voltage at the source end of the line");
    }

    [Fact]
    public void Swer_with_line_charging_matches_the_closed_form_pi_section()
    {
        (Complex zLine, double cLine) = SwerLine(40.0);
        double w = 2.0 * Math.PI * 50.0;
        Complex ysh = C(0, w * cLine);
        Complex emf = 12700.0, rs = 5.0, rl = 10.0, zLoad = C(600, 200);
        MeshedResult r = Newton3Ph.Solve(Swer.Feeder(emf, rs, zLine, ysh, rl, SFor(zLoad, 12700.0), 12700.0));
        Assert.Equal(MeshedStatus.Converged, r.Status);

        // Chain from the load end: Y_L = 1/(Z_load + R_load_earth) + y/2; Z_in = Z_line + 1/Y_L; Y_in = 1/Z_in + y/2; Vs = E / (1 + R_s Y_in).
        Complex yl = 1 / (zLoad + rl) + ysh / 2;
        Complex zin = zLine + 1 / yl;
        Complex yin = 1 / zin + ysh / 2;
        Complex vs = emf / (1 + rs * yin);
        Complex vl = vs / (zin * yl);   // V_L = V_s (1/Y_L)/(Z_in)
        Near(vs, r.V("S", "a"), 1e-6, "source-end voltage");
        // The load-end conductor voltage is V_L, and the load current is V_L/(Z_load + R_e).
        Near(vl, r.V("L", "a"), 1e-6, "load-end voltage");
        Near(vl / (zLoad + rl), r.Load("load").Current[0], 1e-8, "load current");
        // The From-end current includes half the charging: I_from = (Vs - VL)/Z_line + y/2 Vs.
        Near((vs - vl) / zLine + ysh / 2 * vs, r.Branch("line").IFrom[0], 1e-8, "From-end line current");
    }

    [Fact]
    public void Earth_grid_gives_the_ground_potential_rise_and_shared_electrodes_act_in_parallel()
    {
        // Source E behind a line to a load whose return goes through two electrodes (4 and 6 ohm, in parallel = 2.4) to a grid, and the
        // grid reaches remote earth through 2 ohm. The electrodes are on wire e of node L; the source end is solidly earthed (wire e is
        // not used there: the source is referenced to remote earth directly).
        Complex zLine = C(1.0, 2.0), zLoad = C(30.0, 10.0);
        var nodes = new[] { new FrameNode("S", ["a"]), new FrameNode("L", ["a", "e"]) };
        var model = new PhaseFrameModel("grid", nodes, [new FrameSource("S", ["a"], [C(1000, 0)])],
            [new FrameBranch("line", "S", "L", ["a"], new[,] { { zLine } })],
            [FrameLoad.LineLine("load", "L", "a", "e", SFor(zLoad, 1000.0), 1000.0, Zip.ConstantImpedance)],
            [new EarthingElement("L", "e", 4.0, "G"), new EarthingElement("L", "e", 6.0, "G")], [new EarthGrid("G", 2.0)]);
        MeshedResult r = Newton3Ph.Solve(model);
        Assert.Equal(MeshedStatus.Converged, r.Status);
        double rElectrodes = 4.0 * 6.0 / (4.0 + 6.0);
        Complex i = 1000.0 / (zLine + zLoad + rElectrodes + 2.0);
        Near(i, r.Grids.Single().Current, 1e-9, "current into remote earth");
        Near(2.0 * i, r.Grids.Single().Voltage, 1e-8, "ground potential rise of the grid");
        Near((rElectrodes + 2.0) * i, r.V("L", "e"), 1e-8, "electrode wire potential");
        // Division of the current between the two electrodes: inversely to their resistances.
        Near(i * 6.0 / 10.0, r.Earthing[0].Current, 1e-8, "4 ohm electrode share");
        Near(i * 4.0 / 10.0, r.Earthing[1].Current, 1e-8, "6 ohm electrode share");
        Assert.Equal(100.0, EarthingFormulas.HemisphereResistance(100.0, 0.5 / Math.PI), 9);   // rho / (2 pi r) with r = 0.5/pi is rho
    }

    // ---------------------------------------------------------------------------------------------------------------
    // (7) Floating star, references, INCOMPLETE, gauge pin.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Floating_wye_star_point_shifts_to_the_admittance_weighted_mean_of_the_phase_voltages()
    {
        Complex[] zl = [C(10, 2), C(20, 5), C(5, 1)];
        double vln = 7200.0;
        var model = new PhaseFrameModel("star", [new FrameNode("S", ["a", "b", "c"])], [new FrameSource("S", ["a", "b", "c"], Abc(vln))], [],
            [FrameLoad.WyeFloating("load", "S", zl.Select(q => SFor(q, vln)).ToArray(), vln, Zip.ConstantImpedance)]);
        MeshedResult r = Newton3Ph.Solve(model);
        Assert.Equal(MeshedStatus.Converged, r.Status);
        Complex[] e = Abc(vln);
        Complex vn = (e[0] / zl[0] + e[1] / zl[1] + e[2] / zl[2]) / (1 / zl[0] + 1 / zl[1] + 1 / zl[2]);
        for (int p = 0; p < 3; p++) Near(e[p] - vn, r.Load("load").Voltage[p], 1e-7, "leg voltage " + "abc"[p]);
        // KCL at the star: the three leg currents sum to zero.
        Near(Complex.Zero, r.Load("load").Current[0] + r.Load("load").Current[1] + r.Load("load").Current[2], 1e-8, "sum of leg currents");
        Assert.True(Abs(vn) > 100.0, "the unbalance moves the star point");
    }

    [Fact]
    public void Missing_neutral_reference_and_missing_delta_return_path_are_incomplete_and_named()
    {
        double vln = 7200.0;
        Complex[] s = [C(1e4, 0), C(1e4, 0), C(1e4, 0)];
        // (a) a wye load needs the neutral wire, the node has none.
        var m1 = new PhaseFrameModel("m1", [new FrameNode("S", ["a", "b", "c"])], [new FrameSource("S", ["a", "b", "c"], Abc(vln))], [], [FrameLoad.Wye("w", "S", s, vln)]);
        MeshedResult r1 = Newton3Ph.Solve(m1);
        Assert.Equal(MeshedStatus.Incomplete, r1.Status);
        Assert.Contains(r1.IncompleteReasons, x => x.Contains("wye load 'w'", StringComparison.Ordinal) && x.Contains("neutral has no reference", StringComparison.Ordinal) && x.Contains("'S'", StringComparison.Ordinal));
        // (b) a delta load where the node lacks wire b.
        var m2 = new PhaseFrameModel("m2", [new FrameNode("S", ["a", "c"])], [new FrameSource("S", ["a", "c"], [Abc(vln)[0], Abc(vln)[2]])], [], [FrameLoad.Delta("d", "S", s, vln * Math.Sqrt(3.0))]);
        MeshedResult r2 = Newton3Ph.Solve(m2);
        Assert.Equal(MeshedStatus.Incomplete, r2.Status);
        Assert.Contains(r2.IncompleteReasons, x => x.Contains("delta load 'd'", StringComparison.Ordinal) && x.Contains("no return path", StringComparison.Ordinal) && x.Contains("'b'", StringComparison.Ordinal));
        // (c) a four-wire line whose neutral is earthed nowhere and has no load tying it to anything.
        var nodes = new[] { new FrameNode("S", ["a", "b", "c", "n"]), new FrameNode("L", ["a", "b", "c", "n"]) };
        var m3 = new PhaseFrameModel("m3", nodes, [new FrameSource("S", ["a", "b", "c"], Abc(vln))], [new FrameBranch("line", "S", "L", ["a", "b", "c", "n"], Z4())],
            [FrameLoad.WyeEarthed("e", "L", s, vln)]);
        MeshedResult r3 = Newton3Ph.Solve(m3);
        Assert.Equal(MeshedStatus.Incomplete, r3.Status);
        Assert.Contains(r3.IncompleteReasons, x => x.StartsWith("no reference:", StringComparison.Ordinal) && x.Contains("S.n", StringComparison.Ordinal) && x.Contains("L.n", StringComparison.Ordinal));
        Assert.Empty(r3.Voltages);   // no number is reported for an incomplete case
        // (d) a zero-impedance branch is named, not solved.
        var m4 = new PhaseFrameModel("m4", [new FrameNode("S", ["a"]), new FrameNode("L", ["a"])], [new FrameSource("S", ["a"], [C(100, 0)])],
            [new FrameBranch("sw", "S", "L", ["a"], new Complex[,] { { Complex.Zero } })], [FrameLoad.WyeEarthed("x", "L", [C(1, 0)], 100.0, null, ["a"])]);
        MeshedResult r4 = Newton3Ph.Solve(m4);
        Assert.Equal(MeshedStatus.Incomplete, r4.Status);
        Assert.Contains(r4.IncompleteReasons, x => x.Contains("branch 'sw'", StringComparison.Ordinal) && x.Contains("singular", StringComparison.Ordinal));
    }

    [Fact]
    public void An_isolated_secondary_is_incomplete_until_a_reference_is_pinned_and_then_matches_the_closed_form()
    {
        // Single-phase isolation transformer 1000:100 turns, leakage zp, zs, load R on an ungrounded secondary.
        Complex zp = C(2.0, 6.0), zs = C(0.02, 0.06), rl = C(5.0, 0.0);
        var nodes = new[] { new FrameNode("P", ["a", "n"]), new FrameNode("Q", ["x", "y"]) };
        PhaseFrameModel Model() => new("iso", nodes, [new FrameSource("P", ["a"], [C(1000, 0)])], [],
            [FrameLoad.LineLine("r", "Q", "x", "y", SFor(rl, 100.0), 100.0, Zip.ConstantImpedance)],
            [new EarthingElement("P", "n", Complex.Zero)], null,
            [new FrameTransformerUnit("iso", [new FrameWinding("P", "a", "n", 1000, zp), new FrameWinding("Q", "x", "y", 100, zs)])]);
        MeshedResult refused = Newton3Ph.Solve(Model());
        Assert.Equal(MeshedStatus.Incomplete, refused.Status);
        Assert.Contains(refused.IncompleteReasons, x => x.StartsWith("no reference:", StringComparison.Ordinal) && x.Contains("Q.x", StringComparison.Ordinal) && x.Contains("Q.y", StringComparison.Ordinal));

        MeshedResult r = Newton3Ph.Solve(Model(), new MeshedOptions { Floating = FloatingPolicy.PinFirstWire });
        Assert.Equal(MeshedStatus.Converged, r.Status);
        Assert.Contains(r.Notes, n => n.StartsWith("reference pinned:", StringComparison.Ordinal));
        // Closed form: Is (out of the secondary) = t_s E/(R + zs); ampere-turns tp ip = ts Is; Vp = tp E + zp ip.
        double tp = 1000, ts = 100;
        Complex e = 1000.0 / (tp + zp * ts * ts / (tp * (rl + zs)));
        Complex isec = ts * e / (rl + zs);
        Near(isec, r.Load("r").Current[0], 1e-9, "secondary current");
        Near(isec * rl, r.V("Q", "x") - r.V("Q", "y"), 1e-8, "load voltage");
        Near(ts / tp * isec, r.Sources.Single().Current, 1e-9, "primary current");
    }

    [Fact]
    public void A_collapsing_constant_power_load_never_reports_converged()
    {
        Complex zl = C(1.0, 3.0);
        var model = new PhaseFrameModel("collapse", [new FrameNode("S", ["a"]), new FrameNode("L", ["a", "n"])], [new FrameSource("S", ["a"], [C(1000, 0)])],
            [new FrameBranch("line", "S", "L", ["a"], new[,] { { zl } })], [FrameLoad.LineLine("p", "L", "a", "n", C(5e6, 1e6), 1000.0)],
            [new EarthingElement("L", "n", Complex.Zero)]);
        MeshedResult r = Newton3Ph.Solve(model);
        Assert.NotEqual(MeshedStatus.Converged, r.Status);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // (8) ZIP derivative, time series, determinism.
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Zip_leg_current_derivative_matches_a_finite_difference()
    {
        Complex s = C(3000, 1200);
        var zip = new Zip(0.5, 0.3, 0.2);
        Complex u = C(230, -35);
        Newton3Ph.LegCurrent(s, 240.0, zip, u, out Complex a, out Complex b);
        double h = 1e-5;
        Complex Cur(Complex x) => Newton3Ph.LegCurrent(s, 240.0, zip, x, out _, out _);
        Complex dRe = (Cur(u + h) - Cur(u - h)) / (2 * h);
        Complex dIm = (Cur(u + C(0, h)) - Cur(u - C(0, h))) / (2 * h);
        // dI = A du + B conj(du): du = 1 gives A + B; du = j gives j A - j B.
        Near(a + b, dRe, 1e-6, "d/dRe");
        Near(C(0, 1) * (a - b), dIm, 1e-6, "d/dIm");
    }

    [Fact]
    public void Time_series_steps_are_independent_by_default_and_warm_start_agrees_within_tolerance()
    {
        Complex[] s = [C(60e3, 25e3), C(40e3, 12e3), C(90e3, 30e3)];
        double vln = 7200.0;
        var nodes = new[] { new FrameNode("S", ["a", "b", "c", "n"]), new FrameNode("L", ["a", "b", "c", "n"]) };
        var model = new PhaseFrameModel("ts", nodes, [new FrameSource("S", ["a", "b", "c"], Abc(vln))],
            [new FrameBranch("line", "S", "L", ["a", "b", "c", "n"], Z4())], [FrameLoad.Wye("load", "L", s, vln, new Zip(0.6, 0.2, 0.2))],
            [new EarthingElement("S", "n", Complex.Zero)]);
        MeshedStep[] steps = [new("t0", 0.4), new("t1", 1.0), new("t2", 1.7, new Dictionary<string, double> { ["load"] = 0.5 }), new("t3", 0.9)];
        MeshedSeriesResult indep = UnbalancedTimeSeries.Run(model, steps);
        for (int k = 0; k < steps.Length; k++)
        {
            Assert.Equal(MeshedStatus.Converged, indep.Results[k].Status);
            // Reproducible alone: a single solve with that step's factors is bit-identical.
            MeshedResult alone = Newton3Ph.Solve(model, new MeshedOptions { GlobalLoadScale = steps[k].GlobalScale, LoadScale = steps[k].LoadScale });
            Assert.Equal(alone.State.Select(c => BitConverter.DoubleToInt64Bits(c.Real)), indep.Results[k].State.Select(c => BitConverter.DoubleToInt64Bits(c.Real)));
            Assert.Equal(alone.State.Select(c => BitConverter.DoubleToInt64Bits(c.Imaginary)), indep.Results[k].State.Select(c => BitConverter.DoubleToInt64Bits(c.Imaginary)));
        }
        MeshedSeriesResult warm = UnbalancedTimeSeries.Run(model, steps, warmStart: true);
        for (int k = 0; k < steps.Length; k++)
        {
            Assert.Equal(MeshedStatus.Converged, warm.Results[k].Status);
            for (int v = 0; v < warm.Results[k].State.Count; v++) Near(indep.Results[k].State[v], warm.Results[k].State[v], 1e-5, $"step {k} unknown {v}");
        }
        Assert.Contains(warm.Results[1].Notes, n => n.StartsWith("start: previous state", StringComparison.Ordinal));
        Assert.Contains(indep.Results[1].Notes, n => n.StartsWith("start: no-load", StringComparison.Ordinal));
        // Load factors scale the load: the step with factor 0.4 draws less than the step with 1.0.
        Assert.True(Abs(indep.Results[0].Load("load").Current[0]) < Abs(indep.Results[1].Load("load").Current[0]));
    }

    [Fact]
    public void Repeat_runs_are_bit_identical()
    {
        MultiPhaseModel inc = IncumbentFeeder();
        PhaseFrameModel m = MultiPhaseFrameAdapter.FromMultiPhase(inc, 7200.0, 10e6 / 3.0).Model!;
        MeshedResult a = Newton3Ph.Solve(m), b = Newton3Ph.Solve(m);
        Assert.Equal(a.Iterations, b.Iterations);
        Assert.Equal(a.State.Select(c => BitConverter.DoubleToInt64Bits(c.Real)), b.State.Select(c => BitConverter.DoubleToInt64Bits(c.Real)));
        Assert.Equal(a.State.Select(c => BitConverter.DoubleToInt64Bits(c.Imaginary)), b.State.Select(c => BitConverter.DoubleToInt64Bits(c.Imaginary)));
        Assert.Equal(BitConverter.DoubleToInt64Bits(a.MismatchAmps), BitConverter.DoubleToInt64Bits(b.MismatchAmps));
    }

    [Fact]
    public void Structural_errors_are_refused_at_construction()
    {
        var n = new[] { new FrameNode("S", ["a"]), new FrameNode("L", ["a"]) };
        var src = new FrameSource("S", ["a"], [C(1, 0)]);
        Assert.Throws<ArgumentException>(() => new PhaseFrameModel("x", n, [], []));
        Assert.Throws<ArgumentException>(() => new PhaseFrameModel("x", n, [src], [new FrameBranch("b", "S", "Z", ["a"], new Complex[,] { { C(1, 1) } })]));
        Assert.Throws<ArgumentException>(() => new PhaseFrameModel("x", n, [src], [new FrameBranch("b", "S", "L", ["a", "b"], new Complex[,] { { C(1, 1) } })]));
        Assert.Throws<ArgumentException>(() => new PhaseFrameModel("x", n, [src], [new FrameBranch("b", "S", "L", ["a"], new Complex[,] { { C(1, 1) } }, null, "a")]));
        Assert.Throws<ArgumentException>(() => new PhaseFrameModel("x", n, [src], [], [FrameLoad.LineLine("l", "L", "a", "a", C(1, 0), 1.0, new Zip(0.5, 0.2, 0.2))]));
        Assert.Throws<ArgumentException>(() => new PhaseFrameModel("x", n, [src], [], null, [new EarthingElement("L", "a", 0, "nope")]));
    }
}
