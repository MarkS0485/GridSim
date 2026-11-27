using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GridSim.Core.Abstractions;
using GridSim.Core.Analysis.VoltageStability;
using GridSim.Core.Model;
using GridSim.Core.Numerics;
using GridSim.Core.PowerFlow;
using Xunit;

namespace GridSim.Tests;

/// <summary>
/// RC5 WP25: the opt-in fast-decoupled (XB, BX) and Gauss-Seidel engines, and QV modal analysis.
/// <para>Sources: the two-bus closed form (re-derived to 40 digits with mpmath: V2 = 0.978248230618626176..., theta2 =
/// -2.929765360158135021... degrees, J_R = (2V - sec theta)/X = 9.551876897982780768...); MATPOWER makeB.m (B' and B''
/// rules); pandapower 3.5.6's PYPOWER ports of fdpf / gausspf / makeB run by tools/crossval/pf/fdlf_gs.py on the bundled
/// case14 and case30 (no reactive limits, tolerance 1e-12); SciPy 1.18.1 scipy.linalg.eig on J_R formed by the same
/// script at a NumPy Newton solution of case14 solved to a mismatch of 6.5e-15.</para>
/// <para>Tolerances. A solve stopped at a mismatch below tol is within ||J^-1||_inf tol of the exact solution (first
/// order). ||J^-1||_inf at the solution, from the same NumPy script, is 2.24 on case14, 6.25 on case30 and 7.10 on case118
/// (no reactive limits), and 0.149 on the two-bus case. Two solves at tol 1e-10 therefore differ by at most
/// 2 ||J^-1||_inf x 1e-10: 4.48e-10 on case14, 1.25e-9 on case30 and 1.42e-9 on case118 in |V| (pu) and angle (rad).
/// T2 and T3 assert exactly those bounds per case (<see cref="Bound"/>), angles compared in radians; T3's reference is
/// itself within ||J^-1||_inf x 1.1e-12 of the solution, inside the same bound. For the two-bus case at 1e-10:
/// 1.5e-11 pu / rad, 8.5e-10 degrees, inside the spec's 1e-8.</para>
/// </summary>
public sealed class Rc5Wp25FdlfGsQvModalTests
{
    private const double V2 = 0.9782482306186262;
    private const double Theta2Deg = -2.929765360158135;
    private const double JrTwoBus = 9.55187689798278;

    private static GridModel TwoBus(double k = 1.0) => new()
    {
        Name = "two-bus", BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack, Vm: 1.0), new Bus(2, BusType.Pq, Pd: 50 * k, Qd: 20 * k) },
        Generators = new[] { new Generator(1, 0, Vg: 1.0) },
        Branches = new[] { new Branch(1, 2, 0, 0.1) },
    };

    private static GridModel Case(string name) => S10TestSupport.LoadMatpower("lib/" + name + ".m").Model;

    /// <summary>2 ||J^-1||_inf x 1e-10 (see the class summary): two solves each stopped at a mismatch below 1e-10 pu.</summary>
    private static double Bound(string caseName) => 2 * 1e-10 * caseName switch
    {
        "case14" => 2.24,
        "case30" => 6.25,
        "case118" => 7.10,
        _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
    };

    private static IPowerFlowSolver Engine(string name) => name switch
    {
        "fdxb" => new FastDecoupledPowerFlow(FdlfVariant.XB),
        "fdbx" => new FastDecoupledPowerFlow(FdlfVariant.BX),
        "gs" => new GaussSeidelPowerFlow(),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    private static PowerFlowOptions Opt(string engine, double tol, bool qLimits = false)
        => new() { Tolerance = tol, EnforceQLimits = qLimits, MaxIterations = engine == "gs" ? 20000 : 200 };

    // ---- 1. two-bus closed form, every engine ------------------------------------------------------------------------

    [Theory]
    [InlineData("fdxb")]
    [InlineData("fdbx")]
    [InlineData("gs")]
    public void T1_every_engine_reaches_the_two_bus_closed_form(string engine)
    {
        PowerFlowResult r = Engine(engine).Solve(TwoBus(), Opt(engine, 1e-10));
        Assert.True(r.Converged, $"{engine}: {r.Status} after {r.Iterations}");
        Assert.True(r.MaxMismatch < 1e-10);
        Assert.Equal(1.0, r.Vm[0]);
        Assert.Equal(0.0, r.VaDeg[0]);
        Assert.True(Math.Abs(r.Vm[1] - V2) <= 1e-8, $"{engine}: V2 {r.Vm[1]:R}");
        Assert.True(Math.Abs(r.VaDeg[1] - Theta2Deg) <= 1e-8, $"{engine}: theta2 {r.VaDeg[1]:R}");
    }

    // ---- 2. same solution as Newton ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("case14", "fdxb")]
    [InlineData("case14", "fdbx")]
    [InlineData("case14", "gs")]
    [InlineData("case30", "fdxb")]
    [InlineData("case30", "fdbx")]
    [InlineData("case30", "gs")]
    [InlineData("case118", "fdxb")]
    [InlineData("case118", "fdbx")]
    [InlineData("case118", "gs")]
    public void T2_every_engine_gives_newtons_solution(string caseName, string engine)
    {
        GridModel m = Case(caseName);
        PowerFlowResult n = SolverSelector.CreateNewton(m).Solve(m, Opt("newton", 1e-10));
        PowerFlowResult r = Engine(engine).Solve(m, Opt(engine, 1e-10));
        Assert.True(n.Converged);
        Assert.True(r.Converged, $"{caseName} {engine}: {r.Status} after {r.Iterations}");
        for (int i = 0; i < m.BusCount; i++)
        {
            Assert.True(Math.Abs(r.Vm[i] - n.Vm[i]) <= Bound(caseName), $"{caseName} {engine} bus {r.BusIds[i]}: Vm {r.Vm[i]:R} vs {n.Vm[i]:R}");
            Assert.True(Math.Abs(r.VaDeg[i] - n.VaDeg[i]) * Math.PI / 180 <= Bound(caseName), $"{caseName} {engine} bus {r.BusIds[i]}: Va {r.VaDeg[i]:R} vs {n.VaDeg[i]:R}");
        }
        // Converged engines report their own iteration count; the Newton bookkeeping fields stay unset.
        Assert.Equal(NewtonRetryOutcome.NotTried, r.NewtonRetry);
        Assert.Equal(m.BusCount, r.SolvedBusTypes!.Count);
    }

    // ---- 3. pandapower cross-check -----------------------------------------------------------------------------------

    // pandapower 3.5.6 (tools/crossval/pf/fdlf_gs.py), fdxb solution at tolerance 1e-12; its fdbx and gs solutions are
    // within 4.9e-14 and 6.0e-13 of it on case14, 1.2e-13 and 1.0e-12 on case30. pandapower's iteration counts, for
    // information only (fdpf divides the mismatch by |V| before testing it, and gausspf's test is GridSim's): case14 fdxb
    // 10, fdbx 13, gs 232; case30 fdxb 17, fdbx 11, gs 1066; case118 fdxb 12, fdbx 11, gs 3572.
    private static readonly double[] Case14Vm = { 1.06, 1.0449999999999997, 1.01, 1.0176708536917531, 1.01951385981905, 1.0699999999999998, 1.0615195324909383, 1.0900000000000003, 1.055931720636976, 1.050984624999852, 1.056906518540367, 1.0551885631970996, 1.0503817136285967, 1.03552994585357 };
    private static readonly double[] Case14VaDeg = { 0.0, -4.982589141973926, -12.725099938265181, -10.312901092330502, -8.773853898294456, -14.220946463701438, -13.359627365345334, -13.359627365345325, -14.938521295228126, -15.09728846307016, -14.790622031320822, -15.075584520423677, -15.156276336221307, -16.03364452920471 };
    private static readonly double[] Case30Vm = { 1.0, 0.9999999999999999, 0.9831382891026118, 0.9800929953702965, 0.9824061967885452, 0.9731840211507012, 0.9673554477041294, 0.9606237082935918, 0.9805061170582429, 0.9844042957926571, 0.9805061170582429, 0.9854683170130899, 1.0, 0.9766768340627144, 0.9802290289138849, 0.9773956551705048, 0.9768654098897369, 0.968440329094488, 0.9652870396344538, 0.9691663507744921, 0.9933832967633921, 1.0, 1.0000000000000002, 0.9885662961943511, 0.990214836901088, 0.9721941498012204, 0.9999999999999999, 0.9747148997416054, 0.979596704666336, 0.9678828791877787 };
    private static readonly double[] Case30VaDeg = { 0.0, -0.4154907190126572, -1.5220739374599639, -1.7947276548033484, -1.8638226717442348, -2.266956706861925, -2.6518367677769246, -2.725769438626546, -2.9969330988345293, -3.3749359503310923, -2.9969330988345293, -1.5369115844038452, 1.4761632514903997, -2.308035432107573, -2.3118353952401223, -2.6444862121887667, -3.39233922082908, -3.4783877236297966, -3.958204696190899, -3.8710243321493327, -3.488393326843631, -3.39272902005959, -1.5892279268686913, -2.631461475009879, -1.6899888937491694, -2.1393459890418303, -0.8284393251275672, -2.265928628096087, -2.1284981814720463, -3.0415235838797563 };

    [Theory]
    [InlineData("case14", "fdxb")]
    [InlineData("case14", "fdbx")]
    [InlineData("case14", "gs")]
    [InlineData("case30", "fdxb")]
    [InlineData("case30", "fdbx")]
    [InlineData("case30", "gs")]
    public void T3_every_engine_gives_pandapowers_solution(string caseName, string engine)
    {
        GridModel m = Case(caseName);
        (double[] vm, double[] va) = caseName == "case14" ? (Case14Vm, Case14VaDeg) : (Case30Vm, Case30VaDeg);
        Assert.Equal(vm.Length, m.BusCount);
        PowerFlowResult r = Engine(engine).Solve(m, Opt(engine, 1e-10));
        Assert.True(r.Converged, $"{caseName} {engine}: {r.Status}");
        // Reference within 2.24 (case14) or 6.25 (case30) x 1.1e-12 of the solution; GridSim within ||J^-1|| x 1e-10.
        for (int i = 0; i < vm.Length; i++)
        {
            Assert.True(Math.Abs(r.Vm[i] - vm[i]) <= Bound(caseName), $"{caseName} {engine} bus {r.BusIds[i]}: Vm {r.Vm[i]:R} vs pandapower {vm[i]:R}");
            Assert.True(Math.Abs(r.VaDeg[i] - va[i]) * Math.PI / 180 <= Bound(caseName), $"{caseName} {engine} bus {r.BusIds[i]}: Va {r.VaDeg[i]:R} vs pandapower {va[i]:R}");
        }
    }

    // ---- 4. B' and B'' structure -------------------------------------------------------------------------------------

    private static GridModel OneBranch(double shiftDeg = 0) => new()
    {
        Name = "b", BaseMva = 100,
        Buses = new[] { new Bus(1, BusType.Slack), new Bus(2, BusType.Pq, Pd: 10) },
        Generators = new[] { new Generator(1, 10, Vg: 1.0) },
        Branches = new[] { new Branch(1, 2, 0.01, 0.1, B: 0.02, TapRatio: 1.05, PhaseShiftDeg: shiftDeg) },
    };

    private static double[,] Dense(CsrMatrix m)
    {
        var d = new double[m.N, m.N];
        for (int r = 0; r < m.N; r++)
            for (int c = 0; c < m.N; c++) d[r, c] = FdlfMatrices.Entry(m, r, c);
        return d;
    }

    [Fact]
    public void T4_b_prime_and_b_double_prime_follow_makeB()
    {
        // MATPOWER makeB: B = -Im(Ybus) of modified data; Ybus off-diagonal = -y / conj(t), so B'_12 = Im(y) when t = 1.
        Complex yR = Complex.One / new Complex(0.01, 0.1);   // r kept
        Complex yX = Complex.One / new Complex(0.0, 0.1);    // r zeroed
        double a = 1.05, half = 0.02 / 2.0;

        FdlfMatrixPair xb = FdlfMatrices.Build(OneBranch(), FdlfVariant.XB);
        FdlfMatrixPair bx = FdlfMatrices.Build(OneBranch(), FdlfVariant.BX);
        double[,] xbP = Dense(xb.BPrime), xbPP = Dense(xb.BDoublePrime), bxP = Dense(bx.BPrime), bxPP = Dense(bx.BDoublePrime);

        // B' (no charging, tap 1): XB from 1/x, BX from 1/(r + jx).
        Assert.Equal(-10.0, xbP[0, 1]);
        Assert.Equal(-10.0, xbP[1, 0]);
        Assert.Equal(10.0, xbP[0, 0]);
        Assert.Equal(10.0, xbP[1, 1]);
        Assert.Equal(yR.Imaginary, bxP[0, 1]);
        Assert.True(Math.Abs(bxP[0, 1] - (-9.900990099009901)) <= 2e-15);   // -0.1 / 0.0101, one ulp of 9.9 is 1.8e-15
        Assert.Equal(-yR.Imaginary, bxP[0, 0]);
        Assert.Equal(-yR.Imaginary, bxP[1, 1]);

        // B'' (charging and tap kept, shift zeroed): XB keeps r, BX zeroes it. Yff = (y + jb/2) / a^2, Ytt = y + jb/2,
        // Yft = Ytf = -y / a (real a).
        foreach ((double[,] pp, Complex y) in new[] { (xbPP, yR), (bxPP, yX) })
        {
            Assert.Equal(-((y + new Complex(0, half)) / (a * a)).Imaginary, pp[0, 0]);
            Assert.Equal(-(y + new Complex(0, half)).Imaginary, pp[1, 1]);
            Assert.Equal(-(-y / new Complex(a, 0)).Imaginary, pp[0, 1]);
            Assert.Equal(-(-y / new Complex(a, 0)).Imaginary, pp[1, 0]);
        }
    }

    [Fact]
    public void T4b_b_prime_keeps_the_phase_shift_as_makeB_does_and_b_double_prime_drops_it()
    {
        // makeB.m zeroes SHIFT for B'' only. With t = e^(j phi) in B': Yft = -y / conj(t), Ytf = -y / t.
        Complex yX = Complex.One / new Complex(0.0, 0.1);
        Complex t = DetMath.FromPolar(1.0, 10.0 * Math.PI / 180.0);
        FdlfMatrixPair xb = FdlfMatrices.Build(OneBranch(10.0), FdlfVariant.XB);
        Assert.Equal(-(-yX / Complex.Conjugate(t)).Imaginary, FdlfMatrices.Entry(xb.BPrime, 0, 1));
        Assert.Equal(-(-yX / t).Imaginary, FdlfMatrices.Entry(xb.BPrime, 1, 0));
        Assert.NotEqual(-10.0, FdlfMatrices.Entry(xb.BPrime, 0, 1));
        // B'' is the unshifted one.
        Assert.Equal(FdlfMatrices.Entry(FdlfMatrices.Build(OneBranch(), FdlfVariant.XB).BDoublePrime, 0, 1), FdlfMatrices.Entry(xb.BDoublePrime, 0, 1));
    }

    [Fact]
    public void T4c_a_bus_shunt_enters_b_double_prime_only()
    {
        GridModel m = OneBranch();
        GridModel s = m.CopyWith(buses: new[] { m.Buses[0], m.Buses[1] with { Bs = 19 } });
        FdlfMatrixPair a = FdlfMatrices.Build(m, FdlfVariant.XB), b = FdlfMatrices.Build(s, FdlfVariant.XB);
        Assert.Equal(FdlfMatrices.Entry(a.BPrime, 1, 1), FdlfMatrices.Entry(b.BPrime, 1, 1));
        // The shunt adds -Bs / base (Y gains +j 0.19 pu) to B''_22, summed after the branch as YBus sums it.
        Assert.Equal(FdlfMatrices.Entry(a.BDoublePrime, 1, 1) + -(19.0 / 100.0), FdlfMatrices.Entry(b.BDoublePrime, 1, 1));
    }

    // ---- 5. reactive limits ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("fdxb")]
    [InlineData("fdbx")]
    [InlineData("gs")]
    public void T5_reactive_limits_switch_the_same_buses_as_newton(string engine)
    {
        GridModel m = Case("case14");
        // Bus 3's unit holds 23.4 MVAr in the case; Qmax 10 forces it to its limit. Bus 2 switches too (Newton gives its unit
        // 55.4 MVAr against the case's Qmax of 50), so two buses change type; the full type arrays are compared below.
        var gens = m.Generators.Select(g => g.Bus == 3 ? g with { Qmax = 10 } : g).ToArray();
        GridModel lim = m.CopyWith(generators: gens);
        PowerFlowResult n = SolverSelector.CreateNewton(lim).Solve(lim, Opt("newton", 1e-10, qLimits: true));
        PowerFlowResult r = Engine(engine).Solve(lim, Opt(engine, 1e-10, qLimits: true));
        Assert.True(n.Converged);
        Assert.True(r.Converged, $"{engine}: {r.Status}");
        int bus3 = m.Buses.Select(b => b.Id).ToList().IndexOf(3);
        Assert.Equal(BusType.Pq, n.SolvedBusTypes![bus3]);
        Assert.Equal(n.SolvedBusTypes, r.SolvedBusTypes);
        for (int i = 0; i < m.BusCount; i++)
            Assert.True(Math.Abs(r.Vm[i] - n.Vm[i]) <= 1e-7, $"{engine} bus {r.BusIds[i]}");
        // Bus 3 is held at its limit: its unit's reactive output (net injection plus load) is 10 MVAr to the solve.
        double q3 = r.QInjMvar[bus3] + m.Buses[bus3].Qd;
        Assert.True(Math.Abs(q3 - 10.0) <= 1e-6, $"{engine}: bus 3 unit Q {q3:R}");
    }

    // ---- refusals and options ----------------------------------------------------------------------------------------

    [Fact]
    public void The_engines_refuse_what_they_do_not_solve_and_check_their_options()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GaussSeidelPowerFlow(0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GaussSeidelPowerFlow(2.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GaussSeidelPowerFlow(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FastDecoupledPowerFlow((FdlfVariant)7));
        var traced = new PowerFlowOptions { Trace = new GridSim.Core.Diagnostics.SolveTrace() };
        Assert.Throws<InvalidOperationException>(() => new FastDecoupledPowerFlow(FdlfVariant.XB).Solve(TwoBus(), traced));
        Assert.Throws<InvalidOperationException>(() => new GaussSeidelPowerFlow().Solve(TwoBus(), traced));
    }

    [Fact]
    public void Gauss_seidel_with_acceleration_reaches_the_same_solution()
    {
        PowerFlowResult plain = new GaussSeidelPowerFlow().Solve(Case("case14"), Opt("gs", 1e-10));
        PowerFlowResult sor = new GaussSeidelPowerFlow(1.4).Solve(Case("case14"), Opt("gs", 1e-10));
        Assert.True(plain.Converged && sor.Converged);
        Assert.NotEqual(plain.Iterations, sor.Iterations);
        for (int i = 0; i < plain.Vm.Length; i++) Assert.True(Math.Abs(plain.Vm[i] - sor.Vm[i]) <= 1e-8);
    }

    [Fact]
    public void A_capped_run_is_reported_not_converged_with_its_iterations()
    {
        var opt = new PowerFlowOptions { Tolerance = 1e-10, EnforceQLimits = false, MaxIterations = 3 };
        PowerFlowResult r = new GaussSeidelPowerFlow().Solve(Case("case30"), opt);
        Assert.False(r.Converged);
        Assert.Equal(PowerFlowStatus.MaxIterations, r.Status);
        Assert.Equal(3, r.Iterations);
        Assert.True(r.MaxMismatch >= 1e-10);
        PowerFlowResult f = new FastDecoupledPowerFlow(FdlfVariant.XB).Solve(Case("case30"), opt with { MaxIterations = 1 });
        Assert.False(f.Converged);
        Assert.Equal(1, f.Iterations);
    }

    [Fact]
    public void The_engines_are_deterministic()
    {
        foreach (string e in new[] { "fdxb", "fdbx", "gs" })
        {
            PowerFlowResult a = Engine(e).Solve(Case("case30"), Opt(e, 1e-10));
            PowerFlowResult b = Engine(e).Solve(Case("case30"), Opt(e, 1e-10));
            Assert.Equal(a.Iterations, b.Iterations);
            Assert.Equal(a.Vm, b.Vm);
            Assert.Equal(a.VaDeg, b.VaDeg);
            Assert.Equal(a.QInjMvar, b.QInjMvar);
        }
    }

    // ---- 6 to 8. QV modal ------------------------------------------------------------------------------------------

    private static PowerFlowResult Newton(GridModel m) =>
        new NewtonRaphsonPowerFlow().Solve(m, new PowerFlowOptions { Tolerance = 1e-12, EnforceQLimits = false });

    [Fact]
    public void T6_qv_modal_two_bus_closed_form()
    {
        GridModel m = TwoBus();
        PowerFlowResult pf = Newton(m);
        QvModalResult r = QvModalAnalysis.Analyse(m, pf);
        Assert.Equal(new[] { 2 }, r.PqBusIds);
        Assert.Equal(1, r.TotalModes);
        QvMode mode = Assert.Single(r.Modes);
        Assert.Equal(0.0, mode.Eigenvalue.Imaginary);
        // State within ||J^-1|| 1e-12 = 1.5e-13 of the exact one; |d lambda / dx|_1 = 21.9 (2/X + |sin/cos^2|/X), so the
        // state moves lambda by at most 3.3e-12; the reduction's rounding is a few ulps of terms near 20 (1e-14).
        Assert.True(Math.Abs(mode.Eigenvalue.Real - JrTwoBus) <= 1e-10, $"lambda {mode.Eigenvalue.Real:R}");
        // The same reduction from the solver's own state, by the closed form (2V - sec theta)/X: agreement to rounding.
        double v = pf.Vm[1], th = pf.VaDeg[1] * Math.PI / 180;
        Assert.True(Math.Abs(mode.Eigenvalue.Real - (2 * v - 1 / DetMath.Cos(th)) / 0.1) <= 1e-12);
        // One bus: P = a / a for the complex product a, which is 1 to the rounding of one complex division (a few u).
        Assert.True(Complex.Abs(Assert.Single(mode.Participation) - Complex.One) <= 4 * Math.Pow(2, -53));
        QvModeBus top = Assert.Single(mode.TopBuses);
        Assert.Equal(2, top.BusId);
        Assert.True(Math.Abs(top.Participation - 1.0) <= 4 * Math.Pow(2, -53));
    }

    [Fact]
    public void T7_qv_modal_falls_towards_the_nose()
    {
        // Load 0.5 + j0.2 pu scaled by k at constant power factor. The nose (2V cos theta = 1) is at P = sqrt(29) - 2
        // (from V^4 + (2QX - 1)V^2 + X^2(P^2 + Q^2) = 0 with a double root), k_nose = 2 (sqrt(29) - 2). Closed-form J_R at
        // each fraction of the nose load (mpmath, 40 digits). The spec expected J_R below 1e-2 at 99.9 % of the nose; it is
        // 0.456 there (it falls as the square root of the margin, so 1e-2 needs about 99.99995 %).
        double kNose = 2 * (Math.Sqrt(29) - 2);
        (double F, double Jr, double Tol)[] points =
        {
            // Tol: ||J^-1||_inf x |d lambda / dx|_1 x 1e-12 from the closed-form state (3.3e-12, 8.7e-12, 3.3e-11,
            // 1.1e-10), with a 9x margin rounded up to 1e-9.
            (0.5, 7.979493696087662, 1e-9),
            (0.9, 4.11046701785471, 1e-9),
            (0.99, 1.404962291210355, 1e-9),
            (0.999, 0.4557075751928118, 1e-9),
        };
        double previous = double.PositiveInfinity;
        foreach ((double f, double jr, double tol) in points)
        {
            GridModel m = TwoBus(kNose * f);
            PowerFlowResult pf = Newton(m);
            Assert.True(pf.Converged, $"{f}: {pf.Status}");
            double lambda = Assert.Single(QvModalAnalysis.Analyse(m, pf).Modes).Eigenvalue.Real;
            Assert.True(Math.Abs(lambda - jr) <= tol, $"{f}: lambda {lambda:R} vs {jr:R}");
            Assert.True(lambda < previous, $"{f}: lambda {lambda:R} did not fall from {previous:R}");
            Assert.True(lambda > 0);
            previous = lambda;
        }
        Assert.True(previous > 0.4);
    }

    [Fact]
    public void T8_qv_modal_case14_matches_scipy_and_participation_sums_to_one()
    {
        GridModel m = Case("case14");
        PowerFlowResult pf = Newton(m);
        Assert.True(pf.Converged);
        QvModalResult r = QvModalAnalysis.Analyse(m, pf, modes: 9);
        Assert.Equal(new[] { 4, 5, 7, 9, 10, 11, 12, 13, 14 }, r.PqBusIds);
        Assert.Equal(9, r.TotalModes);
        Assert.Equal(9, r.Modes.Count);

        // SciPy 1.18.1 scipy.linalg.eig of J_R at a NumPy Newton solution (mismatch 6.5e-15). GridSim's state is within
        // ||J^-1|| 1e-12 = 2.24e-12 of the solution; |d lambda_min / dx|_1 = 8.68 (finite differences, same script), so the
        // eigenvalue may move by 1.9e-11; the bound 1e-10 keeps a 5x margin.
        Assert.Equal(1, r.Modes[0].Rank);
        Assert.True(Math.Abs(r.Modes[0].Eigenvalue.Real - 2.705999122610921) <= 1e-10, $"lambda_min {r.Modes[0].Eigenvalue.Real:R}");
        Assert.Equal(0.0, r.Modes[0].Eigenvalue.Imaginary);
        // The other eigenvalues, in |lambda| order (SciPy at the fdxb solution, tolerance 1e-12): an observed check with
        // a stated margin, 1e-8, about 100 times the state-driven movement of the largest of them.
        double[] scipy = { 2.7059991226109346, 5.569260665342948, 7.662056059138548, 11.335143956528858, 16.43174253715496, 18.921730400545187, 21.982824501854168, 39.95278499822518, 65.34235638707956 };
        for (int k = 0; k < 9; k++) Assert.True(Math.Abs(r.Modes[k].Eigenvalue.Real - scipy[k]) <= 1e-8, $"mode {k + 1}: {r.Modes[k].Eigenvalue:R}");

        // Participation sums to 1: each factor is divided by the mode's own sum, so the sum is 1 to the rounding of n
        // complex divisions (a few u each) and n - 1 additions: |sum - 1| <= (n + 4) u sum|P_k| with u = 2^-53.
        foreach (QvMode mode in r.Modes)
        {
            Complex sum = Complex.Zero;
            double abs = 0;
            foreach (Complex p in mode.Participation) { sum += p; abs += Complex.Abs(p); }
            double bound = (mode.Participation.Count + 4) * Math.Pow(2, -53) * abs;
            Assert.True(Complex.Abs(sum - Complex.One) <= bound, $"mode {mode.Rank}: sum {sum} bound {bound:R}");
            Assert.True(Complex.Abs(sum - Complex.One) <= 1e-12);
        }

        // The smallest mode's participation (SciPy): bus 14 leads with 0.316, then 10 (0.239) and 9 (0.200). An observed
        // check with a stated margin of 1e-8 (the eigenvector moves by about ||dJ_R|| / gap, gap 2.86, ~1e-10).
        QvMode m1 = r.Modes[0];
        Assert.Equal(new[] { 14, 10, 9 }, m1.TopBuses.Take(3).Select(b => b.BusId));
        double[] part = { 0.008249212345518247, 0.0039738764939189, 0.06991226540699813, 0.1999460696558936, 0.23940050982192632, 0.11077847877149635, 0.01896491942210711, 0.03240944873817976, 0.31636521934396156 };
        for (int k = 0; k < 9; k++) Assert.True(Math.Abs(m1.Participation[k].Real - part[k]) <= 1e-8, $"bus {r.PqBusIds[k]}: {m1.Participation[k]:R}");
        Assert.Equal(9, m1.TopBuses.Count);
        Assert.Equal(5, QvModalAnalysis.Analyse(m, pf).Modes.Count);
        Assert.Equal(3, QvModalAnalysis.Analyse(m, pf, modes: 2, topBuses: 3).Modes[1].TopBuses.Count);
    }

    [Fact]
    public void Qv_modal_refuses_an_unconverged_or_foreign_state()
    {
        GridModel m = TwoBus();
        PowerFlowResult bad = new NewtonRaphsonPowerFlow().Solve(TwoBus(20), new PowerFlowOptions { EnforceQLimits = false, RetryFromDcStart = false });
        Assert.False(bad.Converged);   // 20 x (0.5 + j0.2) is beyond the nose (k_nose 6.77)
        Assert.Throws<InvalidOperationException>(() => QvModalAnalysis.Analyse(TwoBus(20), bad));
        Assert.Throws<ArgumentException>(() => QvModalAnalysis.Analyse(Case("case14"), Newton(m)));
        Assert.Throws<ArgumentOutOfRangeException>(() => QvModalAnalysis.Analyse(m, Newton(m), modes: 0));
    }

    [Fact]
    public void Qv_modal_refuses_a_model_with_coupled_devices()
    {
        // Newton solves an SVC in an outer loop with the AC solve; J_R from the plain admittance matrix would leave it out.
        GridModel m = new()
        {
            Name = "two-bus-svc", BaseMva = 100,
            Buses = new[] { new Bus(1, BusType.Slack, Vm: 1.0), new Bus(2, BusType.Pq, Pd: 50, Qd: 20) },
            Generators = new[] { new Generator(1, 0, Vg: 1.0) },
            Branches = new[] { new Branch(1, 2, 0, 0.1) },
            StaticVarCompensators = new[] { new StaticVarCompensator(2, -20, 20, 0, 0.99, 1.01, "svc") },
        };
        Assert.True(m.HasCoupledDevices);
        PowerFlowResult solved = SolverSelector.CreateNewton(m).Solve(m, new PowerFlowOptions { MaxIterations = 100, EnforceQLimits = true });
        Assert.True(solved.Converged);
        var ex = Assert.Throws<InvalidOperationException>(() => QvModalAnalysis.Analyse(m, solved));
        Assert.Contains("static var compensators", ex.Message, StringComparison.Ordinal);
    }

    // ---- 9. bit-identity of the default pf ---------------------------------------------------------------------------

    /// <summary>The SHA-256 of a CLI result document with its "tool" line (the build stamp) and its "buildStatus" line left out, as
    /// Rc5Wp11WriterVerbsTests takes it.</summary>
    private static string ShaWithoutTool(byte[] raw)
    {
        byte[] bytes = Rc5LineEndings.ToCrLf(raw);
        byte[] tool = Encoding.ASCII.GetBytes("  \"tool\": ");
        byte[] status = Encoding.ASCII.GetBytes("  \"buildStatus\": ");
        var kept = new List<byte>(bytes.Length);
        bool first = true;
        int start = 0;
        for (int i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != (byte)'\n') continue;
            ReadOnlySpan<byte> line = bytes.AsSpan(start, i - start);
            if (!line.StartsWith(tool) && !line.StartsWith(status))
            {
                if (!first) kept.Add((byte)'\n');
                kept.AddRange(line.ToArray());
                first = false;
            }
            start = i + 1;
        }
        return Convert.ToHexStringLower(SHA256.HashData(kept.ToArray()));
    }

    private static (int Exit, string StdOut) RunIn(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } h ? h : "dotnet")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add(CliProcess.BuiltCliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);
        psi.Environment["GRIDSIM_CRASH_DIR"] = Path.Combine(dir, "crash");
        psi.Environment.Remove("GRIDSIM_LICENCE");
        psi.Environment["GRIDSIM_LICENCE_FILE"] = Path.Combine(dir, CliProcess.IsolatedLicenceFileName);
        using Process p = Process.Start(psi)!;
        p.StandardInput.Close();
        Task<string> o = p.StandardOutput.ReadToEndAsync();
        Task<string> e = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(300_000));
        p.WaitForExit();
        _ = e.Result;
        return (p.ExitCode, o.Result);
    }

    // Pinned from the 2.1.130 Release CLI (dev main 0ba32dc4) before this package's edits, run exactly as here: a fresh
    // working folder, "pf <case> --json pf.json", the licence isolated.
    [Theory]
    [InlineData("case14", "d09d28e8671f3e7d9406c93195b4d17f9a1c81060a927dcf9597e3cbf86303ef")]
    [InlineData("case30", "bece81c8a8be5b1ab478ead79f5c5ec3e4f495d8f43c79d4134ec49714bc0e10")]
    [InlineData("case118", "63f370c7f84b8a6d52147f7cb3fd5e0fdc3c1688472fe3564c135a987ae27af3")]
    public void T9_pf_without_engine_writes_the_2_1_130_bytes(string caseName, string sha)
    {
        string dir = TestTemp.Under("gridsim-wp25-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(0, RunIn(dir, "pf", caseName, "--json", "pf.json").Exit);
            Assert.Equal(sha, ShaWithoutTool(File.ReadAllBytes(Path.Combine(dir, "pf.json"))));
            // --engine newton is the same solve: the same bus table, with "engine" added to the summary.
            Assert.Equal(0, RunIn(dir, "pf", caseName, "--engine", "newton", "--json", "pfn.json").Exit);
            using JsonDocument a = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "pf.json")));
            using JsonDocument b = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "pfn.json")));
            Assert.Equal(a.RootElement.GetProperty("tables").GetRawText(), b.RootElement.GetProperty("tables").GetRawText());
            Assert.Equal("newton", b.RootElement.GetProperty("summary").GetProperty("engine").GetString());
            Assert.False(a.RootElement.GetProperty("summary").TryGetProperty("engine", out _));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Cli_engines_and_qv_modal_run_and_refuse_what_they_should()
    {
        string dir = TestTemp.Under("gridsim-wp25-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            (int exit, string text) = RunIn(dir, "pf", "case14", "--engine", "fdxb", "--json", "fd.json");
            Assert.Equal(0, exit);
            Assert.Contains("[engine fdxb]", text, StringComparison.Ordinal);
            using (JsonDocument d = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "fd.json"))))
                Assert.Equal("fdxb", d.RootElement.GetProperty("summary").GetProperty("engine").GetString());
            (exit, text) = RunIn(dir, "pf", "case14", "--engine", "gs", "--gs-accel", "1.5");
            Assert.Equal(0, exit);
            Assert.Contains("[engine gs, acceleration 1.5]", text, StringComparison.Ordinal);
            Assert.Equal(2, RunIn(dir, "pf", "case14", "--engine", "nr").Exit);
            Assert.Equal(2, RunIn(dir, "pf", "case14", "--gs-accel", "1.2").Exit);
            Assert.Equal(2, RunIn(dir, "pf", "case14", "--engine", "gs", "--gs-accel", "2").Exit);
            Assert.Equal(2, RunIn(dir, "pf", "case14", "--engine", "fdbx", "--distributed-slack", "pmax").Exit);

            (exit, text) = RunIn(dir, "qv-modal", "case14", "--modes", "3", "--out", "qv.json");
            Assert.Equal(0, exit);
            Assert.Contains("mode 1: lambda ", text, StringComparison.Ordinal);
            using (JsonDocument q = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "qv.json"))))
            {
                Assert.Equal("gridsim-qv-modal/1", q.RootElement.GetProperty("schema").GetString());
                Assert.Equal(3, q.RootElement.GetProperty("modes").GetArrayLength());
                Assert.True(q.RootElement.GetProperty("modes")[0].GetProperty("lambdaRe").GetDouble() > 0);
            }
            Assert.Equal(2, RunIn(dir, "qv-modal", "case14", "--out", "qv.json").Exit);   // never overwritten
            Assert.Equal(2, RunIn(dir, "qv-modal", "case14", "--modes", "0").Exit);
            Assert.Equal(2, RunIn(dir, "qv-modal", "case14", "--top", "3").Exit);
            Assert.Equal(2, RunIn(dir, "qv-modal").Exit);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
